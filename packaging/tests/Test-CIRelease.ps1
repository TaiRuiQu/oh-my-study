#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$root = Join-Path $workspace ('output/ci/release-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$script = Join-Path $workspace '.github/scripts/Publish-Release.ps1'
$package = 'OhMyStudy-v1.2.3-win-x64.zip'
$zip = Join-Path $root $package
[IO.File]::WriteAllText($zip, 'synthetic package payload', [Text.UTF8Encoding]::new($false))
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{ ref='v1.2.3'; package=$package; sha256=$hash }
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'build-manifest.json') -Encoding utf8
[IO.File]::WriteAllText((Join-Path $root 'SHA256SUMS.txt'), "$hash  $package`n", [Text.UTF8Encoding]::new($false))
& $script -Tag v1.2.3 -Repository TaiRuiQu/oh-my-study -AssetDirectory $root -DryRun
$manifest.ref = '2026-10-18'
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'build-manifest.json') -Encoding utf8
& $script -Tag 2026-10-18 -Repository TaiRuiQu/oh-my-study -AssetDirectory $root -DryRun
$manifest.ref = 'v1.2.3'
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'build-manifest.json') -Encoding utf8
function Reject([scriptblock]$Operation) {
    $rejected = $false
    try { & $Operation } catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid release input was accepted' }
}
Reject { & $script -Tag '-bad' -Repository TaiRuiQu/oh-my-study -AssetDirectory $root -DryRun }
Reject { & $script -Tag v2.0.0 -Repository TaiRuiQu/oh-my-study -AssetDirectory $root -DryRun }
Reject { & $script -Tag v1.2.3 -Repository 'invalid repository' -AssetDirectory $root -DryRun }
[IO.File]::WriteAllText((Join-Path $root 'SHA256SUMS.txt'), 'incorrect checksum list')
Reject { & $script -Tag v1.2.3 -Repository TaiRuiQu/oh-my-study -AssetDirectory $root -DryRun }
[IO.File]::WriteAllText((Join-Path $root 'SHA256SUMS.txt'), "$hash  $package`n", [Text.UTF8Encoding]::new($false))
[IO.File]::AppendAllText($zip, 'tamper')
Reject { & $script -Tag v1.2.3 -Repository TaiRuiQu/oh-my-study -AssetDirectory $root -DryRun }
Write-Output 'PASS release dry run and five invalid-input cases; no GitHub writes'
