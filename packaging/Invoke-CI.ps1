#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Lint','Checks','Test','Build')][string]$Phase,
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
function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet exited with $LASTEXITCODE" }
}
function Invoke-CountedCheck([string]$Name, [scriptblock]$Action) {
    $lines = @(& $Action 2>&1)
    $lines | Out-Host
    $text = $lines -join "`n"
    $count = 1
    if ($text -match '(?:# tests|ℹ tests)\s+(\d+)') { $count = [int]$Matches[1] }
    elseif ($text -match 'Ran (\d+) tests?') { $count = [int]$Matches[1] }
    return [ordered]@{ name=$Name; testCount=$count }
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
            $checkCounts = [Collections.Generic.List[object]]::new()
            & (Join-Path $PSScriptRoot 'Test-RepositoryLayout.ps1')
            foreach ($root in @('packaging','probes','.github')) {
                foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -Filter '*.ps1' -File) {
                    $tokens = $null; $errors = $null
                    [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors) | Out-Null
                    if ($errors.Count) { throw "PowerShell syntax failed: $($file.FullName): $($errors.Message -join '; ')" }
                }
            }
            $files = @(& git ls-files --cached --others --exclude-standard -- '*.json' '*.csproj' '*.props' '*.slnx' '*.xaml' '*.manifest' 'NuGet.Config')
            if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate tracked source files' }
            $files = @($files | Where-Object { Test-Path -LiteralPath (Join-Path $workspace $_) -PathType Leaf })
            foreach ($file in $files) {
                $text = [IO.File]::ReadAllText((Join-Path $workspace $file))
                if ($file.EndsWith('.json', [StringComparison]::OrdinalIgnoreCase)) {
                    $json = [Text.Json.JsonDocument]::Parse($text); $json.Dispose()
                } else { $null = [xml]$text }
            }
            foreach ($name in @('CIRelease','VelopackBaseline','ReleasePayload','VelopackManifest','ScannerAssets','PhoneCaptureMobile')) {
                $checkCounts.Add((Invoke-CountedCheck $name { & (Join-Path $PSScriptRoot "tests/Test-$name.ps1") }))
            }
            $docresPython = Join-Path $workspace 'output/migration/toolchain/runtime/python/python.exe'
            if (-not (Test-Path -LiteralPath $docresPython -PathType Leaf)) {
                $found = Get-Command python -ErrorAction SilentlyContinue
                if (-not $found) { throw 'Python is required to check DocRes install archive cleanup. Prepare the private runtime or install Python.' }
                $docresPython = $found.Source
            }
            foreach ($name in @('DocResInstall','DocResPipeline')) {
                $checkCounts.Add((Invoke-CountedCheck $name {
                    & $docresPython (Join-Path $PSScriptRoot "tests/Test-$name.py")
                    if ($LASTEXITCODE -ne 0) { throw "$name check failed" }
                }))
            }
            Invoke-Dotnet @('build','OhMyStudy.slnx','-c','Release','--no-restore','-warnaserror')
            $checkCounts | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $workspace 'output/ci/packaging-test-counts.json') -Encoding utf8
        }
        'Test' {
            & (Join-Path $PSScriptRoot 'Invoke-MigrationChecks.ps1') -SkipApp -NoRestore -ManagedSdkTests:(!$FullValidation)
            $report = Get-Content -LiteralPath 'output/migration/latest-migration-run.json' -Raw | ConvertFrom-Json
            foreach ($name in @('domain','storage','provider','execution','grading','media','documents','infrastructure')) {
                if (-not @($report.checks | Where-Object { $_.name -eq $name -and $_.status -eq 'passed' }).Count) { throw "Required test suite did not pass: $name" }
            }
            if (@($report.notRun | Where-Object { $_ -like 'execution:*' }).Count) { throw 'Private computation runtime test was skipped' }
            $installerCount = 0
            if ($FullValidation) {
                & (Join-Path $PSScriptRoot 'tests/Test-VelopackInstaller.ps1') -ReportFile (Join-Path $workspace 'output/ci/velopack-regression.json')
                $installerCount = @((Get-Content -LiteralPath (Join-Path $workspace 'output/ci/velopack-regression.json') -Raw | ConvertFrom-Json).checks).Count
            }
            $packaging = @()
            $packagingReport = Join-Path $workspace 'output/ci/packaging-test-counts.json'
            if (Test-Path -LiteralPath $packagingReport) { $packaging = @(Get-Content -LiteralPath $packagingReport -Raw | ConvertFrom-Json) }
            $packagingCount = ($packaging | Measure-Object -Property testCount -Sum).Sum
            $total = [int]$report.testCount + [int]$packagingCount + $installerCount
            [ordered]@{ limit=300; total=$total; regressions=$report.testCount; packaging=$packaging; installer=$installerCount } |
                ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $workspace 'output/ci/test-budget.json') -Encoding utf8
            if ($total -gt 300) { throw "Complete CI test budget exceeded: $total > 300" }
            Write-Host "PASS complete CI test budget: $total / 300"
            if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "Complete CI: $total tests (limit 300). Regression: $($report.testCount); packaging: $packagingCount; installer: $installerCount." }
        }
        'Build' {
            $releaseOutput=Join-Path $workspace ('output/releases/'+$Variant)
            & (Join-Path $PSScriptRoot 'Publish-OfflinePackage.ps1') -Variant $Variant -OutputDirectory $releaseOutput -BaselinePackage $BaselinePackage
            $reportFile = Get-ChildItem -LiteralPath $releaseOutput -Filter 'package-report.json' -File -Recurse |
                Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
            if ($null -eq $reportFile) { throw 'Package report missing' }
            $report = Get-Content -LiteralPath $reportFile.FullName -Raw | ConvertFrom-Json
            if ($report.packageStatus -ne 'created-and-local-checks-passed' -or $report.variant -ne $Variant) { throw 'Package verification failed' }
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
