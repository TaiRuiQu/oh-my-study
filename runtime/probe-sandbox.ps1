# Read-only M0 inventory. Never enables features, elevates, starts or stops Sandbox.
[CmdletBinding()]
param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$evidence = [ordered]@{ schema_version=1; state='unknown'; reason='Read-only inventory cannot prove startup/bridge readiness'; launch_tested=$false; observations=[ordered]@{}; errors=@() }
function Observe([string]$Name, [scriptblock]$Read) {
    try { $evidence.observations[$Name] = & $Read }
    catch { $evidence.errors += "$Name`: $($_.Exception.Message)" }
}
Observe 'os' { Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber,OperatingSystemSKU,TotalVisibleMemorySize }
Observe 'cpu' { @(Get-CimInstance Win32_Processor | Select-Object NumberOfCores,VirtualizationFirmwareEnabled,SecondLevelAddressTranslationExtensions,VMMonitorModeExtensions) }
Observe 'computer' { Get-CimInstance Win32_ComputerSystem | Select-Object HypervisorPresent,TotalPhysicalMemory }
Observe 'feature' { Get-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM | Select-Object FeatureName,State }
Observe 'sandbox_executable' { Test-Path -LiteralPath (Join-Path $env:SystemRoot 'System32/WindowsSandbox.exe') }
Observe 'existing_sessions' { @(Get-Process -Name WindowsSandbox,WindowsSandboxClient -ErrorAction SilentlyContinue | Select-Object Id,ProcessName) }
Observe 'policy' { if (Test-Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Sandbox') { Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Sandbox' | Select-Object AllowWindowsSandbox } else { $null } }
Observe 'system_drive_free_bytes' { (Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$($env:SystemDrive)'").FreeSpace }
$obs = $evidence.observations
if ($obs.os -and ([int]$obs.os.BuildNumber -lt 18362 -or [int]$obs.os.OperatingSystemSKU -in @(2,3,5,26,47,98,99,100,101))) {
    $evidence.state='unsupported'; $evidence.reason='Windows version/edition does not support Windows Sandbox'
} elseif ($obs.policy -and $obs.policy.AllowWindowsSandbox -eq 0) {
    $evidence.state='policy_blocked'; $evidence.reason='Windows Sandbox is blocked by policy'
} elseif ($obs.existing_sessions.Count -gt 0) {
    $evidence.state='busy'; $evidence.reason='Existing Sandbox process observed; do not take ownership or close it'
} elseif ($obs.feature -and [string]$obs.feature.State -match 'Pending') {
    $evidence.state='needs_reboot'; $evidence.reason='Optional feature reports pending transition/restart'
} elseif ($obs.feature -and [string]$obs.feature.State -ne 'Enabled') {
    $evidence.state='needs_enable'; $evidence.reason='Sandbox optional feature is not enabled'
} elseif ($obs.cpu -and $obs.computer -and $obs.computer.HypervisorPresent -eq $false -and @($obs.cpu | Where-Object { $_.VirtualizationFirmwareEnabled -eq $false }).Count -gt 0) {
    $evidence.state='virtualization_unavailable'; $evidence.reason='Firmware virtualization is not available according to CIM; verify nested virtualization separately'
}
$json = $evidence | ConvertTo-Json -Depth 10
if ($OutputPath) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $json, [Text.UTF8Encoding]::new($false)) }
$json
