#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [string]$OutputDirectory,
    [ValidateRange(1,30)][int]$Runs = 3,
    [ValidateRange(15,600)][int]$TimeoutSeconds = 120,
    [ValidateRange(5,100)][int]$ReadyPollMilliseconds = 10
)
$ErrorActionPreference = 'Stop'
$taskExecutable = (Resolve-Path -LiteralPath $Executable).Path
if (-not (Test-Path -LiteralPath $taskExecutable -PathType Leaf)) { throw 'Application executable is missing.' }
if (-not $OutputDirectory) {
    $taskWorkspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
    $OutputDirectory = Join-Path $taskWorkspace ('output/migration/evidence/performance-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
}
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskObservations = [Collections.Generic.List[object]]::new()
$taskFailures = [Collections.Generic.List[string]]::new()
function Get-NearestRank([double[]]$Values, [double]$Percentile) {
    if ($Values.Count -eq 0) { return $null }
    $taskSorted = @($Values | Sort-Object)
    return $taskSorted[[Math]::Max(0, [int][Math]::Ceiling($taskSorted.Count * $Percentile) - 1)]
}
for ($taskRun = 1; $taskRun -le $Runs; $taskRun++) {
    $taskReport = Join-Path $taskOutput ('run-' + $taskRun + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8) + '.json')
    $taskReady = $taskReport + '.ready'
    $taskProcess = $null
    $taskStartupMilliseconds = $null
    $taskClock = [Diagnostics.Stopwatch]::StartNew()
    try {
        # This is the measured UI, not a background helper. Rendering requires a shown window.
        # No process enumeration or process-name termination is used: only this returned PID is owned.
        $taskProcess = Start-Process -FilePath $taskExecutable -ArgumentList @('--performance-smoke', ('"' + $taskReport + '"')) -WindowStyle Normal -PassThru
        while (-not $taskProcess.HasExited -and $taskClock.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
            if ($null -eq $taskStartupMilliseconds -and (Test-Path -LiteralPath $taskReady -PathType Leaf)) {
                try {
                    $taskMarker = Get-Content -LiteralPath $taskReady -Raw | ConvertFrom-Json
                    if ($taskMarker.ProcessId -eq $taskProcess.Id -and $taskMarker.Ready) { $taskStartupMilliseconds = $taskClock.Elapsed.TotalMilliseconds }
                }
                catch { } # The writer may still be completing this owned marker; retry at the next poll.
            }
            Start-Sleep -Milliseconds $ReadyPollMilliseconds
            $taskProcess.Refresh()
        }
        if (-not $taskProcess.HasExited) { throw "Owned process $($taskProcess.Id) exceeded $TimeoutSeconds seconds." }
        $taskProcess.WaitForExit()
        if (-not (Test-Path -LiteralPath $taskReport -PathType Leaf)) { throw "Owned process $($taskProcess.Id) produced no report (exit $($taskProcess.ExitCode))." }
        $taskResult = Get-Content -LiteralPath $taskReport -Raw | ConvertFrom-Json
        $taskEmptyIdle = @($taskResult.Memory | Where-Object Phase -eq 'empty-natural-idle')
        $taskReleasedIdle = @($taskResult.Memory | Where-Object Phase -eq 'released-natural-idle')
        $taskSuccess = $taskProcess.ExitCode -eq 0 -and $taskResult.Collected -and $null -ne $taskStartupMilliseconds
        $taskObservations.Add([pscustomobject]@{
            run = $taskRun; processId = $taskProcess.Id; collected = $taskSuccess; exitCode = $taskProcess.ExitCode
            readyObservedMilliseconds = $taskStartupMilliseconds; elapsedMilliseconds = $taskClock.Elapsed.TotalMilliseconds
            emptyIdlePrivateBytesP95 = Get-NearestRank @($taskEmptyIdle | ForEach-Object { [double]$_.PrivateBytes }) .95
            releasedIdlePrivateBytesP95 = Get-NearestRank @($taskReleasedIdle | ForEach-Object { [double]$_.PrivateBytes }) .95
            readingP95Milliseconds = $taskResult.PageLoadMilliseconds.P95
            renderingCallbackP95Milliseconds = $taskResult.RenderingCallbackMilliseconds.P95
            dispatcherP95Milliseconds = $taskResult.DispatcherLatencyMilliseconds.P95
            report = $taskReport; error = $taskResult.Error
        })
        if (-not $taskSuccess) { $taskFailures.Add("Run $taskRun did not complete all sampling checks; inspect $taskReport.") }
        Write-Host "Performance run $taskRun/${Runs}: $taskReport"
    }
    catch {
        $taskFailures.Add($_.Exception.Message)
        $taskObservations.Add([pscustomobject]@{ run=$taskRun; processId=if ($taskProcess) { $taskProcess.Id } else { $null }; collected=$false; readyObservedMilliseconds=$taskStartupMilliseconds; report=$taskReport; error=$_.Exception.Message })
    }
    finally {
        if ($taskProcess) {
            $taskProcess.Refresh()
            if (-not $taskProcess.HasExited) { $taskProcess.Kill(); $taskProcess.WaitForExit(5000) | Out-Null }
            $taskProcess.Dispose()
        }
    }
}
$taskStartups = @($taskObservations | Where-Object { $null -ne $_.readyObservedMilliseconds } | ForEach-Object { [double]$_.readyObservedMilliseconds })
$taskSummary = [ordered]@{
    timestampUtc = [DateTimeOffset]::UtcNow.ToString('O'); collected = $taskFailures.Count -eq 0
    executable = $taskExecutable; executableSha256 = (Get-FileHash -LiteralPath $taskExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
    requestedRuns = $Runs; completedStartupSamples = $taskStartups.Count; readyPollMilliseconds = $ReadyPollMilliseconds
    processStartToReadyP50Milliseconds = Get-NearestRank $taskStartups .50
    processStartToReadyP95Milliseconds = Get-NearestRank $taskStartups .95
    processStartToReadyP99Milliseconds = Get-NearestRank $taskStartups .99
    runs = $taskObservations.ToArray(); failures = $taskFailures.ToArray()
    measurementBoundaries = @(
        'Ready time is launcher Stopwatch before Start-Process to observing an owned PID marker after isolated empty-library initialization and dispatcher response; polling and reporter overhead are included.'
        'Runs are sequential. No OS cache flush is performed, so the first run is not certified cold and later runs are not certified warm.'
        'Default sample size is three. P95/P99 use nearest rank and equal the maximum at this size; this is an initial regression sample, not a stable population estimate.'
        'The measured UI is shown for rendering. Rendering callback intervals are not GPU presentation or ETW frame intervals, and callback miss estimates use a stated 60 Hz reference.'
        'No forced GC is used. Natural idle private bytes may retain memory; raw memory, cache, GC collection-count and CPU-time curves remain in every app report.'
        'This script starts and tracks only its returned process IDs. It never finds, closes or kills other application windows, and every run uses a new isolated library and pointer.'
        'Collection success does not assert an 8 GB reference-device, startup, memory or frame-time budget passed. DPI, IME, physical input, accessibility and GPU validation remain user checks.'
    )
}
$taskSummary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $taskOutput 'summary.json') -Encoding utf8
Write-Host ('Performance evidence: ' + (Join-Path $taskOutput 'summary.json'))
if ($taskFailures.Count -ne 0) { throw ($taskFailures -join [Environment]::NewLine) }
