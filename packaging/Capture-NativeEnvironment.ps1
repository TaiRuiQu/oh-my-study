#requires -Version 7.0
[CmdletBinding()]
param([string]$OutputFile = (Join-Path $PSScriptRoot '../output/migration/environment.json'))
$ErrorActionPreference = 'Stop'
$report = [ordered]@{ timestampUtc=[DateTimeOffset]::UtcNow.ToString('O'); windowsVersion=[Environment]::OSVersion.VersionString; architecture=[System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString(); limitations=@() }
foreach ($entry in @(
    @{key='operatingSystem';class='Win32_OperatingSystem';fields=@('Caption','Version','BuildNumber','OSArchitecture')},
    @{key='cpu';class='Win32_Processor';fields=@('Name','NumberOfCores','NumberOfLogicalProcessors')},
    @{key='memory';class='Win32_ComputerSystem';fields=@('TotalPhysicalMemory')},
    @{key='gpu';class='Win32_VideoController';fields=@('Name','DriverVersion','CurrentHorizontalResolution','CurrentVerticalResolution','CurrentRefreshRate')}
)) {
    try { $report[$entry.key] = @(Get-CimInstance -ClassName $entry.class -ErrorAction Stop | Select-Object -Property $entry.fields) }
    catch { $report.limitations += "CIM $($entry.class): unavailable ($($_.Exception.GetType().Name))" }
}
$report.limitations += 'A single development machine is not the clean Windows 10/11, 8 GB reference, DPI, accessibility or performance acceptance matrix.'
New-Item -ItemType Directory -Path (Split-Path -Parent ([IO.Path]::GetFullPath($OutputFile))) -Force | Out-Null
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputFile -Encoding utf8
Write-Output ([IO.Path]::GetFullPath($OutputFile))
