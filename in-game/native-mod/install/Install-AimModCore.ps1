<#
.SYNOPSIS
Installs AimModCore permanently into KovaaK's (UE4SS dwmapi.dll proxy), or
repairs an existing install after a game update.

.DESCRIPTION
Copies dwmapi.dll and ue4ss\ from the verified UE4SS v3.0.1-1152-ge3ba1016
release zip, the stable UE4SS settings, Mods\AimModCore (mod + native service)
and optionally the Lua UI mod, enables them in mods.txt, and records every
placed file with its SHA-256 in ue4ss\aimmod-install.json. Existing files that
are replaced are kept as <name>.aimmod-backup and restored by the uninstaller.
The package and zip are cached under %LOCALAPPDATA%\AimMod\install so -Repair
works without them. Nothing is downloaded.

.EXAMPLE
.\Install-AimModCore.ps1 -Package ..\out\package -Ue4ssZip C:\Downloads\UE4SS_v3.0.1-1152-ge3ba1016.zip
.\Install-AimModCore.ps1 -Repair
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Package,
    [string]$Ue4ssZip,
    [string]$GameDir,
    [switch]$Repair,
    [switch]$WithoutLuaUi
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AimModInstall.psm1') -Force

$win64 = Find-KovaaksBinaries -GameDir $GameDir
Assert-GameClosed $win64
$ue4ssDir = Join-Path $win64 'ue4ss'
$previous = Read-Manifest $win64
$cache = Join-Path $env:LOCALAPPDATA 'AimMod\install'

if ($Repair) {
    if (-not $previous) { throw "No AimMod install found in $win64; run without -Repair first." }
    if (-not $Package) { $Package = Join-Path $cache 'package' }
    if (-not $Ue4ssZip) { $Ue4ssZip = Join-Path $cache 'ue4ss.zip' }
    if (-not $PSBoundParameters.ContainsKey('WithoutLuaUi')) { $WithoutLuaUi = -not [bool]$previous.luaUi }
}
if (-not $Package -or -not (Test-Path -LiteralPath (Join-Path $Package 'AimModCore\dlls\main.dll'))) { throw 'Pass -Package <folder containing AimModCore\dlls\main.dll> (see Build-AimModPackage.ps1).' }
if (-not $Ue4ssZip -or -not (Test-Path -LiteralPath $Ue4ssZip)) { throw 'Pass -Ue4ssZip <UE4SS_v3.0.1-1152-ge3ba1016.zip>.' }
$Package = (Resolve-Path -LiteralPath $Package).Path
$Ue4ssZip = (Resolve-Path -LiteralPath $Ue4ssZip).Path
$zipHash = Get-FileSha256 $Ue4ssZip
if ($zipHash -ne $Ue4ssZipSha256) { throw "UE4SS zip rejected: SHA-256 $zipHash does not match the verified release ($Ue4ssZipSha256)." }

# Cache the verified inputs for -Repair (skip when repairing from the cache).
$cachePackage = Join-Path $cache 'package'
$resolvedCache = if (Test-Path -LiteralPath $cachePackage) { (Resolve-Path -LiteralPath $cachePackage).Path } else { '' }
if ($Package -ne $resolvedCache) {
    if (Test-Path -LiteralPath $cachePackage) { Remove-Item -LiteralPath $cachePackage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    Copy-Item -LiteralPath $Package -Destination $cachePackage -Recurse
}
$cacheZip = Join-Path $cache 'ue4ss.zip'
if ($Ue4ssZip -ne $cacheZip) { New-Item -ItemType Directory -Force -Path $cache | Out-Null; Copy-Item -LiteralPath $Ue4ssZip -Destination $cacheZip -Force }

$extract = Join-Path ([IO.Path]::GetTempPath()) ('aimmod-ue4ss-' + [guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $Ue4ssZip -DestinationPath $extract
try {
    # Plan: destination (relative to Win64) -> source.
    $plan = [ordered]@{}
    $plan['dwmapi.dll'] = Join-Path $extract 'dwmapi.dll'
    foreach ($name in 'UE4SS.dll', 'LICENSE') { $plan["ue4ss\$name"] = Join-Path $extract "ue4ss\$name" }
    function Add-Tree([string]$From, [string]$To) {
        if (-not (Test-Path -LiteralPath $From)) { return }
        foreach ($f in Get-ChildItem -LiteralPath $From -Recurse -File) {
            $plan[(Join-Path $To $f.FullName.Substring($From.Length).TrimStart('\'))] = $f.FullName
        }
    }
    Add-Tree (Join-Path $extract 'ue4ss\UE4SS_SDK_Backends') 'ue4ss\UE4SS_SDK_Backends'
    Add-Tree (Join-Path $extract 'ue4ss\Mods\shared') 'ue4ss\Mods\shared'
    $settings = Join-Path $Package 'UE4SS-settings.ini'
    if (-not (Test-Path -LiteralPath $settings)) { $settings = Join-Path $PSScriptRoot 'UE4SS-settings.ini' }
    $plan['ue4ss\UE4SS-settings.ini'] = $settings
    Add-Tree (Join-Path $Package 'AimModCore') 'ue4ss\Mods\AimModCore'
    $luaUi = -not $WithoutLuaUi -and (Test-Path -LiteralPath (Join-Path $Package 'AimModNativeUI\Scripts\main.lua'))
    if ($luaUi) { Add-Tree (Join-Path $Package 'AimModNativeUI') 'ue4ss\Mods\AimModNativeUI' }

    $ownedBefore = @{}
    $backups = [ordered]@{}
    $created = [System.Collections.Generic.List[string]]::new()
    if ($previous) {
        foreach ($f in $previous.files) { $ownedBefore[$f.path] = $f.sha256 }
        foreach ($p in $previous.backups.PSObject.Properties) { $backups[$p.Name] = $p.Value }
        foreach ($d in $previous.createdDirectories) { $created.Add($d) }
    }
    $files = [System.Collections.Generic.List[object]]::new()
    foreach ($relative in $plan.Keys) {
        $dest = Join-Path $win64 $relative
        $source = $plan[$relative]
        $hash = Get-FileSha256 $source
        $dir = Split-Path -Parent $dest
        $walk = $dir; $missing = @()
        while (-not (Test-Path -LiteralPath $walk)) { $missing += $walk; $walk = Split-Path -Parent $walk }
        foreach ($m in $missing) { $rel = $m.Substring($win64.Length).TrimStart('\'); if (-not $created.Contains($rel)) { $created.Add($rel) } }
        if ($missing) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        if (Test-Path -LiteralPath $dest) {
            $current = Get-FileSha256 $dest
            # Keep anything we did not place ourselves before replacing it.
            if ($current -ne $hash -and -not $ownedBefore.ContainsKey($relative) -and -not $backups.Contains($relative)) {
                Copy-Item -LiteralPath $dest -Destination "$dest.aimmod-backup" -Force
                $backups[$relative] = "$relative.aimmod-backup"
            }
            if ($current -ne $hash -and $PSCmdlet.ShouldProcess($relative, 'update')) { Copy-Item -LiteralPath $source -Destination $dest -Force }
        } elseif ($PSCmdlet.ShouldProcess($relative, 'install')) { Copy-Item -LiteralPath $source -Destination $dest }
        $files.Add([ordered]@{ path = $relative; sha256 = $hash })
    }
    # Files from an earlier AimMod package that the new one no longer ships.
    foreach ($old in $ownedBefore.Keys) {
        if ($plan.Contains($old)) { continue }
        $dest = Join-Path $win64 $old
        if ((Test-Path -LiteralPath $dest) -and (Get-FileSha256 $dest) -eq $ownedBefore[$old]) { Remove-Item -LiteralPath $dest -Force }
    }

    $modsDir = Join-Path $ue4ssDir 'Mods'
    $createdLists = @(if ($previous -and $previous.PSObject.Properties['createdModLists']) { $previous.createdModLists })
    foreach ($list in 'ue4ss\Mods\mods.txt', 'ue4ss\Mods\mods.json') {
        $dest = Join-Path $win64 $list
        if ($previous) { continue }
        if (Test-Path -LiteralPath $dest) {
            Copy-Item -LiteralPath $dest -Destination "$dest.aimmod-backup" -Force
            $backups[$list] = "$list.aimmod-backup"
        } else { $createdLists += $list }
    }
    $ours = @('AimModCore'); if ($luaUi) { $ours += 'AimModNativeUI' }
    Update-ModList -ModsDir $modsDir -Ours $ours

    $manifest = [ordered]@{
        product = 'AimModCore'
        installedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        ue4ss = 'v3.0.1-1152-ge3ba1016'
        ue4ssZipSha256 = $zipHash
        luaUi = $luaUi
        files = $files
        backups = $backups
        createdDirectories = $created
        createdModLists = $createdLists
    }
    Set-Content -LiteralPath (Join-Path $ue4ssDir $ManifestName) -Value ($manifest | ConvertTo-Json -Depth 5) -Encoding utf8
    $verb = if ($Repair) { 'repaired' } else { 'installed' }
    Write-Host "AimModCore $verb in $win64 ($($files.Count) files; manifest ue4ss\$ManifestName)."
} finally {
    Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue
}
