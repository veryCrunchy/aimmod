<#
.SYNOPSIS
Adds (or removes) the AimModSteam UE4SS mod in an existing AimMod in-game
install. Interim helper until AimModSteam is part of Install-AimModCore.ps1.

.DESCRIPTION
Requires KovaaK's with UE4SS and AimModCore already installed
(Install-AimModCore.ps1) and the game closed. Copies main.dll to
ue4ss\Mods\AimModSteam\dlls\main.dll and enables "AimModSteam" in
ue4ss\Mods\mods.txt and mods.json. -Remove deletes the folder and the
entries again. Nothing is downloaded.

-Config copies a config.txt (for example the ghost demo one) next to dlls\.
-Scenario copies .sce files to FPSAimTrainer\Saved\SaveGames\Scenarios.

.EXAMPLE
.\Install-AimModSteam.ps1 -Dll ..\build-mod\Game__Shipping__Win64\main.dll -Config ..\mod\config.ghost-demo.txt
.\Install-AimModSteam.ps1 -Remove
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Dll,
    [string]$Config,
    [string[]]$Scenario,
    [string]$GameDir,
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'

function Find-Win64([string]$GameDir) {
    if ($GameDir) {
        $candidate = Join-Path $GameDir 'FPSAimTrainer\Binaries\Win64'
        if (Test-Path -LiteralPath $candidate) { return (Resolve-Path -LiteralPath $candidate).Path }
        throw "KovaaK's not found under $GameDir."
    }
    $steam = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { throw 'Steam is not installed; pass -GameDir.' }
    $libraries = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path -LiteralPath $vdf) {
        foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) { $libraries += $m.Groups[1].Value -replace '\\\\', '\' }
    }
    foreach ($lib in $libraries) {
        $candidate = Join-Path $lib 'steamapps\common\FPSAimTrainer\FPSAimTrainer\Binaries\Win64'
        if (Test-Path -LiteralPath $candidate) { return (Resolve-Path -LiteralPath $candidate).Path }
    }
    throw "KovaaK's not found in the Steam libraries; pass -GameDir."
}

$win64 = Find-Win64 $GameDir
if (Get-Process -Name 'FPSAimTrainer*' -ErrorAction SilentlyContinue) { throw "Close KovaaK's first." }
$mods = Join-Path $win64 'ue4ss\Mods'
if (-not (Test-Path -LiteralPath (Join-Path $mods 'AimModCore'))) { throw 'AimModCore is not installed here. Run Install-AimModCore.ps1 first.' }
$target = Join-Path $mods 'AimModSteam'
$modsTxt = Join-Path $mods 'mods.txt'
$modsJson = Join-Path $mods 'mods.json'

# mods.txt: one "Name : 1" line per mod.
if (Test-Path -LiteralPath $modsTxt) {
    $lines = @(Get-Content -LiteralPath $modsTxt | Where-Object { $_ -notmatch '^\s*AimModSteam\s*:' })
    if (-not $Remove) { $lines += 'AimModSteam : 1' }
    if ($PSCmdlet.ShouldProcess($modsTxt, 'update')) { Set-Content -LiteralPath $modsTxt -Value $lines -Encoding ascii }
}
# mods.json: {"mods":[{"mod_name":..,"mod_enabled":..}]}
if (Test-Path -LiteralPath $modsJson) {
    $json = Get-Content -LiteralPath $modsJson -Raw | ConvertFrom-Json
    $list = @($json.mods | Where-Object { $_.mod_name -ne 'AimModSteam' })
    if (-not $Remove) { $list += [pscustomobject]@{ mod_name = 'AimModSteam'; mod_enabled = $true } }
    $json.mods = $list
    if ($PSCmdlet.ShouldProcess($modsJson, 'update')) { Set-Content -LiteralPath $modsJson -Value ($json | ConvertTo-Json -Depth 4) -Encoding utf8 }
}

if ($Remove) {
    if ((Test-Path -LiteralPath $target) -and $PSCmdlet.ShouldProcess($target, 'remove')) { Remove-Item -LiteralPath $target -Recurse -Force }
    Write-Host 'AimModSteam removed.'
    return
}
if (-not $Dll) { $Dll = Join-Path $PSScriptRoot '..\build-mod\Game__Shipping__Win64\main.dll' }
if (-not (Test-Path -LiteralPath $Dll)) { throw "main.dll not found at $Dll; pass -Dll." }
New-Item -ItemType Directory -Force -Path (Join-Path $target 'dlls') | Out-Null
if ($PSCmdlet.ShouldProcess($target, 'install')) { Copy-Item -LiteralPath $Dll -Destination (Join-Path $target 'dlls\main.dll') -Force }
if ($Config) {
    if (-not (Test-Path -LiteralPath $Config)) { throw "Config not found: $Config" }
    Copy-Item -LiteralPath $Config -Destination (Join-Path $target 'config.txt') -Force
}
foreach ($sce in @($Scenario | Where-Object { $_ })) {
    if (-not (Test-Path -LiteralPath $sce)) { throw "Scenario not found: $sce" }
    $scenarios = Join-Path (Split-Path -Parent (Split-Path -Parent $win64)) 'Saved\SaveGames\Scenarios'
    New-Item -ItemType Directory -Force -Path $scenarios | Out-Null
    Copy-Item -LiteralPath $sce -Destination $scenarios -Force
    Write-Host "Scenario installed: $(Split-Path -Leaf $sce)"
}
$hash = (Get-FileHash -LiteralPath (Join-Path $target 'dlls\main.dll') -Algorithm SHA256).Hash
Write-Host "AimModSteam installed (main.dll SHA-256 $hash). Start KovaaK's and look for [AimModSteam] lines in ue4ss\UE4SS.log."
