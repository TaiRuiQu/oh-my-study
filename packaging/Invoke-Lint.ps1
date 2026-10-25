#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$start = [Diagnostics.ProcessStartInfo]::new('git')
$start.WorkingDirectory = $workspace
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in @('-c','core.autocrlf=false','diff','--check','HEAD')) { $start.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($start)
try {
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(25000)) {
        $process.Kill($true)
        throw 'Local lint stopped before the 30-second limit.'
    }
    $output.GetAwaiter().GetResult() | Write-Output
    $errors.GetAwaiter().GetResult() | Write-Output
    if ($process.ExitCode -ne 0) { throw "Local whitespace lint failed with exit code $($process.ExitCode)" }
} finally { $process.Dispose() }
