#requires -Version 7.0
<#
P1 native proof, independent of Node.js, npm, Cargo, rustc and WebView.
Use a normal interactive user session for credentials and WinUI smoke tests.
Each run has a fresh publish directory: stale JIT files cannot masquerade as AOT.
#>
[CmdletBinding()]
param(
    [switch]$SkipRestore,
    [switch]$SkipDocuments,
    [switch]$SkipDesktopBuild,
    [switch]$DesktopSmoke,
    [switch]$Credentials
)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$nativeRoot = $workspace
$outputRoot = Join-Path $workspace 'output/migration'
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runRoot = Join-Path $outputRoot "runs/$runId"
$originalLocation = Get-Location
$savedEnvironment = @{}
foreach ($key in @('DOTNET_CLI_HOME','NUGET_PACKAGES','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_GENERATE_ASPNET_CERTIFICATE')) {
    $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
}
$report = [ordered]@{ runId=$runId; timestampUtc=[DateTimeOffset]::UtcNow.ToString('O'); status='running'; runRoot=$runRoot; checks=@(); notRun=@() }
if ($SkipDocuments) { $report.notRun += 'documents-probe-and-pdf-smoke' }
if ($SkipDesktopBuild) { $report.notRun += 'desktop-build-and-smoke' }
elseif (-not $DesktopSmoke) { $report.notRun += 'desktop-smoke' }
if (-not $Credentials) { $report.notRun += 'credential-store-roundtrip' }
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

function Invoke-CheckedDotnet([string[]]$Arguments, [string]$LogFile) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath $LogFile | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE); see $LogFile" }
}

function Publish-NativeProject([string]$RelativeProject, [string]$Name) {
    $project = Join-Path $nativeRoot $RelativeProject
    if (-not $SkipRestore) {
        # Configuration must be present during restore, when ILCompiler targets are selected.
        # RID lives on each executable csproj; passing a global -r also mutates library lock graphs.
        Invoke-CheckedDotnet -Arguments @('restore', $project, '-p:Configuration=Release', '--locked-mode', '--disable-parallel') -LogFile (Join-Path $runRoot "$Name-restore.log")
    }
    $publish = Join-Path $runRoot "publish/$Name"
    Invoke-CheckedDotnet -Arguments @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--no-restore', '-p:TrimmerSingleWarn=false', '-o', $publish) -LogFile (Join-Path $runRoot "$Name-publish.log")
    if (Test-Path -LiteralPath (Join-Path $publish 'coreclr.dll')) { throw "JIT runtime found in native publish: $publish" }
    return $publish
}

try {
    Set-Location -LiteralPath $nativeRoot
    $env:DOTNET_CLI_HOME = Join-Path $outputRoot 'dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $outputRoot 'nuget'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    & (Join-Path $nativeRoot 'packaging/Test-RepositoryLayout.ps1')
    $report.checks += 'csharp-repository-layout'
    $sdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdk -ne '10.0.402') { throw "Expected locked .NET SDK 10.0.402, found $sdk" }
    & dotnet --info | Set-Content -LiteralPath (Join-Path $runRoot 'dotnet-info.txt') -Encoding utf8

    $infrastructure = Publish-NativeProject 'tests/OhMyStudy.IntegrationTests/OhMyStudy.IntegrationTests.csproj' 'infrastructure'
    $probeArguments = @((Join-Path $runRoot 'infrastructure'))
    if ($Credentials) { $probeArguments += '--credentials' }
    & (Join-Path $infrastructure 'OhMyStudy.IntegrationTests.exe') @probeArguments
    if ($LASTEXITCODE -ne 0) { throw 'Native infrastructure probes failed' }
    $report.checks += 'infrastructure-native-aot'

    if (-not $SkipDocuments) {
        & (Join-Path $nativeRoot 'probes/prepare-documents.ps1') -WorkspaceRoot $workspace
        $documents = Publish-NativeProject 'probes/OhMyStudy.Documents.Probe/OhMyStudy.Documents.Probe.csproj' 'documents'
        & (Join-Path $documents 'OhMyStudy.Documents.Probe.exe') $workspace (Join-Path $runRoot 'documents')
        if ($LASTEXITCODE -ne 0) { throw 'Native document probes failed' }
        $documentReport = Get-Content -LiteralPath (Join-Path $runRoot 'documents/verification.json') -Raw | ConvertFrom-Json
        if ($documentReport.DynamicCodeSupported) { throw 'Document probe was not Native AOT' }
        $report.checks += 'documents-native-aot'
    }

    if (-not $SkipDesktopBuild) {
        $app = Publish-NativeProject 'src/OhMyStudy.App/OhMyStudy.App.csproj' 'app'
        $appExe = Join-Path $app 'OhMyStudy.App.exe'
        if (-not (Test-Path -LiteralPath $appExe -PathType Leaf)) { throw 'Native application executable missing' }
        $nativeDirectory = Join-Path $app 'native'
        New-Item -ItemType Directory -Path $nativeDirectory -Force | Out-Null
        $pdfium = Join-Path $outputRoot 'toolchain/pdfium/pdfium.dll'
        if (-not (Test-Path -LiteralPath $pdfium)) {
            throw 'App PDFium runtime missing. Prepare pinned native document tools before publishing the native App, even when document probes are skipped.'
        }
        $pdfiumLock = Get-Content -LiteralPath (Join-Path $nativeRoot 'probes/native-documents.lock.json') -Raw | ConvertFrom-Json
        if ((Get-FileHash -LiteralPath $pdfium -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pdfiumLock.pdfium.librarySha256) { throw 'App PDFium runtime hash mismatch' }
        $pdfiumNotices = Join-Path $outputRoot 'toolchain/pdfium/licenses'
        if (-not (Test-Path -LiteralPath (Join-Path $pdfiumNotices 'pdfium.txt'))) { throw 'App PDFium license notices missing; prepare native document tools again' }
        if (Test-Path -LiteralPath $pdfium) {
            Copy-Item -LiteralPath $pdfium -Destination (Join-Path $nativeDirectory 'pdfium.dll')
            Copy-Item -LiteralPath $pdfiumNotices -Destination (Join-Path $nativeDirectory 'pdfium-licenses') -Recurse
        }
        $report.appExecutable = $appExe
        $report.checks += 'app-native-publish'
        if ($DesktopSmoke) {
            $desktopReport = Join-Path $runRoot 'desktop-smoke.json'
            $arguments = @('--smoke-test', ('"' + $desktopReport + '"'))
            if (-not $SkipDocuments) { $arguments += @('--preview-pdf', ('"' + (Join-Path $runRoot 'documents/a4-questions.pdf') + '"')) }
            $process = Start-Process -FilePath $appExe -ArgumentList $arguments -WindowStyle Hidden -PassThru
            if (-not $process.WaitForExit(45000)) { $process.Kill(); throw 'Desktop smoke timed out after 45 seconds' }
            if ($process.ExitCode -ne 0) { throw "Desktop smoke exit $($process.ExitCode); check startup diagnostic beside $desktopReport" }
            $desktop = Get-Content -LiteralPath $desktopReport -Raw | ConvertFrom-Json
            if (-not $desktop.Passed -or -not $desktop.IsNativeAot) { throw 'Desktop native assertions failed' }
            $report.checks += 'desktop-native-aot-smoke'
        }
    }
    $report.status = if ($report.notRun.Count -eq 0) { 'passed' } else { 'selected-checks-passed' }
}
catch {
    $report.status = 'failed'
    $report.error = $_.Exception.Message
    throw
}
finally {
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'run.json') -Encoding utf8
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot 'latest-run.json') -Encoding utf8
    foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key], 'Process') }
    Set-Location -LiteralPath $originalLocation
    Write-Host "Native check evidence: $runRoot"
}
