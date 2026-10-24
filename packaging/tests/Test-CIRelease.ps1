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
function New-Build([string]$Variant) {
    $channel="win-x64-$Variant"
    $directory=Join-Path $root $Variant
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $definitions=@(
        @{kind='portable';name="OhMyStudy-v1.2.3-win-x64-$Variant-portable.zip"},
        @{kind='installer';name="OhMyStudy-v1.2.3-win-x64-$Variant-Setup.exe"},
        @{kind='full';name="TaiRuiQu.OhMyStudy-1.2.3-$channel-full.nupkg"},
        @{kind='legacyFeed';name="RELEASES-$channel"}
    )
    $packages=@()
    foreach ($item in $definitions) {
        $path=Join-Path $directory $item.name
        [IO.File]::WriteAllText($path,'synthetic '+$item.kind)
        $packages+=@{variant=$Variant;channel=$channel;kind=$item.kind;name=$item.name;bytes=(Get-Item $path).Length;sha256=(Get-FileHash $path).Hash.ToLowerInvariant()}
    }
    $full=$packages | Where-Object kind -EQ 'full'
    $feed=@{Assets=@(@{PackageId='TaiRuiQu.OhMyStudy';Version='1.2.3';Type='Full';FileName=$full.name;SHA256=$full.sha256.ToUpperInvariant();Size=$full.bytes})}
    $feedName="releases.$channel.json"
    $feed | ConvertTo-Json -Depth 5 -Compress | Set-Content -LiteralPath (Join-Path $directory $feedName)
    $feedPath=Join-Path $directory $feedName
    $packages+=@{variant=$Variant;channel=$channel;kind='feed';name=$feedName;bytes=(Get-Item $feedPath).Length;sha256=(Get-FileHash $feedPath).Hash.ToLowerInvariant()}
    $manifest=@{schemaVersion=3;ref='v1.2.3';commit=('a'*40);variant=$Variant;channel=$channel;version='1.2.3';packages=$packages;validationStatus='release-candidate-user-acceptance-pending'}
    $manifest | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $directory 'build-manifest.json')
}
function Validate([string]$Variant) { & $script -Tag v1.2.3 -Repository TaiRuiQu/oh-my-study -Variant $Variant -AssetDirectory (Join-Path $root $Variant) -DryRun }
foreach ($variant in @('aot','self-contained','framework-dependent')) {
    New-Build $variant
    Validate $variant
    $aggregate=Get-Content (Join-Path $root "$variant/build-manifest.json") -Raw | ConvertFrom-Json
    if ($aggregate.packages.Count -ne 5 -or @(Get-Content (Join-Path $root "$variant/SHA256SUMS.txt")).Count -ne 5) { throw 'Incomplete aggregate' }
}
$manifestFile=Join-Path $root 'aot/build-manifest.json'
$feedFile=Join-Path $root 'aot/releases.win-x64-aot.json'
$original=[IO.File]::ReadAllText($manifestFile)
$originalFeed=[IO.File]::ReadAllText($feedFile)
$deltaName='TaiRuiQu.OhMyStudy-1.2.3-win-x64-aot-delta.nupkg'
$deltaPath=Join-Path $root "aot/$deltaName"
[IO.File]::WriteAllText($deltaPath,'synthetic delta')
$manifest=$original | ConvertFrom-Json
$manifest.packages += [pscustomobject]@{variant='aot';channel='win-x64-aot';kind='delta';name=$deltaName;bytes=(Get-Item $deltaPath).Length;sha256=(Get-FileHash $deltaPath).Hash.ToLowerInvariant()}
$manifest | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $manifestFile
$feed=$originalFeed | ConvertFrom-Json
$feed.Assets = @($feed.Assets) + @(
    [pscustomobject]@{PackageId='TaiRuiQu.OhMyStudy';Version='1.2.2';Type='Delta';FileName='TaiRuiQu.OhMyStudy-1.2.2-win-x64-aot-delta.nupkg';SHA256=('c'*64);Size=10},
    [pscustomobject]@{PackageId='TaiRuiQu.OhMyStudy';Version='1.2.3';Type='Delta';FileName=$deltaName;SHA256=(Get-FileHash $deltaPath).Hash;Size=(Get-Item $deltaPath).Length}
)
$feed | ConvertTo-Json -Depth 5 -Compress | Set-Content -LiteralPath $feedFile
$manifest=Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
$feedPackage=$manifest.packages | Where-Object kind -EQ 'feed'
$feedPackage.bytes=(Get-Item $feedFile).Length
$feedPackage.sha256=(Get-FileHash $feedFile).Hash.ToLowerInvariant()
$manifest | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $manifestFile
Validate 'aot'
$deltaOriginal=[IO.File]::ReadAllText($manifestFile)
$deltaFeedOriginal=[IO.File]::ReadAllText($feedFile)
foreach ($case in @('feed-hash','feed-version','orphan-delta')) {
    [IO.File]::WriteAllText($manifestFile,$deltaOriginal)
    [IO.File]::WriteAllText($feedFile,$deltaFeedOriginal)
    if ($case -eq 'orphan-delta') {
        $manifest=Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
        $manifest.packages=@($manifest.packages | Where-Object kind -NE 'delta')
        $manifest | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $manifestFile
    } else {
        $feed=Get-Content -LiteralPath $feedFile -Raw | ConvertFrom-Json
        if ($case -eq 'feed-hash') { $feed.Assets[0].SHA256='0'*64 } else { $feed.Assets[0].Version='1.2.2' }
        $feed | ConvertTo-Json -Depth 5 -Compress | Set-Content -LiteralPath $feedFile
    }
    Write-Output "Checking invalid feed case: $case"
    Reject { Validate 'aot' }
}
[IO.File]::WriteAllText($manifestFile,$original)
[IO.File]::WriteAllText($feedFile,$originalFeed)
foreach ($case in @('missing-kind','duplicate-kind','wrong-variant','wrong-commit','traversal','hash','size','legacy-msi')) {
    $manifest=$original | ConvertFrom-Json
    switch ($case) {
        'missing-kind' { $manifest.packages=@($manifest.packages | Where-Object kind -NE 'full') }
        'duplicate-kind' { $manifest.packages[1].kind='portable' }
        'wrong-variant' { $manifest.channel='win' }
        'wrong-commit' { $manifest.commit='g'*40 }
        'traversal' { $manifest.packages[0].name='../outside.zip' }
        'hash' { $manifest.packages[0].sha256='0'*64 }
        'size' { $manifest.packages[0].bytes=99999 }
        'legacy-msi' { $manifest.packages[1].name=$manifest.packages[1].name.Replace('.exe','.msi') }
    }
    $manifest | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $manifestFile
    Write-Output "Checking invalid manifest case: $case"
    Reject { Validate 'aot' }
    [IO.File]::WriteAllText($manifestFile,$original)
}
Reject { & $script -Tag '-bad' -Repository TaiRuiQu/oh-my-study -Variant aot -AssetDirectory $root -DryRun }
Reject { & $script -Tag v2.0.0 -Repository TaiRuiQu/oh-my-study -Variant aot -AssetDirectory (Join-Path $root 'aot') -DryRun }
Reject { & $script -Tag v1.2.3 -Repository 'invalid repository' -Variant aot -AssetDirectory (Join-Path $root 'aot') -DryRun }
Write-Output 'PASS three independent Velopack releases, full/delta feeds and invalid/missing/mixed inputs; no GitHub writes'
