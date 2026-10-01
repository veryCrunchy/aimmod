<#
.SYNOPSIS
Builds the AimModCore install package (in-game/native-mod/out/package).

.DESCRIPTION
Requires the local RE-UE4SS checkout (see DESIGN.md, Building), Visual Studio
2022 and the .NET 8 SDK. Produces:
  AimModCore\dlls\main.dll       the UE4SS mod
  AimModCore\service\            the native service (self-contained)
  AimModCore\service\cosmetics\  the curated cosmetics catalog.json and its
                                 hash-pinned catalog-manifest.json
  AimModNativeUI\                the Lua UI mod
  UE4SS-settings.ini             the stable settings profile
  Paks\~AimMod\*.pak             AimMod cosmetics paks (only with -CosmeticsPaks),
                                 installed into the game's Content\Paks\~AimMod
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\out\package'),
    [switch]$FrameworkDependentService,
    # Folder with the team-built AimMod cosmetics paks (not in the repository).
    [string]$CosmeticsPaks
)
$ErrorActionPreference = 'Stop'
$native = Resolve-Path (Join-Path $PSScriptRoot '..')
$inGame = Resolve-Path (Join-Path $native '..')
$build = Join-Path $native 'build'

if (-not (Test-Path (Join-Path $build 'AimModNative.sln'))) {
    cmake -S $native -B $build -G 'Visual Studio 17 2022' -A x64
    if ($LASTEXITCODE) { throw 'CMake configure failed.' }
}
cmake --build $build --config Game__Shipping__Win64 --target AimModCore aimmod_core_tests
if ($LASTEXITCODE) { throw 'Build failed.' }
$tests = Get-ChildItem (Join-Path $build 'Game__Shipping__Win64') -Recurse -Filter aimmod_core_tests.exe | Select-Object -First 1
& $tests.FullName
if ($LASTEXITCODE) { throw 'Core tests failed.' }

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $Output 'AimModCore\dlls') | Out-Null
Copy-Item (Join-Path $build 'Game__Shipping__Win64\main.dll') (Join-Path $Output 'AimModCore\dlls\main.dll')

$service = Join-Path $Output 'AimModCore\service'
$publish = @('publish', (Join-Path $inGame 'native-service\AimMod.InGame.csproj'), '-c', 'Release', '-r', 'win-x64', '-o', $service)
if ($FrameworkDependentService) { $publish += @('--self-contained', 'false') }
else { $publish += @('--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true') }
dotnet @publish
if ($LASTEXITCODE) { throw 'Service publish failed.' }
Get-ChildItem $service -Filter *.pdb | Remove-Item

# Curated cosmetics: catalog.json plus its manifest (size and SHA-256 of the
# catalog and every pak). The service and AimModCore use only matching files.
$manifestArgs = @{ Output = (Join-Path $service 'cosmetics') }
if ($CosmeticsPaks) { $manifestArgs.Paks = (Resolve-Path -LiteralPath $CosmeticsPaks).Path }
& (Join-Path $inGame 'cosmetics\New-CosmeticsManifest.ps1') @manifestArgs
if ($CosmeticsPaks) {
    $paksOut = Join-Path $Output 'Paks\~AimMod'
    New-Item -ItemType Directory -Force -Path $paksOut | Out-Null
    Get-ChildItem -LiteralPath $manifestArgs.Paks -File | Copy-Item -Destination $paksOut
}

Copy-Item (Join-Path $inGame 'ue4ss\AimModNativeUI') (Join-Path $Output 'AimModNativeUI') -Recurse
Copy-Item (Join-Path $PSScriptRoot 'UE4SS-settings.ini') (Join-Path $Output 'UE4SS-settings.ini')
Write-Host "Package ready: $((Resolve-Path $Output).Path)"
