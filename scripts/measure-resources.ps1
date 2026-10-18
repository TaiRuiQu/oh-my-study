#requires -Version 7.0
<#!
Read-only measurement. Process IDs must be identified by the caller; this script
never starts, stops, attaches to, or claims ownership of a Sandbox session.
!#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Scenario,
    [Parameter(Mandatory)][int[]]$RootProcessIds,
    [int[]]$VmProcessIds = @(),
    [int[]]$SystemProcessIds = @(),
    [ValidateRange(1, 86400)][int]$DurationSeconds = 30,
    [ValidateRange(200, 10000)][int]$IntervalMilliseconds = 1000,
    [string]$ComponentsManifest,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This measurement probe requires Windows.' }
if (@($RootProcessIds | Where-Object { $_ -le 0 }).Count) { throw 'Invalid root process ID.' }
$allIds = @($RootProcessIds) + @($VmProcessIds) + @($SystemProcessIds)
if (($allIds | Sort-Object -Unique).Count -ne $allIds.Count) { throw 'Process groups must not overlap.' }
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$identities = @{}
foreach ($group in @('host', 'vm', 'system')) {
    $groupIds = switch ($group) { 'host' { $RootProcessIds }; 'vm' { $VmProcessIds }; 'system' { $SystemProcessIds } }
    foreach ($processNumber in $groupIds) {
        $processInfo = Get-Process -Id $processNumber
        $identities[[int]$processNumber] = @{ start = $processInfo.StartTime.ToUniversalTime().Ticks; group = $group }
    }
}
$samples = [Collections.Generic.List[object]]::new()
$warnings = [Collections.Generic.HashSet[string]]::new()
$clock = [Diagnostics.Stopwatch]::StartNew()
do {
    $censusAvailable = $true
    try { $census = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, CreationDate) }
    catch { $census = @(); $censusAvailable = $false; [void]$warnings.Add('Process tree census unavailable: descendants may be missing.') }
    # Keep discovered descendants after their parent exits. Bind every PID to its
    # creation time so PID reuse cannot silently enter another application's group.
    do {
        $added = $false
        foreach ($entry in $census) {
            $number = [int]$entry.ProcessId
            $parentNumber = [int]$entry.ParentProcessId
            if ($identities.ContainsKey($number) -or -not $identities.ContainsKey($parentNumber)) { continue }
            $parentIdentity = $identities[$parentNumber]
            $parentEntry = $census | Where-Object { [int]$_.ProcessId -eq $parentNumber } | Select-Object -First 1
            if ($null -eq $parentEntry -or $null -eq $parentEntry.CreationDate -or $null -eq $entry.CreationDate) { continue }
            # CIM timestamps have lower precision than Process.StartTime.
            if ([Math]::Abs($parentEntry.CreationDate.ToUniversalTime().Ticks - $parentIdentity.start) -gt 10000) { continue }
            if ($entry.CreationDate.ToUniversalTime().Ticks -lt $parentIdentity.start) { continue }
            $identities[$number] = @{ start = $entry.CreationDate.ToUniversalTime().Ticks; group = $parentIdentity.group }
            $added = $true
        }
    } while ($added)
    $totals = @{ host = [long]0; vm = [long]0; system = [long]0 }
    $counts = @{ host = 0; vm = 0; system = 0 }
    $unreadable = 0
    $processRows = [Collections.Generic.List[object]]::new()
    foreach ($number in @($identities.Keys)) {
        try {
            $processInfo = Get-Process -Id $number -ErrorAction SilentlyContinue
            if ($null -eq $processInfo) { continue }
            $identity = $identities[$number]
            if ([Math]::Abs($processInfo.StartTime.ToUniversalTime().Ticks - $identity.start) -gt 10000) { continue }
            $privateBytes = [long]$processInfo.PrivateMemorySize64
            $totals[$identity.group] += $privateBytes
            $counts[$identity.group]++
            $processRows.Add([ordered]@{ id = $number; name = $processInfo.ProcessName; group = $identity.group; privateBytes = $privateBytes })
        } catch { $unreadable++; [void]$warnings.Add('Some process metrics could not be read; totals are lower bounds.') }
    }
    $samples.Add([ordered]@{
        elapsedMs = $clock.ElapsedMilliseconds
        timestampUtc = [DateTime]::UtcNow.ToString('o')
        treeCensusAvailable = $censusAvailable
        unreadableProcesses = $unreadable
        hostPrivateBytes = $totals.host
        vmPrivateBytes = $(if ($VmProcessIds.Count) { $totals.vm } else { $null })
        systemPrivateBytes = $(if ($SystemProcessIds.Count) { $totals.system } else { $null })
        combinedObservedPrivateBytes = $totals.host + $totals.vm + $totals.system
        processes = @($processRows.ToArray())
    })
    if ($clock.Elapsed.TotalSeconds -lt $DurationSeconds) { Start-Sleep -Milliseconds $IntervalMilliseconds }
} while ($clock.Elapsed.TotalSeconds -lt $DurationSeconds)

$components = [Collections.Generic.List[object]]::new()
if ($ComponentsManifest) {
    $manifestPath = (Resolve-Path -LiteralPath $ComponentsManifest).Path
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    foreach ($component in $manifest) {
        $componentPath = [IO.Path]::GetFullPath([IO.Path]::Combine((Split-Path $manifestPath), $component.path))
        if (-not (Test-Path -LiteralPath $componentPath)) {
            $components.Add([ordered]@{ name = $component.name; status = 'missing'; bytes = $null }); continue
        }
        $item = Get-Item -LiteralPath $componentPath -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Do not measure a reparse point as a component.' }
        $pending = [Collections.Generic.Stack[IO.FileSystemInfo]]::new()
        $pending.Push($item)
        $size = [long]0
        while ($pending.Count) {
            $current = $pending.Pop()
            if ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Component contains a reparse point.' }
            if ($current.PSIsContainer) { foreach ($child in Get-ChildItem -LiteralPath $current.FullName -Force) { $pending.Push($child) } }
            else { $size += $current.Length }
        }
        $components.Add([ordered]@{ name = $component.name; status = 'measured'; bytes = $size })
    }
}
$report = [ordered]@{
    schemaVersion = 1
    scenario = $Scenario
    timestampUtc = [DateTime]::UtcNow.ToString('o')
    os = [Environment]::OSVersion.VersionString
    measurement = 'sampled process private bytes; not VM physical RAM or a proven workload peak'
    intervalMilliseconds = $IntervalMilliseconds
    requestedDurationSeconds = $DurationSeconds
    durationMilliseconds = $clock.ElapsedMilliseconds
    sampleCount = $samples.Count
    rootProcessIds = $RootProcessIds
    vmStatus = $(if ($VmProcessIds.Count) { 'explicitly_selected_processes_only' } else { 'not_measured' })
    systemStatus = $(if ($SystemProcessIds.Count) { 'explicitly_selected_processes_only' } else { 'not_measured' })
    hostPeakObservedPrivateBytes = ($samples.hostPrivateBytes | Measure-Object -Maximum).Maximum
    combinedPeakObservedPrivateBytes = ($samples.combinedObservedPrivateBytes | Measure-Object -Maximum).Maximum
    warnings = @($warnings)
    components = @($components.ToArray())
    samples = @($samples.ToArray())
}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $outputPath 'resources.json') -Encoding utf8
$samples | Select-Object elapsedMs, timestampUtc, treeCensusAvailable, unreadableProcesses, hostPrivateBytes, vmPrivateBytes, systemPrivateBytes, combinedObservedPrivateBytes |
    Export-Csv -LiteralPath (Join-Path $outputPath 'resources.csv') -NoTypeInformation -Encoding utf8
Write-Output (Join-Path $outputPath 'resources.json')
