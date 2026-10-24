#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][ValidateSet('aot','self-contained','framework-dependent')][string]$Variant,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$CurrentTag
)
$ErrorActionPreference = 'Stop'
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid repository name' }
$root = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $root -Force | Out-Null
if (@(Get-ChildItem -LiteralPath $root -Force).Count) { throw 'Baseline download directory must be empty' }
$tags = @(& gh release list --repo $Repository --limit 100 --json tagName,isDraft --jq '.[] | select(.isDraft | not) | .tagName')
if ($LASTEXITCODE -ne 0) { throw 'Cannot list previous releases' }
$excluded = if ($CurrentTag) { "$CurrentTag-$Variant" } else { '' }
$tag = $tags | Where-Object { $_.EndsWith("-$Variant", [StringComparison]::Ordinal) -and $_ -cne $excluded } | Select-Object -First 1
if (-not $tag) {
    Write-Host "No earlier Velopack release for $Variant; creating the bootstrap full package only."
    return $null
}
& gh release download $tag --repo $Repository --pattern "*-win-x64-$Variant-full.nupkg" --pattern SHA256SUMS.txt --dir $root | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Cannot download Velopack baseline $tag" }
$files = @(Get-ChildItem -LiteralPath $root -Filter '*.nupkg' -File)
if ($files.Count -ne 1) { throw 'Exactly one baseline full package is required' }
$file = $files[0]
$pattern = '^([a-fA-F0-9]{64})  ' + [regex]::Escape($file.Name) + '$'
$checksumLines = @(Get-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt') | Where-Object { $_ -match $pattern })
if ($checksumLines.Count -ne 1 -or $checksumLines[0] -notmatch $pattern -or $Matches[1].ToLowerInvariant() -cne (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()) { throw 'Baseline package checksum missing or mismatched' }
Write-Host "Using Velopack baseline $tag."
return $file.FullName
