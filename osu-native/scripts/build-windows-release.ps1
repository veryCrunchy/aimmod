[CmdletBinding()]
param(
    [Alias('verify-toolchain')]
    [switch] $VerifyToolchain
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$rid = if ($env:AIMMOD_RUNTIME_ID) { $env:AIMMOD_RUNTIME_ID } else { 'win-x64' }
$version = if ($env:AIMMOD_VERSION) { $env:AIMMOD_VERSION } else { '' }
$configuration = 'Release'

if ($rid -ne 'win-x64') {
    throw "Unsupported Windows runtime identifier: $rid"
}
if ($version -and $version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z]+)*([+][0-9A-Za-z.-]+)?$') {
    throw "AIMMOD_VERSION must be a SemVer-compatible version without a leading v: $version"
}

$versionSegment = if ($version) { "$version-" } else { '' }
[string[]] $versionArgs = @()
if ($version) {
    $versionArgs = @("--property:Version=$version")
}
$artifactName = "aimmod-osu-$versionSegment$rid"
$artifactRoot = Join-Path $repoRoot 'artifacts'
$stage = Join-Path $artifactRoot $artifactName
$archive = Join-Path $artifactRoot "$artifactName.zip"
$archiveChecksum = "$archive.sha256"
$dotnet = if ($env:AIMMOD_DOTNET) { $env:AIMMOD_DOTNET } else { (Get-Command dotnet -ErrorAction Stop).Source }
$python = if ($env:AIMMOD_PYTHON) { $env:AIMMOD_PYTHON } elseif (Get-Command py -ErrorAction SilentlyContinue) { 'py' } else { 'python' }
$pythonPrefix = if ($python -eq 'py') { @('-3') } else { @() }

function Invoke-DotNet {
    & $dotnet $args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE."
    }
}

function Invoke-Python {
    $pythonArgs = @($pythonPrefix) + @($args)
    & $python $pythonArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Python exited with code $LASTEXITCODE."
    }
}

$requiredSdk = (Get-Content -LiteralPath "$repoRoot/packaging/ppy-packages.json" -Raw | ConvertFrom-Json).dotnetSdk
$actualSdk = (& $dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $requiredSdk) {
    throw "Expected .NET SDK $requiredSdk, found $actualSdk."
}

Invoke-Python "$repoRoot/scripts/verify-ppy-pins.py" `
    --root $repoRoot `
    --manifest "$repoRoot/packaging/ppy-packages.json"

if ($VerifyToolchain) {
    Write-Host "Toolchain ready: .NET SDK $actualSdk"
    exit 0
}

$expectedStage = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts/aimmod-osu-$versionSegment$rid"))
$resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
$stageParent = [System.IO.Path]::GetDirectoryName($expectedStage).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
if (
    [System.IO.Path]::GetFullPath($stage) -ne $expectedStage -or
    $stageParent -ne $resolvedArtifactRoot -or
    [System.IO.Path]::GetFileName($expectedStage) -ne $artifactName -or
    -not $artifactName.StartsWith('aimmod-osu-', [System.StringComparison]::Ordinal)
) {
    throw "Refusing to clear unexpected staging path: $stage"
}

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $archive, $archiveChecksum -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path (Join-Path $stage 'app') -Force | Out-Null

Push-Location $repoRoot
try {
    Invoke-DotNet restore AimMod.Native.sln --locked-mode --verbosity minimal
    Invoke-DotNet build AimMod.Native.sln `
        --configuration $configuration `
        --no-restore `
        @versionArgs `
        --property:ContinuousIntegrationBuild=true `
        --verbosity minimal

    $workerTestProject = 'tests/AimMod.Osu.Worker.Tests/AimMod.Osu.Worker.Tests.csproj'
    $realmFixtures = @(
        'ExternalLazerCatalogReaderTests',
        'ExternalLazerRealmBridgeTests',
        'ExternalLazerSkinCatalogReaderTests'
    )
    $nonRealmFilter = 'FullyQualifiedName!~ExternalLazerCatalogReaderTests&FullyQualifiedName!~ExternalLazerRealmBridgeTests&FullyQualifiedName!~ExternalLazerSkinCatalogReaderTests'

    foreach ($testProject in Get-ChildItem -Path 'tests/*/*.csproj') {
        $relativeProject = [System.IO.Path]::GetRelativePath($repoRoot, $testProject.FullName).Replace('\', '/')
        Invoke-DotNet restore $relativeProject --locked-mode --verbosity minimal

        if ($relativeProject -eq $workerTestProject) {
            foreach ($filter in @($nonRealmFilter) + $realmFixtures) {
                $passed = $false
                foreach ($attempt in 1..2) {
                    & $dotnet test $workerTestProject `
                        --configuration $configuration `
                        --no-restore `
                        --property:ContinuousIntegrationBuild=true `
                        --filter $filter `
                        --verbosity minimal
                    if ($LASTEXITCODE -eq 0) {
                        $passed = $true
                        break
                    }
                    if ($attempt -eq 1) {
                        Write-Warning 'The isolated Realm test host aborted; retrying once in a fresh process.'
                    }
                }
                if (-not $passed) {
                    throw "Worker tests failed for filter: $filter"
                }
            }
            continue
        }

        Invoke-DotNet test $relativeProject `
            --configuration $configuration `
            --no-restore `
            --property:ContinuousIntegrationBuild=true `
            --verbosity minimal
    }

    Invoke-DotNet restore src/AimMod.Desktop/AimMod.Desktop.csproj `
        --runtime $rid `
        --locked-mode `
        --property:NuGetLockFilePath="packages.$rid.lock.json" `
        --verbosity minimal
    Invoke-DotNet publish src/AimMod.Desktop/AimMod.Desktop.csproj `
        --configuration $configuration `
        --runtime $rid `
        --self-contained true `
        --no-restore `
        --output "artifacts/$artifactName/app" `
        @versionArgs `
        --property:NuGetLockFilePath="packages.$rid.lock.json" `
        --property:ContinuousIntegrationBuild=true `
        --property:DebugSymbols=false `
        --property:DebugType=None `
        --verbosity minimal
}
finally {
    Pop-Location
}

Copy-Item -LiteralPath "$repoRoot/packaging/ppy-packages.json" -Destination "$stage/ppy-packages.json"
Invoke-Python "$repoRoot/scripts/audit-windows-package.py" $stage `
    --policy "$repoRoot/packaging/windows-artifact-policy.json" `
    --pins "$repoRoot/packaging/ppy-packages.json" `
    --inventory "$stage/artifact-inventory.json"

& "$repoRoot/tests/test-worker-mode.ps1" "$stage/app/AimMod.exe"
if ($LASTEXITCODE -ne 0) {
    throw "Worker smoke test exited with code $LASTEXITCODE."
}

$sourceDateEpoch = if ($env:SOURCE_DATE_EPOCH) { $env:SOURCE_DATE_EPOCH } else { '0' }
$parsedEpoch = 0L
if (-not [long]::TryParse($sourceDateEpoch, [ref] $parsedEpoch) -or $parsedEpoch -lt 0) {
    throw 'SOURCE_DATE_EPOCH must be a non-negative integer.'
}
$entryTimestamp = [DateTimeOffset]::FromUnixTimeSeconds($parsedEpoch)
$minimumZipTimestamp = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
if ($entryTimestamp -lt $minimumZipTimestamp) {
    $entryTimestamp = $minimumZipTimestamp
}

Add-Type -AssemblyName System.IO.Compression
$archiveStream = [System.IO.File]::Open($archive, [System.IO.FileMode]::CreateNew)
try {
    $zip = [System.IO.Compression.ZipArchive]::new(
        $archiveStream,
        [System.IO.Compression.ZipArchiveMode]::Create,
        $false,
        [System.Text.Encoding]::UTF8)
    try {
        Get-ChildItem -LiteralPath $stage -Recurse -File |
            Sort-Object { [System.IO.Path]::GetRelativePath($artifactRoot, $_.FullName).Replace('\', '/') } |
            ForEach-Object {
                $entryName = [System.IO.Path]::GetRelativePath($artifactRoot, $_.FullName).Replace('\', '/')
                $entry = $zip.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $entryTimestamp
                $inputStream = $_.OpenRead()
                $outputStream = $entry.Open()
                try {
                    $inputStream.CopyTo($outputStream)
                }
                finally {
                    $outputStream.Dispose()
                    $inputStream.Dispose()
                }
            }
    }
    finally {
        $zip.Dispose()
    }
}
finally {
    $archiveStream.Dispose()
}

$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText($archiveChecksum, "$hash  $([System.IO.Path]::GetFileName($archive))`n", [System.Text.UTF8Encoding]::new($false))
$inventory = Get-Content -LiteralPath "$stage/artifact-inventory.json" -Raw | ConvertFrom-Json

Write-Host "Release: $archive"
Write-Host "Checksum: $archiveChecksum"
Write-Host "Inventory: $stage/artifact-inventory.json"
Write-Host "Logical publish bytes: $($inventory.logicalBytes)"
Write-Host "Archive bytes: $((Get-Item -LiteralPath $archive).Length)"
