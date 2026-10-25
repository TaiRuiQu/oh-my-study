$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../ReleasePayload.ps1')
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$area = Join-Path $workspace ('output/ci/manifest-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $area -Force | Out-Null
$packId = 'OMS.Manifest.Regression'
$channel = 'win-x64-aot'
$vpk = & (Join-Path $PSScriptRoot '../prepare-velopack.ps1')
$updaters = @(Get-ChildItem -LiteralPath (Split-Path -Parent $vpk) -Filter 'update_x64.exe' -Recurse -File)
if ($updaters.Count -ne 1) { throw 'Expected one pinned native Windows x64 updater' }
$nativeRoot = Join-Path $area 'native-patch'
New-Item -ItemType Directory -Path (Join-Path $nativeRoot 'current') -Force | Out-Null
$updater = Join-Path $nativeRoot 'Update.exe'
Copy-Item -LiteralPath $updaters[0].FullName -Destination $updater

function New-Manifest([string]$Version) {
    return "<package><metadata><id>$packId</id><version>$Version</version><channel>$channel</channel><mainExe>OhMyStudy.App.exe</mainExe><os>win</os><rid>win-x64</rid></metadata></package>"
}

function New-Package([string]$Path, [string]$Version, [string]$MetadataName, [string]$Manifest = (New-Manifest $Version)) {
    $archive = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $files = [ordered]@{}
        $files["$packId.nuspec"] = $Manifest
        $files['lib/app/sq.version'] = $Manifest
        $files['lib/app/OhMyStudy.App.exe'] = "SYNTHETIC EXECUTABLE $Version"
        $files["lib/app/licenses/dependency/$MetadataName"] = '<package><metadata><id>Dependency</id><version>1.0.0</version></metadata></package>'
        foreach ($file in $files.GetEnumerator()) {
            $writer = [IO.StreamWriter]::new($archive.CreateEntry($file.Key).Open())
            try { $writer.Write($file.Value) } finally { $writer.Dispose() }
        }
    } finally { $archive.Dispose() }
}

function Assert-Rejected([string]$Path, [string]$Message) {
    try { Assert-VelopackPackageManifest $Path $packId '0.1.2' $channel }
    catch {
        if ($_.Exception.Message -like $Message) { return }
        throw
    }
    throw "Invalid package was accepted: $Path"
}

function Invoke-DeltaRoundTrip([string]$BasePackage, [string]$FullPackage, [string]$Name) {
    $delta = Join-Path $area "$Name-delta.nupkg"
    $reconstructed = Join-Path $area "$Name-reconstructed.nupkg"
    & $vpk delta generate --base $BasePackage --new $FullPackage --output $delta | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Delta generation failed' }
    & $updater patch --old $BasePackage --delta $delta --output $reconstructed --rootDir $nativeRoot --log (Join-Path $area "$Name-patch.log") | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Delta reconstruction failed' }
    Assert-NupkgContentEqual $FullPackage $reconstructed
    return $reconstructed
}

$metadata = Join-Path $area 'dependency.nuspec'
$notices = Join-Path $area 'notices'
New-Item -ItemType Directory -Path $notices | Out-Null
[IO.File]::WriteAllText($metadata, '<package><metadata><id>Dependency</id></metadata></package>')
Copy-ReleaseDependencyMetadata $metadata $notices
$copied = Join-Path $notices 'dependency.nuspec.xml'
if (-not (Test-Path -LiteralPath $copied) -or (Test-Path -LiteralPath (Join-Path $notices 'dependency.nuspec'))) { throw 'Dependency manifest must be retained with an XML extension' }
if ((Get-FileHash -LiteralPath $metadata).Hash -ne (Get-FileHash -LiteralPath $copied).Hash) { throw 'Dependency metadata content changed' }
$license = Join-Path $area 'LICENSE.txt'
[IO.File]::WriteAllText($license, 'SYNTHETIC LICENSE')
Copy-ReleaseDependencyMetadata $license $notices
if (-not (Test-Path -LiteralPath (Join-Path $notices 'LICENSE.txt'))) { throw 'License filename changed' }

$baseline = Join-Path $area 'baseline-full.nupkg'
$unsafe = Join-Path $area 'unsafe-full.nupkg'
$safe = Join-Path $area 'safe-full.nupkg'
New-Package $baseline '0.1.1' 'dependency.nuspec'
New-Package $unsafe '0.1.2' 'dependency.nuspec'
New-Package $safe '0.1.2' 'dependency.nuspec.xml'
[IO.File]::WriteAllText((Join-Path $nativeRoot 'current/sq.version'), (New-Manifest '0.1.1'))
Assert-Rejected $unsafe 'Ambiguous or missing Velopack manifest*'
$broken = Invoke-DeltaRoundTrip $baseline $unsafe 'unsafe'
Assert-Rejected $broken 'Ambiguous or missing Velopack manifest*'
$archive = [IO.Compression.ZipFile]::OpenRead($broken)
try {
    $first = $archive.Entries | Where-Object FullName -Like '*.nuspec' | Select-Object -First 1
    if ($first.FullName -notlike 'lib/app/licenses/*') { throw 'Fixture no longer reproduces dependency manifest selection after delta reconstruction' }
} finally { $archive.Dispose() }

Assert-VelopackPackageManifest $safe $packId '0.1.2' $channel
$repaired = Invoke-DeltaRoundTrip $baseline $safe 'safe'
Assert-VelopackPackageManifest $repaired $packId '0.1.2' $channel
$invalid = Join-Path $area 'invalid-full.nupkg'
New-Package $invalid '0.1.2' 'dependency.nuspec.xml' ((New-Manifest '0.1.2').Replace('<mainExe>OhMyStudy.App.exe</mainExe>', ''))
Assert-Rejected $invalid "Invalid Velopack manifest property 'mainExe'*"
$wrongVersion = Join-Path $area 'wrong-version-full.nupkg'
New-Package $wrongVersion '0.1.1' 'dependency.nuspec.xml'
Assert-Rejected $wrongVersion "Invalid Velopack manifest property 'version'*"
Write-Output 'PASS Velopack manifests: reproduced ambiguous delta reconstruction; dependency metadata retained; upgrade from an old package has one valid application manifest; invalid mainExe and version rejected'
