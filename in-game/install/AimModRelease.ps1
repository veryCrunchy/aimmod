# Shared helpers for the AimMod in-game release scripts (dot-sourced).
Set-StrictMode -Version Latest

$script:Ue4ssVersion = 'v3.0.1-1152-ge3ba1016'
$script:Ue4ssZipSha256 = 'af8ea9d8975e8eff7967423f43b8b50875e66a29a0f434cffce6e0867ea17252'

function Find-OpenSsl([string]$Path) {
    if ($Path) { return $Path }
    $command = Get-Command openssl -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($candidate in "$env:ProgramFiles\Git\usr\bin\openssl.exe", "$env:ProgramFiles\Git\mingw64\bin\openssl.exe") {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    throw 'OpenSSL 3 was not found. Install Git for Windows or pass -OpenSsl.'
}

# Raw 32-byte Ed25519 public key (base64) from a PEM private key: the DER
# SubjectPublicKeyInfo ends with the key bytes.
function Get-Ed25519PublicKey([string]$OpenSsl, [string]$Key) {
    $der = [IO.Path]::GetTempFileName()
    try {
        & $OpenSsl pkey -in $Key -pubout -outform DER -out $der
        if ($LASTEXITCODE) { throw 'openssl pkey failed.' }
        $bytes = [IO.File]::ReadAllBytes($der)
        if ($bytes.Length -ne 44) { throw 'Not an Ed25519 key.' }
        [Convert]::ToBase64String($bytes, 12, 32)
    } finally { Remove-Item -LiteralPath $der -Force -ErrorAction SilentlyContinue }
}

# <file>.sig = base64 Ed25519 signature over the exact bytes of <file>.
function New-Ed25519Signature([string]$OpenSsl, [string]$Key, [string]$File) {
    $raw = [IO.Path]::GetTempFileName()
    try {
        & $OpenSsl pkeyutl -sign -inkey $Key -rawin -in $File -out $raw
        if ($LASTEXITCODE) { throw "openssl could not sign $File." }
        $bytes = [IO.File]::ReadAllBytes($raw)
        if ($bytes.Length -ne 64) { throw 'Unexpected signature length.' }
        [IO.File]::WriteAllText("$File.sig", [Convert]::ToBase64String($bytes) + "`n", [Text.Encoding]::ASCII)
    } finally { Remove-Item -LiteralPath $raw -Force -ErrorAction SilentlyContinue }
}

# SemVer 2 precedence: -1, 0 or 1 (same rules as the service's SemanticVersion).
function Compare-SemVer([string]$A, [string]$B) {
    function Split([string]$v) {
        $core, $pre = $v -split '-', 2
        $ids = [string[]]::new(0)
        if ($pre) { $ids = [string[]]($pre -split '\.') }
        [pscustomobject]@{ Core = [long[]]($core -split '\.'); Pre = $ids }
    }
    $x = Split $A; $y = Split $B
    for ($i = 0; $i -lt 3; $i++) { if ($x.Core[$i] -ne $y.Core[$i]) { return [Math]::Sign($x.Core[$i] - $y.Core[$i]) } }
    if ($x.Pre.Count -eq 0 -and $y.Pre.Count -eq 0) { return 0 }
    if ($x.Pre.Count -eq 0) { return 1 }
    if ($y.Pre.Count -eq 0) { return -1 }
    for ($i = 0; $i -lt [Math]::Min($x.Pre.Count, $y.Pre.Count); $i++) {
        $p = $x.Pre[$i]; $q = $y.Pre[$i]
        $pn = $p -match '^\d+$'; $qn = $q -match '^\d+$'
        if ($pn -and $qn) { if ([long]$p -ne [long]$q) { return [Math]::Sign([long]$p - [long]$q) } }
        elseif ($pn -ne $qn) { return $(if ($pn) { -1 } else { 1 }) }
        else { $c = [string]::CompareOrdinal($p, $q); if ($c -ne 0) { return [Math]::Sign($c) } }
    }
    [Math]::Sign($x.Pre.Count - $y.Pre.Count)
}

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

# UTF-8 without BOM and LF only, so the bytes that are signed are stable.
function Write-Utf8([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, ($Text -replace "`r`n", "`n"), [Text.UTF8Encoding]::new($false))
}
