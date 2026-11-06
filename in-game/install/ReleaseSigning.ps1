# P-256 is supported by the net8 clients without an additional crypto library.
function Protect-ReleaseMetadata([string]$Path, [long]$Counter) {
    if (-not $env:AIMMOD_RELEASE_SIGNING_KEY) { throw 'AIMMOD_RELEASE_SIGNING_KEY is required to sign published releases.' }
    $keys = Get-Content (Join-Path $PSScriptRoot '../install-core/release-keys.json') -Raw | ConvertFrom-Json -AsHashtable
    $key = [Security.Cryptography.ECDsa]::Create()
    try {
        $key.ImportFromPem($env:AIMMOD_RELEASE_SIGNING_KEY)
        $public = [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo())
        $id = @($keys.Keys | Where-Object { $keys[$_] -ceq $public })
        if ($key.KeySize -ne 256 -or $id.Count -ne 1) { throw 'Pin the signing public key with a unique key ID in release-keys.json before publishing.' }
        $data = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
        $data.Remove('signature')
        $data.releaseCounter = $Counter
        $data.expiresAt = [DateTimeOffset]::UtcNow.AddDays(90).ToString('O')
        $payload = [Text.Encoding]::UTF8.GetBytes(($data | ConvertTo-Json -Depth 12 -Compress))
        $signature = $key.SignData($payload, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
        $data.signature = @{ keyId = $id[0]; payload = [Convert]::ToBase64String($payload); value = [Convert]::ToBase64String($signature) }
        [IO.File]::WriteAllText($Path, ($data | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    } finally { $key.Dispose() }
}
