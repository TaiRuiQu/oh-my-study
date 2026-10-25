#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$forbiddenExtensions = @('.rs','.ts','.tsx','.js','.jsx','.mjs','.cjs')
$forbiddenNames = @('Cargo.toml','Cargo.lock','package.json','package-lock.json','yarn.lock','pnpm-lock.yaml')
$mobileScripts = @('capture.js','paper-detector.js','paper-worker.js') | ForEach-Object {
    [IO.Path]::GetFullPath((Join-Path $workspace "src/OhMyStudy.App/MobileCapture/$_"))
}
$sourceCount = 0
$projectCount = 0
$queue = [Collections.Generic.Queue[string]]::new()
$queue.Enqueue($workspace)
while ($queue.Count -gt 0) {
    foreach ($entry in Get-ChildItem -LiteralPath $queue.Dequeue() -Force) {
        if ($entry.PSIsContainer) {
            if ($entry.Name -in @('.git','.reference','output','tmp','.artifacts','bin','obj','.vs','TestResults','__pycache__')) { continue }
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Unexpected linked source directory: $($entry.FullName)" }
            $queue.Enqueue($entry.FullName)
            continue
        }
        if (($entry.Extension -in $forbiddenExtensions -or $entry.Name -in $forbiddenNames) -and $entry.FullName -notin $mobileScripts) {
            throw "Legacy implementation file outside .reference: $($entry.FullName)"
        }
        if ($entry.Extension -eq '.cs') { $sourceCount++ }
        if ($entry.Extension -in @('.csproj','.props','.targets','.slnx')) {
            $projectCount++
            $text = Get-Content -LiteralPath $entry.FullName -Raw
            if ($text -match '[\\/]\.reference[\\/]' -or $text -match 'src-windows' -or $text -match 'src-tauri') {
                throw "Build input references the archived implementation: $($entry.FullName)"
            }
        }
    }
}
$ignore = Get-Content -LiteralPath (Join-Path $workspace '.gitignore')
if ('/.reference/' -notin $ignore) { throw '.reference must be ignored at the repository root' }
if (Get-Command git -ErrorAction SilentlyContinue) {
    $tracked = @(& git -C $workspace ls-files -- .reference)
    if ($LASTEXITCODE -ne 0 -or $tracked.Count -ne 0) { throw '.reference has tracked files or Git could not verify the index' }
}
Write-Output "PASS C# repository layout: $sourceCount C# files, $projectCount project/build files; .reference is ignored and isolated"
