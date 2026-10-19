#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$OutputFile,
    [string]$PackageVersion,
    [string]$ProductName = 'Oh My Study',
    [guid]$UpgradeCode = '98b2e630-a149-4ed5-9b54-4bb645396dd0',
    [string]$RegistryKey = 'Software\OhMyStudy\Installer'
)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$payload = (Resolve-Path -LiteralPath $PackageDirectory).Path.TrimEnd('\','/')
$output = [IO.Path]::GetFullPath($OutputFile)
if ($output.StartsWith($payload + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'MSI output must be outside its payload' }
$manifest = Get-Content -LiteralPath (Join-Path $payload 'package-manifest.json') -Raw | ConvertFrom-Json
if (-not $PackageVersion) {
    $label = if ($manifest.version) { $manifest.version } else { '0.1.1' }
    $PackageVersion = if ($label -match '^v?(\d+\.\d+\.\d+)(?:[-+].*)?$') { $Matches[1] } else { '0.1.1' }
}
$version = [version]$PackageVersion
if ($version.Major -gt 255 -or $version.Minor -gt 255 -or $version.Build -lt 0 -or $version.Build -gt 65535 -or $version.Revision -ne -1) { throw 'MSI requires a three-part version (major/minor <= 255, build <= 65535)' }
if (Test-Path -LiteralPath (Join-Path $payload 'OhMyStudy.Setup.exe')) { throw 'MSI payload must not include the legacy custom installer' }
$files = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName)
foreach ($item in @((Get-Item -LiteralPath $payload)) + $files + @(Get-ChildItem -LiteralPath $payload -Recurse -Directory)) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked MSI input rejected: $($item.FullName)" }
}
$listed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $manifest.files) {
    if (-not $listed.Add($file.path) -or $file.path -match '(^|[/\\])\.\.([/\\]|$)' -or [IO.Path]::IsPathRooted($file.path) -or $file.path.Contains(':')) { throw 'Invalid MSI input manifest path' }
    $path = [IO.Path]::GetFullPath((Join-Path $payload $file.path))
    if (-not $path.StartsWith($payload + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'MSI input path escapes payload' }
    if ((Get-Item -LiteralPath $path).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw "MSI input hash mismatch: $($file.path)" }
}
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace('\','/')
    if ($relative -ne 'package-manifest.json' -and -not $listed.Contains($relative)) { throw "Unlisted MSI input: $relative" }
}
if (-not (Test-Path -LiteralPath (Join-Path $payload 'OhMyStudy.App.exe'))) { throw 'MSI input has no application' }
New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
# WiX's native binder/cabinet APIs still reject long paths. Keep intermediate,
# source and MSI paths short regardless of the requested package/output names.
$stage = Join-Path $workspace ('output/ci/msi-build-' + [guid]::NewGuid().ToString('N').Substring(0,12))
$stagedPayload = Join-Path $stage 'files'
New-Item -ItemType Directory -Path $stagedPayload -Force | Out-Null
$tool = & (Join-Path $PSScriptRoot 'prepare-msi-tools.ps1')
function Escape-Xml([string]$Value) { return [Security.SecurityElement]::Escape($Value) }
function Identifier([string]$Prefix, [string]$Value) {
    $hash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant())))
    return $Prefix + $hash.Substring(0,32)
}
$xml = [Text.StringBuilder]::new()
$references = [Collections.Generic.List[string]]::new()
$null = $xml.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"><Fragment><DirectoryRef Id="INSTALLFOLDER">')
function Add-Directory([string]$Directory, [string]$DirectoryId) {
    $relative = [IO.Path]::GetRelativePath($payload, $Directory).Replace('\','/')
    $cleanup = Identifier 'D' $relative
    $references.Add($cleanup)
    $null = $xml.AppendLine("<Component Id='$cleanup' Guid='*'><RemoveFolder Id='R$cleanup' On='uninstall'/><RegistryValue Root='HKLM' Key='$(Escape-Xml $RegistryKey)\Folders' Name='$cleanup' Value='1' Type='integer' KeyPath='yes'/></Component>")
    foreach ($file in Get-ChildItem -LiteralPath $Directory -File | Sort-Object Name) {
        $relativeFile = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace('\','/')
        $id = Identifier 'F' $relativeFile
        $componentGuid = ([guid]::ParseExact((Identifier '' "$RegistryKey/$relativeFile"), 'N')).ToString('B')
        $source = Join-Path $stagedPayload ($id + $file.Extension)
        Copy-Item -LiteralPath $file.FullName -Destination $source
        $references.Add("C$id")
        $null = $xml.AppendLine("<Component Id='C$id' Guid='$componentGuid'><File Id='$id' Name='$(Escape-Xml $file.Name)' Source='$(Escape-Xml $source)' KeyPath='yes'/></Component>")
    }
    foreach ($child in Get-ChildItem -LiteralPath $Directory -Directory | Sort-Object Name) {
        $id = Identifier 'P' ([IO.Path]::GetRelativePath($payload,$child.FullName).Replace('\','/'))
        $null = $xml.AppendLine("<Directory Id='$id' Name='$(Escape-Xml $child.Name)'>")
        Add-Directory $child.FullName $id
        $null = $xml.AppendLine('</Directory>')
    }
}
Add-Directory $payload 'INSTALLFOLDER'
$null = $xml.AppendLine('</DirectoryRef><ComponentGroup Id="PayloadComponents">')
foreach ($id in $references) { $null = $xml.AppendLine("<ComponentRef Id='$id'/>") }
$null = $xml.AppendLine('</ComponentGroup></Fragment></Wix>')
$harvest = Join-Path $stage 'Payload.wxs'
[IO.File]::WriteAllText($harvest, $xml.ToString(), [Text.UTF8Encoding]::new($false))
$license = [IO.File]::ReadAllText((Join-Path $workspace 'LICENSE')).Replace('\','\\').Replace('{','\{').Replace('}','\}').Replace("`r",'').Replace("`n",'\par ')
$licenseRtf = Join-Path $stage 'License.rtf'
[IO.File]::WriteAllText($licenseRtf, '{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs18 ' + $license + '}', [Text.Encoding]::ASCII)
$iconSource = Join-Path $payload 'Assets/App.ico'
if (-not (Test-Path -LiteralPath $iconSource)) { $iconSource = Join-Path $workspace 'src/OhMyStudy.App/Assets/App.ico' }
$icon = Join-Path $stage 'App.ico'
Copy-Item -LiteralPath $iconSource -Destination $icon
$stagedMsi = Join-Path $stage 'package.msi'
$arguments = @('build', (Join-Path $PSScriptRoot 'msi/Package.wxs'), (Join-Path $PSScriptRoot 'msi/InstallDirDlg.wxs'), (Join-Path $PSScriptRoot 'msi/InstallUI.wxs'), $harvest,
    '-arch','x64','-culture','zh-CN','-ext',$tool.extension,'-intermediateFolder',$stage,
    '-d',"PackageVersion=$PackageVersion",'-d',"ProductName=$ProductName",'-d',"UpgradeCode=$UpgradeCode",'-d',"RegistryKey=$RegistryKey",'-d',"LicenseRtf=$licenseRtf",'-d',"AppIcon=$icon",'-o',$stagedMsi)
$buildLog = Join-Path $stage 'wix-build.log'
& dotnet $tool.cli @arguments 2>&1 | Tee-Object -FilePath $buildLog | Out-Host
if ($LASTEXITCODE -ne 0) { throw "WiX MSI build failed: $LASTEXITCODE; see $buildLog" }
Copy-Item -LiteralPath $stagedMsi -Destination $output
$report = [ordered]@{ schemaVersion=1; package=$output; sha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant(); packageVersion=$PackageVersion; fileCount=$files.Count; wixVersion=$tool.version; installerType='msi'; scope='perMachine'; payload=$payload }
$report | ConvertTo-Json | Set-Content -LiteralPath ($output + '.json') -Encoding utf8
return [pscustomobject]$report
