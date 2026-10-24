#requires -Version 7.0
param([string]$WorkspaceRoot = (Join-Path $PSScriptRoot '..'), [string]$CheckpointPath, [string]$PythonExecutable)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath $WorkspaceRoot).Path
$lock = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'scanner.lock.json') -Raw | ConvertFrom-Json).rectifier
$runtimeLock = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'runtime.lock.json') -Raw | ConvertFrom-Json).cpython
$target = Join-Path $workspace 'packaging/models/scanner'
$cache = Join-Path $workspace 'output/migration/downloads/docscanner'
New-Item -ItemType Directory -Path $target,$cache -Force | Out-Null
function Test-Digest([string]$Path, [string]$Sha256) {
    return (Test-Path -LiteralPath $Path -PathType Leaf) -and (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $Sha256
}
function Get-Verified([string]$Url, [string]$Path, [string]$Sha256) {
    if (Test-Digest $Path $Sha256) { return }
    $partial = "$Path.partial"
    Invoke-WebRequest -Uri $Url -OutFile $partial
    if (-not (Test-Digest $partial $Sha256)) { throw "Document scanner download checksum mismatch: $Path" }
    Move-Item -LiteralPath $partial -Destination $Path -Force
}
$model = Join-Path $target 'docscanner-l.onnx'
if (-not (Test-Digest $model $lock.sha256)) {
    Write-Warning 'DocScanner is licensed for non-commercial use with attribution/share-alike. Commercial use requires prior written permission from Hao Feng. See the included DocScanner-LICENSE.md.'
    $sourceArchive = Join-Path $cache 'source.zip'
    Get-Verified $lock.sourceUrl $sourceArchive $lock.sourceSha256
    Expand-Archive -LiteralPath $sourceArchive -DestinationPath $cache -Force
    $source = Join-Path $cache 'DocScanner-54f6063a61a52e4ce4012832e943d1871a9c3c66'
    if (-not $CheckpointPath) {
        $CheckpointPath = Join-Path $cache 'DocScanner-L.pth'
        Get-Verified $lock.checkpointUrl $CheckpointPath $lock.checkpointSha256
    }
    if (-not (Test-Digest $CheckpointPath $lock.checkpointSha256)) { throw 'The provided DocScanner-L checkpoint checksum is incorrect' }
    $segmentation = Join-Path $cache 'seg.pth'
    Get-Verified $lock.segmentationUrl $segmentation $lock.segmentationSha256
    if (-not $PythonExecutable) {
        $pythonRoot = Join-Path $cache 'python'
        $PythonExecutable = Join-Path $pythonRoot 'python.exe'
        $archive = Join-Path $cache $runtimeLock.filename
        Get-Verified $runtimeLock.url $archive $runtimeLock.sha256
        Expand-Archive -LiteralPath $archive -DestinationPath $pythonRoot -Force
        [IO.File]::WriteAllLines((Join-Path $pythonRoot 'python313._pth'), @('python313.zip','.','Lib/site-packages','import site',''))
        $pip = Invoke-RestMethod 'https://pypi.org/pypi/pip/25.3/json'
        $wheel = $pip.urls | Where-Object { $_.filename.EndsWith('.whl') } | Select-Object -First 1
        $pipArchive = Join-Path $cache $wheel.filename
        Get-Verified $wheel.url $pipArchive $wheel.digests.sha256
        $pipZip = Join-Path $cache 'pip.zip'
        Copy-Item -LiteralPath $pipArchive -Destination $pipZip -Force
        Expand-Archive -LiteralPath $pipZip -DestinationPath (Join-Path $pythonRoot 'Lib/site-packages') -Force
        & $PythonExecutable -I -B -m pip install --disable-pip-version-check 'torch==2.9.1' 'torchvision==0.24.1' --index-url https://download.pytorch.org/whl/cpu
        if ($LASTEXITCODE) { throw 'Could not prepare CPU PyTorch for the scanner export' }
        & $PythonExecutable -I -B -m pip install --disable-pip-version-check 'onnx==1.19.1' 'onnxruntime==1.23.2' 'numpy==2.2.6' 'pillow==11.3.0'
        if ($LASTEXITCODE) { throw 'Could not prepare ONNX for the scanner export' }
    }
    $partialModel = Join-Path $target 'docscanner-l.partial.onnx'
    & $PythonExecutable (Join-Path $PSScriptRoot 'export-docscanner.py') --source $source --segmentation $segmentation --rectifier $CheckpointPath --output $partialModel
    if ($LASTEXITCODE) { throw 'DocScanner export or inference verification failed' }
    if (-not (Test-Digest $partialModel $lock.sha256)) { throw 'DocScanner ONNX checksum differs from the pinned verified export' }
    Move-Item -LiteralPath $partialModel -Destination $model -Force
}
$license = Join-Path $target 'DocScanner-LICENSE.md'
Get-Verified $lock.licenseUrl $license $lock.licenseSha256
[IO.File]::WriteAllText((Join-Path $target 'DocScanner-NOTICE.txt'), 'DocScanner by Hao Feng, upstream commit 54f6063a61a52e4ce4012832e943d1871a9c3c66. Modified for Oh My Study by combining U2NETP segmentation and DocScanner-L into a CPU ONNX graph with normalized backward-grid output. Non-commercial use only, with attribution and share-alike; commercial use requires prior written permission. https://github.com/fh2019ustc/DocScanner See DocScanner-LICENSE.md.')
Write-Output $model
