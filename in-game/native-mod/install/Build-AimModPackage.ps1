<#
.SYNOPSIS
Builds the AimModCore install package (in-game/native-mod/out/package).

.DESCRIPTION
Requires the local RE-UE4SS checkout (see DESIGN.md, Building), Visual Studio
2022 and the .NET 8 SDK. Produces:
  AimModCore\dlls\main.dll       the UE4SS mod
  AimModCore\service\            the native service (self-contained)
  AimModNativeUI\                the Lua UI mod
  UE4SS-settings.ini             the stable settings profile
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\out\package'),
    [switch]$FrameworkDependentService
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

Copy-Item (Join-Path $inGame 'ue4ss\AimModNativeUI') (Join-Path $Output 'AimModNativeUI') -Recurse
Copy-Item (Join-Path $PSScriptRoot 'UE4SS-settings.ini') (Join-Path $Output 'UE4SS-settings.ini')
Write-Host "Package ready: $((Resolve-Path $Output).Path)"
