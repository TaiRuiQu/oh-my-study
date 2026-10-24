#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$IconFile
)
$ErrorActionPreference = 'Stop'
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$iconPath = (Resolve-Path -LiteralPath $IconFile).Path
Add-Type -AssemblyName System.Drawing.Common
foreach ($size in @(16, 32, 48, 256)) {
    $expected = $actual = $expectedBitmap = $actualBitmap = $expectedStream = $actualStream = $null
    try {
        $expected = [Drawing.Icon]::ExtractIcon($iconPath, 0, $size)
        $actual = [Drawing.Icon]::ExtractIcon($executablePath, 0, $size)
        if ($null -eq $expected) { throw "Application icon cannot be read at ${size}px: $iconPath" }
        if ($null -eq $actual) { throw "Executable has no embedded application icon at ${size}px: $executablePath" }
        $expectedBitmap = $expected.ToBitmap()
        $actualBitmap = $actual.ToBitmap()
        $expectedStream = [IO.MemoryStream]::new()
        $actualStream = [IO.MemoryStream]::new()
        $expectedBitmap.Save($expectedStream, [Drawing.Imaging.ImageFormat]::Png)
        $actualBitmap.Save($actualStream, [Drawing.Imaging.ImageFormat]::Png)
        $expectedHash = [Convert]::ToBase64String([Security.Cryptography.SHA256]::HashData($expectedStream.ToArray()))
        $actualHash = [Convert]::ToBase64String([Security.Cryptography.SHA256]::HashData($actualStream.ToArray()))
        if ($actualHash -cne $expectedHash) { throw "Executable icon differs from $iconPath at ${size}px: $executablePath" }
    } finally {
        foreach ($resource in @($expected, $actual, $expectedBitmap, $actualBitmap, $expectedStream, $actualStream)) {
            if ($null -ne $resource) { $resource.Dispose() }
        }
    }
}
Write-Host "PASS embedded application icon at 16, 32, 48 and 256px: $executablePath"
