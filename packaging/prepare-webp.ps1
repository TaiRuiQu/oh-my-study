#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkspaceRoot = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath $WorkspaceRoot).Path
$webpLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'webp.lock.json') -Raw | ConvertFrom-Json
$webpRoot = Join-Path $workspace 'output/migration/toolchain/libwebp'
New-Item -ItemType Directory -Path $webpRoot -Force | Out-Null
function Test-WebpHash([string]$Path, [string]$Sha256) {
    (Test-Path -LiteralPath $Path -PathType Leaf) -and (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $Sha256
}
function Get-WebpPinned([string]$Url, [string]$Path, [string]$Sha256) {
    if (Test-WebpHash $Path $Sha256) { return }
    Invoke-WebRequest -Uri $Url -OutFile $Path
    if (-not (Test-WebpHash $Path $Sha256)) { throw "WebP dependency checksum mismatch: $Path" }
}
$archive = Join-Path $webpRoot "libwebp-$($webpLock.version)-windows-x64.zip"
Get-WebpPinned $webpLock.url $archive $webpLock.archiveSha256
$extracted = Join-Path $webpRoot "libwebp-$($webpLock.version)-windows-x64"
$decoder = Join-Path $extracted 'bin/dwebp.exe'
if (-not (Test-WebpHash $decoder $webpLock.executableSha256)) { Expand-Archive -LiteralPath $archive -DestinationPath $webpRoot -Force }
if (-not (Test-WebpHash $decoder $webpLock.executableSha256)) { throw 'WebP decoder checksum mismatch' }
Get-WebpPinned $webpLock.licenseUrl (Join-Path $webpRoot 'COPYING') $webpLock.licenseSha256
Get-WebpPinned $webpLock.patentsUrl (Join-Path $webpRoot 'PATENTS') $webpLock.patentsSha256
& $decoder -version
if ($LASTEXITCODE) { throw 'WebP decoder did not start' }
Write-Output "Pinned WebP decoder prepared at $decoder"
