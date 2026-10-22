#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallerMsi,
    [string]$ReportFile,
    [switch]$InstallSmoke,
    [switch]$VerifyPayload,
    [switch]$WorkflowSmoke
)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$msi = (Resolve-Path -LiteralPath $InstallerMsi).Path
$area = Join-Path $workspace ('output/migration/msi-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $area -Force | Out-Null
if (-not $ReportFile) { $ReportFile = Join-Path $area 'verification.json' }
. (Join-Path $PSScriptRoot 'MsiElevation.ps1')
if ($InstallSmoke) {
    $elevatedParameters = @{ InstallerMsi=$msi; ReportFile=[IO.Path]::GetFullPath($ReportFile); InstallSmoke=$true; VerifyPayload=[bool]$VerifyPayload; WorkflowSmoke=[bool]$WorkflowSmoke }
    if (Invoke-MsiTestElevation $PSCommandPath $elevatedParameters) { return }
}
$report = [ordered]@{ schemaVersion=1; installer=$msi; sha256=(Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant(); checkStatus='running'; checks=@(); evidence=$area }
$windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
$database = $windowsInstaller.OpenDatabase($msi, 0)
function Query([string]$Sql) {
    $view = $database.OpenView($Sql)
    try {
        $null = $view.Execute()
        while ($record = $view.Fetch()) {
            try { $record.StringData(1) } finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
        }
    } finally { $null = $view.Close(); $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
}
function Run-Msi([string[]]$Arguments, [string]$Log) {
    $start = [Diagnostics.ProcessStartInfo]::new('msiexec.exe')
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.Arguments = (($Arguments + @('/qn','/norestart','/L*v',$Log)) | ForEach-Object {
        if ($_.Contains('"')) { throw 'Unexpected quote in MSI test argument' }
        if ($_ -match '^([A-Z][A-Z0-9_]*)=(.*)$') { $Matches[1] + '="' + $Matches[2].TrimEnd('\') + '"' }
        elseif ($_.StartsWith('/')) { $_ } else { '"' + $_ + '"' }
    }) -join ' '
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(120000)) { throw "MSI did not finish; inspect $Log" }
        if ($process.ExitCode -notin @(0,3010)) { throw "MSI exit $($process.ExitCode); inspect $Log" }
    } finally { $process.Dispose() }
}
$installed = $false
$productCode = $null
if (-not ('OhMyStudy.MsiTestNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
using System.Text;
namespace OhMyStudy {
    public static class MsiTestNative {
        [DllImport("msi.dll", CharSet=CharSet.Unicode)]
        public static extern uint MsiEnumRelatedProducts(string upgradeCode, uint reserved, uint index, StringBuilder productCode);
    }
}
'@
}
try {
    $productCode = Query 'SELECT `Value` FROM `Property` WHERE `Property` = ''ProductCode'''
    $upgradeCode = Query 'SELECT `Value` FROM `Property` WHERE `Property` = ''UpgradeCode'''
    if (-not $productCode -or -not $upgradeCode) { throw 'MSI product identity missing' }
    if ((Query 'SELECT `Value` FROM `Property` WHERE `Property` = ''ALLUSERS''') -ne '1') { throw 'MSI must install for all users' }
    if (-not (Query 'SELECT `Description` FROM `LaunchCondition` WHERE `Condition` = ''UILevel >= 4 OR Privileged''')) { throw 'MSI must explain silent installation elevation requirements before starting a transaction' }
    foreach ($sql in @(
        'SELECT `Control` FROM `Control` WHERE `Dialog_` = ''InstallOptionsDlg'' AND `Type` = ''PathEdit''',
        'SELECT `Control` FROM `Control` WHERE `Dialog_` = ''InstallOptionsDlg'' AND `Type` = ''CheckBox''',
        'SELECT `Control` FROM `Control` WHERE `Dialog_` = ''ProgressDlg'' AND `Type` = ''ProgressBar''',
        'SELECT `Name` FROM `Shortcut` WHERE `Shortcut` = ''DesktopLaunch''',
        'SELECT `Name` FROM `Shortcut` WHERE `Shortcut` = ''StartMenuLaunch''')) {
        if (-not (Query $sql)) { throw 'Standard MSI directory/shortcut/progress UI is incomplete' }
    }
    $report.checks += 'standard-MSI-dialogs-directory-checkbox-progress-shortcuts-per-machine'
    $report.productCode = $productCode
    if ($InstallSmoke) {
        # Never replace an installation that existed before this test.
        $related = [OhMyStudy.MsiTestNative]::MsiEnumRelatedProducts($upgradeCode, 0, 0, [Text.StringBuilder]::new(39))
        if ($related -ne 259) { throw 'A related product is already installed or could not be checked; isolated MSI smoke must not upgrade it' }
        $target = Join-Path $area '自选安装位置'
        $startMenu = Join-Path $area 'start-menu'
        $desktop = Join-Path $area 'desktop'
        $shortcutArguments = @("APPLICATIONSHORTCUTS=$startMenu","DESKTOPSHORTCUTS=$desktop")
        $arguments = @('/i',$msi,"INSTALLFOLDER=$target",'INSTALLDESKTOPSHORTCUT=1') + $shortcutArguments
        Run-Msi $arguments (Join-Path $area 'install.log')
        $installed = $true
        if (-not (Test-Path -LiteralPath (Join-Path $target 'OhMyStudy.App.exe'))) { throw 'MSI ignored the selected install directory' }
        if (-not @(Get-ChildItem -LiteralPath $desktop -Filter '*.lnk' -File).Count -or -not @(Get-ChildItem -LiteralPath $startMenu -Filter '*.lnk' -File).Count) { throw 'Selected shortcuts were not created' }
        if ($VerifyPayload) { & (Join-Path $PSScriptRoot 'Test-OfflinePackage.ps1') -PackageDirectory $target -ReportFile (Join-Path $area 'installed-payload.json') }
        if ($WorkflowSmoke) {
            $workflow = Join-Path $area 'installed-workflow.json'
            $process = Start-Process -FilePath (Join-Path $target 'OhMyStudy.App.exe') -ArgumentList @('--workflow-smoke',('"'+$workflow+'"')) -WindowStyle Hidden -PassThru
            if (-not $process.WaitForExit(90000) -or $process.ExitCode -ne 0 -or -not (Get-Content -LiteralPath $workflow -Raw | ConvertFrom-Json).Passed) { throw 'MSI-installed application workflow failed' }
        }
        $keep = Join-Path $target 'user-keep.txt'
        [IO.File]::WriteAllText($keep,'SYNTHETIC-USER-DATA')
        Run-Msi (@('/x',$productCode) + $shortcutArguments) (Join-Path $area 'uninstall.log')
        $installed = $false
        if ((Test-Path -LiteralPath (Join-Path $target 'OhMyStudy.App.exe')) -or [IO.File]::ReadAllText($keep) -ne 'SYNTHETIC-USER-DATA') { throw 'MSI uninstall failed to remove program files or preserve unrelated files' }
        if (@(Get-ChildItem -LiteralPath $area -Filter '*.lnk' -Recurse -File).Count) { throw 'MSI-owned shortcuts remained after uninstall' }
        $report.checks += 'actual-MSI-custom-location-shortcuts-install-uninstall-preserves-unlisted-data'
    }
    $report.checkStatus = 'selected-local-checks-passed'
} catch { $report.checkStatus='failed'; $report.error=$_.Exception.Message; throw }
finally {
    if ($installed -and $productCode) { Run-Msi (@('/x',$productCode) + $shortcutArguments) (Join-Path $area 'cleanup.log') }
    $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($windowsInstaller)
    New-Item -ItemType Directory -Path (Split-Path -Parent ([IO.Path]::GetFullPath($ReportFile))) -Force | Out-Null
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportFile -Encoding utf8
}
