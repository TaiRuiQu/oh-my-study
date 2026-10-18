#requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$SkipDocuments,
    [switch]$SkipApp,
    [switch]$DesktopSmoke,
    [switch]$WorkflowSmoke,
    [switch]$PrepareDocuments,
    [string]$RuntimeDirectory
)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$output = Join-Path $workspace 'output/migration'
if (-not $RuntimeDirectory) { $RuntimeDirectory=Join-Path $output 'toolchain/runtime/python' }
$runId = 'migration-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$run = Join-Path $output "runs/$runId"
New-Item -ItemType Directory -Path $run -Force | Out-Null
$savedLocation = Get-Location
$savedEnvironment = @{}
foreach ($key in @('DOTNET_CLI_HOME','NUGET_PACKAGES','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_GENERATE_ASPNET_CERTIFICATE')) { $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key,'Process') }
$report = [ordered]@{ schemaVersion=1; runId=$runId; timestampUtc=[DateTimeOffset]::UtcNow.ToString('O'); status='release-candidate-unvalidated'; checkStatus='running'; runRoot=$run; checks=@(); notRun=@(
    'clean-Windows-10-11-offline-installation-matrix','real-library-cutover-and-rollback','real-provider-and-paper-semantics','real-Sandbox-session-ownership','physical-printing','DPI-accessibility-and-performance-budget','real-credential-store-migration') }
$projects = @(
    @{name='domain'; project='tests/OhMyStudy.Domain.Tests/OhMyStudy.Domain.Tests.csproj'; executable='OhMyStudy.Domain.Tests.exe'},
    @{name='storage'; project='tests/OhMyStudy.Storage.Tests/OhMyStudy.Storage.Tests.csproj'; executable='OhMyStudy.Storage.Tests.exe'},
    @{name='provider'; project='tests/OhMyStudy.Provider.Tests/OhMyStudy.Provider.Tests.csproj'; executable='OhMyStudy.Provider.Tests.exe'},
    @{name='execution'; project='tests/OhMyStudy.Execution.Tests/OhMyStudy.Execution.Tests.csproj'; executable='OhMyStudy.Execution.Tests.exe'},
    @{name='grading'; project='tests/OhMyStudy.Grading.Tests/OhMyStudy.Grading.Tests.csproj'; executable='OhMyStudy.Grading.Tests.exe'},
    @{name='media'; project='tests/OhMyStudy.Media.Tests/OhMyStudy.Media.Tests.csproj'; executable='OhMyStudy.Media.Tests.exe'},
    @{name='documents'; project='probes/OhMyStudy.Documents.Probe/OhMyStudy.Documents.Probe.csproj'; executable='OhMyStudy.Documents.Probe.exe'},
    @{name='app'; project='src/OhMyStudy.App/OhMyStudy.App.csproj'; executable='OhMyStudy.App.exe'}
)

function Invoke-Dotnet([string[]]$Arguments,[string]$Log) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath $Log | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet exit $LASTEXITCODE; see $Log" }
}
function Assert-NativeExe([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Executable missing: $Path" }
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 256 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) { throw "Not a PE executable: $Path" }
    $pe = [BitConverter]::ToInt32($bytes,0x3c)
    if ($pe -lt 0 -or $pe + 264 -ge $bytes.Length -or [BitConverter]::ToUInt16($bytes,$pe+4) -ne 0x8664 -or
        [BitConverter]::ToUInt16($bytes,$pe+24) -ne 0x20b -or [BitConverter]::ToUInt32($bytes,$pe+24+112+14*8) -ne 0) { throw "Expected native x64 PE without CLR header: $Path" }
}

try {
    Set-Location -LiteralPath $workspace
    $env:DOTNET_CLI_HOME = Join-Path $output 'dotnet-home'; $env:NUGET_PACKAGES = Join-Path $output 'nuget'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; $env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
    & (Join-Path $PSScriptRoot 'Test-RepositoryLayout.ps1')
    $sdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdk -ne '10.0.402') { throw "Expected locked SDK 10.0.402; found $sdk" }
    $report.sdk = $sdk
    if ($PrepareDocuments) {
        & (Join-Path $workspace 'probes/prepare-documents.ps1') -WorkspaceRoot $workspace
        & (Join-Path $PSScriptRoot 'prepare-webp.ps1') -WorkspaceRoot $workspace
    }
    foreach ($entry in $projects) {
        if (($entry.name -eq 'documents' -and $SkipDocuments) -or ($entry.name -eq 'app' -and $SkipApp)) { $report.notRun += "$($entry.name): explicitly skipped"; continue }
        $project = Join-Path $workspace $entry.project
        if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { $report.notRun += "$($entry.name): project not present"; continue }
        $check = [ordered]@{ name=$entry.name; status='running'; project=$entry.project }
        $report.checks += $check
        $clock = [Diagnostics.Stopwatch]::StartNew()
        try {
            # ILCompiler targets are selected during restore. Never restore Release AOT as Debug.
            Invoke-Dotnet @('restore',$project,'-p:Configuration=Release','--locked-mode','--disable-parallel') (Join-Path $run "$($entry.name)-restore.log")
            $publish = Join-Path $run "publish/$($entry.name)"
            Invoke-Dotnet @('publish',$project,'-c','Release','--no-restore','-p:TrimmerSingleWarn=false','-o',$publish) (Join-Path $run "$($entry.name)-publish.log")
            $exe = Join-Path $publish $entry.executable
            Assert-NativeExe $exe
            if (Test-Path -LiteralPath (Join-Path $publish 'coreclr.dll')) { throw 'CoreCLR found in Native AOT output' }
            $check.executable = $exe; $check.sha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($entry.name -eq 'app') {
                $check.status='publish-passed'; $report.appPublishDirectory=$publish
                if ($DesktopSmoke) {
                    $smoke = Join-Path $run 'desktop-smoke.json'
                    $native = Join-Path $publish 'native'; New-Item -ItemType Directory -Path $native -Force | Out-Null
                    $pdfium = Join-Path $output 'toolchain/pdfium/pdfium.dll'
                    if (-not (Test-Path -LiteralPath $pdfium -PathType Leaf)) { throw 'Prepare pinned document tools before desktop smoke' }
                    Copy-Item -LiteralPath $pdfium -Destination (Join-Path $native 'pdfium.dll')
                    $webpLock=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'webp.lock.json') -Raw | ConvertFrom-Json
                    $webpRoot=Join-Path $output 'toolchain/libwebp'; $webpExe=Join-Path $webpRoot "libwebp-$($webpLock.version)-windows-x64/bin/dwebp.exe"
                    if ((Get-FileHash -LiteralPath $webpExe -Algorithm SHA256).Hash.ToLowerInvariant() -ne $webpLock.executableSha256) { throw 'Pinned WebP decoder is missing or changed' }
                    $webpTarget=Join-Path $native 'webp'; New-Item -ItemType Directory -Path $webpTarget -Force | Out-Null
                    Copy-Item -LiteralPath $webpExe -Destination (Join-Path $webpTarget 'dwebp.exe')
                    foreach ($name in @('COPYING','PATENTS')) { Copy-Item -LiteralPath (Join-Path $webpRoot $name) -Destination $webpTarget }
                    $process = Start-Process -FilePath $exe -ArgumentList @('--smoke-test',('"'+$smoke+'"')) -WindowStyle Hidden -PassThru
                    if (-not $process.WaitForExit(45000)) { $process.Kill(); throw 'Desktop smoke exceeded 45 seconds' }
                    $check.exitCode=$process.ExitCode
                    if ($process.ExitCode -ne 0) { throw "Desktop smoke exit $($process.ExitCode)" }
                    $desktop = Get-Content -LiteralPath $smoke -Raw | ConvertFrom-Json
                    if (-not $desktop.Passed -or -not $desktop.IsNativeAot) { throw 'Desktop native assertions failed' }
                    $check.status='passed'
                } else { $report.notRun += 'app: desktop smoke not requested' }
                if ($WorkflowSmoke) {
                    $workflow = Join-Path $run 'workflow-smoke.json'
                    foreach ($directory in @('fonts','typst','webp')) { New-Item -ItemType Directory -Path (Join-Path $publish "native/$directory") -Force | Out-Null }
                    Copy-Item -LiteralPath (Join-Path $output 'toolchain/pdfium/pdfium.dll') -Destination (Join-Path $publish 'native/pdfium.dll')
                    Copy-Item -LiteralPath (Join-Path $output 'toolchain/fonts/SourceHanSerifSC-Regular.otf') -Destination (Join-Path $publish 'native/fonts/SourceHanSerifSC-Regular.otf')
                    Copy-Item -LiteralPath (Join-Path $output 'toolchain/typst-x86_64-pc-windows-msvc/typst.exe') -Destination (Join-Path $publish 'native/typst/typst.exe')
                    Copy-Item -LiteralPath (Join-Path $output 'toolchain/libwebp/libwebp-1.6.0-windows-x64/bin/dwebp.exe') -Destination (Join-Path $publish 'native/webp/dwebp.exe')
                    $process = Start-Process -FilePath $exe -ArgumentList @('--workflow-smoke',('"'+$workflow+'"')) -WindowStyle Hidden -PassThru
                    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Workflow smoke exceeded 60 seconds' }
                    if ($process.ExitCode -ne 0 -or -not (Get-Content -LiteralPath $workflow -Raw | ConvertFrom-Json).Passed) { throw 'Native persisted workflow assertions failed' }
                    $report.workflowReport=$workflow; $check.status='passed'
                } else { $report.notRun += 'app: persisted workflow smoke not requested' }
            } else {
                $arguments = @()
                if ($entry.name -eq 'documents') { $arguments=@($workspace,(Join-Path $run 'documents')) }
                elseif ($entry.name -eq 'execution') {
                    $arguments=@((Join-Path $run 'execution'))
                    if (Test-Path -LiteralPath (Join-Path $RuntimeDirectory 'python.exe') -PathType Leaf) { $arguments+=@('--runtime',[IO.Path]::GetFullPath($RuntimeDirectory)) }
                    else { $report.notRun+='execution: private Python package computation, runtime not prepared' }
                }
                elseif ($entry.name -eq 'media') { $arguments=@((Join-Path $run 'media.json'),(Join-Path $output 'toolchain/pdfium/pdfium.dll'),(Join-Path $output 'toolchain/libwebp/libwebp-1.6.0-windows-x64/bin/dwebp.exe')) }
                & $exe @arguments 2>&1 | Tee-Object -FilePath (Join-Path $run "$($entry.name)-run.log") | Out-Host
                $check.exitCode=$LASTEXITCODE
                if ($LASTEXITCODE -ne 0) { throw "$($entry.name) checks exit $LASTEXITCODE" }
                if ($entry.name -eq 'documents') {
                    $documents = Get-Content -LiteralPath (Join-Path $run 'documents/verification.json') -Raw | ConvertFrom-Json
                    if ($documents.DynamicCodeSupported) { throw 'Document probe reported dynamic code support' }
                }
                $check.status='passed'
            }
            $inventory = @(Get-ChildItem -LiteralPath $publish -Recurse -File | Sort-Object FullName | ForEach-Object { [ordered]@{ path=[IO.Path]::GetRelativePath($publish,$_.FullName).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
            $inventory | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run "$($entry.name)-files.json") -Encoding utf8
        } catch { $check.status='failed'; $check.error=$_.Exception.Message; throw }
        finally { $check.elapsedMilliseconds=$clock.ElapsedMilliseconds }
    }
    $report.checkStatus='selected-checks-passed'
} catch {
    $report.checkStatus='failed'; $report.error=$_.Exception.Message
    foreach ($entry in $projects) { if ($entry.name -notin @($report.checks | ForEach-Object { $_.name }) -and -not @($report.notRun | Where-Object { $_ -like "$($entry.name):*" }).Count) { $report.notRun += "$($entry.name): not attempted after earlier failure" } }
    throw
} finally {
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $run 'run.json') -Encoding utf8
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $output 'latest-migration-run.json') -Encoding utf8
    foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key,$savedEnvironment[$key],'Process') }
    Set-Location -LiteralPath $savedLocation
    Write-Host "Migration check evidence: $run"
}
