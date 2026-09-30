# map-port

Converts Source-engine maps (CS:S, CS:GO, GMod) into KovaaK's map-creator maps and generates a
scenario with Counter-Strike movement. Pure Python 3.9+, standard library only.

```
map-port <input.bsp|.vmf|.gma|.zip|.rar|.7z> --out <dir> [options]
python -m mapport ...            # same thing, from this folder
```

Output in `<dir>`:

| File | Install to (KovaaK's `FPSAimTrainer` folder) |
| --- | --- |
| `maps/<map>.json` | `FPSAimTrainer/maps/` |
| `Scenarios/<map> CS Movement.sce` | `FPSAimTrainer/Saved/SaveGames/Scenarios/` |
| `<map>.report.json` | not installed: brush counts, drop reasons, material slots |
| `<map>.preview.png` (`--preview`) | not installed: top-down view in radar orientation |

The scenario embeds the map (`[Map Data]`), like the scenarios the game saves, and also names the
map file in `MapName`.

## Options

| Option | Default | Meaning |
| --- | --- | --- |
| `--format json\|reflex\|both` | `json` | `reflex` also writes the legacy `.map` (untextured, see below) |
| `--map-scale` | `4.0` | Unreal units per Source unit. The map is written in Source units; the scenario's `MapScale` scales it |
| `--movement cs\|css\|csgo` | `cs` | `cs`: 250 u/s, accelerate 5.2, friction 4. `css` and `csgo` use those games' defaults |
| `--groups` | `2` | material slot groups. The Default pack has 2 (x 4 surfaces = 8 materials per map) |
| `--disp-step` | `2` | displacement sampling step (1 = every vertex) |
| `--disp-thickness` | `8` | thickness of displacement slabs, Source units |
| `--materials file.json` | built-in | alternative mapping table |
| `--bots` | `5` | harmless strafing target bots on the counter-terrorist spawns |
| `--keep-skybox` | off | keep the 3D skybox and areas detached from the spawns |
| `--pick text` | | convert only archive members whose name contains `text` |

## Map formats in KovaaK's 3.9.x

- The **legacy `.map`** ("reflex map version 8") is rendered with a single world material
  (`LegacyWorldMaterial`), so Reflex material paths do not produce textures. Faces only carry a colour.
- The **map-creator `.json`** (version `1.0.0`) supports textures. `materialSets` holds slot groups
  of four surfaces each (`ground`, `wall`, `ceiling`, `ramp`). Each surface has a material from a
  pack (`Default`, `MI_WA_*`) with `Tint`, `Scale`, `Roughness`, `Metallic` and `FullBright`.
  The Default pack declares `NumSlotGroups = 2`. The editor always writes a third, empty group for
  the Anime DLC pack.
- Brushes are primitives (`mesh` = Cube, Ramp, …) with location, rotation (`roll, pitch, yaw`) and
  scale. A brush whose vertices were edited in the editor is saved with a `procedural` array: one
  section per face with `indices` and `vertices` (`location`, `normal`, optional `uv0` and
  `tangent` = `x, y, z, flipY`). Each section gets a `materialSets` entry `{group, surface}`.
  map-port writes every convex Source brush this way, so the JSON format can represent arbitrary
  convex brushes and per-face materials. The legacy `.map` is not needed as a fallback.
- Triangle winding in `procedural` sections: `cross(v1 - v0, v2 - v0)` points against the outward
  normal.
- Brush `name` is the brush type: `Default`, `DefaultNoCollision`, `Clip` (invisible player
  clip), `FullClip` (glass, blocks everything) or `WeaponClip`.
- `gameObject` spawns: `SpawnPoint` with `TeamMask` (bit 1 = team 1, bit 2 = team 2) and
  `scale = 1 / MapScale`.
- Axes: Unreal is left-handed. Source `(x, y, z)` maps to `(x, -y, z)`, which keeps the layout
  unmirrored: facing north from T spawn, east (the A site on de_dust2) stays on the right. The tests
  check the axis mapping, and `--preview` renders the Source layout in radar orientation.

## Materials

`mapport/materials.json` is the editable mapping table. Rules are matched against the texture
file name first, then the folder path, so `de_dust/` folders do not turn every texture into sand.
With only 8 slots per map, map-port does the following:

1. It groups textures into categories.
2. It merges the smallest categories into their `fallback` until they fit.
3. If slots are left over, it splits the most colour-varied categories.
4. It derives each slot's tint from the Source texdata reflectivity (area-weighted) when `tint` is
   `auto`.

Available Default-pack materials include the following (the loader uses the asset name):
`MI_WA_BrickClay{Beveled,New,Old}`, `MI_WA_BrickCutStone`, `MI_WA_BrickHewnStone`,
`MI_WA_BrickGrey`, `MI_WA_BrickModern`, `MI_WA_BrickFacade`, `MI_WA_PaintedBrickWall`,
`MI_WA_CobbleStone{Pebble,Rough,Smooth}`, `MI_WA_Concrete{Panels,Poured,Tiles,Pavement}`,
`MI_WA_BigConcreteTiles`, `MI_WA_Ground{Grass,Gravel}`, `MI_WA_groundMoss`, `MI_WA_Mud`,
`MI_WA_Metal{Gold,Rust,Steel,Sheet}`, `MI_WA_Rock{Basalt,Sandstone,Slate}`, `MI_WA_StoneGranite`,
`MI_WA_StoneTilesFacade`, `MI_WA_Wood{FloorWalnut,Oak,Pine,Walnut,Parquet,Plank,PlankClean}`,
`MI_WA_{White,Grey}WoodBoard`, `MI_WA_OSB`, `MI_WA_WoodenFloor`, `MI_WA_Drywall`,
`MI_WA_DrywallPanels`, `MI_WA_PlasterFresh`, `MI_WA_Paint`, `MI_WA_Paint_B`,
`MI_WA_MarblePolished`, `MI_WA_*Marble*Tiles`, `MI_WA_WornMarbleFloor`,
`MI_WA_HerringboneBrickPavement`, `MI_WA_PlasticPanelsFacade`, `MI_WA_SciFi*`, `MI_WA_grid*`,
`MI_WA_Checkerboard*`, `MI_WA_invgrid*` and `MI_WA_PureColor`, plus themed sets (Hell, Iron,
Outlaws, Pixel, Christmas, N0ted, Timmy). The Anime pack is DLC.

## What gets converted

- Brushes are world brushes, `func_detail` and solid brush entities (doors, `func_brush`, …).
  Brush-entity origins and angles are applied.
- Player clip, `toolsclip*`, invisible and skybox brushes become `Clip`. Windows, grates and
  `func_breakable_surf` become `FullClip`. `toolsblockbullets` becomes `WeaponClip`.
  `func_illusionary` becomes `DefaultNoCollision`.
- All-nodraw detail brushes are shown as stand-in geometry, because they usually sit behind models
  that cannot be ported.
- Triggers, hint/skip/areaportal/occluder, buy zones, bomb sites, ladders and water are dropped.
- Displacements are turned into convex slabs. Planar patches of the grid are merged greedily, and
  non-planar cells are split into two triangular prisms.
- The 3D skybox (the component around `sky_camera`) and areas detached from the spawn areas are
  dropped.
- Spawns come from `info_player_terrorist` (team 1), `info_player_counterterrorist` (team 2) and
  deathmatch/start spawns (both teams). They are lifted 40 units so the player drops onto the floor.
- Supported inputs: BSP versions 19 to 21, LZMA-compressed lumps and the L4D2-style lump header.
  Also `.vmf`, GMod `.gma` (including LZMA-wrapped workshop downloads), `.zip`, and `.rar`/`.7z`
  through `7z` or the system `tar` (bsdtar).

## Movement profile

Lengths and speeds are Source values x `MapScale`. Gravity is a scale on Unreal's 980 cm/s².
With the default `--map-scale 4`:

| Source | KovaaK's field | Value |
| --- | --- | --- |
| 250 u/s run | `MaxSpeed` | 1000 |
| 5.2 accelerate | `ScaledGroundAcceleration` (+ `EnableQuakeMovement=true`) | 5.2 |
| 4 friction | `ContinuousGroundFriction` / `Friction` | 4 |
| 75 stopspeed | `StopSpeed`, `StopSpeedThreshold` | 300 |
| 10 airaccelerate, 30 u/s air cap | `ScaledAirAcceleration`, `MaxAirSpeed` | 10, 120 |
| sv_gravity 800 | `Gravity` | 3.265 |
| 57 u jump | `JumpVelocityMin/Max` | 1208 |
| 18 u step | `StepUpHeight` | 72 |
| 72 x 32 hull, 54 crouched | `MainBBHeight`, `MainBBRadius`, `CrouchHeightModifier` | 288, 64, 0.75 |

The character's collision capsule follows the main bounding box, so the hull fits Source doorways.
KovaaK's bundled "Counter-Striker" profile uses roughly the same scale (MaxSpeed 1100, step 75).

## Not converted yet

- Models (`prop_static` and others). Nodraw stand-ins approximate some of them.
- Lighting and lightmaps: the game lights maps with its own sky.
- Decals, overlays, water and ladders.
- Texture-accurate UVs. KovaaK's `MI_WA_*` materials are world-aligned, so `uv0` is only a hint.

## Tests

```
python -m unittest discover -s tests
```

The tests build synthetic BSP (v19, v20, v21 and LZMA), GMA, zip and VMF fixtures in code.
