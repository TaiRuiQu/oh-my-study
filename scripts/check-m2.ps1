#requires -Version 7.0
[CmdletBinding()]
param([switch]$Pdfium, [switch]$DesktopSmoke)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$original = Get-Location
$originalCargoHome = $env:CARGO_HOME
try {
    Set-Location -LiteralPath $workspace
    if (-not $env:CARGO_HOME) { $env:CARGO_HOME = Join-Path $workspace 'tmp/cargo-home' }
    & ./scripts/check-m1.ps1
    if ($LASTEXITCODE) { throw 'Base application checks failed' }
    if ($Pdfium -or $DesktopSmoke) {
        & ./scripts/prepare-pdfium.ps1
        cargo test --locked --test grading_integration -- --ignored
        if ($LASTEXITCODE) { throw 'Native PDFium regression failed' }
    }
    if ($DesktopSmoke) {
        cargo run --locked --example m2_fixture
        if ($LASTEXITCODE) { throw 'M2 fixture failed' }
        cargo build --locked --features desktop,custom-protocol --bin oh-my-study
        if ($LASTEXITCODE) { throw 'Desktop build failed' }
        node scripts/m2-desktop-smoke.mjs
        if ($LASTEXITCODE) { throw 'M2 desktop regression failed' }
    }
} finally {
    Set-Location $original
    $env:CARGO_HOME = $originalCargoHome
}
