#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineMsi,
    [Parameter(Mandatory)][string]$UpdateMsi,
    [Parameter(Mandatory)][string]$OutputFile
)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'MsiDatabase.ps1')
$baseline = (Resolve-Path -LiteralPath $BaselineMsi).Path
$update = (Resolve-Path -LiteralPath $UpdateMsi).Path
$output = [IO.Path]::GetFullPath($OutputFile)
if ([IO.Path]::GetExtension($output) -ne '.msp' -or $output -in @($baseline,$update)) { throw 'Patch output must be a separate .msp file' }
$installer = New-Object -ComObject WindowsInstaller.Installer
$oldDatabase = $null; $newDatabase = $null
try {
    $oldDatabase = $installer.OpenDatabase($baseline,0)
    $newDatabase = $installer.OpenDatabase($update,0)
    $old = Read-MsiProperties $oldDatabase
    $new = Read-MsiProperties $newDatabase
    foreach ($property in @('OMS_PATCH_SCHEMA','OMS_BUILD_VARIANT','UpgradeCode','ProductName','Manufacturer','ProductLanguage','ALLUSERS')) {
        if (-not $old[$property] -or $old[$property] -cne $new[$property]) { throw "MSP incompatible $property; use the full MSI" }
    }
    if ($old.OMS_PATCH_SCHEMA -ne '1' -or $old.OMS_BUILD_VARIANT -notin @('aot','aot-avx512','self-contained','framework-dependent') -or $old.ALLUSERS -ne '1') { throw 'Unsupported MSP baseline; first install a current full MSI' }
    if ([version]$new.ProductVersion -le [version]$old.ProductVersion) { throw 'MSP update version must be greater than its baseline' }
    foreach ($database in @($oldDatabase,$newDatabase)) {
        $summary = $database.SummaryInformation(0)
        try { if ($summary.Property(7) -ne 'x64;2052') { throw 'MSP requires matching x64 Chinese MSI packages' } }
        finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }
    }
    foreach ($table in @('Directory','Component','Feature','FeatureComponents','Registry','Shortcut','RemoveFile')) {
        $sql = 'SELECT * FROM `' + $table + '`'
        $oldRows = @((Read-MsiRows $oldDatabase $sql) | ForEach-Object { ConvertTo-Json -InputObject $_ -Compress } | Sort-Object)
        $newRows = @((Read-MsiRows $newDatabase $sql) | ForEach-Object { ConvertTo-Json -InputObject $_ -Compress } | Sort-Object)
        if (@(Compare-Object $oldRows $newRows).Count) { throw "MSP $table layout changed; use the full MSI" }
    }
    $sql = 'SELECT `File`, `Component_`, `FileName`, `Attributes`, `Sequence` FROM `File`'
    $oldFiles = @((Read-MsiRows $oldDatabase $sql) | ForEach-Object { ConvertTo-Json -InputObject $_ -Compress } | Sort-Object)
    $newFiles = @((Read-MsiRows $newDatabase $sql) | ForEach-Object { ConvertTo-Json -InputObject $_ -Compress } | Sort-Object)
    if (@(Compare-Object $oldFiles $newFiles).Count) { throw 'MSP file layout changed; use the full MSI' }
} finally {
    foreach ($resource in @($oldDatabase,$newDatabase,$installer)) { if ($null -ne $resource) { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($resource) } }
}
$stage = Join-Path $workspace ('output/ci/msp-build-' + [guid]::NewGuid().ToString('N').Substring(0,12))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$stagedBaseline = Join-Path $stage 'baseline.msi'
$stagedUpdate = Join-Path $stage 'update.msi'
Copy-Item -LiteralPath $baseline -Destination $stagedBaseline
Copy-Item -LiteralPath $update -Destination $stagedUpdate
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $null
try {
    $database = $installer.OpenDatabase($stagedUpdate,1)
    $productCode = ([guid]$old.ProductCode).ToString('B').ToUpperInvariant()
    Invoke-MsiSql $database ('UPDATE `Property` SET `Value` = ''' + $productCode + ''' WHERE `Property` = ''ProductCode''')
    $summary = $database.SummaryInformation(1)
    try { $summary.Property(9) = [guid]::NewGuid().ToString('B').ToUpperInvariant(); $summary.Persist() }
    finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }
    $database.Commit()
} finally {
    if ($null -ne $database) { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
    $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}
$tool = & (Join-Path $PSScriptRoot 'prepare-msi-tools.ps1')
$patchCode = [guid]::NewGuid().ToString('B').ToUpperInvariant()
$stagedPatch = Join-Path $stage 'update.msp'
$arguments = @('build',(Join-Path $PSScriptRoot 'msi/Patch.wxs'),'-arch','x64','-intermediateFolder',$stage,
    '-d',"BaselineMsi=$stagedBaseline",'-d',"UpdateMsi=$stagedUpdate",'-d',"BaselineVersion=$($old.ProductVersion)",
    '-d',"PatchVersion=$($new.ProductVersion)",'-d',"ProductName=$($old.ProductName)",'-d',"PatchCode=$patchCode",'-o',$stagedPatch)
$buildLog = Join-Path $stage 'wix-build.log'
& dotnet $tool.cli @arguments 2>&1 | Tee-Object -FilePath $buildLog | Out-Host
if ($LASTEXITCODE -ne 0) { throw "WiX MSP build failed: $LASTEXITCODE; see $buildLog" }
New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
Copy-Item -LiteralPath $stagedPatch -Destination $output
$report = [ordered]@{ schemaVersion=1; package=$output; sha256=(Get-FileHash -LiteralPath $output).Hash.ToLowerInvariant(); bytes=(Get-Item -LiteralPath $output).Length;
    installerType='msp'; variant=$old.OMS_BUILD_VARIANT; productCode=$productCode; patchCode=$patchCode; baselineVersion=$old.ProductVersion; packageVersion=$new.ProductVersion;
    baselineSha256=(Get-FileHash -LiteralPath $baseline).Hash.ToLowerInvariant(); updateSha256=(Get-FileHash -LiteralPath $update).Hash.ToLowerInvariant(); wixVersion=$tool.version }
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath ($output + '.json') -Encoding utf8
return [pscustomobject]$report
