#requires -Version 7.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../ReleasePayload.ps1')
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$area = Join-Path $workspace ('output/ci/payload-test-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $area 'source'
$destination = Join-Path $area 'clean'
$kept = @('OhMyStudy.App.exe','native/pdfium.dll','native/typst/typst.exe','source/source-snapshot.zip',
    'licenses/LICENSE.txt','runtime/python/python313.zip','runtime/python/Lib/site-packages/numpy/testing/helpers.py',
    'runtime/python/Lib/site-packages/numpy/_core/tests/__init__.py','runtime/python/Lib/site-packages/numpy/_core/tests/_natype.py',
    'runtime/python/Lib/site-packages/numpy/_core/core.pyd','runtime/python/Lib/site-packages/fontTools/LICENSE',
    'runtime/python/Lib/site-packages/fontTools/module.c','runtime/python/Lib/site-packages/numpy/module.pyi')
$removed = @('OhMyStudy.App.PDB','native/library.pdb','native/linker.ilk','native/library.lib',
    'runtime/python/Lib/site-packages/numpy/__pycache__/module.cpython-313.pyc',
    'runtime/python/Lib/site-packages/sympy/tests/test_module.py',
    'runtime/python/Lib/site-packages/numpy/_core/tests/data/fixture.npy')
foreach ($relative in $kept + $removed) {
    $file = Join-Path $source $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $file) -Force | Out-Null
    [IO.File]::WriteAllText($file, 'SYNTHETIC ' + $relative)
}
$runtimeSource = Join-Path $source 'runtime'
$runtimeFiles = @(Get-ChildItem -LiteralPath $runtimeSource -Recurse -File | ForEach-Object {
    @{ path=[IO.Path]::GetRelativePath($runtimeSource,$_.FullName).Replace('\','/'); size=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
@{ schemaVersion=1; lockSha256='SYNTHETIC_LOCK'; files=$runtimeFiles } | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $runtimeSource 'runtime-manifest.json') -Encoding utf8
$originalManifest = [IO.File]::ReadAllText((Join-Path $runtimeSource 'runtime-manifest.json'))
$excluded = @(Copy-ReleasePayload $source $destination)
if ($excluded.Count -ne $removed.Count) { throw 'Unexpected release exclusions' }
foreach ($relative in $kept) {
    if (-not (Test-Path -LiteralPath (Join-Path $destination $relative))) { throw "Required runtime, source or license omitted: $relative" }
}
foreach ($relative in $removed) {
    if (Test-Path -LiteralPath (Join-Path $destination $relative)) { throw "Development file copied: $relative" }
    if (-not (Test-Path -LiteralPath (Join-Path $source $relative))) { throw "Original publish was modified: $relative" }
}
Assert-ReleasePayload $destination
Update-ReleaseRuntimeManifest (Join-Path $destination 'runtime')
$runtime = Get-Content -LiteralPath (Join-Path $destination 'runtime/runtime-manifest.json') -Raw | ConvertFrom-Json
if ($runtime.lockSha256 -ne 'SYNTHETIC_LOCK' -or $originalManifest -ne [IO.File]::ReadAllText((Join-Path $runtimeSource 'runtime-manifest.json'))) { throw 'Original runtime manifest or lock identity changed' }
foreach ($file in $runtime.files) {
    if ((Get-FileHash -LiteralPath (Join-Path $destination ('runtime/' + $file.path)) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw 'Retained runtime hash changed' }
    if (Get-ReleaseExclusionReason ('runtime/' + $file.path)) { throw 'Runtime manifest still requires excluded files' }
}
if ($runtime.files.Count -ne @($runtimeFiles | Where-Object { -not (Get-ReleaseExclusionReason ('runtime/' + $_.path)) }).Count) { throw 'Runtime manifest lost required files' }
$prefixed = Join-Path $area 'prefixed-runtime'
$prefixExclusions = @(Copy-ReleasePayload $runtimeSource $prefixed -Prefix 'runtime')
if ($prefixExclusions.Count -ne 3) { throw 'Runtime prefix did not apply exclusion policy' }
$rejected = $false
try { Copy-ReleasePayload $source (Join-Path $source 'nested') | Out-Null } catch { $rejected = $true }
if (-not $rejected) { throw 'Nested release destination accepted' }
foreach ($relative in $removed) {
    $bad = Join-Path $area ('bad-' + [guid]::NewGuid().ToString('N'))
    $file = Join-Path $bad $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $file) -Force | Out-Null
    [IO.File]::WriteAllText($file, 'SYNTHETIC DEVELOPMENT FILE')
    [IO.File]::WriteAllText((Join-Path $bad 'package-manifest.json'), '{"version":"0.1.1","files":[]}')
    $rejected = $false
    try { & (Join-Path $PSScriptRoot '../Build-MsiPackage.ps1') -PackageDirectory $bad -OutputFile (Join-Path $area 'rejected.msi') | Out-Null }
    catch { if ($_.Exception.Message -like 'Development file in release payload*') { $rejected = $true } else { throw } }
    if (-not $rejected) { throw "MSI builder accepted development file: $relative" }
}
$runtimeSource = Join-Path $workspace 'output/migration/toolchain/runtime'
if (-not (Test-Path -LiteralPath (Join-Path $runtimeSource 'python/python.exe'))) { throw 'Prepare the pinned computation runtime before testing release payloads' }
$runtimeTarget = Join-Path $area 'computation-runtime'
$null = Copy-ReleasePayload $runtimeSource $runtimeTarget -Prefix 'runtime'
Update-ReleaseRuntimeManifest $runtimeTarget
$savedMplCache = $env:MPLCONFIGDIR
try {
    $env:MPLCONFIGDIR = Join-Path $area 'plot-cache'
    $script = @'
import io
import numpy as np
import numpy.testing as npt
import sympy as sp
import matplotlib
matplotlib.use('Agg')
from matplotlib import pyplot as plt
from PIL import Image
from fontTools.ttLib import TTFont
x = sp.Symbol('x')
assert sp.integrate(x*x, (x, 0, 1)) == sp.Rational(1, 3)
assert sp.Matrix([[1, 2], [3, 4]]).det() == -2
npt.assert_allclose(np.linalg.solve([[3, 1], [1, 2]], [9, 8]), [2, 3])
assert len(np.fft.fft([1, 2, 3, 4])) == 4
figure, axes = plt.subplots()
axes.plot([0, 1, 2], [0, 1, 4])
output = io.BytesIO()
figure.savefig(output, format='png')
plt.close(figure)
output.seek(0)
assert Image.open(output).size[0] > 0
'@
    & (Join-Path $runtimeTarget 'python/python.exe') -I -B -X utf8 -c $script
    if ($LASTEXITCODE -ne 0) { throw 'Trimmed computation runtime lost required functionality' }
} finally { $env:MPLCONFIGDIR = $savedMplCache }
foreach ($file in Get-ChildItem -LiteralPath $runtimeTarget -Recurse -File) {
    if (Get-ReleaseExclusionReason ('runtime/' + [IO.Path]::GetRelativePath($runtimeTarget,$file.FullName).Replace('\','/'))) { throw 'Computation wrote development files into its release payload' }
}
Write-Output 'PASS release payload: symbols, linker files, Python caches and tests excluded; runtime, licenses, source and original hashes preserved; MSI rejects regressions'
Write-Output 'PASS trimmed runtime: NumPy linear algebra, FFT and testing helpers; SymPy integration and matrices; Matplotlib PNG export; Pillow decoding and fontTools import'
