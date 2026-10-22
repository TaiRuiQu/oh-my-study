function Test-MsiAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { return [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
    finally { $identity.Dispose() }
}

function Invoke-MsiTestElevation([string]$ScriptPath, [System.Collections.IDictionary]$Parameters) {
    if (Test-MsiAdministrator) { return $false }
    $request = @{ script = [IO.Path]::GetFullPath($ScriptPath); parameters = $Parameters } | ConvertTo-Json -Depth 6 -Compress
    $encodedRequest = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($request))
    $command = @"
`$ErrorActionPreference = 'Stop'
try {
    `$request = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$encodedRequest')) | ConvertFrom-Json -AsHashtable
    `$parameters = `$request.parameters
    & `$request.script @parameters
    exit 0
} catch { Write-Error `$_ -ErrorAction Continue; exit 1 }
"@
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    Write-Host 'Per-machine MSI tests require administrator privileges. Approve the Windows UAC prompt to continue.'
    try {
        $process = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList @('-NoProfile','-NonInteractive','-EncodedCommand',$encodedCommand)
    } catch { throw "Administrator approval was unavailable or cancelled. Run this test from an elevated PowerShell; silent per-machine MSI execution cannot request UAC. $($_.Exception.Message)" }
    try {
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Elevated MSI test failed (exit $($process.ExitCode)). Inspect the requested report and MSI logs." }
    } finally { $process.Dispose() }
    return $true
}
