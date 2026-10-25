#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Lint','Build')][string]$Phase,
    [ValidateSet('aot','self-contained','framework-dependent')][string]$Variant = 'aot',
    [string]$BaselinePackage,
    [switch]$FullValidation)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$savedLocation = Get-Location
$savedEnvironment = @{}
foreach ($name in @('DOTNET_CLI_HOME','NUGET_PACKAGES','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_GENERATE_ASPNET_CERTIFICATE')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    Set-Location -LiteralPath $workspace
    $env:DOTNET_CLI_HOME = Join-Path $workspace 'output/migration/dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $workspace 'output/migration/nuget'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    switch ($Phase) {
        'Lint' {
            & (Join-Path $PSScriptRoot 'Invoke-Lint.ps1')
        }
        'Build' {
            $releaseOutput=Join-Path $workspace ('output/releases/'+$Variant)
            & (Join-Path $PSScriptRoot 'Publish-OfflinePackage.ps1') -Variant $Variant -OutputDirectory $releaseOutput -BaselinePackage $BaselinePackage
            $reportFile = Get-ChildItem -LiteralPath $releaseOutput -Filter 'package-report.json' -File -Recurse |
                Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
            if ($null -eq $reportFile) { throw 'Package report missing' }
            $report = Get-Content -LiteralPath $reportFile.FullName -Raw | ConvertFrom-Json
            if ($report.packageStatus -ne 'created' -or $report.variant -ne $Variant) { throw 'Package creation failed' }
            $ref = if ($env:RELEASE_TAG) { $env:RELEASE_TAG } elseif ($env:GITHUB_REF_TYPE -eq 'tag') { $env:GITHUB_REF_NAME } else { 'build-' + ($env:GITHUB_RUN_NUMBER ?? 'local') }
            $slug = ($ref -replace '[^A-Za-z0-9._-]', '-').Trim('.','-')
            if ([string]::IsNullOrWhiteSpace($slug)) { $slug = 'build' }
            if ($slug.Length -gt 80) { $slug = $slug.Substring(0,80) }
            $assets = Join-Path $workspace ('output/ci/artifacts/'+$Variant)
            New-Item -ItemType Directory -Path $assets -Force | Out-Null
            if (@(Get-ChildItem -LiteralPath $assets -Force).Count) { throw 'CI artifact directory must be empty' }
            $packages=@()
            $items=@(
                @{kind='portable'; source=$report.zip; expected=$report.zipSha256; name="OhMyStudy-$slug-win-x64-$Variant-portable.zip"},
                @{kind='installer'; source=$report.installer; expected=$report.installerSha256; name="OhMyStudy-$slug-win-x64-$Variant-Setup.exe"},
                @{kind='full'; source=$report.fullPackage; expected=$report.fullPackageSha256; name=[IO.Path]::GetFileName($report.fullPackage)},
                @{kind='feed'; source=$report.feed; expected=$report.feedSha256; name=[IO.Path]::GetFileName($report.feed)},
                @{kind='legacyFeed'; source=$report.legacyFeed; expected=$report.legacyFeedSha256; name=[IO.Path]::GetFileName($report.legacyFeed)}
            )
            if ($report.deltaPackage) { $items+=@{kind='delta'; source=$report.deltaPackage; expected=$report.deltaPackageSha256; name=[IO.Path]::GetFileName($report.deltaPackage)} }
            foreach ($item in $items) {
                $path=Join-Path $assets $item.name
                Copy-Item -LiteralPath $item.source -Destination $path
                $digest = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($digest -cne $item.expected) { throw 'Copied package hash changed' }
                $packages += [ordered]@{ variant=$Variant; channel=$report.channel; kind=$item.kind; name=$item.name; sha256=$digest; bytes=(Get-Item -LiteralPath $path).Length }
            }
            $commit = (& git rev-parse HEAD).Trim()
            if ($LASTEXITCODE -ne 0) { throw 'Cannot identify package source commit' }
            [ordered]@{ schemaVersion=3; commit=$commit; ref=$ref; variant=$Variant; channel=$report.channel; version=$report.version; packages=$packages; validationStatus=$report.status;
                runUrl=if ($env:GITHUB_RUN_ID) { "$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID" } else { $null };
                notRun=$report.notRun } | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $assets 'build-manifest.json') -Encoding utf8
            if ($env:GITHUB_OUTPUT) {
                "artifact-name=OhMyStudy-win-x64-$Variant-${env:GITHUB_RUN_ID}-${env:GITHUB_RUN_ATTEMPT}" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
            }
            Write-Output "Offline artifacts: $assets"
        }
    }
} finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    Set-Location -LiteralPath $savedLocation
}
