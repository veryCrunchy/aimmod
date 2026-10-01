<#
.SYNOPSIS
Assembles a single zip a friend can use to install the whole AimMod in-game
stack (UE4SS, AimModCore + service, AimModNativeUI, AimModSteam).

.DESCRIPTION
Inputs are things you already built or have:
  -Package       AimModCore package folder from Build-AimModPackage.ps1
                 (contains AimModCore\dlls\main.dll)
  -InstallDir    in-game\native-mod\install (Install-AimModCore.ps1 and
                 AimModInstall.psm1, from the AimModCore branch)
  -Ue4ssZip      the verified UE4SS_v3.0.1-1152-ge3ba1016.zip
  -SteamDll      AimModSteam main.dll (build-mod\Game__Shipping__Win64\main.dll)
  -SteamConfig   optional AimModSteam config.txt (e.g. mod\config.ghost-demo.txt)
  -Scenario      optional .sce files installed to Saved\SaveGames\Scenarios
The bundle adds AimModSteam to the package, both installers and a one-click
Install-AimMod.cmd. Nothing is downloaded or sent anywhere.

.EXAMPLE
.\New-AimModFriendBundle.ps1 -Package <pkg> -InstallDir <native-mod\install> -Ue4ssZip <zip> -SteamDll <main.dll> -Output <folder>
#>
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$InstallDir,
    [Parameter(Mandatory)][string]$Ue4ssZip,
    [Parameter(Mandatory)][string]$SteamDll,
    [string]$SteamConfig,
    [string[]]$Scenario,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
foreach ($p in @((Join-Path $Package 'AimModCore\dlls\main.dll'), (Join-Path $InstallDir 'Install-AimModCore.ps1'), $Ue4ssZip, $SteamDll)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "Missing input: $p" }
}
$stage = Join-Path $Output 'AimMod-install'
if (Test-Path -LiteralPath $stage) { throw "$stage already exists; choose another -Output." }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item -LiteralPath $Package -Destination (Join-Path $stage 'package') -Recurse
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'package\AimModSteam\dlls') | Out-Null
Copy-Item -LiteralPath $SteamDll -Destination (Join-Path $stage 'package\AimModSteam\dlls\main.dll')
$steamArgs = '-Dll "package\AimModSteam\dlls\main.dll"'
if ($SteamConfig) {
    Copy-Item -LiteralPath $SteamConfig -Destination (Join-Path $stage 'package\AimModSteam\config.txt')
    $steamArgs += ' -Config "package\AimModSteam\config.txt"'
}
$scenarioNames = @()
foreach ($sce in @($Scenario | Where-Object { $_ })) {
    New-Item -ItemType Directory -Force -Path (Join-Path $stage 'package\Scenarios') | Out-Null
    Copy-Item -LiteralPath $sce -Destination (Join-Path $stage 'package\Scenarios')
    $leaf = Split-Path -Leaf $sce
    $scenarioNames += [IO.Path]::GetFileNameWithoutExtension($leaf)
    $steamArgs += ' -Scenario "package\Scenarios\' + $leaf + '"'
}
Copy-Item -LiteralPath $InstallDir -Destination (Join-Path $stage 'install') -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-AimModSteam.ps1') -Destination (Join-Path $stage 'install\Install-AimModSteam.ps1')
Copy-Item -LiteralPath $Ue4ssZip -Destination (Join-Path $stage (Split-Path -Leaf $Ue4ssZip))
$zipName = Split-Path -Leaf $Ue4ssZip

$cmd = @"
@echo off
cd /d "%~dp0"
echo Installing AimMod into KovaaK's. Close KovaaK's first.
powershell -NoProfile -ExecutionPolicy Bypass -File "install\Install-AimModCore.ps1" -Package "package" -Ue4ssZip "$zipName"
if errorlevel 1 goto failed
powershell -NoProfile -ExecutionPolicy Bypass -File "install\Install-AimModSteam.ps1" $steamArgs
if errorlevel 1 goto failed
echo.
echo Done. Start KovaaK's from Steam.
pause
exit /b 0
:failed
echo.
echo Install failed; see the message above.
pause
exit /b 1
"@
Set-Content -LiteralPath (Join-Path $stage 'Install-AimMod.cmd') -Value $cmd -Encoding ascii

$scenarioLine = if ($scenarioNames) { 'load the scenario "' + ($scenarioNames -join '" / "') + '"' } else { 'load the same scenario as your friend' }
$readme = @"
AimMod in-game install (multiplayer test)
=========================================

Install (KovaaK's must be closed)
1. Unzip this folder anywhere.
2. Double-click Install-AimMod.cmd. It installs UE4SS, AimModCore (with its
   service), the AimMod UI and AimModSteam into your KovaaK's folder (found
   through Steam), plus the test scenario. Replaced files are backed up.

Play together
3. Start KovaaK's from Steam and stay in the main menu.
4. Your friend sends you a Steam invite from KovaaK's. Accept it in the Steam
   chat popup (or right-click your friend in the Steam friends list and pick
   "Join Game"). AimMod joins automatically; nothing appears on screen.
5. Both of you $scenarioLine.
   You will see each other as a simple capsule with a head and a visor that
   follows the other player's position and view direction.
   They don't collide with you and don't affect scores.

Uninstall (KovaaK's closed), in PowerShell from this folder:
   powershell -ExecutionPolicy Bypass -File install\Install-AimModSteam.ps1 -Remove
   powershell -ExecutionPolicy Bypass -File install\Uninstall-AimModCore.ps1
Delete the test scenario from FPSAimTrainer\Saved\SaveGames\Scenarios if you
like. Steam's "Verify integrity of game files" also restores the original game.

Problems: send ue4ss\UE4SS.log (in FPSAimTrainer\Binaries\Win64) to your
friend; AimMod lines start with [AimModSteam]. SteamIDs in it are shortened.
"@
Set-Content -LiteralPath (Join-Path $stage 'README.txt') -Value $readme -Encoding ascii

$zip = Join-Path $Output 'AimMod-install.zip'
Compress-Archive -Path $stage -DestinationPath $zip
Write-Host "Bundle ready: $zip"
