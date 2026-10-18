#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$directory = Join-Path $workspace 'output/ci/tools/actionlint-1.7.12'
$archive = Join-Path $workspace 'output/ci/tools/actionlint-1.7.12.zip'
$expected = '6e7241b51e6817ea6a047693d8e6fed13b31819c9a0dd6c5a726e1592d22f6e9'
New-Item -ItemType Directory -Path (Split-Path -Parent $archive) -Force | Out-Null
if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
    Invoke-WebRequest -Uri 'https://github.com/rhysd/actionlint/releases/download/v1.7.12/actionlint_1.7.12_windows_amd64.zip' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'actionlint archive hash mismatch' }
# Re-extract from the verified archive, rather than trusting a previously extracted executable.
Expand-Archive -LiteralPath $archive -DestinationPath $directory -Force
$executable = Join-Path $directory 'actionlint.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'actionlint executable missing' }
return $executable
