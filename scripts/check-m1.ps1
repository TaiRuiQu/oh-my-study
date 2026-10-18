#requires -Version 7.0
[CmdletBinding()]
param([switch]$Runtime, [switch]$DesktopSmoke)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$original = Get-Location
$originalCargoHome = $env:CARGO_HOME
try {
    Set-Location -LiteralPath $workspace
    if (-not $env:CARGO_HOME) { $env:CARGO_HOME = Join-Path $workspace 'tmp/cargo-home' }
    npm run typecheck
    if ($LASTEXITCODE) { throw 'Frontend typecheck failed' }
    npm run lint
    if ($LASTEXITCODE) { throw 'Frontend lint failed' }
    npm run build
    if ($LASTEXITCODE) { throw 'Frontend build failed' }
    cargo fmt --all -- --check
    if ($LASTEXITCODE) { throw 'Rust formatting failed' }
    cargo clippy --locked --workspace --all-targets --features desktop,custom-protocol -- -D warnings
    if ($LASTEXITCODE) { throw 'Rust static analysis failed' }
    cargo test --locked --workspace
    if ($LASTEXITCODE) { throw 'Rust tests failed' }
    if ($Runtime) {
        cargo test --locked --workspace -- --ignored
        if ($LASTEXITCODE) { throw 'Runtime / native credential integration failed' }
        & ./runtime/staging/python/python.exe -I ./runtime/test_runner.py
        if ($LASTEXITCODE) { throw 'Private runner tests failed' }
    }
    if ($DesktopSmoke) {
        cargo run --locked --example m1_fixture
        if ($LASTEXITCODE) { throw 'Could not prepare self-authored fixtures' }
        cargo build --locked --features desktop,custom-protocol --bin oh-my-study
        if ($LASTEXITCODE) { throw 'Desktop build failed' }
        node scripts/m1-desktop-smoke.mjs
        if ($LASTEXITCODE) { throw 'Desktop acceptance failed' }
    }
} finally {
    Set-Location $original
    $env:CARGO_HOME = $originalCargoHome
}
