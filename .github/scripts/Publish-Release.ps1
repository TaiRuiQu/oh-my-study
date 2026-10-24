#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][ValidateSet('aot','self-contained','framework-dependent')][string]$Variant,
    [Parameter(Mandatory)][string]$AssetDirectory,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Tag) -or $Tag.StartsWith('-') -or $Tag.Contains("`n") -or $Tag.Contains("`r") -or $Tag -notmatch '^v?\d+\.\d+\.\d+(?:[-+][A-Za-z0-9._-]+)?$') { throw 'Invalid release tag' }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid repository name' }
$root = (Resolve-Path -LiteralPath $AssetDirectory).Path
$manifestFile = Get-Item -LiteralPath (Join-Path $root 'build-manifest.json')
$manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json -DateKind String
$channel = "win-x64-$Variant"
$releaseTag = "$Tag-$Variant"
$version = $Tag.TrimStart('v')
if ($manifest.schemaVersion -ne 3 -or $manifest.ref -cne $Tag -or $manifest.variant -cne $Variant -or $manifest.channel -cne $channel -or
    $manifest.version -cne $version -or $manifest.commit -notmatch '^[a-f0-9]{40}$' -or
    $manifest.validationStatus -ne 'release-candidate-user-acceptance-pending') { throw 'Invalid build identity, variant, channel or tag' }
$required = @('portable','installer','full','feed','legacyFeed')
$packages = @($manifest.packages)
if (@($packages | Where-Object kind -In $required).Count -ne $required.Count -or
    @($packages | Where-Object kind -EQ 'delta').Count -gt 1 -or $packages.Count -notin @(5,6)) { throw 'Portable, installer, full package and update feeds are required; one optional delta is allowed' }
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$kinds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$assets = @()
foreach ($package in $packages) {
    if ($package.variant -cne $Variant -or $package.channel -cne $channel -or $package.kind -cnotin @('portable','installer','full','delta','feed','legacyFeed') -or
        -not $kinds.Add($package.kind) -or $package.name -notmatch '^[A-Za-z0-9._-]+$' -or
        -not $names.Add($package.name) -or $package.sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Invalid package identity' }
    $expectedName = switch ($package.kind) {
        'portable' { "OhMyStudy-$Tag-win-x64-$Variant-portable.zip" }
        'installer' { "OhMyStudy-$Tag-win-x64-$Variant-Setup.exe" }
        'full' { "TaiRuiQu.OhMyStudy-$version-$channel-full.nupkg" }
        'delta' { "TaiRuiQu.OhMyStudy-$version-$channel-delta.nupkg" }
        'feed' { "releases.$channel.json" }
        'legacyFeed' { "RELEASES-$channel" }
    }
    if ($package.name -cne $expectedName) { throw "Package name does not match $($package.kind): $($package.name)" }
    $path = Join-Path $manifestFile.DirectoryName $package.name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $package.bytes -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $package.sha256) { throw 'Release package size or hash mismatch' }
    $assets += $path
}
$feed = Get-Content -LiteralPath (Join-Path $manifestFile.DirectoryName "releases.$channel.json") -Raw | ConvertFrom-Json
$full = $packages | Where-Object kind -EQ 'full'
$fullAssets = @($feed.Assets | Where-Object { $_.Type -eq 'Full' -and $_.FileName -ceq $full.name })
if ($fullAssets.Count -ne 1 -or $fullAssets[0].Version -cne $version -or $fullAssets[0].SHA256 -cin @('', $null) -or
    $fullAssets[0].SHA256.ToLowerInvariant() -cne $full.sha256 -or $fullAssets[0].Size -ne $full.bytes) { throw 'Update feed does not identify the full package' }
$delta = $packages | Where-Object kind -EQ 'delta'
$allDeltaAssets = @($feed.Assets | Where-Object Type -EQ 'Delta')
if ($delta) {
    $deltaAssets = @($allDeltaAssets | Where-Object { $_.FileName -ceq $delta.name -and $_.Version -ceq $version })
    if ($deltaAssets.Count -ne 1 -or $deltaAssets[0].SHA256.ToLowerInvariant() -cne $delta.sha256 -or $deltaAssets[0].Size -ne $delta.bytes) { throw 'Update feed does not identify the delta package' }
} elseif ($allDeltaAssets.Count) { throw 'Delta feed entry has no release asset' }
$packages = @($packages | Sort-Object name)
[IO.File]::WriteAllText((Join-Path $root 'SHA256SUMS.txt'), (($packages | ForEach-Object { "$($_.sha256)  $($_.name)" }) -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
[ordered]@{ schemaVersion=3; ref=$Tag; releaseTag=$releaseTag; commit=$manifest.commit; variant=$Variant; channel=$channel; version=$version; validationStatus=$manifest.validationStatus; packages=$packages } |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root 'build-manifest.json') -Encoding utf8
$assets += @((Join-Path $root 'SHA256SUMS.txt'), (Join-Path $root 'build-manifest.json'))
$prerelease = $Tag -notmatch '^v?\d+\.\d+\.\d+$'
if ($DryRun) { Write-Output "Validated release $Repository@$releaseTag; prerelease=$prerelease; assets=$($assets.Count)"; return }
function Invoke-Gh([string[]]$Arguments) { & gh @Arguments; if ($LASTEXITCODE -ne 0) { throw "gh failed with exit $LASTEXITCODE" } }
$existing = & gh release view $releaseTag --repo $Repository --json isDraft 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Output "Updating release $releaseTag with the complete checked build set."
} else {
    Invoke-Gh @('release','create',$releaseTag,'--repo',$Repository,'--target',$manifest.commit,'--draft','--generate-notes','--title',"Oh My Study $Tag ($Variant)")
}
Invoke-Gh (@('release','upload',$releaseTag,'--repo',$Repository,'--clobber') + $assets)
$arguments = @('release','edit',$releaseTag,'--repo',$Repository,'--draft=false')
if ($prerelease) { $arguments += '--prerelease' } else { $arguments += '--latest' }
Invoke-Gh $arguments
$body = & gh release view $releaseTag --repo $Repository --json body --jq .body
if ($LASTEXITCODE -ne 0) { throw 'Cannot read release notes' }
$marker = '<!-- distribution-variant -->'
$body = ($body -split [regex]::Escape($marker),2)[0].TrimEnd()
$deltaText = if ($delta) { '此 Release 包含上一版到当前版的差分包；安装版会自动优先使用增量更新，必要时回退到全量包。' } else { '这是首个 Velopack 安装版本，仅包含全量包；后续 Release 将提供差分包。' }
$runtime = switch ($Variant) {
    'aot' { 'Native AOT，无需另装 .NET。' }
    'self-contained' { '自带 .NET 运行时，非 Native AOT。' }
    'framework-dependent' { '需要先安装 Microsoft ASP.NET Core 10 x64 Runtime（包含 .NET Runtime）。' }
}
$notes = @"
$body

$marker
## 下载与更新

- `-Setup.exe`：Velopack 一键安装版，安装到当前用户目录，可在应用“关于”中检查更新。
- `-portable.zip`：完整解压后运行，不支持安装式应用内更新。
$deltaText
$runtime
旧 MSI 用户请先卸载旧程序再安装此版本；学习资料会保留。
二进制未代码签名，人工验收待完成；自动检查结果见 build-manifest.json。源码提交：$($manifest.commit)。
"@
$bodyFile = Join-Path $root 'release-notes.md'
[IO.File]::WriteAllText($bodyFile,$notes,[Text.UTF8Encoding]::new($false))
Invoke-Gh @('release','edit',$releaseTag,'--repo',$Repository,'--notes-file',$bodyFile)
