#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Lint','Checks','Test','Build')][string]$Phase)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$savedLocation = Get-Location
$savedEnvironment = @{}
foreach ($name in @('DOTNET_CLI_HOME','NUGET_PACKAGES','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_GENERATE_ASPNET_CERTIFICATE')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet exited with $LASTEXITCODE" }
}
try {
    Set-Location -LiteralPath $workspace
    $env:DOTNET_CLI_HOME = Join-Path $workspace 'output/migration/dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $workspace 'output/migration/nuget'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    switch ($Phase) {
        'Lint' {
            Invoke-Dotnet @('format','whitespace','OhMyStudy.slnx','--verify-no-changes','--no-restore')
            $actionlint = & (Join-Path $PSScriptRoot 'prepare-ci-tools.ps1')
            & $actionlint '-color' '-shellcheck=' '-pyflakes=' '.github/workflows/ci.yml'
            if ($LASTEXITCODE -ne 0) { throw 'Workflow lint failed' }
        }
        'Checks' {
            & (Join-Path $PSScriptRoot 'Test-RepositoryLayout.ps1')
            foreach ($root in @('packaging','probes','.github')) {
                foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -Filter '*.ps1' -File) {
                    $tokens = $null; $errors = $null
                    [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors) | Out-Null
                    if ($errors.Count) { throw "PowerShell syntax failed: $($file.FullName): $($errors.Message -join '; ')" }
                }
            }
            $files = @(& git ls-files -- '*.json' '*.csproj' '*.props' '*.slnx' '*.xaml' '*.manifest' 'NuGet.Config')
            if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate tracked source files' }
            foreach ($file in $files) {
                $text = [IO.File]::ReadAllText((Join-Path $workspace $file))
                if ($file.EndsWith('.json', [StringComparison]::OrdinalIgnoreCase)) {
                    $json = [Text.Json.JsonDocument]::Parse($text); $json.Dispose()
                } else { $null = [xml]$text }
            }
            & (Join-Path $PSScriptRoot 'tests/Test-CIRelease.ps1')
            Invoke-Dotnet @('build','OhMyStudy.slnx','-c','Release','--no-restore','-warnaserror')
        }
        'Test' {
            # Windows hosted runners are server/headless environments. Desktop and physical
            # printing acceptance remain separate; every console suite still runs actual AOT.
            & (Join-Path $PSScriptRoot 'Invoke-MigrationChecks.ps1') -SkipApp
            $report = Get-Content -LiteralPath 'output/migration/latest-migration-run.json' -Raw | ConvertFrom-Json
            foreach ($name in @('domain','storage','provider','execution','grading','media','documents','infrastructure')) {
                if (-not @($report.checks | Where-Object { $_.name -eq $name -and $_.status -eq 'passed' }).Count) { throw "Required test suite did not pass: $name" }
            }
            if (@($report.notRun | Where-Object { $_ -like 'execution:*' }).Count) { throw 'Private computation runtime test was skipped' }
        }
        'Build' {
            & (Join-Path $PSScriptRoot 'Publish-OfflinePackage.ps1')
            $reportFile = Get-ChildItem -LiteralPath 'output/releases' -Filter 'package-report.json' -File -Recurse |
                Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
            if ($null -eq $reportFile) { throw 'Package report missing' }
            $report = Get-Content -LiteralPath $reportFile.FullName -Raw | ConvertFrom-Json
            if ($report.packageStatus -ne 'created-and-local-checks-passed') { throw 'Offline package verification failed' }
            $ref = if ($env:GITHUB_REF_TYPE -eq 'tag') { $env:GITHUB_REF_NAME } else { 'build-' + ($env:GITHUB_RUN_NUMBER ?? 'local') }
            $slug = ($ref -replace '[^A-Za-z0-9._-]', '-').Trim('.','-')
            if ([string]::IsNullOrWhiteSpace($slug)) { $slug = 'build' }
            if ($slug.Length -gt 80) { $slug = $slug.Substring(0,80) }
            $assets = Join-Path $workspace 'output/ci/artifacts'
            New-Item -ItemType Directory -Path $assets -Force | Out-Null
            if (@(Get-ChildItem -LiteralPath $assets -Force).Count) { throw 'CI artifact directory must be empty' }
            $name = "OhMyStudy-$slug-win-x64.zip"
            Copy-Item -LiteralPath $report.zip -Destination (Join-Path $assets $name)
            $digest = (Get-FileHash -LiteralPath (Join-Path $assets $name) -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($digest -ne $report.zipSha256) { throw 'Copied package hash changed' }
            [IO.File]::WriteAllText((Join-Path $assets 'SHA256SUMS.txt'), "$digest  $name`n", [Text.UTF8Encoding]::new($false))
            $commit = (& git rev-parse HEAD).Trim()
            if ($LASTEXITCODE -ne 0) { throw 'Cannot identify package source commit' }
            [ordered]@{ schemaVersion=1; commit=$commit; ref=$ref; package=$name; sha256=$digest;
                bytes=(Get-Item -LiteralPath (Join-Path $assets $name)).Length; validationStatus=$report.status;
                runUrl=if ($env:GITHUB_RUN_ID) { "$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID" } else { $null };
                notRun=$report.notRun } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $assets 'build-manifest.json') -Encoding utf8
            if ($env:GITHUB_OUTPUT) {
                "artifact-name=OhMyStudy-win-x64-${env:GITHUB_RUN_ID}-${env:GITHUB_RUN_ATTEMPT}" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
            }
            Write-Output "Offline artifacts: $assets"
        }
    }
} finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    Set-Location -LiteralPath $savedLocation
}
