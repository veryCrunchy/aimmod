<#
.SYNOPSIS
Removes an AimModCore install recorded in ue4ss\aimmod-install.json.

.DESCRIPTION
Deletes exactly the files the installer placed (only while unchanged, unless
-Force), restores the files it had backed up, removes the directories it
created when empty, and deletes the manifest and the install cache. Run data
under %LOCALAPPDATA%\AimMod\KovaaksNative (history, replays) is kept.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$GameDir,
    [switch]$Force,
    [switch]$KeepCache
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AimModInstall.psm1') -Force

$win64 = Find-KovaaksBinaries -GameDir $GameDir
Assert-GameClosed $win64
$manifest = Read-Manifest $win64
if (-not $manifest) { throw "No AimMod install manifest in $win64\ue4ss." }

$kept = 0
foreach ($f in $manifest.files) {
    $path = Join-Path $win64 $f.path
    if (-not (Test-Path -LiteralPath $path)) { continue }
    if (-not $Force -and (Get-FileSha256 $path) -ne $f.sha256) { Write-Warning "kept modified file $($f.path) (use -Force to remove)"; $kept++; continue }
    if ($PSCmdlet.ShouldProcess($f.path, 'remove')) { Remove-Item -LiteralPath $path -Force }
}
$restored = @{}
foreach ($p in $manifest.backups.PSObject.Properties) {
    $backup = Join-Path $win64 $p.Value
    $target = Join-Path $win64 $p.Name
    if ((Test-Path -LiteralPath $backup) -and $PSCmdlet.ShouldProcess($p.Name, 'restore')) {
        Move-Item -LiteralPath $backup -Destination $target -Force
        $restored[$p.Name] = $true
    }
}
# Mod lists the installer created are removed; backed-up ones were restored.
foreach ($list in @($manifest.createdModLists)) {
    if ($list -and -not $restored.ContainsKey($list)) { Remove-Item -LiteralPath (Join-Path $win64 $list) -Force -ErrorAction SilentlyContinue }
}
Remove-Item -LiteralPath (Join-Path $win64 "ue4ss\$ManifestName") -Force
# Runtime files UE4SS writes into a folder the installer created.
if ($manifest.createdDirectories -contains 'ue4ss') {
    Get-ChildItem -LiteralPath (Join-Path $win64 'ue4ss') -File -Force | Where-Object { $_.Name -eq 'UE4SS.log' -or $_.Extension -eq '.dmp' } | Remove-Item -Force
}
foreach ($dir in ($manifest.createdDirectories | Sort-Object { $_.Length } -Descending)) {
    $path = Join-Path $win64 $dir
    if ((Test-Path -LiteralPath $path) -and -not (Get-ChildItem -LiteralPath $path -Force)) { Remove-Item -LiteralPath $path -Force }
}
if (-not $KeepCache) { Remove-Item -LiteralPath (Join-Path $env:LOCALAPPDATA 'AimMod\install') -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host ("AimModCore removed from $win64" + $(if ($kept) { "; $kept modified file(s) kept" } else { '' }) + '.')
