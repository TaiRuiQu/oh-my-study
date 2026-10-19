#requires -Version 7.0
param(
    [string]$WorkspaceRoot = (Join-Path $PSScriptRoot '..')
)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath $WorkspaceRoot).Path
$toolchain = Join-Path $workspace 'output/migration/toolchain'
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'native-documents.lock.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force -Path $toolchain | Out-Null

function Test-PinnedHash([string]$Path, [string]$Sha256) {
    return (Test-Path -LiteralPath $Path -PathType Leaf) -and ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $Sha256)
}
function Get-PinnedFile([string]$Url, [string]$Path, [string]$Sha256) {
    if (Test-PinnedHash $Path $Sha256) { return }
    Invoke-WebRequest -Uri $Url -OutFile $Path
    if (-not (Test-PinnedHash $Path $Sha256)) { throw "Checksum mismatch; refusing to use $Path" }
}

$typstArchive = Join-Path $toolchain 'typst-0.14.2.zip'
Get-PinnedFile $lock.typst.url $typstArchive $lock.typst.archiveSha256
$typstExecutable = Join-Path $toolchain 'typst-x86_64-pc-windows-msvc/typst.exe'
if (-not (Test-PinnedHash $typstExecutable $lock.typst.executableSha256) -or
    -not (Test-Path -LiteralPath (Join-Path $toolchain 'typst-x86_64-pc-windows-msvc/LICENSE')) -or
    -not (Test-Path -LiteralPath (Join-Path $toolchain 'typst-x86_64-pc-windows-msvc/NOTICE'))) {
    Expand-Archive -LiteralPath $typstArchive -DestinationPath $toolchain -Force
}
if (-not (Test-PinnedHash $typstExecutable $lock.typst.executableSha256)) { throw 'Typst executable checksum mismatch' }

$pdfiumDirectory = Join-Path $toolchain 'pdfium'
$pdfiumLibrary = Join-Path $pdfiumDirectory 'pdfium.dll'
New-Item -ItemType Directory -Force -Path $pdfiumDirectory | Out-Null
$pdfiumLicenseDirectory = Join-Path $pdfiumDirectory 'licenses'
if (-not (Test-PinnedHash $pdfiumLibrary $lock.pdfium.librarySha256) -or
    -not (Test-Path -LiteralPath (Join-Path $pdfiumLicenseDirectory 'pdfium.txt'))) {
    $pdfiumArchive = Join-Path $toolchain 'pdfium-win-x64-7881.tgz'
    Get-PinnedFile $lock.pdfium.url $pdfiumArchive $lock.pdfium.archiveSha256
    $extractedPdfium = Join-Path $toolchain 'pdfium-extracted'
    New-Item -ItemType Directory -Force -Path $extractedPdfium | Out-Null
    tar -xzf $pdfiumArchive -C $extractedPdfium
    if ($LASTEXITCODE) { throw 'PDFium extraction failed' }
    Copy-Item -LiteralPath (Join-Path $extractedPdfium 'bin/pdfium.dll') -Destination $pdfiumLibrary -Force
    $licenseSource = Join-Path $extractedPdfium 'licenses'
    New-Item -ItemType Directory -Force -Path $pdfiumLicenseDirectory | Out-Null
    Get-ChildItem -LiteralPath $licenseSource -File | Copy-Item -Destination $pdfiumLicenseDirectory -Force
}
if (-not (Test-PinnedHash $pdfiumLibrary $lock.pdfium.librarySha256)) { throw 'PDFium checksum mismatch' }
if (-not (Test-Path -LiteralPath (Join-Path $pdfiumLicenseDirectory 'pdfium.txt'))) { throw 'PDFium licenses are missing' }
& $typstExecutable --version
if ($LASTEXITCODE) { throw 'Typst launch failed' }
Write-Output "Native document tools prepared at $toolchain. No Node.js, browser, Rust SDK or bundled font is used; printing relies on the Windows-installed 新宋体 (NSimSun)."
