#requires -Version 7.0

function Get-ReleaseExclusionReason([string]$RelativePath) {
    $path = $RelativePath.Replace('\','/')
    if ($path -match '(?i)\.(pdb|dbg|ilk|ipdb|iobj|exp|lib)$|\.dSYM(/|$)') { return 'debug-symbol-or-linker-output' }
    if ($path -match '(?i)^runtime/python/(?:.*/)?__pycache__/|^runtime/python/.*\.(pyc|pyo)$') { return 'python-bytecode-cache' }
    if ($path -match '(?i)^runtime/python/Lib/site-packages/numpy/_core/tests/(?:__init__|_natype)\.py$') { return $null }
    if ($path -match '(?i)^runtime/python/Lib/site-packages/(?:[^/]+/)+tests/') { return 'python-package-tests' }
    return $null
}

function Copy-ReleasePayload([string]$Source, [string]$Destination, [string]$Prefix = '') {
    $sourceRoot = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\','/')
    $targetRoot = [IO.Path]::GetFullPath($Destination).TrimEnd('\','/')
    if ($targetRoot.Equals($sourceRoot, [StringComparison]::OrdinalIgnoreCase) -or $targetRoot.StartsWith($sourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release payload destination must be outside its source' }
    for ($ancestor = Get-Item -LiteralPath $sourceRoot; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked release source rejected: $($ancestor.FullName)" }
    }
    $entries = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -Force)
    foreach ($entry in $entries) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked release input rejected: $($entry.FullName)" }
    }
    New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null
    foreach ($file in $entries | Where-Object { -not $_.PSIsContainer }) {
        $relative = [IO.Path]::GetRelativePath($sourceRoot, $file.FullName).Replace('\','/')
        $payloadPath = if ($Prefix) { $Prefix.TrimEnd('/') + '/' + $relative } else { $relative }
        $reason = Get-ReleaseExclusionReason $payloadPath
        if ($reason) {
            [pscustomobject]@{ path = $payloadPath; bytes = $file.Length; reason = $reason }
            continue
        }
        $target = Join-Path $targetRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}

function Assert-ReleasePayload([string]$Root) {
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\','/')
        $reason = Get-ReleaseExclusionReason $relative
        if ($reason) { throw "Development file in release payload ($reason): $relative" }
    }
}

function Get-ZipEntryHash([System.IO.Compression.ZipArchiveEntry]$Entry) {
    $stream = $Entry.Open()
    try { return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
    finally { $stream.Dispose() }
}

function Copy-ReleaseDependencyMetadata([string]$Source, [string]$Destination) {
    $name = [IO.Path]::GetFileName($Source)
    if ([IO.Path]::GetExtension($name).Equals('.nuspec', [StringComparison]::OrdinalIgnoreCase)) { $name += '.xml' }
    Copy-Item -LiteralPath $Source -Destination (Join-Path $Destination $name)
}

function Assert-VelopackPackageManifest([string]$Path, [string]$PackId, [string]$Version, [string]$Channel) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $manifests = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) })
        if ($manifests.Count -ne 1 -or $manifests[0].FullName -cne "$PackId.nuspec") { throw "Ambiguous or missing Velopack manifest in package: $Path" }
        $reader = [IO.StreamReader]::new($manifests[0].Open())
        try { $manifest = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
        $expected = @{ id = $PackId; version = $Version; channel = $Channel; mainExe = 'OhMyStudy.App.exe' }
        foreach ($key in $expected.Keys) {
            $node = $manifest.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='$key']")
            if ($null -eq $node -or $node.InnerText -cne $expected[$key]) { throw "Invalid Velopack manifest property '$key' in package: $Path" }
        }
    } finally { $archive.Dispose() }
}

function Assert-NupkgContentEqual([string]$Expected, [string]$Actual) {
    $expectedArchive = [System.IO.Compression.ZipFile]::OpenRead($Expected)
    $actualArchive = [System.IO.Compression.ZipFile]::OpenRead($Actual)
    try {
        $expectedEntries = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $expectedArchive.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            if (-not $expectedEntries.TryAdd($entry.FullName, (Get-ZipEntryHash $entry))) { throw "Duplicate package entry: $($entry.FullName)" }
        }
        $actualEntries = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $actualArchive.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            if (-not $actualEntries.TryAdd($entry.FullName, (Get-ZipEntryHash $entry))) { throw "Duplicate package entry: $($entry.FullName)" }
        }
        if ($expectedEntries.Count -ne $actualEntries.Count) { throw 'Delta reconstruction file count differs from the full package' }
        foreach ($pair in $expectedEntries.GetEnumerator()) {
            $hash = $null
            if (-not $actualEntries.TryGetValue($pair.Key, [ref]$hash) -or $hash -cne $pair.Value) { throw "Delta reconstruction differs from the full package: $($pair.Key)" }
        }
    } finally {
        $expectedArchive.Dispose()
        $actualArchive.Dispose()
    }
}

function Update-ReleaseRuntimeManifest([string]$RuntimeDirectory) {
    $path = Join-Path $RuntimeDirectory 'runtime-manifest.json'
    $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $manifest.files = @($manifest.files | Where-Object { -not (Get-ReleaseExclusionReason ('runtime/' + $_.path)) })
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
}
