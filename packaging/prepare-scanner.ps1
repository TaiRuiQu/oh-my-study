#requires -Version 7.0
param([string]$WorkspaceRoot = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath $WorkspaceRoot).Path
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'scanner.lock.json') -Raw | ConvertFrom-Json
$source = Join-Path $workspace 'packaging/models/scanner'
$target = Join-Path $workspace 'output/migration/toolchain/scanner'
$models = @(
    @{ Name = 'docaligner-lcnet.onnx'; Lock = $lock },
    @{ Name = 'document-orientation.onnx'; Lock = $lock.orientation },
    @{ Name = 'docscanner-l.onnx'; Lock = $lock.rectifier }
)
foreach ($model in $models) {
    $path = Join-Path $source $model.Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Bundled scanner model missing: $path. Run git lfs install and git lfs pull before preparing the scanner."
    }
    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $actualBytes = (Get-Item -LiteralPath $path).Length
    if ($actualHash -ne $model.Lock.sha256 -or $actualBytes -ne $model.Lock.bytes) {
        throw "Bundled scanner model mismatch: $path. Expected SHA256 $($model.Lock.sha256), $($model.Lock.bytes) bytes; actual SHA256 $actualHash, $actualBytes bytes. Run git lfs pull to retrieve the pinned model."
    }
}
$license = Join-Path $source 'DocScanner-LICENSE.md'
if ((Get-FileHash -LiteralPath $license -Algorithm SHA256).Hash.ToLowerInvariant() -ne $lock.rectifier.licenseSha256) {
    throw "DocScanner license checksum mismatch: $license"
}
New-Item -ItemType Directory -Path $target -Force | Out-Null
foreach ($name in @('docaligner-lcnet.onnx', 'document-orientation.onnx', 'docscanner-l.onnx', 'LICENSE.txt', 'DocScanner-LICENSE.md', 'DocScanner-NOTICE.txt')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $target $name) -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'scanner.lock.json') -Destination (Join-Path $target 'model.json') -Force
Write-Output $target
