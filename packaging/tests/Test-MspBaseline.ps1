#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$area = Join-Path $workspace ('output/ci/baseline-regression-' + [guid]::NewGuid().ToString('N'))
$mode = 'valid'
$savedExitCode = $global:LASTEXITCODE
function gh {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $directory = $Arguments[[array]::IndexOf($Arguments,'--dir') + 1]
    $name = 'OhMyStudy-v1.2.2-win-x64-aot-installer.msi'
    $file = Join-Path $directory $name
    [IO.File]::WriteAllText($file,'SYNTHETIC BASELINE')
    $hash = (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()
    if ($mode -eq 'hash') { $hash = '0'*64 }
    $lines = @("$hash  $name")
    if ($mode -eq 'missing') { $lines = @() }
    if ($mode -eq 'duplicate') { $lines += $lines[0] }
    if ($mode -eq 'multiple') { [IO.File]::WriteAllText((Join-Path $directory 'other.msi'),'EXTRA') }
    $lines | Set-Content -LiteralPath (Join-Path $directory 'SHA256SUMS.txt')
    $global:LASTEXITCODE = if ($mode -eq 'download') { 1 } else { 0 }
}
function Download([string]$Tag = 'v1.2.2') {
    & (Join-Path $PSScriptRoot '../Get-MspBaseline.ps1') -Tag $Tag -Repository TaiRuiQu/oh-my-study -Variant aot -OutputDirectory (Join-Path $area ([guid]::NewGuid().ToString('N')))
}
try {
    $path = Download
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Baseline download did not return the verified MSI' }
    foreach ($mode in @('hash','missing','duplicate','multiple','download')) {
        $rejected = $false
        try { $null = Download } catch { $rejected = $true }
        if (-not $rejected) { throw "Invalid baseline download accepted: $mode" }
    }
    $rejected = $false
    try { $null = Download '-bad-tag' } catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid baseline tag accepted' }
} finally { $global:LASTEXITCODE = $savedExitCode }
Write-Output 'PASS baseline download validation: checksums, duplicate/missing entries, multiple installers, failed download and invalid tags; no network calls'
