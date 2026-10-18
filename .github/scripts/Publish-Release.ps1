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
$document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText((Join-Path $root 'build-manifest.json')))
try {
    # Tag names are opaque strings, including date-shaped names; do not normalize them to DateTime.
    $manifest = [pscustomobject]@{ ref=$document.RootElement.GetProperty('ref').GetString();
        package=$document.RootElement.GetProperty('package').GetString(); sha256=$document.RootElement.GetProperty('sha256').GetString() }
} finally { $document.Dispose() }
if ($manifest.ref -ne $Tag -or [IO.Path]::GetFileName($manifest.package) -ne $manifest.package) { throw 'Package is not for this tag' }
$zip = Join-Path $root $manifest.package
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.sha256) { throw 'Release package hash mismatch' }
$assets = @($zip, (Join-Path $root 'SHA256SUMS.txt'), (Join-Path $root 'build-manifest.json'))
foreach ($asset in $assets) { if (-not (Test-Path -LiteralPath $asset -PathType Leaf)) { throw "Release asset missing: $asset" } }
$checksums = [IO.File]::ReadAllText((Join-Path $root 'SHA256SUMS.txt')).Trim()
if ($checksums -ne "$($manifest.sha256)  $($manifest.package)") { throw 'Release checksum list does not match the package' }
$prerelease = $Tag -notmatch '^v?\d+\.\d+\.\d+$'
if ($DryRun) { Write-Output "Validated release $Repository@$Tag; prerelease=$prerelease; assets=$($assets.Count)"; return }
function Invoke-Gh([string[]]$Arguments) { & gh @Arguments; if ($LASTEXITCODE -ne 0) { throw "gh failed with exit $LASTEXITCODE" } }
$existing = & gh release view $Tag --repo $Repository --json isDraft 2>$null
if ($LASTEXITCODE -eq 0) {
    if (-not ($existing | ConvertFrom-Json).isDraft) {
        Write-Output "Release $Tag is already published; preserving existing release and assets."
        return
    }
} else {
    Invoke-Gh @('release','create',$Tag,'--repo',$Repository,'--verify-tag','--draft','--generate-notes','--title',$Tag)
}
# Publish only after all checked assets have uploaded; retries can resume our draft.
Invoke-Gh (@('release','upload',$Tag,'--repo',$Repository,'--clobber') + $assets)
$arguments = @('release','edit',$Tag,'--repo',$Repository,'--draft=false')
if ($prerelease) { $arguments += '--prerelease' } else { $arguments += '--latest' }
Invoke-Gh $arguments
