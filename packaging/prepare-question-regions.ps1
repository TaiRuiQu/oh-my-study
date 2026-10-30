param([string]$WorkspaceRoot = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath $WorkspaceRoot).Path
$environment = Join-Path $workspace 'output/question-regions-export'
$python = Join-Path $environment 'Scripts/python.exe'
if (-not (Test-Path -LiteralPath $python)) {
    uv venv $environment --python 3.13
    if ($LASTEXITCODE) { throw 'Could not prepare the detector export environment.' }
}
uv pip install --python $python torch==2.15.0+cpu torchvision==0.29.2+cpu --index-url https://download.pytorch.org/whl/cpu
if ($LASTEXITCODE) { throw 'Could not install the detector export framework.' }
uv pip install --python $python ultralytics==8.4.192 onnx==1.23.2
if ($LASTEXITCODE) { throw 'Could not install the detector export dependencies.' }
& $python (Join-Path $PSScriptRoot 'export-question-regions.py')
if ($LASTEXITCODE) { throw 'Question region model export failed.' }
