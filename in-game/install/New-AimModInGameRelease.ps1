<#
.SYNOPSIS
Builds an AimMod in-game release: the versioned package zip, its release
manifest and the update feed for one channel.

.DESCRIPTION
Two steps, run together locally or separately in CI (build on Windows,
package and publish on Linux):

  -Step Stage   Lay out out\<name>\ from the build outputs:
                  files\<path relative to FPSAimTrainer\Binaries\Win64>
                  aimmod-release.json (version, every file with SHA-256 and
                  size, UE4SS version, minimum and tested game builds)
                  Install-AimMod.cmd, Repair-AimMod.cmd, Uninstall-AimMod.cmd, README.txt
  -Step Finish  Zip the folder
                into AimMod-InGame-<version>.zip and write
                aimmod-ingame-<channel>.json: version, channel, notes,
                package URL, size and SHA-256, the manifest SHA-256, and
                the installer fields (minimumInstallerVersion, installerUrl)
                read by AimMod-Setup.exe. -SetupExe adds the installer's
                SHA-256 to the .sha256 file.

Inputs for Stage:
  -Package    AimModCore package from in-game/native-mod/install/Build-AimModPackage.ps1
              (AimModCore\, AimModNativeUI\, UE4SS-settings.ini)
  -SteamDll   AimModSteam main.dll (in-game/steam-bridge, Game__Shipping__Win64); optional
  -Ue4ssZip   UE4SS_v3.0.1-1152-ge3ba1016.zip, accepted only by SHA-256
Nothing is downloaded or published.

.EXAMPLE
.\New-AimModInGameRelease.ps1 -Version 0.2.0 -Package ..\native-mod\out\package -SteamDll <main.dll> -Ue4ssZip <zip> -TestedBuild 3.9.11=25635011
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'Stage', 'Finish')][string]$Step = 'All',
    [Parameter(Mandatory)][string]$Version,
    [ValidateSet('stable', 'beta')][string]$Channel = 'stable',
    [string]$Output = (Join-Path $PSScriptRoot '..\out\release'),
    [string]$Package,
    [string]$SteamDll,
    [string]$SteamConfig,
    [string]$Ue4ssZip,
    # "<game version>=<Steam build id>", e.g. 3.9.11=25635011
    [string[]]$TestedBuild = @(),
    [long]$MinimumSteamBuildId = 0,
    [string]$Commit = '',
    [string]$NotesFile,
    [string]$DownloadBaseUrl,
    # Stable releases also become the beta feed unless this (current) beta
    # feed already offers a newer version. Pass the downloaded beta feed, or
    # -WriteBetaFeed alone when there is none yet.
    [switch]$WriteBetaFeed,
    [string]$ExistingBetaFeed,
    # Installer fields of the feed (see AimModRelease.ps1). {channel} is replaced per feed.
    [string]$MinimumInstallerVersion,
    [string]$InstallerUrl,
    # The built AimMod-Setup.exe, listed in the .sha256 file; optional.
    [string]$SetupExe
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AimModRelease.ps1')

if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$') { throw "Invalid version $Version." }
if ($Channel -eq 'stable' -and $Version.Contains('-')) { throw 'A prerelease version belongs on the beta channel.' }
if (-not $MinimumInstallerVersion) { $MinimumInstallerVersion = $script:DefaultMinimumInstallerVersion }
if (-not $InstallerUrl) { $InstallerUrl = $script:DefaultInstallerUrl }
if ($MinimumInstallerVersion -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$') { throw "Invalid installer version $MinimumInstallerVersion." }
if (-not $InstallerUrl.StartsWith('https://')) { throw 'The installer link must use HTTPS.' }
$name = "AimMod-InGame-$Version"
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$Output = (Resolve-Path -LiteralPath $Output).Path
$stage = Join-Path $Output $name
if (-not $DownloadBaseUrl) { $DownloadBaseUrl = "https://github.com/verycrunchy/aimmod/releases/download/aimmod-ingame-v$Version" }

function Invoke-Stage {
    foreach ($p in @($Package, $Ue4ssZip)) { if (-not $p -or -not (Test-Path -LiteralPath $p)) { throw "Stage needs -Package and -Ue4ssZip (missing: $p)." } }
    if (-not (Test-Path -LiteralPath (Join-Path $Package 'AimModCore\dlls\main.dll'))) { throw 'The package has no AimModCore\dlls\main.dll (run Build-AimModPackage.ps1).' }
    if (-not (Test-Path -LiteralPath (Join-Path $Package 'AimModCore\service\AimMod.InGame.exe'))) { throw 'The package has no service\AimMod.InGame.exe.' }
    $zipHash = Get-Sha256 $Ue4ssZip
    if ($zipHash -ne $Ue4ssZipSha256) { throw "UE4SS zip rejected: SHA-256 $zipHash does not match the verified release ($Ue4ssZipSha256)." }
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    $files = Join-Path $stage 'files'
    New-Item -ItemType Directory -Force -Path $files | Out-Null

    $extract = Join-Path ([IO.Path]::GetTempPath()) ('aimmod-ue4ss-' + [guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath $Ue4ssZip -DestinationPath $extract
    try {
        # Same layout as Install-AimModCore.ps1.
        function Put([string]$From, [string]$To) {
            if (-not (Test-Path -LiteralPath $From)) { return }
            $dest = Join-Path $files $To
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
            if ((Get-Item -LiteralPath $From).PSIsContainer) { Copy-Item -LiteralPath $From -Destination $dest -Recurse -Force }
            else { Copy-Item -LiteralPath $From -Destination $dest -Force }
        }
        Put (Join-Path $extract 'dwmapi.dll') 'dwmapi.dll'
        Put (Join-Path $extract 'ue4ss\UE4SS.dll') 'ue4ss\UE4SS.dll'
        Put (Join-Path $extract 'ue4ss\LICENSE') 'ue4ss\LICENSE'
        Put (Join-Path $extract 'ue4ss\UE4SS_SDK_Backends') 'ue4ss\UE4SS_SDK_Backends'
        Put (Join-Path $extract 'ue4ss\Mods\shared') 'ue4ss\Mods\shared'
        $settings = Join-Path $Package 'UE4SS-settings.ini'
        if (-not (Test-Path -LiteralPath $settings)) { throw 'The package has no UE4SS-settings.ini.' }
        Put $settings 'ue4ss\UE4SS-settings.ini'
        Put (Join-Path $Package 'AimModCore') 'ue4ss\Mods\AimModCore'
        $mods = @('AimModCore')
        if (Test-Path -LiteralPath (Join-Path $Package 'AimModNativeUI\Scripts\main.lua')) { Put (Join-Path $Package 'AimModNativeUI') 'ue4ss\Mods\AimModNativeUI'; $mods += 'AimModNativeUI' }
        if ($SteamDll) {
            if (-not (Test-Path -LiteralPath $SteamDll)) { throw "AimModSteam main.dll not found: $SteamDll" }
            Put $SteamDll 'ue4ss\Mods\AimModSteam\dlls\main.dll'
            if ($SteamConfig) { Put $SteamConfig 'ue4ss\Mods\AimModSteam\config.txt' }
            $mods += 'AimModSteam'
        }
        # AimMod cosmetics paks: release path paks/~AimMod/<name>.pak, installed
        # into the game's Content\Paks\~AimMod (ReleasePaths.IsPak). Flat, never _P.
        $paks = Join-Path $Package 'Paks\~AimMod'
        if (Test-Path -LiteralPath $paks) {
            foreach ($item in Get-ChildItem -LiteralPath $paks -Force) {
                $ok = -not $item.PSIsContainer -and $item.Name -cmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\.pak$' -and -not $item.Name.Contains('..') -and -not $item.Name.EndsWith('_P.pak', [StringComparison]::OrdinalIgnoreCase)
                if (-not $ok) { throw "Cosmetics pak not allowed in a release: $($item.Name)" }
                Put $item.FullName "paks\~AimMod\$($item.Name)"
            }
        }
        if (-not (Test-Path -LiteralPath (Join-Path $files 'dwmapi.dll'))) { throw 'The UE4SS zip has no dwmapi.dll.' }
    } finally { Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue }

    foreach ($template in 'Install-AimMod.cmd', 'Repair-AimMod.cmd', 'Uninstall-AimMod.cmd', 'README.txt') {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "package\$template") -Destination (Join-Path $stage $template)
    }

    $entries = foreach ($f in Get-ChildItem -LiteralPath $files -Recurse -File | Sort-Object { $_.FullName.Substring($files.Length).ToLowerInvariant() }) {
        if ($f.Extension -eq '.pdb') { Remove-Item -LiteralPath $f.FullName; continue }
        [ordered]@{ path = $f.FullName.Substring($files.Length + 1).Replace('\', '/'); sha256 = Get-Sha256 $f.FullName; size = $f.Length }
    }
    $builds = foreach ($b in $TestedBuild) {
        if ($b -notmatch '^([^=]+)=(\d+)$') { throw "TestedBuild must be <version>=<steam build id>: $b" }
        [ordered]@{ version = $Matches[1]; steamBuildId = [long]$Matches[2] }
    }
    $manifest = [ordered]@{
        schema = 'aimmod.ingame.release/1'
        product = "AimMod for KovaaK's (in-game)"
        version = $Version
        channel = $Channel
        publishedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        commit = $Commit
        ue4ss = [ordered]@{ version = $Ue4ssVersion; zipSha256 = $Ue4ssZipSha256 }
        game = [ordered]@{ appId = 824270; minimumSteamBuildId = $MinimumSteamBuildId; testedBuilds = @($builds) }
        mods = @($mods)
        files = @($entries)
    }
    Write-Utf8 (Join-Path $stage 'aimmod-release.json') ($manifest | ConvertTo-Json -Depth 6)
    Write-Host "Staged $name ($(@($entries).Count) files, mods: $($mods -join ', '))."
}

function Invoke-Finish {
    $manifestPath = Join-Path $stage 'aimmod-release.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Nothing staged at $stage." }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.version -ne $Version -or $manifest.channel -ne $Channel) { throw 'The staged manifest is for another version or channel.' }

    $zip = Join-Path $Output "$name.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

    $notes = if ($NotesFile -and (Test-Path -LiteralPath $NotesFile)) { (Get-Content -LiteralPath $NotesFile -Raw).Trim() } else { '' }
    if ($notes.Length -gt 16000) { $notes = $notes.Substring(0, 16000) }
    $feed = [ordered]@{
        schema = 'aimmod.ingame.feed/1'
        channel = $Channel
        version = $Version
        publishedAt = $manifest.publishedAt
        notes = $notes
        minimumSteamBuildId = [long]$manifest.game.minimumSteamBuildId
        manifestSha256 = Get-Sha256 $manifestPath
        package = [ordered]@{ url = "$DownloadBaseUrl/$name.zip"; sha256 = Get-Sha256 $zip; size = (Get-Item -LiteralPath $zip).Length }
        # Read by AimMod-Setup.exe; the service ignores both.
        minimumInstallerVersion = $MinimumInstallerVersion
        installerUrl = $InstallerUrl.Replace('{channel}', $Channel)
    }
    $feedPath = Join-Path $Output "aimmod-ingame-$Channel.json"
    Write-Utf8 $feedPath ($feed | ConvertTo-Json -Depth 4)
    if ($Channel -eq 'stable' -and $WriteBetaFeed) {
        $write = $true
        if ($ExistingBetaFeed -and (Test-Path -LiteralPath $ExistingBetaFeed)) {
            $existing = Get-Content -LiteralPath $ExistingBetaFeed -Raw | ConvertFrom-Json
            $write = (Compare-SemVer $Version ([string]$existing.version)) -gt 0
        }
        if ($write) {
            $feed.channel = 'beta'
            $feed.installerUrl = $InstallerUrl.Replace('{channel}', 'beta')
            $betaPath = Join-Path $Output 'aimmod-ingame-beta.json'
            Write-Utf8 $betaPath ($feed | ConvertTo-Json -Depth 4)
            Write-Host "The beta feed also offers $Version."
        }
    }
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $Output "$name.manifest.json") -Force
    $listed = @($zip, (Join-Path $Output "$name.manifest.json"))
    if ($SetupExe) {
        if (-not (Test-Path -LiteralPath $SetupExe)) { throw "AimMod-Setup.exe not found: $SetupExe" }
        $listed += (Resolve-Path -LiteralPath $SetupExe).Path
    }
    $sums = foreach ($f in $listed) { "$(Get-Sha256 $f)  $(Split-Path -Leaf $f)" }
    Write-Utf8 (Join-Path $Output "$name.sha256") (($sums -join "`n") + "`n")
    Write-Host "Release ready in ${Output}: $name.zip, $name.manifest.json, aimmod-ingame-$Channel.json."
}

if ($Step -in 'All', 'Stage') { Invoke-Stage }
if ($Step -in 'All', 'Finish') { Invoke-Finish }
