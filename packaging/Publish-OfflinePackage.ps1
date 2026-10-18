#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$AppPublishDirectory,
    [string]$RuntimeDirectory,
    [string]$OutputDirectory,
    [switch]$DesktopSmoke,
    [switch]$WorkflowSmoke
)
$ErrorActionPreference='Stop'
$workspace=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$cache=Join-Path $workspace 'output/migration'
if (-not $RuntimeDirectory) { $RuntimeDirectory=Join-Path $cache 'toolchain/runtime' }
if (-not $OutputDirectory) { $OutputDirectory=Join-Path $workspace 'output/releases' }
$destination=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$runId='OhMyStudy-win-x64-rc-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$run=Join-Path $destination $runId
$package=Join-Path $run 'portable'
New-Item -ItemType Directory -Path $package -Force | Out-Null
$savedLocation=Get-Location; $savedEnvironment=@{}
foreach ($key in @('DOTNET_CLI_HOME','NUGET_PACKAGES','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_GENERATE_ASPNET_CERTIFICATE')) { $savedEnvironment[$key]=[Environment]::GetEnvironmentVariable($key,'Process') }
$report=[ordered]@{ schemaVersion=1; status='release-candidate-unvalidated'; packageStatus='running'; timestampUtc=[DateTimeOffset]::UtcNow.ToString('O'); runRoot=$run; packageDirectory=$package; notRun=@('clean-Windows-10-11-offline-matrix','installer-upgrade-uninstall','real-data-and-credential-cutover','real-provider-semantics','real-Sandbox-ownership','physical-printing','redistribution-license-review','source-correspondence-review') }

function Assert-UnlinkedTree([string]$Root) {
    $item=Get-Item -LiteralPath $Root
    for ($ancestor=$item; $null -ne $ancestor; $ancestor=if ($ancestor -is [IO.FileInfo]) { $ancestor.Directory } else { $ancestor.Parent }) {
        if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked package input rejected: $($ancestor.FullName)" }
    }
    foreach ($entry in Get-ChildItem -LiteralPath $Root -Recurse -Force) { if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked package input rejected: $($entry.FullName)" } }
}
function Copy-Tree([string]$Source,[string]$Target) {
    $sourceFull=[IO.Path]::GetFullPath($Source).TrimEnd('\','/'); $targetFull=[IO.Path]::GetFullPath($Target)
    if ($targetFull.StartsWith($sourceFull+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $targetFull.Equals($sourceFull,[StringComparison]::OrdinalIgnoreCase)) { throw 'Package copy destination must be outside its input tree' }
    Assert-UnlinkedTree $Source
    New-Item -ItemType Directory -Path $Target -Force | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $Source -Force) { Copy-Item -LiteralPath $entry.FullName -Destination $Target -Recurse }
}
function Assert-Hash([string]$Path,[string]$Expected) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Expected.ToLowerInvariant()) { throw "Pinned file missing or changed: $Path" }
}
function Invoke-Dotnet([string[]]$Arguments,[string]$Log) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath $Log | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet exit $LASTEXITCODE; see $Log" }
}

try {
    Set-Location -LiteralPath $workspace
    $env:DOTNET_CLI_HOME=Join-Path $cache 'dotnet-home'; $env:NUGET_PACKAGES=Join-Path $cache 'nuget'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; $env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
    if (-not $AppPublishDirectory) {
        $project=Join-Path $workspace 'src/OhMyStudy.App/OhMyStudy.App.csproj'
        Invoke-Dotnet @('restore',$project,'-p:Configuration=Release','--locked-mode','--disable-parallel') (Join-Path $run 'app-restore.log')
        $AppPublishDirectory=Join-Path $run 'app-publish'
        Invoke-Dotnet @('publish',$project,'-c','Release','--no-restore','-p:TrimmerSingleWarn=false','-o',$AppPublishDirectory) (Join-Path $run 'app-publish.log')
    }
    $appSource=(Resolve-Path -LiteralPath $AppPublishDirectory).Path
    $report.appPublishDirectory=$appSource
    $report.appSourceProvenance='Working-tree snapshot included; when supplying an existing publish, its exact source correspondence is a caller responsibility.'
    if (Test-Path -LiteralPath (Join-Path $appSource 'coreclr.dll')) { throw 'CoreCLR runtime found; provide a fresh Native AOT publish' }
    Copy-Tree $appSource $package
    foreach ($required in @('OhMyStudy.App.exe','OhMyStudy.App.pri','App.xbf','MainWindow.xbf','Themes/WorkspaceResources.xbf','e_sqlite3.dll','Microsoft.ui.xaml.dll','Assets/App.ico')) {
        if (-not (Test-Path -LiteralPath (Join-Path $package $required) -PathType Leaf)) { throw "Required native app output missing: $required" }
    }
    $documentLock=Get-Content -LiteralPath (Join-Path $workspace 'probes/native-documents.lock.json') -Raw | ConvertFrom-Json
    $tools=Join-Path $cache 'toolchain'
    Assert-Hash (Join-Path $tools 'pdfium/pdfium.dll') $documentLock.pdfium.librarySha256
    Assert-Hash (Join-Path $tools 'typst-x86_64-pc-windows-msvc/typst.exe') $documentLock.typst.executableSha256
    Assert-Hash (Join-Path $tools 'fonts/SourceHanSerifSC-Regular.otf') $documentLock.font.sha256
    Assert-Hash (Join-Path $tools 'fonts/OFL.txt') $documentLock.font.licenseSha256
    $native=Join-Path $package 'native'; New-Item -ItemType Directory -Path $native -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $tools 'pdfium/pdfium.dll') -Destination (Join-Path $native 'pdfium.dll')
    Copy-Tree (Join-Path $tools 'pdfium/licenses') (Join-Path $native 'pdfium-licenses')
    if (-not (Test-Path -LiteralPath (Join-Path $native 'pdfium-licenses/pdfium.txt'))) { throw 'PDFium third-party notices missing' }
    $typst=Join-Path $native 'typst'; New-Item -ItemType Directory -Path $typst -Force | Out-Null
    foreach ($name in @('typst.exe','LICENSE','NOTICE')) {
        $file=Join-Path $tools "typst-x86_64-pc-windows-msvc/$name"
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Typst distribution file missing: $name" }
        Copy-Item -LiteralPath $file -Destination $typst
    }
    Copy-Tree (Join-Path $tools 'fonts') (Join-Path $native 'fonts')
    $webpLock=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'webp.lock.json') -Raw | ConvertFrom-Json
    $webpRoot=Join-Path $tools 'libwebp'; $webp=Join-Path $native 'webp'; New-Item -ItemType Directory -Path $webp -Force | Out-Null
    $webpExe=Join-Path $webpRoot "libwebp-$($webpLock.version)-windows-x64/bin/dwebp.exe"
    Assert-Hash $webpExe $webpLock.executableSha256; Assert-Hash (Join-Path $webpRoot 'COPYING') $webpLock.licenseSha256; Assert-Hash (Join-Path $webpRoot 'PATENTS') $webpLock.patentsSha256
    Copy-Item -LiteralPath $webpExe -Destination (Join-Path $webp 'dwebp.exe')
    foreach ($name in @('COPYING','PATENTS')) { Copy-Item -LiteralPath (Join-Path $webpRoot $name) -Destination $webp }
    foreach ($required in @('runtime-manifest.json','python/python.exe','python/execute.py')) {
        if (-not (Test-Path -LiteralPath (Join-Path $RuntimeDirectory $required) -PathType Leaf)) { throw "Private runtime incomplete: $required; run packaging/prepare-runtime.ps1 explicitly" }
    }
    Copy-Tree $RuntimeDirectory (Join-Path $package 'runtime')
    Copy-Item -LiteralPath (Join-Path $workspace 'LICENSE') -Destination (Join-Path $package 'LICENSE')
    $notices=Join-Path $package 'licenses'; New-Item -ItemType Directory -Path $notices -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $workspace 'probes/native-documents.lock.json') -Destination (Join-Path $notices 'native-documents.lock.json')
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'runtime.lock.json'))) { throw 'Private runtime lock is missing' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'runtime.lock.json') -Destination (Join-Path $notices 'runtime.lock.json')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'webp.lock.json') -Destination (Join-Path $notices 'webp.lock.json')
    $pythonLock=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'runtime.lock.json') -Raw | ConvertFrom-Json
    $components=[Collections.Generic.List[object]]::new()
    $components.Add([ordered]@{ name='Typst'; version=$documentLock.typst.version; license=$documentLock.typst.license; path='native/typst/typst.exe'; sha256=$documentLock.typst.executableSha256; source=$documentLock.typst.url })
    $components.Add([ordered]@{ name='PDFium'; version=$documentLock.pdfium.version; license=$documentLock.pdfium.license; path='native/pdfium.dll'; sha256=$documentLock.pdfium.librarySha256; source=$documentLock.pdfium.url })
    $components.Add([ordered]@{ name='Source Han Serif SC'; version=$documentLock.font.version; license=$documentLock.font.license; path='native/fonts/SourceHanSerifSC-Regular.otf'; sha256=$documentLock.font.sha256; source=$documentLock.font.url })
    $components.Add([ordered]@{ name='libwebp dwebp'; version=$webpLock.version; license='BSD-3-Clause plus patent grant'; path='native/webp/dwebp.exe'; sha256=$webpLock.executableSha256; source=$webpLock.url })
    $components.Add([ordered]@{ name='CPython'; version=$pythonLock.cpython.version; distributionArchiveSha256=$pythonLock.cpython.sha256; path='runtime/python/python.exe'; source=$pythonLock.cpython.url; notices='runtime/python/LICENSE.txt' })
    foreach ($wheel in $pythonLock.wheels) { $components.Add([ordered]@{ name=$wheel.name; version=$wheel.version; distributionArchiveSha256=$wheel.sha256; source=$wheel.url; scope='extracted private runtime wheel'; notices='runtime/python/Lib/site-packages/*dist-info/' }) }
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($lockPath in @('src/OhMyStudy.App/packages.lock.json','src/OhMyStudy.Infrastructure/packages.lock.json')) {
        $graph=Get-Content -LiteralPath (Join-Path $workspace $lockPath) -Raw | ConvertFrom-Json
        foreach ($framework in $graph.dependencies.PSObject.Properties) {
            foreach ($dependency in $framework.Value.PSObject.Properties) {
                if (-not $dependency.Value.resolved -or $dependency.Value.type -eq 'Project') { continue }
                $id=$dependency.Name; $version=$dependency.Value.resolved
                if (-not $seen.Add("$id/$version")) { continue }
                $packageCache=Join-Path $env:NUGET_PACKAGES "$($id.ToLowerInvariant())/$version"
                if ($id -eq 'SourceGear.sqlite3') { Assert-Hash (Join-Path $package 'e_sqlite3.dll') (Get-FileHash -LiteralPath (Join-Path $packageCache 'runtimes/win-x64/native/e_sqlite3.dll') -Algorithm SHA256).Hash }
                $target=Join-Path $notices "$id-$version"; New-Item -ItemType Directory -Path $target -Force | Out-Null
                $metadata=@(Get-ChildItem -LiteralPath $packageCache -File | Where-Object { $_.Name -match '(?i)license|notice|copying|\.nuspec$|^README\.md$' })
                foreach ($file in $metadata) { Copy-Item -LiteralPath $file.FullName -Destination $target }
                $components.Add([ordered]@{ name=$id; version=$version; nugetContentHash=$dependency.Value.contentHash; graph=$lockPath; metadataPath="licenses/$id-$version"; distributionScope='dependency-graph; build tools may not be runtime files'; licenseReview='upstream metadata retained; redistribution review not completed' })
            }
        }
    }
    $nativeRuntime=Join-Path $env:NUGET_PACKAGES 'microsoft.netcore.app.runtime.nativeaot.win-x64/10.0.13'
    $runtimeNotice=Join-Path $notices 'dotnet-nativeaot-10.0.13'; New-Item -ItemType Directory -Path $runtimeNotice -Force | Out-Null
    foreach ($name in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')) { Copy-Item -LiteralPath (Join-Path $nativeRuntime $name) -Destination $runtimeNotice }
    @'
Oh My Study: GPL-3.0-or-later. The complete license is included as LICENSE.
The working-tree source snapshot is source/source-snapshot.zip. Existing supplied publishes require source-correspondence review.
Native third-party tools keep their licenses beside native/typst, native/fonts and native/pdfium-licenses.
Dependency-graph metadata and .NET notices are under licenses/. The private Python runtime keeps its own notices.
components.json records pinned versions and hashes. Some graph entries are build dependencies.
Redistribution/license review and clean-system installation acceptance remain unvalidated.
'@ | Set-Content -LiteralPath (Join-Path $package 'THIRD-PARTY-NOTICES.txt') -Encoding utf8
    $components | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $package 'components.json') -Encoding utf8
    # Include the actual working-tree sources, including uncommitted C# work. Never use the
    # archived Rust tree, caches, user data, credentials, or a stale commit as corresponding source.
    $sourceStage=Join-Path $run 'source-snapshot'; New-Item -ItemType Directory -Path $sourceStage -Force | Out-Null
    foreach ($sourceRoot in @('src','tests','probes','packaging','docs','runtime','.github')) {
        $queue=[Collections.Generic.Queue[string]]::new(); $queue.Enqueue((Join-Path $workspace $sourceRoot))
        while ($queue.Count -gt 0) {
            foreach ($entry in Get-ChildItem -LiteralPath $queue.Dequeue() -Force) {
                if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked source rejected: $($entry.FullName)" }
                if ($entry.PSIsContainer) { if ($entry.Name -notin @('bin','obj','.git','.reference','.artifacts','output','__pycache__')) { $queue.Enqueue($entry.FullName) }; continue }
                $relative=[IO.Path]::GetRelativePath($workspace,$entry.FullName); $target=Join-Path $sourceStage $relative
                New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null; Copy-Item -LiteralPath $entry.FullName -Destination $target
            }
        }
    }
    foreach ($name in @('LICENSE','README.md','AGENTS.md','OhMyStudy.slnx','Directory.Build.props','Directory.Packages.props','NuGet.Config','global.json','migration.md','migration-progress.md','.editorconfig','.gitattributes','.gitignore')) {
        $file=Join-Path $workspace $name; if (Test-Path -LiteralPath $file -PathType Leaf) { Copy-Item -LiteralPath $file -Destination $sourceStage }
    }
    $sourceDirectory=Join-Path $package 'source'; New-Item -ItemType Directory -Path $sourceDirectory -Force | Out-Null
    [IO.Compression.ZipFile]::CreateFromDirectory($sourceStage,(Join-Path $sourceDirectory 'source-snapshot.zip'),[IO.Compression.CompressionLevel]::Optimal,$false)
    @'
解压到新的文件夹后运行 OhMyStudy.App.exe。无需安装 .NET、Node.js、Python 或 TeX。
本候选包尚未完成干净 Windows 10/11、安装升级、真实资料迁移与硬件打印验收。
首次检查旧资料请使用只读方式；切换前退出旧版，并保留一致备份。恢复请使用新目录副本。
不要覆盖已有资料库，也不要将此候选包的运行结果视为旧版回退兼容承诺。
'@ | Set-Content -LiteralPath (Join-Path $package 'README-PORTABLE.zh-CN.txt') -Encoding utf8
    $files=@(Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName | ForEach-Object { [ordered]@{ path=[IO.Path]::GetRelativePath($package,$_.FullName).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
    $manifest=[ordered]@{ schemaVersion=1; status='release-candidate-unvalidated'; createdUtc=[DateTimeOffset]::UtcNow.ToString('O'); architecture='win-x64'; entryPoint='OhMyStudy.App.exe'; manifestExcludesItself=$true; sourceSnapshot='source/source-snapshot.zip'; files=$files; notRun=$report.notRun }
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Encoding utf8
    $verification=Join-Path $run 'package-verification.json'
    & (Join-Path $PSScriptRoot 'Test-OfflinePackage.ps1') -PackageDirectory $package -ReportFile $verification -DesktopSmoke:$DesktopSmoke -WorkflowSmoke:$WorkflowSmoke
    $report.verificationReport=$verification
    $zip=Join-Path $run "$runId.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($package,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
    $report.zip=$zip; $report.zipSha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant(); $report.fileCount=$files.Count
    $report.packageStatus='created-and-local-checks-passed'
} catch { $report.packageStatus='failed'; $report.error=$_.Exception.Message; throw }
finally {
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $run 'package-report.json') -Encoding utf8
    foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key,$savedEnvironment[$key],'Process') }
    Set-Location -LiteralPath $savedLocation
    Write-Host "Portable release candidate evidence: $run"
}
