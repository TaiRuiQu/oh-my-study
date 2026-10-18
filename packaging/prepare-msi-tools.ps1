#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'msi/wix.lock.json') -Raw | ConvertFrom-Json
$cache = Join-Path $workspace 'output/ci/tools/msi-downloads'
$tools = Join-Path $workspace "output/ci/tools/wix-$($lock.version)"
New-Item -ItemType Directory -Path $cache -Force | Out-Null
foreach ($package in $lock.packages) {
    $name = "$($package.id).$($lock.version).nupkg"
    $archive = Join-Path $cache $name
    if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $package.sha256) {
        Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/$($package.id)/$($lock.version)/$name" -OutFile $archive
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $package.sha256) { throw "WiX package hash mismatch: $name" }
}
# Install from the verified local feed; do not change global tools or use an unpinned extension cache.
$nugetConfig = Join-Path $cache 'NuGet.Config'
$escapedCache = [Security.SecurityElement]::Escape($cache)
[IO.File]::WriteAllText($nugetConfig, "<configuration><packageSources><clear/><add key='local' value='$escapedCache'/></packageSources></configuration>")
$toolDll = Join-Path $tools ".store/wix/$($lock.version)/wix/$($lock.version)/tools/net6.0/any/wix.dll"
if (-not (Test-Path -LiteralPath $toolDll)) {
    & dotnet tool install wix --version $lock.version --tool-path $tools --configfile $nugetConfig | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Cannot prepare the pinned WiX CLI' }
}
# Always extract the extension from its verified archive.
$extensionRoot = Join-Path $cache "ui-$($lock.version)"
New-Item -ItemType Directory -Path $extensionRoot -Force | Out-Null
[IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $cache "wixtoolset.ui.wixext.$($lock.version).nupkg"), $extensionRoot, $true)
$extension = Join-Path $extensionRoot 'wixext5/WixToolset.UI.wixext.dll'
$version = (& dotnet $toolDll --version).Trim()
if ($LASTEXITCODE -ne 0 -or $version -notlike "$($lock.version)*") { throw 'Unexpected WiX CLI version' }
return [pscustomobject]@{ cli = $toolDll; extension = $extension; version = $lock.version }
