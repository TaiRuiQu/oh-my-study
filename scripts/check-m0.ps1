#requires -Version 7.0
[CmdletBinding()]
param([switch]$Typesetting, [switch]$Runtime)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$originalLocation = Get-Location
$originalCargoHome = $env:CARGO_HOME
try {
    Set-Location -LiteralPath $workspace
    if (-not $env:CARGO_HOME) { $env:CARGO_HOME = Join-Path $workspace 'tmp/cargo-home' }
    cargo fmt --all -- --check
    if ($LASTEXITCODE) { throw 'cargo fmt failed' }
    cargo clippy --locked --workspace --all-targets -- -D warnings
    if ($LASTEXITCODE) { throw 'cargo clippy failed' }
    cargo test --locked --workspace
    if ($LASTEXITCODE) { throw 'cargo test failed' }
    if ($Typesetting) {
        Push-Location -LiteralPath (Join-Path $workspace 'probes/typesetting')
        try {
            npm test
            if ($LASTEXITCODE) { throw 'typesetting tests failed; follow docs/m0-typesetting.zh-CN.md for setup' }
            npm run probe
            if ($LASTEXITCODE) { throw 'typesetting probe failed' }
        } finally { Pop-Location }
    }
    if ($Runtime) {
        $privatePython = Join-Path $workspace 'runtime/staging/python/python.exe'
        if (-not (Test-Path -LiteralPath $privatePython)) { throw 'Private runtime missing; follow docs/m0-execution.zh-CN.md for setup.' }
        # A fixed executable path is intentional: never substitute system Python.
        & $privatePython -I (Join-Path $workspace 'runtime/test_runner.py')
        if ($LASTEXITCODE) { throw 'runtime tests failed' }
        cargo test --locked --test host_execution -- --ignored
        if ($LASTEXITCODE) { throw 'native host executor integration failed' }
    }
} finally {
    Set-Location $originalLocation
    $env:CARGO_HOME = $originalCargoHome
}
