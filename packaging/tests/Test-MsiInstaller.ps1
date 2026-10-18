#requires -Version 7.0
[CmdletBinding()]
param([string]$ReportFile)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$area = Join-Path $workspace ('output/ci/msi-regression-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $area -Force | Out-Null
if (-not $ReportFile) { $ReportFile = Join-Path $area 'verification.json' }
$key = 'Software\OhMyStudy\InstallerTests\' + [guid]::NewGuid().ToString('N')
$upgrade = [guid]::NewGuid()
$name = 'Oh My Study 测试 ' + $upgrade.ToString('N').Substring(0,8)
$checks = [Collections.Generic.List[string]]::new()
$installedMsi = $null
$shortcuts = @("APPLICATIONSHORTCUTS=$(Join-Path $area 'start')","DESKTOPSHORTCUTS=$(Join-Path $area 'desktop')")
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message }; $checks.Add($Message) }
function Package([string]$Version, [string]$Content) {
    $root = Join-Path $area ("payload-$Version-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path (Join-Path $root 'native/字体') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'OhMyStudy.App.exe'), $Content)
    [IO.File]::WriteAllText((Join-Path $root 'native/字体/许可.txt'), 'SYNTHETIC LICENSE ' + $Content)
    $files = @(Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object { @{path=[IO.Path]::GetRelativePath($root,$_.FullName).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} })
    @{ version=$Version; files=$files } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'package-manifest.json')
    $msi = Join-Path $area ("$Version-" + [guid]::NewGuid().ToString('N') + '.msi')
    $null = & (Join-Path $PSScriptRoot '../Build-MsiPackage.ps1') -PackageDirectory $root -OutputFile $msi -ProductName $name -UpgradeCode $upgrade -RegistryKey $key
    return $msi
}
function Execute([string[]]$Arguments, [string]$Label, [bool]$Success = $true) {
    $start = [Diagnostics.ProcessStartInfo]::new('msiexec.exe')
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    # msiexec needs PROPERTY="value with spaces" rather than "PROPERTY=value with spaces".
    $start.Arguments = (($Arguments + $shortcuts + @('/qn','/norestart','/L*v',(Join-Path $area "$Label.log"))) | ForEach-Object {
        if ($_.Contains('"')) { throw 'Unexpected quote in MSI test argument' }
        if ($_ -match '^([A-Z][A-Z0-9_]*)=(.*)$') { $Matches[1] + '="' + $Matches[2].TrimEnd('\') + '"' }
        elseif ($_.StartsWith('/')) { $_ } else { '"' + $_ + '"' }
    }) -join ' '
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(120000)) { throw "MSI timeout: $Label" }
        if ($Success -and $process.ExitCode -notin @(0,3010) -or -not $Success -and $process.ExitCode -in @(0,3010)) { throw "Unexpected MSI exit $($process.ExitCode): $Label" }
    } finally { $process.Dispose() }
}
$report = [ordered]@{schemaVersion=1; checkStatus='running'; evidence=$area; checks=$checks}
try {
    $v1 = Package '0.1.1' 'VERSION_ONE'
    & (Join-Path $PSScriptRoot '../Test-MsiPackage.ps1') -InstallerMsi $v1 -ReportFile (Join-Path $area 'standard-and-install.json') -InstallSmoke
    $checks.Add('standard wizard, custom directory, desktop and start menu, uninstall and unrelated file preservation')
    $target = Join-Path $area '中文 自选位置'
    # An unchecked UI choice is empty. The marker prevents execute-sequence defaults from re-checking it.
    Execute @('/i',$v1,"INSTALLFOLDER=$target",'DESKTOPCHOICESET=1') 'unchecked-install'
    $installedMsi=$v1
    Assert ((Test-Path -LiteralPath (Join-Path $target 'OhMyStudy.App.exe')) -and -not (Test-Path -LiteralPath (Join-Path $area "desktop/$name.lnk"))) 'unchecked desktop choice is respected'
    Assert ((Get-ItemPropertyValue -LiteralPath "HKLM:\$key" -Name DesktopShortcut) -eq '0') 'unchecked preference is saved as zero'
    $v2 = Package '0.1.2' 'VERSION_TWO'
    Execute @('/i',$v2) 'upgrade'
    $installedMsi=$v2
    Assert ([IO.File]::ReadAllText((Join-Path $target 'OhMyStudy.App.exe')) -eq 'VERSION_TWO') 'upgrade remembers the custom location and replaces program files'
    Assert (-not (Test-Path -LiteralPath (Join-Path $area "desktop/$name.lnk"))) 'upgrade preserves desktop opt-out'
    Execute @('/i',$v1) 'downgrade-rejected' $false
    Assert ([IO.File]::ReadAllText((Join-Path $target 'OhMyStudy.App.exe')) -eq 'VERSION_TWO') 'downgrade refusal preserves the installed version'
    $bad = Package '0.1.3' 'FAILED_VERSION'
    # Inject an MSI error action into this disposable test package only; production authoring has no fault hook.
    $testInstaller = New-Object -ComObject WindowsInstaller.Installer
    $testDatabase = $testInstaller.OpenDatabase($bad,1)
    try {
        foreach ($sql in @(
            'INSERT INTO `CustomAction` (`Action`,`Type`,`Target`) VALUES (''SyntheticFailure'',19,''Synthetic rollback test'')',
            'INSERT INTO `InstallExecuteSequence` (`Action`,`Condition`,`Sequence`) VALUES (''SyntheticFailure'',''1'',4001)')) {
            $view = $testDatabase.OpenView($sql)
            try { $null=$view.Execute() } finally { $null=$view.Close(); $null=[Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
        }
        $null=$testDatabase.Commit()
    } finally { $null=[Runtime.InteropServices.Marshal]::FinalReleaseComObject($testDatabase); $null=[Runtime.InteropServices.Marshal]::FinalReleaseComObject($testInstaller) }
    $installedMsi=$bad
    Execute @('/i',$bad) 'upgrade-rollback' $false
    $installedMsi=$v2
    Assert ([IO.File]::ReadAllText((Join-Path $target 'OhMyStudy.App.exe')) -eq 'VERSION_TWO') 'failed major upgrade restores the old program files'
    Assert ((Get-ItemPropertyValue -LiteralPath "HKLM:\$key" -Name DesktopShortcut) -eq '0') 'failed upgrade restores saved options'
    $sameVersion = Package '0.1.2' 'SWITCHED_VARIANT'
    Execute @('/i',$sameVersion,'INSTALLDESKTOPSHORTCUT=1') 'same-version-variant'
    $installedMsi=$sameVersion
    Assert ([IO.File]::ReadAllText((Join-Path $target 'OhMyStudy.App.exe')) -eq 'SWITCHED_VARIANT') 'same-version package replacement supports changing variants'
    Assert (Test-Path -LiteralPath (Join-Path $area "desktop/$name.lnk")) 'desktop opt-in creates the shortcut'
    $keep = Join-Path $target 'my-notes.txt'; [IO.File]::WriteAllText($keep,'KEEP_PRIVATE')
    Execute @('/x',$installedMsi) 'uninstall'
    $installedMsi=$null
    Assert (-not (Test-Path -LiteralPath (Join-Path $target 'OhMyStudy.App.exe')) -and [IO.File]::ReadAllText($keep) -eq 'KEEP_PRIVATE') 'uninstall removes program files and preserves unrelated files'
    Assert (-not (Test-Path -LiteralPath "HKLM:\$key") -and -not @(Get-ChildItem -LiteralPath $area -Filter '*.lnk' -Recurse -File).Count) 'uninstall removes MSI settings and owned shortcuts'
    $report.checkStatus='selected-local-checks-passed'
} catch { $report.checkStatus='failed'; $report.error=$_.Exception.Message; throw }
finally {
    if ($installedMsi) { Execute @('/x',$installedMsi) 'cleanup' }
    New-Item -ItemType Directory -Path (Split-Path -Parent ([IO.Path]::GetFullPath($ReportFile))) -Force | Out-Null
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportFile -Encoding utf8
}
Write-Output "PASS MSI regression: $($checks.Count) checks; $ReportFile"
