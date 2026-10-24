#requires -Version 7.0
[CmdletBinding(DefaultParameterSetName='Directory')]
param(
    [Parameter(Mandatory,ParameterSetName='Directory')][string]$PackageDirectory,
    [Parameter(Mandatory,ParameterSetName='Zip')][string]$PackageZip,
    [string]$ReportFile,
    [switch]$DesktopSmoke,
    [switch]$WorkflowSmoke
)
$ErrorActionPreference='Stop'
$workspace=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'ReleasePayload.ps1')
$runId='package-check-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$evidence=Join-Path $workspace "output/migration/runs/$runId"
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
if (-not $ReportFile) { $ReportFile=Join-Path $evidence 'package-verification.json' }
$reportPath=[IO.Path]::GetFullPath($ReportFile)
New-Item -ItemType Directory -Path (Split-Path -Parent $reportPath) -Force | Out-Null
$report=[ordered]@{ schemaVersion=1; status='release-candidate-user-acceptance-pending'; checkStatus='running'; timestampUtc=[DateTimeOffset]::UtcNow.ToString('O'); checks=@(); userValidation=@('clean-Windows-10-11-offline-installation','actual-installed-identity-and-system-entry-points','real-user-library-and-credentials','real-provider-semantics','real-Sandbox-and-physical-printing'); notRun=@('redistribution-license-review','binary-code-signing') }

function Assert-Path([string]$Root,[string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or $Relative.Contains(':') -or [IO.Path]::IsPathRooted($Relative)) { throw "Invalid manifest path: $Relative" }
    foreach ($part in ($Relative -split '[/\\]')) { if ($part -in @('','.', '..') -or $part.EndsWith('.') -or $part.EndsWith(' ')) { throw "Invalid manifest path component: $Relative" } }
    $full=[IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $full.StartsWith($Root.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw "Manifest path escapes package: $Relative" }
    for ($item=Get-Item -LiteralPath $full; $null -ne $item; $item=if ($item -is [IO.FileInfo]) { $item.Directory } else { $item.Parent }) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked package path rejected: $full" }
    }
    return $full
}
function Assert-FileHash([string]$File,[string]$Expected) {
    if (-not (Test-Path -LiteralPath $File -PathType Leaf) -or (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Expected.ToLowerInvariant()) { throw "File hash mismatch: $File" }
}
function Assert-NativeApp([string]$Path) {
    $bytes=[IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 256 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) { throw 'Application is not PE' }
    $pe=[BitConverter]::ToInt32($bytes,0x3c)
    if ($pe -lt 0 -or $pe+264 -ge $bytes.Length -or [BitConverter]::ToUInt16($bytes,$pe+4) -ne 0x8664 -or [BitConverter]::ToUInt16($bytes,$pe+24) -ne 0x20b -or [BitConverter]::ToUInt32($bytes,$pe+24+112+14*8) -ne 0) { throw 'Application must be native x64 without CLR header' }
    $stem=Join-Path (Split-Path -Parent $Path) ([IO.Path]::GetFileNameWithoutExtension($Path))
    foreach ($suffix in @('.dll','.deps.json','.runtimeconfig.json')) { if(Test-Path -LiteralPath ($stem+$suffix)){throw "Managed apphost is not Native AOT: $Path"} }
}

try {
    if ($PSCmdlet.ParameterSetName -eq 'Zip') {
        $archive=(Resolve-Path -LiteralPath $PackageZip).Path
        $report.zipSha256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        $PackageDirectory=Join-Path $evidence 'extracted'
        [IO.Compression.ZipFile]::ExtractToDirectory($archive,$PackageDirectory)
    }
    $root=(Resolve-Path -LiteralPath $PackageDirectory).Path.TrimEnd('\','/')
    Assert-ReleasePayload $root
    $report.checks += 'no-debug-symbols-linker-output-python-caches-or-package-tests'
    $report.packageDirectory=$root
    if ($reportPath.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Verification report must be outside the immutable package' }
    $manifestPath=Assert-Path $root 'package-manifest.json'
    $manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.status -ne 'release-candidate-user-acceptance-pending' -or -not $manifest.manifestExcludesItself) { throw 'Unsupported package manifest/status' }
    $variant=if ($manifest.variant) { $manifest.variant } else { 'aot' }
    $profile=& (Join-Path $PSScriptRoot 'Get-PublishProfile.ps1') -Variant $variant
    $report.variant=$variant
    $listed=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) {
        if (-not $listed.Add($file.path)) { throw "Duplicate manifest path: $($file.path)" }
        $path=Assert-Path $root $file.path
        if ((Get-Item -LiteralPath $path).Length -ne $file.bytes) { throw "File size mismatch: $($file.path)" }
        Assert-FileHash $path $file.sha256
    }
    $allFiles=@(Get-ChildItem -LiteralPath $root -Recurse -File)
    foreach ($file in $allFiles) {
        $relative=[IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/')
        if ($relative -ne 'package-manifest.json' -and -not $listed.Contains($relative)) { throw "Unlisted package file: $relative" }
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked package file: $relative" }
        if ($file.Name -match '(?i)^(node|npm|npx|cargo|rustc|msedge|msedgewebview2|chrome|chromium|firefox)\.(exe|dll|cmd|bat)$' -or $file.Extension -eq '.rs') { throw "Unexpected legacy/browser/JIT runtime file: $relative" }
    }
    $report.checks+='all-manifest-file-sizes-and-sha256'; $report.fileCount=$manifest.files.Count
    foreach ($required in @('OhMyStudy.App.exe','OhMyStudy.App.pri','App.xbf','MainWindow.xbf','Themes/WorkspaceResources.xbf','Assets/App.ico','Microsoft.ui.xaml.dll','e_sqlite3.dll','native/pdfium.dll','native/pdfium-licenses/pdfium.txt','native/typst/typst.exe','native/typst/LICENSE','native/typst/NOTICE','runtime/runtime-manifest.json','runtime/python/python.exe','runtime/python/execute.py','LICENSE','THIRD-PARTY-NOTICES.txt','components.json','source/source-snapshot.zip')) { $null=Assert-Path $root $required }
    foreach ($required in @('VelociTex.Core.Native.dll','licenses/VelociTex.Core/LICENSE','licenses/VelociTex.Core/THIRD-PARTY-NOTICES.md','licenses/VelociTex.Core/licenses/KaTeX-FONTS-OFL.txt','licenses/VelociTex.Core/licenses/KaTeX-LICENSE.txt','licenses/VelociTex.Rendering.Direct2D/LICENSE','licenses/VelociTex.WinUI/LICENSE')) { $null=Assert-Path $root $required }
    foreach ($required in @('onnxruntime.dll','onnxruntime_providers_shared.dll','native/scanner/docaligner-lcnet.onnx','native/scanner/document-orientation.onnx','native/scanner/LICENSE.txt','native/scanner/model.json')) { $null=Assert-Path $root $required }
    $scannerLock=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'scanner.lock.json') -Raw | ConvertFrom-Json
    foreach ($required in @('OpenCvSharpExtern.dll','native/scanner/docscanner-l.onnx','native/scanner/DocScanner-LICENSE.md','native/scanner/DocScanner-NOTICE.txt','native/docres/install.py','native/docres/worker.py','native/docres/models.json')) { $null=Assert-Path $root $required }
    Assert-FileHash (Join-Path $root 'native/scanner/docscanner-l.onnx') $scannerLock.rectifier.sha256
    Assert-FileHash (Join-Path $root 'native/scanner/DocScanner-LICENSE.md') $scannerLock.rectifier.licenseSha256
    Assert-FileHash (Join-Path $root 'native/scanner/docaligner-lcnet.onnx') $scannerLock.sha256
    Assert-FileHash (Join-Path $root 'native/scanner/document-orientation.onnx') $scannerLock.orientation.sha256
    & (Join-Path $PSScriptRoot 'Test-ApplicationIcon.ps1') -Executable (Join-Path $root 'OhMyStudy.App.exe') -IconFile (Join-Path $root 'Assets/App.ico')
    $report.checks += 'executable-embeds-application-icon-at-shell-sizes'
    if ($profile.nativeAot) {
        Assert-NativeApp (Join-Path $root 'OhMyStudy.App.exe')
        foreach ($name in @('coreclr.dll','clrjit.dll')) { if (Test-Path -LiteralPath (Join-Path $root $name)) { throw "JIT file in AOT package: $name" } }
    } else {
        foreach ($name in @('OhMyStudy.App.dll','OhMyStudy.App.deps.json','OhMyStudy.App.runtimeconfig.json')) { $null=Assert-Path $root $name }
        $config=Get-Content -LiteralPath (Join-Path $root 'OhMyStudy.App.runtimeconfig.json') -Raw | ConvertFrom-Json
        if ($profile.selfContained) {
            foreach ($name in @('coreclr.dll','clrjit.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll')) { $null=Assert-Path $root $name }
            if (-not $config.runtimeOptions.includedFrameworks) { throw 'Self-contained runtime configuration missing' }
        } else {
            foreach ($name in @('coreclr.dll','clrjit.dll','hostfxr.dll','System.Private.CoreLib.dll')) { if (Test-Path -LiteralPath (Join-Path $root $name)) { throw "Bundled .NET runtime in framework-dependent package: $name" } }
            if (-not @(@($config.runtimeOptions.framework) + @($config.runtimeOptions.frameworks) | Where-Object { $_ } | Where-Object { $_.name -eq 'Microsoft.NETCore.App' -and $_.version -like '10.*' }).Count) { throw 'Expected .NET 10 framework requirement' }
        }
    }
    $null=Assert-Path $root 'runtime/python/sandbox-worker.py'
    if (Test-Path -LiteralPath (Join-Path $root 'OhMyStudy.Setup.exe')) { throw 'New portable packages must not ship the legacy custom installer' }
    $report.checks+='native-x64-app-PRI-XBF-and-private-runtime-present'
    $documentLock=Get-Content -LiteralPath (Join-Path $root 'licenses/native-documents.lock.json') -Raw | ConvertFrom-Json
    Assert-FileHash (Join-Path $root 'native/pdfium.dll') $documentLock.pdfium.librarySha256
    Assert-FileHash (Join-Path $root 'native/typst/typst.exe') $documentLock.typst.executableSha256
    $webpLock=Get-Content -LiteralPath (Join-Path $root 'licenses/webp.lock.json') -Raw | ConvertFrom-Json
    Assert-FileHash (Join-Path $root 'native/webp/dwebp.exe') $webpLock.executableSha256
    Assert-FileHash (Join-Path $root 'native/webp/COPYING') $webpLock.licenseSha256
    Assert-FileHash (Join-Path $root 'native/webp/PATENTS') $webpLock.patentsSha256
    $report.checks+='pinned-native-document-tools'
    $runtimeRoot=Join-Path $root 'runtime'
    $runtime=Get-Content -LiteralPath (Join-Path $runtimeRoot 'runtime-manifest.json') -Raw | ConvertFrom-Json
    if ($runtime.schemaVersion -ne 1 -or $runtime.status -ne 'computation-verified-sandbox-guest-packaged' -or -not $runtime.files -or $runtime.files.Count -eq 0) { throw 'Private computation runtime manifest has no verifiable file list/status' }
    foreach ($file in $runtime.files) {
        $runtimeFile=Assert-Path $runtimeRoot $file.path
        Assert-FileHash $runtimeFile $file.sha256
        if ((Get-Item -LiteralPath $runtimeFile).Length -ne $file.size) { throw "Private runtime file size mismatch: $($file.path)" }
    }
    Assert-FileHash (Join-Path $root 'licenses/runtime.lock.json') $runtime.lockSha256
    Assert-FileHash (Join-Path $runtimeRoot 'runtime.lock.json') $runtime.lockSha256
    Assert-FileHash (Join-Path $runtimeRoot 'python/execute.py') $runtime.workerSha256
    Assert-FileHash (Join-Path $runtimeRoot 'python/sandbox-worker.py') $runtime.sandboxWorkerSha256
    $report.checks+='private-runtime-manifest-hashes'
    $optionalInterop=@($allFiles | Where-Object { $_.Extension -eq '.dll' -and $_.Name -match '(?i)webview2' } | ForEach-Object { [IO.Path]::GetRelativePath($root,$_.FullName).Replace('\','/') })
    $report.optionalWinUiWebViewInteropFiles=$optionalInterop
    $report.checks+='no-bundled-Node-Rust-browser-executable; runtime-matches-profile'
    # Inspect the shipped corresponding App source for fixed browser controls/launches. Generic
    # user-approved shell/Python tools are not asserted incapable of starting arbitrary programs.
    $sourceArchive=[IO.Compression.ZipFile]::OpenRead((Join-Path $root 'source/source-snapshot.zip'))
    try {
        foreach ($entry in $sourceArchive.Entries) {
            if ($entry.FullName -notmatch '^src[/\\]OhMyStudy\.App[/\\].*\.(cs|xaml)$') { continue }
            $reader=[IO.StreamReader]::new($entry.Open()); try { $text=$reader.ReadToEnd() } finally { $reader.Dispose() }
            if ($text -match '<\s*(?:\w+:)?WebView2\b|new\s+WebView2\b|EnsureCoreWebView2|Process\.Start\s*\(\s*"(?:msedge|chrome|node|cargo|rustc)(?:\.exe)?"') { throw "Fixed browser/legacy launch path in App source: $($entry.FullName)" }
        }
    } finally { $sourceArchive.Dispose() }
    $report.checks+='App-source-no-fixed-browser-control-or-legacy-launch'
    if ($DesktopSmoke) {
        $smoke=Join-Path $evidence 'desktop-smoke.json'
        $app=Join-Path $root 'OhMyStudy.App.exe'
        $process=Start-Process -FilePath $app -ArgumentList @('--smoke-test',('"'+$smoke+'"')) -WindowStyle Hidden -PassThru
        $ids=[Collections.Generic.HashSet[uint32]]::new(); $null=$ids.Add([uint32]$process.Id)
        $observed=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $observationAvailable=$true; $timer=[Diagnostics.Stopwatch]::StartNew()
        while (-not $process.HasExited) {
            if ($timer.ElapsedMilliseconds -gt 45000) { $process.Kill(); throw 'Portable desktop smoke exceeded 45 seconds' }
            if ($observationAvailable) {
                try {
                    $snapshot=@(Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId,Name -ErrorAction Stop)
                    $changed=$true
                    while ($changed) { $changed=$false; foreach ($child in $snapshot) { if ($ids.Contains([uint32]$child.ParentProcessId) -and $ids.Add([uint32]$child.ProcessId)) { $changed=$true; $null=$observed.Add($child.Name) } } }
                } catch { $observationAvailable=$false; $report.notRun+='desktop-child-process-observation: CIM unavailable' }
            }
            Start-Sleep -Milliseconds 100
            $process.Refresh()
        }
        $process.WaitForExit(); $report.desktopExitCode=$process.ExitCode
        if ($process.ExitCode -ne 0) { throw "Portable desktop smoke exit $($process.ExitCode)" }
        $desktop=Get-Content -LiteralPath $smoke -Raw | ConvertFrom-Json
        if (-not $desktop.Passed -or ($desktop.IsNativeAot -ne $profile.nativeAot)) { throw 'Portable native desktop assertions failed' }
        $forbidden=@($observed | Where-Object { $_ -match '(?i)^(node|npm|npx|cargo|rustc|msedge|msedgewebview2|chrome|chromium|firefox)\.exe$' })
        if ($forbidden.Count) { throw "Unexpected child processes: $($forbidden -join ', ')" }
        $report.observedChildProcessNames=@($observed); $report.childProcessObservation='100 ms CIM samples; very short-lived children may be missed; not an execution sandbox'
        $report.checks+='portable-native-desktop-smoke'
        if ($observationAvailable) { $report.checks+='no-forbidden-child-observed-during-desktop-smoke' }
    } else { $report.notRun+='portable-desktop-smoke-and-child-process-observation: not requested' }
    if ($WorkflowSmoke) {
        $workflow=Join-Path $evidence 'workflow-smoke.json'
        $app=Join-Path $root 'OhMyStudy.App.exe'
        $process=Start-Process -FilePath $app -ArgumentList @('--workflow-smoke',('"'+$workflow+'"')) -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Portable workflow smoke exceeded 60 seconds' }
        if ($process.ExitCode -ne 0) { throw "Portable workflow smoke exit $($process.ExitCode)" }
        $result=Get-Content -LiteralPath $workflow -Raw | ConvertFrom-Json
        if (-not $result.Passed -or ($result.Checks.'native-aot' -ne $profile.nativeAot)) { throw 'Portable persisted workflow assertions failed' }
        $report.checks+='portable-persisted-twelve-page-workflow'
        $report.workflowReport=$workflow
    } else { $report.notRun+='portable-persisted-workflow: not requested' }
    $report.notRun += 'actual-Velopack-install-update-uninstall: covered by Test-VelopackInstaller.ps1'
    # The portable tree is immutable. A diagnostic run must keep reports, libraries and caches
    # outside it, and must not quietly invalidate the package that will be archived next.
    foreach ($file in $manifest.files) { Assert-FileHash (Assert-Path $root $file.path) $file.sha256 }
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
        $relative=[IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/')
        if ($relative -ne 'package-manifest.json' -and -not $listed.Contains($relative)) { throw "Diagnostic run wrote into portable package: $relative" }
    }
    $report.checkStatus='selected-local-checks-passed'
} catch { $report.checkStatus='failed'; $report.error=$_.Exception.Message; throw }
finally { $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding utf8; Write-Host "Portable verification evidence: $reportPath" }
