#requires -Version 7.0
[CmdletBinding()]
param([string]$ReportFile, [string]$AppPublishDirectory, [ValidateSet('aot','self-contained')][string]$Variant = 'self-contained')
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$area = Join-Path $workspace ('output/ci/vr-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $area -Force | Out-Null
if (-not $ReportFile) { $ReportFile = Join-Path $area 'verification.json' }
$ReportFile = [IO.Path]::GetFullPath($ReportFile)
$packId = 'OMS.VR.' + [guid]::NewGuid().ToString('N').Substring(0,8)
$channel = "win-x64-$Variant"
$versions = @('0.1.1', '0.1.2')
$runtimeDirectory = Join-Path $workspace 'output/migration/toolchain/runtime'
$installRoot = Join-Path $area 'isolated-install'
$updateExe = Join-Path $installRoot 'Update.exe'
$installedExe = Join-Path $installRoot 'current/OhMyStudy.App.exe'
$userDirectory = Join-Path $area 'user-data'
$userFile = Join-Path $userDirectory 'keep.txt'
$userContent = 'PRESERVE USER DATA ' + [guid]::NewGuid().ToString('N')
$smokeReport = Join-Path $area 'update-smoke.json'
$checks = [Collections.Generic.List[string]]::new()
$report = [ordered]@{
    schemaVersion = 1
    checkStatus = 'running'
    evidence = $area
    packId = $packId
    versions = $versions
    installRoot = $installRoot
    userFile = $userFile
    smokeReport = $smokeReport
    checks = $checks
}
$savedReleaseTag = [Environment]::GetEnvironmentVariable('RELEASE_TAG', 'Process')
$installAttempted = $false
$failure = $null
$cleanupFailure = $null

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $checks.Add($Message)
    Write-Host "PASS: $Message"
}

function Invoke-LoggedProcess([string]$FilePath, [string[]]$Arguments, [string]$Label, [int]$TimeoutMilliseconds = 120000) {
    Write-Host "Running $Label`: $FilePath $($Arguments -join ' ')"
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FilePath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $timedOut = -not $process.WaitForExit($TimeoutMilliseconds)
        if ($timedOut) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "$Label exceeded $TimeoutMilliseconds ms; evidence=$area"
        }
        if ($process.ExitCode -ne 0) { throw "$Label exited with $($process.ExitCode); evidence=$area" }
    } finally {
        if (-not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit()
        }
        $process.Dispose()
    }
}

function New-Package([string]$Version, [string]$BaselinePackage) {
    $env:RELEASE_TAG = "v$Version"
    $output = Join-Path $area "package-v$Version"
    Write-Host "Building actual $Variant Velopack release $Version with isolated PackId $packId"
    $parameters = @{
        Variant = $Variant
        RuntimeDirectory = $runtimeDirectory
        OutputDirectory = $output
        PackId = $packId
        PackTitle = "Oh My Study Regression $packId"
    }
    if ($AppPublishDirectory) { $parameters.AppPublishDirectory = $AppPublishDirectory }
    if ($BaselinePackage) { $parameters.BaselinePackage = $BaselinePackage }
    & (Join-Path $PSScriptRoot '../Publish-OfflinePackage.ps1') @parameters
    $packageReports = @(Get-ChildItem -LiteralPath $output -Filter 'package-report.json' -File -Recurse)
    if ($packageReports.Count -ne 1) { throw "Expected one package report for $Version, found $($packageReports.Count)" }
    $packageReport = Get-Content -LiteralPath $packageReports[0].FullName -Raw | ConvertFrom-Json
    if ($packageReport.packageStatus -ne 'created-and-local-checks-passed') { throw "Package $Version failed: $($packageReport.error)" }
    if ($packageReport.version -ne $Version -or $packageReport.variant -ne $Variant -or $packageReport.channel -ne $channel) { throw "Package $Version report has unexpected identity" }
    return $packageReport
}

function Stop-IsolatedProcesses {
    $prefix = $installRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    foreach ($process in @(Get-Process)) {
        try { $path = $process.Path } catch { continue }
        if (-not $path) { continue }
        if ($path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or $path.Equals($updateExe, [StringComparison]::OrdinalIgnoreCase)) {
            Write-Host "Stopping isolated process before uninstall: PID=$($process.Id) Path=$path"
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction Stop }
        }
    }
}

try {
    New-Item -ItemType Directory -Path $userDirectory -Force | Out-Null
    [IO.File]::WriteAllText($userFile, $userContent)
    $v1 = New-Package $versions[0]
    $AppPublishDirectory = $v1.appPublishDirectory
    Assert (Test-Path -LiteralPath $AppPublishDirectory -PathType Container) 'v1 produced a reusable actual app publish'
    $v2 = New-Package $versions[1] $v1.fullPackage
    $report.packageReports = @((Join-Path $v1.runRoot 'package-report.json'), (Join-Path $v2.runRoot 'package-report.json'))

    $v1Expected = @{
        installer = "$packId-$channel-Setup.exe"
        fullPackage = "$packId-$($versions[0])-$channel-full.nupkg"
        feed = "releases.$channel.json"
    }
    $v2Expected = @{
        installer = "$packId-$channel-Setup.exe"
        fullPackage = "$packId-$($versions[1])-$channel-full.nupkg"
        deltaPackage = "$packId-$($versions[1])-$channel-delta.nupkg"
        feed = "releases.$channel.json"
    }
    foreach ($entry in $v1Expected.GetEnumerator()) {
        $path = $v1.($entry.Key)
        Assert ((Test-Path -LiteralPath $path -PathType Leaf) -and [IO.Path]::GetFileName($path) -eq $entry.Value) "v1 expected $($entry.Key) asset exists"
    }
    foreach ($entry in $v2Expected.GetEnumerator()) {
        $path = $v2.($entry.Key)
        Assert ((Test-Path -LiteralPath $path -PathType Leaf) -and [IO.Path]::GetFileName($path) -eq $entry.Value) "v2 expected $($entry.Key) asset exists"
    }
    Assert (-not $v1.deltaPackage) 'v1 full build has no delta package'
    Assert ([IO.Path]::GetFullPath($v2.fullPackage) -ne [IO.Path]::GetFullPath($v1.fullPackage)) 'v1 and v2 are distinct full packages'
    $feed = Get-Content -LiteralPath $v2.feed -Raw | ConvertFrom-Json
    $fullAssets = @($feed.Assets | Where-Object { $_.PackageId -eq $packId -and $_.Version -eq $versions[1] -and $_.Type -eq 'Full' -and $_.FileName -eq $v2Expected.fullPackage })
    $deltaAssets = @($feed.Assets | Where-Object { $_.PackageId -eq $packId -and $_.Version -eq $versions[1] -and $_.Type -eq 'Delta' -and $_.FileName -eq $v2Expected.deltaPackage })
    Assert ($fullAssets.Count -eq 1 -and $deltaAssets.Count -eq 1) 'v2 feed advertises exactly one full and one delta target asset'

    $installAttempted = $true
    Invoke-LoggedProcess $v1.installer @('--silent', '--verbose', '--log', (Join-Path $area 'install-velopack.log'), '--installto', $installRoot, '--', '--update-smoke', $smokeReport, (Split-Path -Parent $v2.feed)) 'install-v1' 180000
    Assert ((Test-Path -LiteralPath $installedExe -PathType Leaf) -and (Test-Path -LiteralPath $updateExe -PathType Leaf)) 'v1 installed only at the isolated custom root'
    Invoke-LoggedProcess $installedExe @('--update-smoke', $smokeReport, (Split-Path -Parent $v2.feed)) 'launch-v1-update-smoke' 180000
    Write-Host "Waiting for the installed app to download, apply, restart, and report: $smokeReport"
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(5)
    $updateResult = $null
    $lastObservation = 'report not created'
    $downloadObserved = $false
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $candidate = $null
        if (Test-Path -LiteralPath $smokeReport -PathType Leaf) {
            try {
                $candidate = Get-Content -LiteralPath $smokeReport -Raw | ConvertFrom-Json
                $lastObservation = Get-Content -LiteralPath $smokeReport -Raw
            } catch {
                $lastObservation = $_.Exception.Message
            }
        }
        if ($candidate) {
            if ($candidate.passed -eq $false) { throw "Update smoke failed: $lastObservation" }
            if ($candidate.restarted -eq $true) {
                $updateResult = $candidate
                break
            }
            if ($candidate.downloaded -eq $true -and -not $downloadObserved) {
                $downloadObserved = $true
                Write-Host "Update downloaded; waiting for restarted process: $lastObservation"
            }
        }
        Start-Sleep -Seconds 1
    }
    if ($null -eq $updateResult) { throw "Update smoke timed out; report=$smokeReport; last observation=$lastObservation" }
    Assert ($updateResult.passed -eq $true -and $updateResult.downloaded -eq $true -and $updateResult.restarted -eq $true) 'installed app checked, downloaded, applied, and restarted'
    Assert ($updateResult.version -eq $versions[1]) "restarted app reports target version $($versions[1])"
    Assert ([IO.File]::ReadAllText($userFile) -eq $userContent) 'update preserves user file outside the install root'
    $report.updateResult = $updateResult
} catch {
    $failure = $_
    $report.checkStatus = 'failed'
    $report.error = $_.Exception.Message
} finally {
    try {
        if ($installAttempted) {
            if (Test-Path -LiteralPath $updateExe -PathType Leaf) {
                Stop-IsolatedProcesses
                Invoke-LoggedProcess $updateExe @('uninstall', '--silent', '--verbose', '--rootDir', $installRoot, '--log', (Join-Path $area 'uninstall-velopack.log')) 'uninstall' 120000
                $uninstallDeadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
                while ((Test-Path -LiteralPath $installedExe) -and [DateTimeOffset]::UtcNow -lt $uninstallDeadline) { Start-Sleep -Milliseconds 500 }
                Assert (-not (Test-Path -LiteralPath $installedExe)) 'isolated Velopack uninstall removes the installed app'
            } elseif (Test-Path -LiteralPath $installRoot) {
                throw "Isolated installation exists but its Update.exe is missing; manual inspection required: $installRoot"
            }
            Assert ([IO.File]::ReadAllText($userFile) -eq $userContent) 'uninstall preserves user file outside the install root'
        }
    } catch {
        $cleanupFailure = $_
        $report.checkStatus = 'failed'
        $report.cleanupError = $_.Exception.Message
    } finally {
        [Environment]::SetEnvironmentVariable('RELEASE_TAG', $savedReleaseTag, 'Process')
        if (-not $failure -and -not $cleanupFailure) { $report.checkStatus = 'selected-local-checks-passed' }
        New-Item -ItemType Directory -Path (Split-Path -Parent $ReportFile) -Force | Out-Null
        $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ReportFile -Encoding utf8
        Write-Host "Velopack installer regression evidence: $area"
    }
}
if ($failure) { throw $failure }
if ($cleanupFailure) { throw $cleanupFailure }
Write-Output "PASS Velopack installer regression: $($checks.Count) checks; $ReportFile"
