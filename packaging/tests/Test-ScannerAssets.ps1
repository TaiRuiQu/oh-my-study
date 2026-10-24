#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$prepare = Join-Path $workspace 'packaging/prepare-scanner.ps1'
$fixture = Join-Path $workspace ('output/ci/scanner-assets-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $fixture 'packaging/models/scanner'
$target = Join-Path $fixture 'output/migration/toolchain/scanner'
New-Item -ItemType Directory -Path $source -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $workspace 'packaging/models/scanner') -File | Copy-Item -Destination $source
& $prepare -WorkspaceRoot $fixture
foreach ($asset in Get-ChildItem -LiteralPath $source -File) {
    $copy = Join-Path $target $asset.Name
    if ((Get-FileHash -LiteralPath $copy).Hash -ne (Get-FileHash -LiteralPath $asset.FullName).Hash) {
        throw "Prepared scanner asset differs from repository asset: $($asset.Name)"
    }
}
if ((Get-FileHash -LiteralPath (Join-Path $target 'model.json')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $workspace 'packaging/scanner.lock.json')).Hash) {
    throw 'Prepared scanner manifest differs from the pinned manifest'
}
function Assert-Rejected([string]$Expected) {
    $failure = $null
    try { & $prepare -WorkspaceRoot $fixture } catch { $failure = $_.Exception.Message }
    if (-not $failure -or -not $failure.Contains($Expected, [StringComparison]::Ordinal)) {
        throw "Expected scanner preparation failure containing '$Expected'; actual: $failure"
    }
}
$model = Join-Path $source 'docscanner-l.onnx'
[IO.File]::WriteAllText($model, "version https://git-lfs.github.com/spec/v1`noid sha256:c9729ca08da28ecf5f4b866c05742c75259e70d8a0daa22c013d7b902dfac480`nsize 34101032`n")
Assert-Rejected 'Run git lfs pull'
Remove-Item -LiteralPath $model
Assert-Rejected 'Bundled scanner model missing'
Copy-Item -LiteralPath (Join-Path $workspace 'packaging/models/scanner/docscanner-l.onnx') -Destination $model
$bytes = [IO.File]::ReadAllBytes($model)
$bytes[0] = $bytes[0] -bxor 1
[IO.File]::WriteAllBytes($model, $bytes)
Assert-Rejected 'Bundled scanner model mismatch'
Write-Output 'Scanner asset preparation passed: pinned assets, manifest, LFS pointer, missing model and same-size corruption.'
