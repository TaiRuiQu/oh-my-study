#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$area = Join-Path $workspace ('output/ci/velopack-baseline-regression-' + [guid]::NewGuid().ToString('N'))
$mode = 'valid'
$savedExitCode = $global:LASTEXITCODE
function gh {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    if ($Arguments[0] -eq 'release' -and $Arguments[1] -eq 'list') {
        if ($mode -notin @('list','none')) { @('v1.2.3-aot','v1.2.2-aot','v1.2.2-self-contained') }
        $global:LASTEXITCODE = if ($mode -eq 'list') { 1 } else { 0 }
        return
    }
    $directory = $Arguments[[array]::IndexOf($Arguments,'--dir') + 1]
    $name = 'TaiRuiQu.OhMyStudy-1.2.2-win-x64-aot-full.nupkg'
    $file = Join-Path $directory $name
    [IO.File]::WriteAllText($file,'SYNTHETIC VELOPACK BASELINE')
    $hash = (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()
    if ($mode -eq 'hash') { $hash = '0'*64 }
    $lines = @("$hash  $name")
    if ($mode -eq 'missing') { $lines = @() }
    if ($mode -eq 'duplicate') { $lines += $lines[0] }
    if ($mode -eq 'multiple') { [IO.File]::WriteAllText((Join-Path $directory 'other.nupkg'),'EXTRA') }
    $lines | Set-Content -LiteralPath (Join-Path $directory 'SHA256SUMS.txt')
    $global:LASTEXITCODE = if ($mode -eq 'download') { 1 } else { 0 }
}
function Download {
    & (Join-Path $PSScriptRoot '../Get-VelopackBaseline.ps1') -CurrentTag v1.2.3 -Repository TaiRuiQu/oh-my-study -Variant aot -OutputDirectory (Join-Path $area ([guid]::NewGuid().ToString('N')))
}
try {
    $path = Download
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or $path -notlike '*1.2.2*full.nupkg') { throw 'Baseline download did not return the previous verified full package' }
    foreach ($mode in @('hash','missing','duplicate','multiple','download','list')) {
        $rejected = $false
        try { $null = Download } catch { $rejected = $true }
        if (-not $rejected) { throw "Invalid baseline download accepted: $mode" }
    }
    $rejected = $false
    try { $null = & (Join-Path $PSScriptRoot '../Get-VelopackBaseline.ps1') -Repository 'invalid repository' -Variant aot -OutputDirectory (Join-Path $area 'invalid') } catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid baseline repository accepted' }
    $mode = 'none'
    $result = Download
    if ($null -ne $result) { throw 'Bootstrap release should not return a baseline' }
} finally { $global:LASTEXITCODE = $savedExitCode }
Write-Output 'PASS Velopack baseline selection and checksum validation, bootstrap behavior and failed downloads; no network calls'
