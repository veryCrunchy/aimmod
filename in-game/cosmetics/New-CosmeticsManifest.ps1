<#
.SYNOPSIS
Validates the curated cosmetics catalog and writes its hash-pinned manifest.

.DESCRIPTION
Checks catalog.json with the same rules as the service (Cosmetics.cs) and
CosmeticsCatalog.lua, then writes catalog-manifest.json:

  { "version": <catalog version>,
    "files": [ { "name": "catalog.json", "size": <bytes>, "sha256": "<hex>" },
               { "name": "<pak>.pak", ... } ] }

The service and AimModCore use catalog.json and each pak only when its size
and SHA-256 match this manifest. There is no signing key: the manifest ships
with the AimMod install and is as trusted as the install.

Paks (-Paks) are built by the AimMod team (UE 4.26 editor, see
in-game/docs/cosmetics.md) and are not stored in the repository. Every pak a
non-draft item references must be supplied; a supplied pak no item
references is an error. Pak names must match ReleasePaths.IsPakName: no
subfolders and never a patch pak (_P).

Not yet checked here: the pak index path rule (only new packages under
/Game/AimModCosmetics/). It must exist before the first pak ships.

.EXAMPLE
.\New-CosmeticsManifest.ps1 -Output ..\native-mod\out\package\AimModCore\service\cosmetics
#>
[CmdletBinding()]
param(
    [string]$Catalog,
    [string]$Paks,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $Catalog) { $Catalog = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'catalog.json' }

$kinds = @{
    avatar_tint = @{ parts = @('body'); pak = $false }; avatar_pattern = @{ parts = @('body'); pak = $true }
    weapon_finish = @{ parts = @('weapon', 'arms'); pak = $false }; weapon_pattern = @{ parts = @('weapon', 'arms'); pak = $true }
    accessory = @{ parts = @('body'); pak = $true }; weapon_model = @{ parts = @('weapon'); pak = $true }
    reload_animation = @{ parts = @('arms'); pak = $true }; player_model = @{ parts = @('body'); pak = $true }
}
$pakName = '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\.pak$'
function Test-PakName([string]$Name) { $Name -cmatch $pakName -and -not $Name.Contains('..') -and -not $Name.EndsWith('_P.pak', [StringComparison]::OrdinalIgnoreCase) }
function Is-Int($v) { $v -is [int] -or $v -is [long] }
function Has([object]$o, [string]$name) { $null -ne $o -and $o.PSObject.Properties[$name] -and $null -ne $o.$name }
function In-Range($v, [double]$lo, [double]$hi) {
    ($v -is [int] -or $v -is [long] -or $v -is [double] -or $v -is [decimal]) -and -not [double]::IsNaN([double]$v) -and [double]$v -ge $lo -and [double]$v -le $hi
}

$text = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $Catalog).Path)
$doc = $text | ConvertFrom-Json
$problems = [System.Collections.Generic.List[string]]::new()
if (-not (Has $doc 'version') -or -not (Is-Int $doc.version) -or $doc.version -lt 1) { $problems.Add('catalog version must be an integer >= 1') }
$items = @(if (Has $doc 'items') { $doc.items })
if ($items.Count -eq 0) { $problems.Add('catalog has no items') }
$ids = @{}
$referenced = @{}
# AimMod runtime meshes (.amsh) live next to the catalog source and ship next to the installed catalog.
$meshesDir = Join-Path (Split-Path -Parent (Resolve-Path -LiteralPath $Catalog).Path) 'meshes'
$shapes = @{}
foreach ($item in $items) {
    $id = if (Has $item 'id') { [string]$item.id } else { '' }
    $where = if ($id) { $id } else { '(item without id)' }
    if ($id -cnotmatch '^[a-z0-9][a-z0-9-]{0,47}$') { $problems.Add("${where}: bad id"); continue }
    if ($ids.ContainsKey($id)) { $problems.Add("${id}: duplicate id"); continue }
    $ids[$id] = $true
    if (-not (Has $item 'version') -or -not (Is-Int $item.version) -or $item.version -lt 1 -or $item.version -gt 100000) { $problems.Add("${id}: bad version") }
    $kind = if (Has $item 'kind') { [string]$item.kind } else { '' }
    if (-not $kinds.ContainsKey($kind)) { $problems.Add("${id}: unknown kind '$kind'"); continue }
    $rule = $kinds[$kind]
    $parts = @(if (Has $item 'parts') { $item.parts })
    if ($parts.Count -eq 0) { $problems.Add("${id}: no parts") }
    foreach ($p in $parts) { if ($rule.parts -notcontains $p) { $problems.Add("${id}: part '$p' not allowed for $kind") } }
    $models = @(if (Has $item 'models') { $item.models })
    if ($rule.parts -contains 'body' -and $rule.parts -notcontains 'weapon' -and $models.Count -eq 0) { $problems.Add("${id}: avatar items need base models") }
    foreach ($m in $models) { if ($m -notin @('Meso', 'Endo')) { $problems.Add("${id}: base model '$m' is not a free Default-pack model in this catalog") } }
    $hasParams = $false
    if (Has $item 'vector') {
        foreach ($p in $item.vector.PSObject.Properties) {
            foreach ($c in 'R', 'G', 'B', 'A') { if (-not (Has $p.Value $c) -or -not (In-Range $p.Value.$c 0 1)) { $problems.Add("${id}: vector $($p.Name).$c out of range") } }
            $hasParams = $true
        }
    }
    if (Has $item 'scalar') {
        foreach ($p in $item.scalar.PSObject.Properties) { if (-not (In-Range $p.Value -10 10)) { $problems.Add("${id}: scalar $($p.Name) out of range") }; $hasParams = $true }
    }
    foreach ($field in 'file', 'path', 'url', 'texture') { if (Has $item $field) { $problems.Add("${id}: field '$field' is not allowed (catalog items never name player files)") } }
    $pak = if ((Has $item 'pak') -and (Has $item.pak 'file')) { [string]$item.pak.file } else { $null }
    # Accessories fitted from the game's own meshes need no pak (the same allow-list as AimModCore and the service).
    $mesh = if (Has $item 'mesh') { [string]$item.mesh } else { '' }
    $material = if (Has $item 'material') { [string]$item.material } else { '' }
    $role = if ((Has $item 'attach') -and $item.attach -is [string]) { $item.attach } elseif ((Has $item 'attach') -and (Has $item.attach 'role')) { [string]$item.attach.role } else { '' }
    $fitted = (Has $item 'attach') -and $item.attach -isnot [string] -and (Has $item.attach 'fit')
    $shape = if (Has $item 'shape') { [string]$item.shape } else { '' }
    if ($shape) {
        if ($kind -ne 'accessory' -or $shape -cnotmatch '^[a-z0-9][a-z0-9-]{0,42}\.amsh$' -or $mesh) { $problems.Add("${id}: a runtime mesh is an accessory's own shape") }
        elseif (-not (Test-Path -LiteralPath (Join-Path $meshesDir $shape))) { $problems.Add("${id}: mesh file $shape is missing from $meshesDir") }
        else { $shapes[$shape] = $true }
    }
    $gameAccessory = $kind -eq 'accessory' -and -not $pak -and $fitted -and -not $mesh.Contains('..') -and -not $material.Contains('..') -and
        ($mesh -cmatch '^(/Engine/BasicShapes/|/Game/Art/StaticMeshes/KMC/Brushes/)[A-Za-z0-9_.-]{1,96}$' -or (-not $mesh -and $shape)) -and
        $material -cmatch '^/Game/Materials/Instances/Characters/S_(Meso|Endo)/Base/MI_PaintedMetal_[A-Za-z0-9_.-]{1,96}$'
    if ($kind -eq 'accessory' -and $role -notin @('head', 'neck', 'spine')) { $problems.Add("${id}: accessories attach to head, neck or spine") }
    if ($rule.pak -and -not $pak -and -not $gameAccessory) { $problems.Add("${id}: $kind needs a pak") }
    if (-not $rule.pak -and -not $hasParams) { $problems.Add("${id}: no parameters") }
    if ($pak) {
        if (-not (Test-PakName $pak)) { $problems.Add("${id}: pak name '$pak' not allowed") }
        $draft = (Has $item 'draft') -and $item.draft -eq $true
        if (-not $referenced.ContainsKey($pak)) { $referenced[$pak] = $false }
        if (-not $draft) { $referenced[$pak] = $true }
    }
}

$pakFiles = @()
if ($Paks) {
    $pakFiles = @(Get-ChildItem -LiteralPath $Paks -File)
    foreach ($f in $pakFiles) {
        if (-not (Test-PakName $f.Name)) { $problems.Add("pak '$($f.Name)' has a name that is not allowed") }
        elseif (-not $referenced.ContainsKey($f.Name)) { $problems.Add("pak '$($f.Name)' is not referenced by any catalog item") }
    }
}
foreach ($p in $referenced.Keys) {
    if ($referenced[$p] -and -not ($pakFiles | Where-Object Name -ceq $p)) { $problems.Add("pak '$p' is needed by a non-draft item but was not supplied (-Paks)") }
}
if ($problems.Count) { throw ("Cosmetics catalog rejected:`n  " + ($problems -join "`n  ")) }

New-Item -ItemType Directory -Force -Path $Output | Out-Null
$Output = (Resolve-Path -LiteralPath $Output).Path
# Byte-exact copy: the manifest pins these bytes.
$catalogOut = Join-Path $Output 'catalog.json'
[IO.File]::WriteAllBytes($catalogOut, [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Catalog).Path))
function Entry([string]$Path) {
    $i = Get-Item -LiteralPath $Path
    [ordered]@{ name = $i.Name; size = $i.Length; sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$meshFiles = @(foreach ($name in $shapes.Keys | Sort-Object) {
    $target = Join-Path $Output $name
    [IO.File]::WriteAllBytes($target, [IO.File]::ReadAllBytes((Join-Path $meshesDir $name)))
    $target
})
$files = @(Entry $catalogOut) + @(foreach ($m in $meshFiles) { Entry $m }) + @(foreach ($f in $pakFiles | Sort-Object Name) { Entry $f.FullName })
$manifest = [ordered]@{ version = [int]$doc.version; files = $files }
$json = ($manifest | ConvertTo-Json -Depth 4) -replace "`r`n", "`n"
[IO.File]::WriteAllText((Join-Path $Output 'catalog-manifest.json'), $json + "`n", [Text.UTF8Encoding]::new($false))
$pickable = @($items | Where-Object { -not ((Has $_ 'draft') -and $_.draft -eq $true) }).Count
Write-Host "Cosmetics catalog v$($doc.version): $($items.Count) items ($pickable pickable), $($meshFiles.Count) mesh(es), $($pakFiles.Count) pak(s); manifest in $Output."
