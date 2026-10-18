#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$lock = Get-Content (Join-Path $workspace 'src-tauri/resources/pdfium-lock.json') -Raw | ConvertFrom-Json
$archive = Join-Path $workspace 'tmp/pdfium-win-x64.tgz'
$destination = Join-Path $workspace 'src-tauri/resources/pdfium'
New-Item -ItemType Directory -Force (Split-Path -Parent $archive), $destination | Out-Null
if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $lock.url -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $lock.sha256) {
    throw 'PDFium archive hash mismatch; no files were extracted'
}
tar -xzf $archive -C $destination
if ($LASTEXITCODE) { throw 'PDFium extraction failed' }
Write-Output "Prepared PDFium $($lock.version) at $destination; licenses are in the same directory."
