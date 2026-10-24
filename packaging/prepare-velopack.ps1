#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'velopack.lock.json') -Raw | ConvertFrom-Json
$cache = Join-Path $workspace 'output/ci/tools/velopack-downloads'
$tools = Join-Path $workspace "output/ci/tools/vpk-$($lock.version)"
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$name = "$($lock.id).$($lock.version).nupkg"
$archive = Join-Path $cache $name
if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $lock.sha256) {
    Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/$($lock.id)/$($lock.version)/$name" -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $lock.sha256) { throw 'Velopack CLI package hash mismatch' }
$escapedCache = [Security.SecurityElement]::Escape($cache)
$nugetConfig = Join-Path $cache 'NuGet.Config'
[IO.File]::WriteAllText($nugetConfig, "<configuration><packageSources><clear/><add key='local' value='$escapedCache'/></packageSources></configuration>")
$executable = Join-Path $tools 'vpk.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    & dotnet tool install $lock.id --version $lock.version --tool-path $tools --configfile $nugetConfig | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Cannot prepare the pinned Velopack CLI' }
}
& $executable pack --help | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Velopack CLI is not executable' }
return $executable
