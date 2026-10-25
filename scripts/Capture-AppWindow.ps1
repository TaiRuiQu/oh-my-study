param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][string]$Destination,
    [string[]]$ArgumentList = @(),
    [double]$DelaySeconds = 2,
    [scriptblock]$PrepareWindow
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class StudyWindowCapture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr device, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
}
'@

$taskStart = @{
    FilePath = (Resolve-Path -LiteralPath $ExecutablePath).Path
    WorkingDirectory = Split-Path -Parent (Resolve-Path -LiteralPath $ExecutablePath).Path
    WindowStyle = 'Hidden'
    PassThru = $true
}
if ($ArgumentList.Count -gt 0) { $taskStart.ArgumentList = $ArgumentList }
$taskProcess = Start-Process @taskStart
try {
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $taskProcess.Refresh()
        if ($taskProcess.HasExited) { throw "The app exited before opening a window. Exit code: $($taskProcess.ExitCode)." }
        if ($taskProcess.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $taskDeadline)
    if ($taskProcess.MainWindowHandle -eq 0) { throw 'The app did not open a main window within 30 seconds.' }
    [StudyWindowCapture]::ShowWindow($taskProcess.MainWindowHandle, 5) | Out-Null
    [StudyWindowCapture]::SetForegroundWindow($taskProcess.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds ([int]($DelaySeconds * 1000))
    if ($PrepareWindow) { & $PrepareWindow $taskProcess }
    $taskProcess.Refresh()
    $taskWindow = $taskProcess.MainWindowHandle
    $taskRectangle = New-Object StudyWindowCapture+Rect
    if (-not [StudyWindowCapture]::GetWindowRect($taskWindow, [ref]$taskRectangle)) { throw 'Could not obtain the app window bounds.' }
    $taskBitmap = [System.Drawing.Bitmap]::new($taskRectangle.Right - $taskRectangle.Left, $taskRectangle.Bottom - $taskRectangle.Top)
    try {
        $taskGraphics = [System.Drawing.Graphics]::FromImage($taskBitmap)
        try {
            $taskDevice = $taskGraphics.GetHdc()
            try { $taskCaptured = [StudyWindowCapture]::PrintWindow($taskWindow, $taskDevice, 2) }
            finally { $taskGraphics.ReleaseHdc($taskDevice) }
        }
        finally { $taskGraphics.Dispose() }
        if (-not $taskCaptured) { throw 'PrintWindow failed.' }
        $taskDestination = [System.IO.Path]::GetFullPath($Destination)
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($taskDestination)) | Out-Null
        $taskBitmap.Save($taskDestination, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Output $taskDestination
    }
    finally { $taskBitmap.Dispose() }
}
finally {
    if (-not $taskProcess.HasExited) {
        $taskProcess.CloseMainWindow() | Out-Null
        if (-not $taskProcess.WaitForExit(5000)) {
            $taskProcess.Kill()
            $taskProcess.WaitForExit()
        }
    }
    Write-Output "Closed app process $($taskProcess.Id)."
    $taskProcess.Dispose()
}
