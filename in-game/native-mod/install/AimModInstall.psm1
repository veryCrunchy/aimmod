# Shared helpers for the AimModCore permanent install. Local files only:
# nothing is downloaded, and the UE4SS release is accepted only by SHA-256.
Set-StrictMode -Version Latest

$script:Ue4ssZipSha256 = 'AF8EA9D8975E8EFF7967423F43B8B50875E66A29A0F434CFFCE6E0867EA17252' # UE4SS v3.0.1-1152-ge3ba1016
$script:GameExe = 'FPSAimTrainer-Win64-Shipping'
$script:ManifestName = 'aimmod-install.json'

function Get-FileSha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

function Find-KovaaksBinaries {
    param([string]$GameDir)
    $candidates = @()
    if ($GameDir) { $candidates += $GameDir }
    else {
        $steam = $null
        foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
            try { $steam = (Get-ItemProperty -Path $key -ErrorAction Stop).SteamPath; if (-not $steam) { $steam = (Get-ItemProperty -Path $key -ErrorAction Stop).InstallPath } } catch { }
            if ($steam) { break }
        }
        $libraries = @()
        if ($steam) {
            $libraries += $steam
            $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
            if (Test-Path -LiteralPath $vdf) {
                foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) { $libraries += ($m.Groups[1].Value -replace '\\\\', '\') }
            }
        }
        foreach ($library in ($libraries | Select-Object -Unique)) { $candidates += (Join-Path $library 'steamapps\common\FPSAimTrainer') }
    }
    foreach ($root in $candidates) {
        foreach ($win64 in @((Join-Path $root 'FPSAimTrainer\Binaries\Win64'), (Join-Path $root 'Binaries\Win64'), $root)) {
            if (Test-Path -LiteralPath (Join-Path $win64 "$script:GameExe.exe")) { return (Resolve-Path -LiteralPath $win64).Path }
        }
    }
    throw "KovaaK's was not found. Pass -GameDir <path to the FPSAimTrainer folder>."
}

function Assert-GameClosed([string]$Win64) {
    # Only this installation matters; a process whose path cannot be read is
    # treated as this game.
    foreach ($p in Get-Process -Name $script:GameExe -ErrorAction SilentlyContinue) {
        $exe = $null; try { $exe = $p.Path } catch { }
        if (-not $exe -or (Split-Path -Parent $exe) -eq $Win64) { throw "KovaaK's is running. Close the game first." }
    }
}

function Read-Manifest([string]$Win64) {
    $path = Join-Path $Win64 "ue4ss\$script:ManifestName"
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Update-ModList {
    # mods.txt / mods.json: AimMod entries first and enabled; other entries kept
    # only if their folder exists.
    param([string]$ModsDir, [string[]]$Ours, [string[]]$Remove = @())
    $txt = Join-Path $ModsDir 'mods.txt'
    $entries = [ordered]@{}
    foreach ($name in $Ours) { $entries[$name] = 1 }
    if (Test-Path -LiteralPath $txt) {
        foreach ($line in Get-Content -LiteralPath $txt) {
            if ($line -match '^\s*([^;:\s][^:]*?)\s*:\s*([01])\s*$') {
                $name = $Matches[1]
                if ($entries.Contains($name) -or $Remove -contains $name) { continue }
                if (Test-Path -LiteralPath (Join-Path $ModsDir $name)) { $entries[$name] = [int]$Matches[2] }
            }
        }
    }
    $lines = foreach ($k in $entries.Keys) { "$k : $($entries[$k])" }
    Set-Content -LiteralPath $txt -Value $lines -Encoding ascii
    $json = @{ mods = @(foreach ($k in $entries.Keys) { [ordered]@{ mod_name = $k; mod_enabled = [bool]$entries[$k] } }) }
    Set-Content -LiteralPath (Join-Path $ModsDir 'mods.json') -Value ($json | ConvertTo-Json -Depth 4) -Encoding ascii
}

Export-ModuleMember -Function * -Variable Ue4ssZipSha256, GameExe, ManifestName
