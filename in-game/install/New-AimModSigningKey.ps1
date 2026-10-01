<#
.SYNOPSIS
Creates the Ed25519 key pair that signs AimMod in-game releases.

.DESCRIPTION
Writes the private key (PEM) to -OutFile, which must be outside this
repository, and prints the public key for in-game/install/aimmod-update-public-key.txt.
Requires OpenSSL 3 (Git for Windows ships one).

Keep the private key offline (password manager or an encrypted drive) and
store it as the AIMMOD_UPDATE_SIGNING_KEY secret of the "ingame-release"
GitHub environment:
  gh secret set AIMMOD_UPDATE_SIGNING_KEY --env ingame-release < <OutFile>

.EXAMPLE
.\New-AimModSigningKey.ps1 -OutFile "$env:USERPROFILE\Documents\aimmod-release-signing.pem"
#>
param(
    [Parameter(Mandatory)][string]$OutFile,
    [string]$OpenSsl
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AimModRelease.ps1')

$openssl = Find-OpenSsl $OpenSsl
$target = [IO.Path]::GetFullPath($OutFile)
$repo = $null
try { $repo = & git -C $PSScriptRoot rev-parse --show-toplevel 2>$null } catch { }
if ($repo) {
    $repo = [IO.Path]::GetFullPath($repo)
    if ($target.StartsWith($repo.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Write the private key outside the repository.' }
}
if (Test-Path -LiteralPath $target) { throw "$target already exists; refusing to overwrite a key." }

& $openssl genpkey -algorithm ed25519 -out $target
if ($LASTEXITCODE) { throw 'openssl genpkey failed.' }
$public = Get-Ed25519PublicKey -OpenSsl $openssl -Key $target
Write-Host "Private key written to $target. Keep it secret; never commit it."
Write-Host ''
Write-Host 'Public key (add this line to in-game/install/aimmod-update-public-key.txt):'
Write-Host $public
