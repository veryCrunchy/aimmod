[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory, [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string]$KeyId)
$ErrorActionPreference = 'Stop'
# Choose a private directory outside the checkout. Existing files are never replaced.
$directory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve]::NamedCurves.nistP256)
try {
    $private = Join-Path $directory 'release-private.pem'
    $stream = [IO.File]::Open($private, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($key.ExportPkcs8PrivateKeyPem())
        $stream.Write($bytes)
    } finally { $stream.Dispose() }
    @{ $KeyId = [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo()) } | ConvertTo-Json
    Write-Host 'Store release-private.pem as AIMMOD_RELEASE_SIGNING_KEY. Keep the private file outside version control.'
} finally { $key.Dispose() }
