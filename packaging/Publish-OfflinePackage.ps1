#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('aot','self-contained','framework-dependent')][string]$Variant = 'aot',
    [string]$AppPublishDirectory,
    [string]$RuntimeDirectory,
    [string]$OutputDirectory,
    [string]$BaselinePackage,
    [string]$PackId = 'TaiRuiQu.OhMyStudy',
    [string]$PackTitle = 'Oh My Study',
    [switch]$DesktopSmoke,
    [switch]$WorkflowSmoke
)
$ErrorActionPreference='Stop'
$workspace=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'ReleasePayload.ps1')
$cache=Join-Path $workspace 'output/migration'
$profile = & (Join-Path $PSScriptRoot 'Get-PublishProfile.ps1') -Variant $Variant
$releaseTag=if($env:RELEASE_TAG){$env:RELEASE_TAG}elseif($env:GITHUB_REF_TYPE -eq 'tag'){$env:GITHUB_REF_NAME}else{'0.1.0-migration'}
if ($releaseTag -notmatch '^v?(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?)$') { throw "Invalid release version: $releaseTag" }
$version=$Matches[1]
$channel="win-x64-$Variant"
if (-not $RuntimeDirectory) { $RuntimeDirectory=Join-Path $cache 'toolchain/runtime' }
if (-not $OutputDirectory) { $OutputDirectory=Join-Path $workspace 'output/releases' }
$destination=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$runId=('OhMyStudy-win-x64-'+$Variant+'-rc-')+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$run=Join-Path $destination $runId
$package=Join-Path $run 'portable'
New-Item -ItemType Directory -Path $package -Force | Out-Null
$savedLocation=Get-Location; $savedEnvironment=@{}
foreach ($key in @('DOTNET_CLI_HOME','NUGET_PACKAGES','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_GENERATE_ASPNET_CERTIFICATE')) { $savedEnvironment[$key]=[Environment]::GetEnvironmentVariable($key,'Process') }
$report=[ordered]@{ schemaVersion=1; status='release-candidate-user-acceptance-pending'; packageStatus='running'; variant=$Variant; timestampUtc=[DateTimeOffset]::UtcNow.ToString('O'); runRoot=$run; packageDirectory=$package; userValidation=@('clean-Windows-10-11-offline-matrix','actual-installation-identity-and-system-entry-points','real-data-and-credential-cutover','real-provider-semantics','real-Sandbox-ownership','physical-printing','DPI-IME-accessibility-reference-device-budgets'); notRun=@('redistribution-license-review','binary-code-signing') }

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
        Invoke-Dotnet (@('restore',$project,'-p:Configuration=Release','--locked-mode','--disable-parallel',"-p:Version=$version") + $profile.properties) (Join-Path $run 'app-restore.log')
        $AppPublishDirectory=Join-Path $run 'app-publish'
        Invoke-Dotnet (@('publish',$project,'-c','Release','--no-restore','-p:TrimmerSingleWarn=false','-o',$AppPublishDirectory,"-p:Version=$version") + $profile.properties) (Join-Path $run 'app-publish.log')
    }
    $appSource=(Resolve-Path -LiteralPath $AppPublishDirectory).Path
    $report.appPublishDirectory=$appSource
    $report.appSourceProvenance='Working-tree snapshot included; when supplying an existing publish, its exact source correspondence is a caller responsibility.'
    if ($profile.nativeAot -and (Test-Path -LiteralPath (Join-Path $appSource 'coreclr.dll'))) { throw 'CoreCLR runtime found; provide a fresh Native AOT publish' }
    $excludedFiles = @(Copy-ReleasePayload $appSource $package)
    foreach ($required in @('OhMyStudy.App.exe','OhMyStudy.App.pri','App.xbf','MainWindow.xbf','Themes/WorkspaceResources.xbf','e_sqlite3.dll','Microsoft.ui.xaml.dll','Assets/App.ico')) {
        if (-not (Test-Path -LiteralPath (Join-Path $package $required) -PathType Leaf)) { throw "Required native app output missing: $required" }
    }
    $documentLock=Get-Content -LiteralPath (Join-Path $workspace 'probes/native-documents.lock.json') -Raw | ConvertFrom-Json
    $tools=Join-Path $cache 'toolchain'
    Assert-Hash (Join-Path $tools 'pdfium/pdfium.dll') $documentLock.pdfium.librarySha256
    Assert-Hash (Join-Path $tools 'typst-x86_64-pc-windows-msvc/typst.exe') $documentLock.typst.executableSha256
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
    $webpLock=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'webp.lock.json') -Raw | ConvertFrom-Json
    $webpRoot=Join-Path $tools 'libwebp'; $webp=Join-Path $native 'webp'; New-Item -ItemType Directory -Path $webp -Force | Out-Null
    $webpExe=Join-Path $webpRoot "libwebp-$($webpLock.version)-windows-x64/bin/dwebp.exe"
    Assert-Hash $webpExe $webpLock.executableSha256; Assert-Hash (Join-Path $webpRoot 'COPYING') $webpLock.licenseSha256; Assert-Hash (Join-Path $webpRoot 'PATENTS') $webpLock.patentsSha256
    Copy-Item -LiteralPath $webpExe -Destination (Join-Path $webp 'dwebp.exe')
    foreach ($name in @('COPYING','PATENTS')) { Copy-Item -LiteralPath (Join-Path $webpRoot $name) -Destination $webp }
    foreach ($required in @('runtime-manifest.json','python/python.exe','python/execute.py','python/sandbox-worker.py')) {
        if (-not (Test-Path -LiteralPath (Join-Path $RuntimeDirectory $required) -PathType Leaf)) { throw "Private runtime incomplete: $required; run packaging/prepare-runtime.ps1 explicitly" }
    }
    $excludedFiles += @(Copy-ReleasePayload $RuntimeDirectory (Join-Path $package 'runtime') -Prefix 'runtime')
    Update-ReleaseRuntimeManifest (Join-Path $package 'runtime')
    $report.excludedDevelopmentFiles = $excludedFiles
    $report.excludedDevelopmentBytes = [long]($excludedFiles | Measure-Object -Property bytes -Sum).Sum
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
    $scannerLock=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'scanner.lock.json') -Raw | ConvertFrom-Json
    Assert-Hash (Join-Path $package 'native/scanner/docscanner-l.onnx') $scannerLock.rectifier.sha256
    $components.Add([ordered]@{ name=$scannerLock.rectifier.name; license=$scannerLock.rectifier.license; path='native/scanner/docscanner-l.onnx'; sha256=$scannerLock.rectifier.sha256; source=$scannerLock.rectifier.source; notices='native/scanner/DocScanner-LICENSE.md'; changes='Combined segmentation and 12-iteration rectifier exported to ONNX normalized backward grid' })
    Assert-Hash (Join-Path $package 'native/scanner/docaligner-lcnet.onnx') $scannerLock.sha256
    $components.Add([ordered]@{ name=$scannerLock.name; license=$scannerLock.license; path='native/scanner/docaligner-lcnet.onnx'; sha256=$scannerLock.sha256; source=$scannerLock.source; notices='native/scanner/LICENSE.txt' })
    Assert-Hash (Join-Path $package 'native/scanner/document-orientation.onnx') $scannerLock.orientation.sha256
    $components.Add([ordered]@{ name=$scannerLock.orientation.name; license=$scannerLock.orientation.license; path='native/scanner/document-orientation.onnx'; sha256=$scannerLock.orientation.sha256; source=$scannerLock.orientation.source; notices='native/scanner/LICENSE.txt' })
    $components.Add([ordered]@{ name='libwebp dwebp'; version=$webpLock.version; license='BSD-3-Clause plus patent grant'; path='native/webp/dwebp.exe'; sha256=$webpLock.executableSha256; source=$webpLock.url })
    $components.Add([ordered]@{ name='CPython'; version=$pythonLock.cpython.version; distributionArchiveSha256=$pythonLock.cpython.sha256; path='runtime/python/python.exe'; source=$pythonLock.cpython.url; notices='runtime/python/LICENSE.txt' })
    foreach ($wheel in $pythonLock.wheels) { $components.Add([ordered]@{ name=$wheel.name; version=$wheel.version; distributionArchiveSha256=$wheel.sha256; source=$wheel.url; scope='extracted private runtime wheel'; notices='runtime/python/Lib/site-packages/*dist-info/' }) }
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($lockPath in @($(if ($profile.nativeAot) { 'src/OhMyStudy.App/packages.lock.json' } else { 'src/OhMyStudy.App/packages.'+$Variant+'.lock.json' }),'src/OhMyStudy.Infrastructure/packages.lock.json')) {
        $graph=Get-Content -LiteralPath (Join-Path $workspace $lockPath) -Raw | ConvertFrom-Json
        foreach ($framework in $graph.dependencies.PSObject.Properties) {
            foreach ($dependency in $framework.Value.PSObject.Properties) {
                if (-not $dependency.Value.resolved -or $dependency.Value.type -eq 'Project') { continue }
                $id=$dependency.Name; $dependencyVersion=$dependency.Value.resolved
                if (-not $seen.Add("$id/$dependencyVersion")) { continue }
                $packageCache=Join-Path $env:NUGET_PACKAGES "$($id.ToLowerInvariant())/$dependencyVersion"
                if ($id -eq 'SourceGear.sqlite3') { Assert-Hash (Join-Path $package 'e_sqlite3.dll') (Get-FileHash -LiteralPath (Join-Path $packageCache 'runtimes/win-x64/native/e_sqlite3.dll') -Algorithm SHA256).Hash }
                $target=Join-Path $notices "$id-$dependencyVersion"; New-Item -ItemType Directory -Path $target -Force | Out-Null
                $metadata=@(Get-ChildItem -LiteralPath $packageCache -File | Where-Object { $_.Name -match '(?i)license|notice|copying|\.nuspec$|^README\.md$' })
                foreach ($file in $metadata) { Copy-Item -LiteralPath $file.FullName -Destination $target }
                if ($id -like 'VelociTex.*') {
                    Copy-Tree (Join-Path $packageCache 'licenses') (Join-Path $target 'licenses')
                    Copy-Item -LiteralPath (Join-Path $packageCache 'THIRD-PARTY-NOTICES.md') -Destination $target
                }
                $components.Add([ordered]@{ name=$id; version=$dependencyVersion; nugetContentHash=$dependency.Value.contentHash; graph=$lockPath; metadataPath="licenses/$id-$dependencyVersion"; distributionScope='dependency-graph; build tools may not be runtime files'; licenseReview='upstream metadata retained; redistribution review not completed' })
            }
        }
    }
    $nativeRuntime=Join-Path $env:NUGET_PACKAGES 'microsoft.netcore.app.runtime.nativeaot.win-x64/10.0.13'
    $runtimeNotice=Join-Path $notices 'dotnet-nativeaot-10.0.13'; New-Item -ItemType Directory -Path $runtimeNotice -Force | Out-Null
    foreach ($name in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')) { Copy-Item -LiteralPath (Join-Path $nativeRuntime $name) -Destination $runtimeNotice }
    @'
Oh My Study: GPL-3.0-or-later. The complete license is included as LICENSE.
The working-tree source snapshot is source/source-snapshot.zip. Existing supplied publishes require source-correspondence review.
Native third-party tools keep their licenses beside native/typst and native/pdfium-licenses.
DocScanner by Hao Feng is restricted to non-commercial use with attribution and share-alike. Commercial use requires prior written permission. Its separate license and modification notice are in native/scanner/DocScanner-LICENSE.md and DocScanner-NOTICE.txt.
Printed prose uses Windows-installed 新宋体 (NSimSun). Individual screen and print formulas use VelociTex's unchanged embedded KaTeX fonts (SIL OFL 1.1 / Reserved Font Names).
VelociTex code and KaTeX rules have separate MIT notices. All upstream font licenses and notices are retained under licenses/VelociTex.*.
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
    foreach ($name in @('LICENSE','README.md','OhMyStudy.slnx','Directory.Build.props','Directory.Packages.props','NuGet.Config','global.json','.editorconfig','.gitattributes','.gitignore')) {
        $file=Join-Path $workspace $name; if (Test-Path -LiteralPath $file -PathType Leaf) { Copy-Item -LiteralPath $file -Destination $sourceStage }
    }
    $sourceDirectory=Join-Path $package 'source'; New-Item -ItemType Directory -Path $sourceDirectory -Force | Out-Null
    [IO.Compression.ZipFile]::CreateFromDirectory($sourceStage,(Join-Path $sourceDirectory 'source-snapshot.zip'),[IO.Compression.CompressionLevel]::Optimal,$false)
    @'
此 ZIP 为便携版，解压后直接运行 OhMyStudy.App.exe；需要一键安装请选择同版本的 Setup.exe。
资料与备份存放在用户资料目录，更新或卸载程序时会保留。
AI 识别与批改需要在设置中添加供应商、密钥和模型；材料会发送给所选供应商，可能产生费用。
安装版可在“关于”中检查更新。更新前请保存修改，重要资料请保留完整备份。
'@ | Set-Content -LiteralPath (Join-Path $package 'README-PORTABLE.zh-CN.txt') -Encoding utf8
    $requirements = switch ($Variant) {
        'framework-dependent' { '运行应用前请安装 Microsoft ASP.NET Core 10 x64 Runtime（包含 .NET Runtime）；安装器不会自动下载运行时。' }
        default { '此版本无需另行安装 .NET 运行时。' }
    }
    Add-Content -LiteralPath (Join-Path $package 'README-PORTABLE.zh-CN.txt') -Value $requirements
    Assert-ReleasePayload $package
    $files=@(Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName | ForEach-Object { [ordered]@{ path=[IO.Path]::GetRelativePath($package,$_.FullName).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
    $report.payloadBytes = ($files | Measure-Object -Property bytes -Sum).Sum
    $sourceCommit=(& git -C $workspace rev-parse HEAD).Trim()
    $sourceStatus=@(& git -C $workspace status --porcelain --untracked-files=normal)
    $manifest=[ordered]@{ schemaVersion=1; status='release-candidate-user-acceptance-pending'; createdUtc=[DateTimeOffset]::UtcNow.ToString('O'); architecture='win-x64'; variant=$Variant; nativeAot=$profile.nativeAot; selfContained=$profile.selfContained; instructionSet=$profile.instructionSet; entryPoint='OhMyStudy.App.exe'; installerType='velopack'; version=$version; channel=$channel; sourceCommit=$sourceCommit; sourceTreeClean=($sourceStatus.Count -eq 0); manifestExcludesItself=$true; sourceSnapshot='source/source-snapshot.zip'; files=$files; notRun=$report.notRun; userValidation=$report.userValidation }
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Encoding utf8
    $verification=Join-Path $run 'package-verification.json'
    & (Join-Path $PSScriptRoot 'Test-OfflinePackage.ps1') -PackageDirectory $package -ReportFile $verification -DesktopSmoke:$DesktopSmoke -WorkflowSmoke:$WorkflowSmoke
    $report.verificationReport=$verification
    $zip=Join-Path $run "$runId.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($package,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
    $releases=Join-Path $run 'velopack'
    New-Item -ItemType Directory -Path $releases -Force | Out-Null
    if ($BaselinePackage) {
        $baseline=(Resolve-Path -LiteralPath $BaselinePackage).Path
        if ($baseline -notlike "*-$channel-full.nupkg") { throw 'Baseline package channel does not match this build' }
        Copy-Item -LiteralPath $baseline -Destination $releases
    }
    $vpk=& (Join-Path $PSScriptRoot 'prepare-velopack.ps1')
    & $vpk pack --packId $PackId --packVersion $version --packDir $package --mainExe OhMyStudy.App.exe --packTitle $PackTitle --packAuthors TaiRuiQu --icon (Join-Path $workspace 'src/OhMyStudy.App/Assets/App.ico') --channel $channel --runtime win-x64 --outputDir $releases | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Velopack pack exited with $LASTEXITCODE" }
    $report.installer=Join-Path $releases "$PackId-$channel-Setup.exe"
    $report.fullPackage=Join-Path $releases "$PackId-$version-$channel-full.nupkg"
    $report.feed=Join-Path $releases "releases.$channel.json"
    $report.legacyFeed=Join-Path $releases "RELEASES-$channel"
    $report.assetsFeed=Join-Path $releases "assets.$channel.json"
    foreach ($name in @('installer','fullPackage','feed','legacyFeed','assetsFeed')) {
        if (-not (Test-Path -LiteralPath $report[$name] -PathType Leaf)) { throw "Velopack output missing: $name" }
        $report[$name+'Sha256']=(Get-FileHash -LiteralPath $report[$name] -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    if ($BaselinePackage) {
        $report.deltaPackage=Join-Path $releases "$PackId-$version-$channel-delta.nupkg"
        if (-not (Test-Path -LiteralPath $report.deltaPackage -PathType Leaf)) { throw 'Required Velopack delta package was not generated' }
        $report.deltaPackageSha256=(Get-FileHash -LiteralPath $report.deltaPackage -Algorithm SHA256).Hash.ToLowerInvariant()
        $reconstructed=Join-Path $run 'reconstructed.nupkg'
        & $vpk delta patch --base $baseline --patch $report.deltaPackage --output $reconstructed | Out-Host
        if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $reconstructed -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $report.fullPackage -Algorithm SHA256).Hash) { throw 'Delta package does not reconstruct the full package' }
        Remove-Item -LiteralPath $reconstructed
    }
    $report.installerType='velopack'
    $report.version=$version
    $report.channel=$channel
    $report.zip=$zip; $report.zipSha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant(); $report.fileCount=$files.Count
    $report.packageStatus='created-and-local-checks-passed'
} catch { $report.packageStatus='failed'; $report.error=$_.Exception.Message; throw }
finally {
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $run 'package-report.json') -Encoding utf8
    foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key,$savedEnvironment[$key],'Process') }
    Set-Location -LiteralPath $savedLocation
    Write-Host "Portable release candidate evidence: $run"
}
