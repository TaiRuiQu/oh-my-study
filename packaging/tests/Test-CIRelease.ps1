#requires -Version 7.0
$ErrorActionPreference='Stop'
$workspace=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$root=Join-Path $workspace ('output/ci/release-test-'+[Guid]::NewGuid().ToString('N'))
$script=Join-Path $workspace '.github/scripts/Publish-Release.ps1'
function Reject([scriptblock]$Operation) {
    $rejected=$false
    try { & $Operation } catch { $rejected=$true }
    if (-not $rejected) { throw 'Invalid release input was accepted' }
}
$variants=@('aot','aot-avx512','self-contained','framework-dependent')
foreach ($variant in $variants) {
    $directory=Join-Path $root $variant
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $packages=@()
    foreach ($kind in @('portable','installer')) {
        $name="OhMyStudy-v1.2.3-win-x64-$variant-$kind"+$(if ($kind -eq 'portable') { '.zip' } else { '.msi' })
        $path=Join-Path $directory $name
        [IO.File]::WriteAllText($path,'synthetic payload')
        $packages+=@{ variant=$variant; kind=$kind; name=$name; bytes=(Get-Item $path).Length; sha256=(Get-FileHash $path).Hash.ToLowerInvariant() }
    }
    @{ schemaVersion=2; ref='v1.2.3'; commit=('a'*40); variant=$variant; packages=$packages; validationStatus='release-candidate-user-acceptance-pending' } |
        ConvertTo-Json -Depth 6 | Set-Content (Join-Path $directory 'build-manifest.json')
}
function Validate { & $script -Tag v1.2.3 -Repository TaiRuiQu/oh-my-study -AssetDirectory $root -DryRun }
Validate
$aggregate=Get-Content (Join-Path $root 'build-manifest.json') -Raw | ConvertFrom-Json
if ($aggregate.packages.Count -ne 8 -or @(Get-Content (Join-Path $root 'SHA256SUMS.txt')).Count -ne 8) { throw 'Incomplete aggregate' }
Reject { & $script -Tag '-bad' -Repository TaiRuiQu/oh-my-study -AssetDirectory $root -DryRun }
Reject { & $script -Tag v2.0.0 -Repository TaiRuiQu/oh-my-study -AssetDirectory $root -DryRun }
Reject { & $script -Tag v1.2.3 -Repository 'invalid repository' -AssetDirectory $root -DryRun }
$manifestFile=Join-Path $root 'aot/build-manifest.json'
$original=[IO.File]::ReadAllText($manifestFile)
foreach ($case in @('missing-kind','duplicate-kind','wrong-variant','wrong-commit','traversal','hash','size','legacy-exe')) {
    $manifest=$original | ConvertFrom-Json
    switch ($case) {
        'missing-kind' { $manifest.packages=@($manifest.packages[0]) }
        'duplicate-kind' { $manifest.packages[1].kind='portable' }
        'wrong-variant' { $manifest.variant='unknown' }
        'wrong-commit' { $manifest.commit='b'*40 }
        'traversal' { $manifest.packages[0].name='../outside.zip' }
        'hash' { $manifest.packages[0].sha256='0'*64 }
        'size' { $manifest.packages[0].bytes=99999 }
        'legacy-exe' { $manifest.packages[1].name=$manifest.packages[1].name.Replace('.msi','.exe') }
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content $manifestFile
    Reject { Validate }
    [IO.File]::WriteAllText($manifestFile,$original)
}
Move-Item -LiteralPath $manifestFile -Destination ($manifestFile+'.hidden')
Reject { Validate }
Move-Item -LiteralPath ($manifestFile+'.hidden') -Destination $manifestFile
# Date-shaped tags remain strings.
foreach ($variant in $variants) {
    $file=Join-Path $root "$variant/build-manifest.json"
    $manifest=Get-Content $file -Raw | ConvertFrom-Json
    $manifest.ref='2026-10-18'
    $manifest | ConvertTo-Json -Depth 6 | Set-Content $file
}
& $script -Tag 2026-10-18 -Repository TaiRuiQu/oh-my-study -AssetDirectory $root -DryRun
Write-Output 'PASS complete eight-asset release and invalid/missing/mixed build inputs; no GitHub writes'
