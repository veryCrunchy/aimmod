<#
.SYNOPSIS
Signs AimMod release files: writes <file>.sig (base64 Ed25519 signature).

.DESCRIPTION
Local counterpart of the signing step in .github/workflows/aimmod-ingame-release.yml,
for a release built and published by hand. Sign aimmod-release.json before
the package is zipped, and the feed after the package hash is known (see
New-AimModInGameRelease.ps1 -Key, which does both).

.EXAMPLE
.\New-AimModSignature.ps1 -Key C:\keys\aimmod-release-signing.pem -File out\release\aimmod-ingame-stable.json
#>
param(
    [Parameter(Mandatory)][string]$Key,
    [Parameter(Mandatory)][string[]]$File,
    [string]$OpenSsl
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AimModRelease.ps1')
$openssl = Find-OpenSsl $OpenSsl
foreach ($f in $File) {
    New-Ed25519Signature -OpenSsl $openssl -Key $Key -File (Resolve-Path -LiteralPath $f).Path
    Write-Host "Signed $f"
}
