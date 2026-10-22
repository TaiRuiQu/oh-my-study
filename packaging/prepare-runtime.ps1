#requires -Version 7.0
[CmdletBinding()]
param([string]$OutputDirectory, [switch]$Offline)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $workspace 'output/migration/toolchain/runtime' }
$target = [IO.Path]::GetFullPath($OutputDirectory)
$cache = Join-Path $workspace 'output/migration/downloads/runtime'
$lockFile = Join-Path $PSScriptRoot 'runtime.lock.json'
$lock = Get-Content -LiteralPath $lockFile -Raw | ConvertFrom-Json
$manifestPath = Join-Path $target 'runtime-manifest.json'

function Test-Digest([string]$Path, [string]$Digest) {
    return (Test-Path -LiteralPath $Path -PathType Leaf) -and ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $Digest.ToLowerInvariant())
}
function Get-LockedArchive($Entry) {
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    $file = Join-Path $cache $Entry.filename
    if (-not (Test-Digest $file $Entry.sha256)) {
        if ($Offline) { throw "Offline archive missing or hash mismatch: $file" }
        $temporary = Join-Path $cache ([Guid]::NewGuid().ToString('N') + '.download')
        try {
            Invoke-WebRequest -Uri $Entry.url -OutFile $temporary
            if (-not (Test-Digest $temporary $Entry.sha256)) { throw "Download hash mismatch: $($Entry.filename)" }
            Move-Item -LiteralPath $temporary -Destination $file -Force
        } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
    }
    return $file
}
function Expand-CheckedArchive([string]$Archive, [string]$Destination) {
    $root = [IO.Path]::GetFullPath($Destination) + [IO.Path]::DirectorySeparatorChar
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('/', [IO.Path]::DirectorySeparatorChar)
            if ([IO.Path]::IsPathRooted($name) -or $name.Contains(':')) { throw 'Archive contains an absolute path' }
            $path = [IO.Path]::GetFullPath((Join-Path $Destination $name))
            if (-not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive path escapes destination' }
            if ($entry.FullName.EndsWith('/')) { New-Item -ItemType Directory -Path $path -Force | Out-Null; continue }
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($path)) -Force | Out-Null
            if (Test-Path -LiteralPath $path) { throw "Archive file collision: $name" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $false)
        }
    } finally { $zip.Dispose() }
}

if (Test-Path -LiteralPath $manifestPath) {
    $existing = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $valid = $existing.lockSha256 -eq (Get-FileHash -LiteralPath $lockFile -Algorithm SHA256).Hash.ToLowerInvariant()
    $workerHash = (Get-FileHash -LiteralPath (Join-Path $workspace 'runtime/execute.py') -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($existing.workerSha256 -ne $workerHash) { $valid = $false }
    $sandboxWorkerHash = (Get-FileHash -LiteralPath (Join-Path $workspace 'runtime/sandbox-worker.py') -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($existing.sandboxWorkerSha256 -ne $sandboxWorkerHash) { $valid = $false }
    foreach ($file in $existing.files) { if (-not (Test-Digest (Join-Path $target $file.path) $file.sha256)) { $valid = $false; break } }
    if ($valid) { Write-Host "Private computation runtime already verified: $target"; return }
    throw 'Existing private runtime differs. Choose a new OutputDirectory to rebuild without overwriting.'
}
if (Test-Path -LiteralPath $target) { throw 'OutputDirectory exists without a verified manifest; choose a new directory.' }
$parent = [IO.Path]::GetDirectoryName($target)
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$stage = Join-Path $parent ('.runtime-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    $python = Join-Path $stage 'python'
    Expand-CheckedArchive (Get-LockedArchive $lock.cpython) $python
    $site = Join-Path $python 'Lib/site-packages'
    foreach ($wheel in $lock.wheels) { Expand-CheckedArchive (Get-LockedArchive $wheel) $site }
    $pth = Join-Path $python ('python' + ($lock.cpython.version.Split('.')[0..1] -join '') + '._pth')
    [IO.File]::WriteAllText($pth, "python313.zip`n.`nLib/site-packages`nimport site`n", [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath (Join-Path $workspace 'runtime/execute.py') -Destination (Join-Path $python 'execute.py')
    Copy-Item -LiteralPath (Join-Path $workspace 'runtime/sandbox-worker.py') -Destination (Join-Path $python 'sandbox-worker.py')
    Copy-Item -LiteralPath $lockFile -Destination (Join-Path $stage 'runtime.lock.json')
    $script = "import sys,json,numpy,sympy,matplotlib; matplotlib.use('Agg'); import matplotlib.pyplot as p; from PIL import Image; p.plot([0,1],[0,1]); p.close(); print(json.dumps({'python':sys.version.split()[0],'numpy':numpy.__version__,'sympy':sympy.__version__,'matplotlib':matplotlib.__version__,'pillow':Image.__version__}))"
    $savedCache = $env:MPLCONFIGDIR
    try { $env:MPLCONFIGDIR = Join-Path $stage 'verification-cache'; & (Join-Path $python 'python.exe') -I -B -X utf8 -c $script; if ($LASTEXITCODE -ne 0) { throw 'Private computation package verification failed' } }
    finally { $env:MPLCONFIGDIR = $savedCache }
    # Keep the install immutable after its manifest: runtime caches belong in task output directories.
    $verificationCache = Join-Path $stage 'verification-cache'
    if (Test-Path -LiteralPath $verificationCache) { Remove-Item -LiteralPath $verificationCache -Recurse -Force }
    $files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object { [ordered]@{
        path=[IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/'); size=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    } })
    $manifest = [ordered]@{ schemaVersion=1; status='computation-verified-sandbox-guest-packaged'; timestampUtc=[DateTimeOffset]::UtcNow.ToString('O');
        cpython=$lock.cpython.version; packages=@($lock.wheels | Select-Object name,version); lockSha256=(Get-FileHash -LiteralPath $lockFile -Algorithm SHA256).Hash.ToLowerInvariant();
        workerSha256=(Get-FileHash -LiteralPath (Join-Path $workspace 'runtime/execute.py') -Algorithm SHA256).Hash.ToLowerInvariant(); files=$files;
        sandboxWorkerSha256=(Get-FileHash -LiteralPath (Join-Path $workspace 'runtime/sandbox-worker.py') -Algorithm SHA256).Hash.ToLowerInvariant();
        notRun=@('appcontainer-execution','clean-windows-matrix','dependency-security-audit') }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage 'runtime-manifest.json') -Encoding utf8
    Move-Item -LiteralPath $stage -Destination $target
    Write-Host "Private computation runtime: $target"
} catch {
    Write-Warning "Runtime preparation stopped. Partial files retained for diagnosis: $stage"
    throw
}
