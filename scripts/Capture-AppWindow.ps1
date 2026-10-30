param(
    [string]$ExecutablePath,
    [string]$Destination,
    [string[]]$ArgumentList = @(),
    [double]$DelaySeconds = 2,
    [int]$ProcessId = 0,
    [int]$Width = 0,
    [int]$Height = 0,
    [switch]$Cursor,
    [scriptblock]$PrepareWindow
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'tools/ScreenCapture/ScreenCapture.csproj'
if (-not (Test-Path -LiteralPath $project)) { throw 'The screen capture project is missing.' }
if ($Destination -eq '') { throw 'Provide -Destination for the PNG output.' }
if ($ProcessId -eq 0 -and $ExecutablePath -eq '') { throw 'Provide -ExecutablePath or -ProcessId.' }

$startedProcess = $null
try {
    if ($ProcessId -eq 0) {
        $resolved = (Resolve-Path -LiteralPath $ExecutablePath).Path
        $startInfo = @{
            FilePath = $resolved
            WorkingDirectory = Split-Path -Parent $resolved
            WindowStyle = 'Hidden'
            PassThru = $true
        }
        if ($ArgumentList.Count -gt 0) { $startInfo.ArgumentList = $ArgumentList }
        $startedProcess = Start-Process @startInfo
        if ($PrepareWindow) { & $PrepareWindow $startedProcess }
        $ProcessId = $startedProcess.Id
    }
    $arguments = @('run', '--project', $project, '-c', 'Release', '--', '--process', $ProcessId.ToString(),
        '--destination', $Destination, '--delay-ms', ([int]($DelaySeconds * 1000)).ToString())
    if ($Width -gt 0) { $arguments += '--width'; $arguments += $Width.ToString() }
    if ($Height -gt 0) { $arguments += '--height'; $arguments += $Height.ToString() }
    if ($Cursor) { $arguments += '--cursor' }
    $build = dotnet build $project -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "The screen capture project did not build.`n$build" }
    $output = & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Screen capture failed.`n$output" }
    Write-Output $output
}
finally {
    if ($null -ne $startedProcess -and -not $startedProcess.HasExited) {
        $startedProcess.CloseMainWindow() | Out-Null
        if (-not $startedProcess.WaitForExit(5000)) {
            $startedProcess.Kill()
            $startedProcess.WaitForExit()
        }
    }
    if ($null -ne $startedProcess) {
        Write-Output "Closed app process $($startedProcess.Id)."
        $startedProcess.Dispose()
    }
}
