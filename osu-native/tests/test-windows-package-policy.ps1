[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) "aimmod-windows-policy-$([guid]::NewGuid().ToString('N'))"
$python = if ($env:AIMMOD_PYTHON) { $env:AIMMOD_PYTHON } elseif (Get-Command py -ErrorAction SilentlyContinue) { 'py' } else { 'python' }
$pythonPrefix = if ($python -eq 'py') { @('-3') } else { @() }

function Invoke-Audit([string] $Package) {
    $pythonArgs = @($pythonPrefix) + @(
        "$repoRoot/scripts/audit-windows-package.py",
        $Package,
        '--policy',
        "$repoRoot/packaging/windows-artifact-policy.json",
        '--pins',
        "$repoRoot/packaging/ppy-packages.json"
    )
    $output = & $python $pythonArgs
    $exitCode = $LASTEXITCODE
    if ($output) {
        $output | ForEach-Object { Write-Host $_ }
    }
    return $exitCode
}

function New-ValidFixture([string] $Root) {
    $app = New-Item -ItemType Directory -Path (Join-Path $Root 'app') -Force
    @(
        'AimMod.exe',
        'AimMod.dll',
        'AimMod.deps.json',
        'AimMod.runtimeconfig.json',
        'aimmod-osu-worker.dll',
        'createdump.exe',
        'osu.Game.dll',
        'osu.Game.Rulesets.Osu.dll',
        'stbi.lib'
    ) | ForEach-Object { [System.IO.File]::WriteAllBytes((Join-Path $app.FullName $_), [byte[]]::new(0)) }
    Copy-Item -LiteralPath "$repoRoot/packaging/ppy-packages.json" -Destination $Root
}

function Copy-Fixture([string] $Source, [string] $Destination) {
    New-Item -ItemType Directory -Path $Destination | Out-Null
    Copy-Item -Path "$Source/*" -Destination $Destination -Recurse
}

function Assert-Rejected([string] $Package, [string] $Description) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $exitCode = Invoke-Audit $Package 2>$null
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -eq 0) {
        throw "Policy accepted $Description."
    }
}

try {
    $valid = Join-Path $fixture 'valid'
    New-ValidFixture $valid
    if ((Invoke-Audit $valid) -ne 0) {
        throw 'Policy rejected a valid Windows package fixture.'
    }

    $web = Join-Path $fixture 'web'
    Copy-Fixture $valid $web
    [System.IO.File]::WriteAllText((Join-Path $web 'app/index.js'), 'ReactDOM')
    Assert-Rejected $web 'a JavaScript frontend asset'

    $debug = Join-Path $fixture 'debug'
    Copy-Fixture $valid $debug
    [System.IO.File]::WriteAllBytes((Join-Path $debug 'app/AimMod.pdb'), [byte[]]::new(0))
    Assert-Rejected $debug 'debug symbols'

    $kovaak = Join-Path $fixture 'kovaak'
    Copy-Fixture $valid $kovaak
    [System.IO.File]::WriteAllText((Join-Path $kovaak 'app/Foreign.dll'), 'KovaaK payload')
    Assert-Rejected $kovaak 'a KovaaK content marker'

    $unexpectedExecutable = Join-Path $fixture 'unexpected-executable'
    Copy-Fixture $valid $unexpectedExecutable
    [System.IO.File]::WriteAllBytes((Join-Path $unexpectedExecutable 'app/helper.exe'), [byte[]]::new(0))
    Assert-Rejected $unexpectedExecutable 'an unapproved executable'

    $duplicateWorker = Join-Path $fixture 'duplicate-worker'
    Copy-Fixture $valid $duplicateWorker
    [System.IO.File]::WriteAllBytes((Join-Path $duplicateWorker 'app/aimmod-osu-worker.exe'), [byte[]]::new(0))
    Assert-Rejected $duplicateWorker 'a second worker apphost'

    foreach ($assembly in @(
        'osu.Desktop.dll',
        'osu.Game.Tournament.dll',
        'osu.Game.Rulesets.Catch.dll',
        'osu.Game.Rulesets.Mania.dll',
        'osu.Game.Rulesets.Taiko.dll'
    )) {
        $deniedAssembly = Join-Path $fixture "denied-$assembly"
        Copy-Fixture $valid $deniedAssembly
        [System.IO.File]::WriteAllBytes((Join-Path $deniedAssembly "app/$assembly"), [byte[]]::new(0))
        Assert-Rejected $deniedAssembly "denied osu assembly $assembly"
    }
}
finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'Windows packaging policy tests passed.'
