# Shared helpers for the AimMod in-game release scripts (dot-sourced).
Set-StrictMode -Version Latest

$script:Ue4ssVersion = 'v3.0.1-1152-ge3ba1016'
$script:Ue4ssZipSha256 = 'af8ea9d8975e8eff7967423f43b8b50875e66a29a0f434cffce6e0867ea17252'

# The oldest AimMod-Setup.exe (in-game/installer, version in in-game/installer/version.txt)
# that can install a release built by these scripts. Raise it, together with the
# installer version, when a package needs something older installers cannot do;
# older installers then show "A new installer is required" instead of installing.
$script:DefaultMinimumInstallerVersion = '1.0.0'
# The permanent installer link: every channel release carries the newest AimMod-Setup.exe.
$script:DefaultInstallerUrl = 'https://github.com/verycrunchy/aimmod/releases/download/aimmod-ingame-{channel}/AimMod-Setup.exe'

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

# UTF-8 without BOM and LF only, so the hashed bytes are stable across machines.
function Write-Utf8([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, ($Text -replace "`r`n", "`n"), [Text.UTF8Encoding]::new($false))
}
