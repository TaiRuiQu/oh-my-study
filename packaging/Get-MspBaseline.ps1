#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][ValidateSet('aot','aot-avx512','self-contained','framework-dependent')][string]$Variant,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if ($Tag -notmatch '^v?\d+\.\d+\.\d+(?:[-+][A-Za-z0-9._-]+)?$' -or $Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid baseline release identity' }
$root = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $root -Force | Out-Null
if (@(Get-ChildItem -LiteralPath $root -Force).Count) { throw 'Baseline download directory must be empty' }
& gh release download $Tag --repo $Repository --pattern "*-win-x64-$Variant-installer.msi" --pattern SHA256SUMS.txt --dir $root | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Cannot download baseline MSI and checksums' }
$files = @(Get-ChildItem -LiteralPath $root -Filter '*.msi' -File)
if ($files.Count -ne 1) { throw 'Exactly one baseline installer is required' }
$file = $files[0]
$pattern = '^([a-fA-F0-9]{64})  ' + [regex]::Escape($file.Name) + '$'
$checksumLines = @(Get-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt') | Where-Object { $_ -match $pattern })
if ($checksumLines.Count -ne 1 -or $checksumLines[0] -notmatch $pattern -or $Matches[1].ToLowerInvariant() -cne (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()) { throw 'Baseline MSI checksum missing or mismatched' }
return $file.FullName
