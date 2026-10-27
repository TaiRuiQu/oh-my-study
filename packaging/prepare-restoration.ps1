param([string]$WorkspaceRoot = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath $WorkspaceRoot).Path
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'restoration.lock.json') -Raw | ConvertFrom-Json
$source = Join-Path $workspace 'packaging/models/restoration'
$target = Join-Path $workspace 'output/migration/toolchain/restoration'
foreach ($model in $lock.models) {
    $path = Join-Path $source $model.filename
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Bundled restoration model missing: $path. Run git lfs pull before building." }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $bytes = (Get-Item -LiteralPath $path).Length
    if ($hash -ne $model.sha256 -or $bytes -ne $model.bytes) { throw "Restoration model mismatch: $path. Expected SHA256 $($model.sha256), $($model.bytes) bytes; actual SHA256 $hash, $bytes bytes." }
}
New-Item -ItemType Directory -Path $target -Force | Out-Null
foreach ($name in @('geometry.onnx', 'illumination.onnx', 'orientation.onnx', 'UVDoc-LICENSE.txt', 'SyntheticDoc-LICENSE.txt', 'Orientation-LICENSE.txt', 'NOTICE.md')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $target $name) -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'restoration.lock.json') -Destination (Join-Path $target 'model.json') -Force
Write-Output $target
