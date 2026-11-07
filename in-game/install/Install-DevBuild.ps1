#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$Ue4ssZip,
    [Parameter(Mandatory)][string]$GameDir,
    [string]$Version = '0.0.0-dev',
    [Parameter(Mandatory)][long]$ReleaseCounter,
    [string]$SteamDll,
    [string]$SteamConfig,
    [switch]$PackageOnly
)
$ErrorActionPreference = 'Stop'
if (-not $env:AIMMOD_RELEASE_SIGNING_KEY) { throw 'Set AIMMOD_RELEASE_SIGNING_KEY to the trusted release key before packaging. The installed service requires signed manifests.' }
if ($ReleaseCounter -le 0) { throw 'ReleaseCounter must be positive.' }
$Package = (Resolve-Path -LiteralPath $Package).Path
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
$output = Join-Path ([IO.Path]::GetTempPath()) ('aimmod-dev-' + [guid]::NewGuid().ToString('N'))
$process = Get-Process -Id $PID
$priority = $process.PriorityClass
try {
    $process.PriorityClass = 'BelowNormal'
    & (Join-Path $PSScriptRoot 'New-AimModInGameRelease.ps1') -Step Stage -Package $Package -Ue4ssZip $Ue4ssZip -Version $Version -Channel beta -Output $output -SteamDll $SteamDll -SteamConfig $SteamConfig
    $stage = Join-Path $output "AimMod-InGame-$Version"
    . (Join-Path $PSScriptRoot 'ReleaseSigning.ps1')
    Protect-ReleaseMetadata (Join-Path $stage 'aimmod-release.json') $ReleaseCounter
    if (-not $PackageOnly) {
        $service = Join-Path $stage 'files/ue4ss/Mods/AimModCore/service/AimMod.InGame.exe'
        & $service --install --package $stage --game-dir $GameDir
        if ($LASTEXITCODE -ne 0) { throw "Installation failed with exit code $LASTEXITCODE. Package retained for inspection." }
    }
    Write-Output $stage
} finally { $process.PriorityClass = $priority }
