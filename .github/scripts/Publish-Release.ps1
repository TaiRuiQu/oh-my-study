#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$AssetDirectory,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Tag) -or $Tag.StartsWith('-') -or $Tag.Contains("`n") -or $Tag.Contains("`r")) { throw 'Invalid release tag' }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid repository name' }
$root = (Resolve-Path -LiteralPath $AssetDirectory).Path
# Each matrix artifact has a separate manifest; require exactly four complete pairs.
$manifests=@(Get-ChildItem -LiteralPath $root -Filter build-manifest.json -Recurse -File | Where-Object { $_.DirectoryName -ne $root })
if ($manifests.Count -ne 4) { throw 'All four build manifests are required' }
$expectedVariants=@('aot','aot-avx512','self-contained','framework-dependent')
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$packages=@(); $assets=@(); $commit=$null; $builds=@()
foreach ($file in $manifests) {
    $manifest=Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -DateKind String
    if ($manifest.schemaVersion -ne 2 -or $manifest.ref -cne $Tag -or $manifest.variant -cnotin $expectedVariants -or
        -not $seen.Add($manifest.variant) -or $manifest.commit -notmatch '^[a-f0-9]{40}$' -or
        $manifest.validationStatus -ne 'release-candidate-user-acceptance-pending') { throw 'Invalid build identity or tag' }
    if ($commit -and $commit -cne $manifest.commit) { throw 'Builds must use the same source commit' }
    $commit=$manifest.commit
    if (@($manifest.packages).Count -notin @(2,3) -or @($manifest.packages | Where-Object kind -eq 'portable').Count -ne 1 -or
        @($manifest.packages | Where-Object kind -eq 'installer').Count -ne 1) { throw 'Portable and installer are both required; one optional patch is allowed' }
    $kinds=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($package in $manifest.packages) {
        if ($package.variant -cne $manifest.variant -or $package.kind -cnotin @('portable','installer','patch') -or
            -not $kinds.Add($package.kind) -or $package.name -notmatch '^[A-Za-z0-9._-]+$' -or
            -not $names.Add($package.name) -or $package.sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Invalid package identity' }
        $suffix = switch ($package.kind) {
            'portable' { '-portable.zip' }
            'installer' { '-installer.msi' }
            'patch' {
                if ($package.baselineVersion -notmatch '^\d+\.\d+\.\d+$' -or $package.packageVersion -notmatch '^\d+\.\d+\.\d+$' -or
                    [version]$package.packageVersion -le [version]$package.baselineVersion -or $package.baselineSha256 -notmatch '^[a-f0-9]{64}$' -or
                    $package.updateSha256 -cne ($manifest.packages | Where-Object kind -eq 'installer').sha256 -or
                    $package.productCode -notmatch '^\{[A-Fa-f0-9-]{36}\}$' -or $package.patchCode -notmatch '^\{[A-Fa-f0-9-]{36}\}$') { throw 'Invalid patch baseline or update identity' }
                '-from-' + $package.baselineVersion + '-patch.msp'
            }
        }
        if (-not $package.name.EndsWith('-'+$manifest.variant+$suffix, [StringComparison]::Ordinal)) { throw 'Package name does not match profile' }
        $path=Join-Path $file.DirectoryName $package.name
        if ((Get-Item -LiteralPath $path).Length -ne $package.bytes -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $package.sha256) { throw 'Release package size or hash mismatch' }
        $packages+=$package; $assets+=$path
    }
    $builds+=$manifest
}
$packages=@($packages | Sort-Object name)
[IO.File]::WriteAllText((Join-Path $root 'SHA256SUMS.txt'), (($packages | ForEach-Object { "$($_.sha256)  $($_.name)" }) -join "`n")+"`n", [Text.UTF8Encoding]::new($false))
[ordered]@{ schemaVersion=2; ref=$Tag; commit=$commit; packages=$packages; builds=$builds } | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath (Join-Path $root 'build-manifest.json') -Encoding utf8
$assets+=@((Join-Path $root 'SHA256SUMS.txt'), (Join-Path $root 'build-manifest.json'))
$prerelease = $Tag -notmatch '^v?\d+\.\d+\.\d+$'
if ($DryRun) { Write-Output "Validated release $Repository@$Tag; prerelease=$prerelease; assets=$($assets.Count)"; return }
function Invoke-Gh([string[]]$Arguments) { & gh @Arguments; if ($LASTEXITCODE -ne 0) { throw "gh failed with exit $LASTEXITCODE" } }
$existing = & gh release view $Tag --repo $Repository --json isDraft 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Output "Updating release $Tag with the complete checked build set."
} else {
    Invoke-Gh @('release','create',$Tag,'--repo',$Repository,'--verify-tag','--draft','--generate-notes','--title',$Tag)
}
# Publish only after all checked assets have uploaded; retries can resume our draft.
Invoke-Gh (@('release','upload',$Tag,'--repo',$Repository,'--clobber') + $assets)
$arguments = @('release','edit',$Tag,'--repo',$Repository,'--draft=false')
if ($prerelease) { $arguments += '--prerelease' } else { $arguments += '--latest' }
Invoke-Gh $arguments
# Keep existing release text and make supplemental-build provenance and choices visible.
$body=& gh release view $Tag --repo $Repository --json body --jq .body
if ($LASTEXITCODE -ne 0) { throw 'Cannot read release notes' }
$marker='<!-- distribution-variants -->'
$body=($body -split [regex]::Escape($marker),2)[0].TrimEnd()
$notes=@"
$body

$marker
## 下载版本

每种版本均提供 portable ZIP 和MSI 安装包。通常选择 **aot**；不确定处理器是否支持 AVX-512 时请选择普通 AOT 版。
若提供 MSP，仅适用于文件名所列基线版本和相同构建类型的 MSI 安装；首次安装、切换类型或不匹配时请选择完整 MSI。MSP 也需要管理员权限。

| 文件标识 | 运行要求 |
| --- | --- |
| aot | 无需安装 .NET；不启用 AVX-512 |
| aot-avx512 | 需要 x86-64-v4（AVX-512 F/BW/CD/DQ/VL）处理器及系统支持 |
| self-contained | 非 AOT，自带 .NET 运行时 |
| framework-dependent | 非 AOT，需要先安装 .NET 10 x64 Runtime |

以上八个文件的源码提交：$commit。补充构建保留原版本标签，准确来源见 build-manifest.json 和包内源码快照。旧的单一 ZIP 如仍列出，不属于本次四种构建。
二进制未代码签名，人工验收待完成；自动检查结果见构建清单。
"@
$bodyFile=Join-Path $root 'release-notes.md'
[IO.File]::WriteAllText($bodyFile,$notes,[Text.UTF8Encoding]::new($false))
Invoke-Gh @('release','edit',$Tag,'--repo',$Repository,'--notes-file',$bodyFile)
