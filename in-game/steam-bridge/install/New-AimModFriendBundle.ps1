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
powershell -NoProfile -ExecutionPolicy Bypass -File "install\Install-AimModSteam.ps1" -Dll "package\AimModSteam\dlls\main.dll"
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

$readme = @'
AimMod in-game install
======================
1. Close KovaaK's.
2. Double-click Install-AimMod.cmd. It installs UE4SS, AimModCore (with its
   service), the AimMod UI and AimModSteam into your KovaaK's folder, found
   through Steam. Files it replaces are backed up.
3. Start KovaaK's from Steam. Open the AimMod multiplayer page to see your
   Steam friends, accept invites, or use "Join Game" in the Steam friends list.

To remove everything: run install\Uninstall-AimModCore.ps1 (and
install\Install-AimModSteam.ps1 -Remove) from PowerShell with KovaaK's closed.
Steam's "Verify integrity of game files" also restores the original game.
'@
Set-Content -LiteralPath (Join-Path $stage 'README.txt') -Value $readme -Encoding ascii

$zip = Join-Path $Output 'AimMod-install.zip'
Compress-Archive -Path $stage -DestinationPath $zip
Write-Host "Bundle ready: $zip"
