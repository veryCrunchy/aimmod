# map-port

Converts Source (CS:S, CS:GO, GMod), GoldSrc (CS 1.6, Half-Life) and Quake 3 / Quake Live maps into
KovaaK's map-creator maps, and generates a scenario with Counter-Strike or Quake movement. Pure
Python 3.9+, standard library only; the Workshop thumbnails additionally need Pillow.

```
map-port <input.bsp|.vmf|.gma|.zip|.pk3|.rar|.7z> --out <dir> [options]
python -m mapport ...            # same thing, from this folder
```

Output in `<dir>`:

| File | Install to (KovaaK's `FPSAimTrainer` folder) |
| --- | --- |
| `maps/aimmod_<mapid>_<game>.json` | `FPSAimTrainer/maps/` |
| `Scenarios/AimMod - <Map> (<Game>) - <Variant>.sce` | `FPSAimTrainer/Saved/SaveGames/Scenarios/` |
| `Abilities/CS Walk.abilsprint` (or `Quake Walk` / `Sprint`) | `FPSAimTrainer/Saved/SaveGames/Abilities/` |
| `aimmod_<mapid>_<game>.workshop-thumb.png/.jpg` (1024²), `-16x9` (1920 x 1080) | not installed: Workshop thumbnails |
| `aimmod_<mapid>_<game>.thumb-views.json` | not installed: camera views for AimModCore's `capture-thumbnail` |
| `Capture/AimMod Capture - <file id>.sce` | install only to capture a thumbnail: the same map with no bots |
| `aimmod_<mapid>_<game>.aimmod.json` | not installed: game-mode metadata for AimMod (see below) |
| `aimmod_<mapid>_<game>.report.json` | not installed: brush counts, drop reasons, material slots |
| `aimmod_<mapid>_<game>.preview.png` | not installed: preview check (see below); `--no-preview` skips it |
| `aimmod_<mapid>_<game>.views/` (`--views`) | not installed: first-person check renders |

The scenario embeds the map (`[Map Data]`) and the Shift ability, like the scenarios the game
saves. It also names the map file in `MapName`.

## Names

Ported scenarios are published to the Workshop, and the scenario name is the leaderboard key, so
names are fixed by `mapport/naming.py` and must never change:

- **Scenario** (internal name, `.sce` file name and Workshop title): `AimMod - <Map> (<Game>) - <Variant>`,
  e.g. `AimMod - Dust2 (CSGO) - CS Movement` or `AimMod - aim_map (CSS) - CS Movement`.
- **Map file:** `aimmod_<mapid>_<game>.json`, e.g. `aimmod_de_dust2_csgo.json`.
- **Game tags:** `CSGO`, `CSS`, `CS2`, `CS16`, `GMod`, `Q3`, `QL`. There is no colon, because `:` is illegal in Windows
  file names; descriptions may write "CS:GO".
- **Map names:** well-known maps use their display name (Dust2, Mirage, Inferno, …); others keep
  their id (aim_map).

`--display-name`, `--game` and `--variant` override the parts. Names containing characters illegal
in Windows file names are rejected.

## Tags and description

`mapport/tags.py` writes the scenario's `SearchTags`, in this order, deduplicated:

1. `AimMod`, `Map port`
2. the source game (`Counter-Strike: Source`, `Counter-Strike 1.6`, `Quake 3`, `Quake Live`, …) and its
   family (`Counter-Strike`, `Quake`)
3. the movement: `CS movement`, `Sprint movement` or `Quake movement`, plus `Strafe jumping` and
   `Bunny hop` when air speed is not capped (the Quake preset)
4. the map type from the map id (`aim_` Aim map, `fy_` Fight yard, `awp_` AWP, `de_` Defuse,
   `cs_` Hostage, `…dm<n>` Deathmatch, `tourney` or a known duel map: Duel) and from objective entities
   (bomb targets, hostages, CTF flags)
5. a weapon the map name implies (`Deagle`, `AWP`, `Scout`, …)
6. features of the emitted objects: `Water`, `Lava`, `Slime`, `Jump pads`, `Teleporters`, `Ladders`
   (ladders become jump pads)

KovaaK's sets no tag limit (`SearchTags` is one string), so ports keep to at most 16 tags of up to 32
characters and 255 characters in all. `AimTypeTag`/`AimSubTypeTag` are `Clicking`/`Dynamic` (values
the game's scenario editor offers), `DifficultyTag` is 2 for CS movement and 3 for Quake movement.
The description names the map, the source game and the movement, and stays within the editor's
352 characters. Generated `AimMod Match - …` arenas carry only `AimMod, AimMod Match`.

## Options

| Option | Default | Meaning |
| --- | --- | --- |
| `--format json\|reflex\|both` | `json` | `reflex` also writes the legacy `.map` (untextured, see below) |
| `--map-scale` | `4.0` | Unreal units per Source unit. The map is written in Source units; the scenario's `MapScale` scales it |
| `--movement` | `cs` | preset from `mapport/movement_presets.json`: `cs`, `css`, `csgo`, `cs2` (Shift walks) or `sprint` (Shift sprints) |
| `--groups` | `2` | material slot groups. The Default pack has 2 (x 4 surfaces = 8 materials per map) |
| `--disp-step` | `2` | displacement sampling step (1 = every vertex) |
| `--disp-thickness` | `8` | thickness of displacement slabs, Source units |
| `--materials file.json` | built-in | alternative mapping table |
| `--bots` | `5` | harmless strafing target bots on the counter-terrorist spawns |
| `--no-preview` | off | skip the preview check PNG |
| `--views` | off | render first-person check views (spawns plus spread-out floor spots) and report holes |
| `--view X,Y,Z,YAW` | | extra check view (Source feet position), repeatable |
| `--no-props` | off | skip model hulls |
| `--no-ground` | off | skip the backdrop ground plane |
| `--keep-skybox` | off | keep the 3D skybox and areas detached from the spawns |
| `--pick text` | | convert only archive members whose name contains `text` |

## Input formats

| Format | How the geometry is rebuilt |
| --- | --- |
| Source BSP v19-21 (`VBSP`) | brushes from the brush lump, displacements as slabs, packed models as hulls |
| Hammer `.vmf` | brushes and displacements from the editor file |
| GoldSrc BSP v30 (CS 1.6) | no brushes are stored: every path from a model's head node to a solid (or sky) leaf of the BSP tree is a convex cell, which is the map's solid volume. Cell faces take the texture of the rendered face on the same plane; embedded miptex (or `.wad` files next to the map) give the average colour. Sky leaves become clips; player-clip hulls are not used. In CS 1.6, `info_player_deathmatch` is T and `info_player_start` is CT. |
| Quake 3 IBSP v46 / Quake Live v47 | brushes and shaders directly (`common/caulk` -> nodraw, `common/clip` -> clip, …); bezier patches are tessellated and turned into slabs that follow the vertex normals. `trigger_push` + `target_position` becomes a KovaaK's **JumpPad** aimed at a **Waypoint**; `trigger_teleport` + `misc_teleporter_dest` becomes a **Teleporter** with its Waypoint. Spawns: `info_player_deathmatch` (both teams). |

**Liquids:**
- Water brushes become map-creator **Water** volumes: Source `CONTENTS_WATER` brushes, GoldSrc
  water leaves and `func_water`, and Quake 3 water shaders.
- Lava and slime become **Hurt** volumes (lava kills, slime does 10 damage a second).
- Each object covers its liquid box exactly. The sizes and pivots come from the game's own meshes
  (3.9.11): Water is a 200-unit cube centred on the actor, so the surface is the brush's top face.
  Hurt, JumpPad and Teleporter are 100-unit cubes with the pivot on the minimum corner. The object
  scale is multiplied by MapScale like the locations.
- Water uses the game's default colours and no wave height.
- KovaaK's Water is only a translucent mesh: no swimming. In AimMod scenarios AimModCore makes every
  Water object swimmable (no collision, an engine water volume with CS or Quake swimming, an
  underwater tint; see `in-game/native-mod/DESIGN.md`, "Water"). The volumes come from the map
  data itself, so generated `AimMod Match - …` arenas and every lobby peer get the same water.

Quake 3 stock textures are not in the map files, so Q3 slots use each category's typical colour
(`colour` in `materials.json`). Ladders (`func_ladder`, ladder contents) cannot be climbed in
KovaaK's; each becomes a jump pad at its foot aimed just above its top.

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
- **Procedural vertices are scaled twice.** In map units, world = location + scale x vertex x
  MapScale. So the editor stores a 100-unit cube as `100 / MapScale` (26.2295 at 3.8125, 20 at 5).
  Locations and spawn points are only scaled once, by MapScale. map-port divides vertices by
  MapScale. Without that, every brush grows around its corner and the map feels cramped.
  This was calibrated against `cataicfps.map` and its JSON remake. Reflex `(a, b, c)` loads as JSON
  `(c, a, b)` in the same units, and the Reflex yaw equals the Unreal yaw.
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
- All-nodraw solid brushes (world and detail) are shown as stand-in geometry. In Source, models
  cover them; without the models they would be invisible walls with holes into the void.
- **Models** (`prop_static` from the `sprp` game lump, and prop entities) are rebuilt from the model
  files packed in the BSP:
  - A solid prop with a packed `.phy` becomes its exact convex collision pieces (visible and solid).
  - Any other packed model (`.mdl` + `.dx90.vtx` + `.vvd`) is split into triangle-connected parts.
    Each part becomes a non-colliding 26-sided hull, which brings back stairs, trims, frames and
    beams; the map's own clip brushes provide their collision.
    Box-like parts use their model-space box (6 faces); others use an 18-sided hull. Parts smaller
    than 12 units are skipped, and at most 40 parts are kept per model.
  - Stock models that ship with the game rather than the map can't be read. Where the model name
    carries a size (`dust_crate_37x37x74`, `dust_door_80x128`), a non-colliding box stands in. The
    pivot (floor, centre or hinge) is chosen per model as the one that leaves the box least buried
    in the map's solids. Everything else is listed in the report.
  - Sloped clip brushes next to an unported stairs model are made visible (stone), because CS:GO
    covers model stairs with an invisible clip ramp.
- A wide ground plane sits 32 units under the lowest geometry, so any remaining hole shows ground
  instead of the void.
- Hint/skip/areaportal/occluder brushes and most triggers are dropped (liquids, `trigger_hurt`,
  `trigger_push` and teleporters become game objects, ladders jump pads). Buy zones, bomb sites
  and other objective volumes are not rendered; they go to the metadata file.
- Displacements are turned into convex slabs. Planar patches of the grid are merged greedily, and
  non-planar cells are split into two triangular prisms.
- The 3D skybox (the component around `sky_camera`) and areas detached from the spawn areas are
  dropped.
- Every spawn is checked against the blocking brushes with a standing CS hull (32 x 72). If the
  hull overlaps a brush, the spawn is moved upwards, then sideways in growing rings. The report
  lists `spawns_nudged` and `spawns_stuck`.
- Spawns come from `info_player_terrorist` (team 1), `info_player_counterterrorist` (team 2) and
  deathmatch/start spawns (both teams). They are lifted 40 units so the player drops onto the floor.
- Supported inputs: BSP versions 19 to 21, LZMA-compressed lumps and the L4D2-style lump header.
  Also `.vmf`, GMod `.gma` (including LZMA-wrapped workshop downloads), `.zip`, and `.rar`/`.7z`
  through `7z` or the system `tar` (bsdtar).

## Game-mode metadata

`aimmod_<mapid>_<game>.aimmod.json` (`"format": "aimmod.map-objectives"`, `"version": 1`) holds the
following, in KovaaK's map units and axes (Unreal X/Y/Z with Source Y mirrored; multiply by
`map_scale` for centimetres):

- `zones`: bomb sites, buy zones, hostage rescue and capture areas, each with its AABB, hull
  points, team and name.
- `points`: `info_bomb_target`, hostage spawns and CTF flags.
- `items`: `weapon_*` and `item_*` spawns, with class, origin and yaw.
- `spawns`: team spawns with their team.

### Liquids under the floor

Water, slime and lava brushes sealed under solid ground are dropped
(`cleanup.remove_buried_liquids`). A brush is sealed when solid geometry
covers 80 % of its top, starting at the water line or up to 96 units above
it. Source hides such brushes under the floor, but KovaaK's draws its Water
through the floor. Hurt triggers and kill volumes are never dropped. The
report counts `buried_water_removed`.

### CS map spec (`cs` block)

CS competitive only runs on maps whose `.aimmod.json` has a `cs` block
(`"format": "aimmod.cs-map"`, `"version": 1`, built by `mapport/csmap.py`):

- `spawns`: `T` and `CT`, each `[x, y, z, yaw]`; at least 5 per side.
- `bomb_sites`: `A` and `B`, each `{name, min, max}`. They come from
  `func_bomb_target` volumes, else a box around `info_bomb_target`. Letters
  come from the targetname (`bombsite_a`, `..B`); otherwise A is the site
  farther from the T spawns (true on dust2).
- `buy_zones`: `T` and `CT`, lists of `{min, max}`. They come from
  `func_buyzone` (Source `TeamNum` 2/3, GoldSrc `team` 1/2), else a box
  around that side's spawns.
- `callouts` (optional): `{name, min, max}` areas shown on the HUD.
- `derived`: what was not in the map and was worked out instead.
- `problems`: why the map isn't eligible. Empty means eligible.

A port adds the block for `de_` maps and any map with bomb sites. To add it
to existing ports, run:

```
python -m mapport cs <.aimmod.json files or folders> [--out <folder>]
```

Without `--out` the files are edited in place. With it, copies are written
to `<out>/<map folder>/`.

## Movement profile

Presets live in `mapport/movement_presets.json`, which other AimMod services reuse. Lengths and
speeds are Source values x `MapScale`. Gravity is a scale on Unreal's 980 cm/s². With the default
`--map-scale 4`:

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

- **Movement model.** CS presets (`model: "ue"`) use Unreal character movement like KovaaK's own
  Counter-Striker profile: MaxSpeed, Acceleration (accelerate x run speed), Friction, and a constant
  BrakingDeceleration (friction x stopspeed). KovaaK's Quake/Source movement mode ignores the sprint
  multiplier, so it would make Shift do nothing. The `quake` preset keeps that mode for strafe
  jumping; its Shift walk is written but may have no effect.
- **No air speed gain.** `ClampVelocityToInputSpeed=true` caps horizontal speed at the input speed
  (run, walk or crouch). With it off, `ScaledAirAcceleration` (a multiple of MaxSpeed) piled speed
  on in the air and jumps outran walking. `AirControl` is 0.3.
- **Shift is Ability 1** (Left Shift by default). The preset puts a held sprint ability there with
  `SpeedModifier = shift_speed_mult`: 0.52 walks like CS (130 u/s) and 1.3 sprints. It works in
  every direction.
- **Ctrl crouches:** `MaxCrouchSpeed` = 34 % of run speed, and the hull goes to 54 / 72.
- `mapport/movesim.py` is a reference Source movement model. The tests use it to check a preset:
  a jump reaches 57 units with 0.755 s air time and never gains speed, and one strafe jump gains
  only a little. KovaaK's own implementation isn't public, so the in-game feel still needs testing.

## Hard checks

Every conversion runs these checks; a failure makes `map-port` exit with code 3 (use
`--allow-check-fail` to accept the output anyway). The results are in the report under `checks`.

- **Walk graph:**
  - Samples every 32 units on walkable faces, each with room for a crouched player hull. The
    outside of the skybox shell is not walkable.
  - Edges use the preset's step and jump height and drops of up to 300 units. A vertical sweep at
    the lower spot stops drops through floors and jumps through ceilings.
  - Running jumps reach up to 128 units (224 for strafe-jumping presets) over a clear arc.
  - Jump pads and teleporters also add edges.
- **Spawns:** every spawn must walk to at least half of its own team. Walled-off teams (awp maps)
  are allowed and reported as `teams_connected`. One cut-off spawn is reported (the walk graph can
  miss a precise jump); two or more fail the run.
- **Hurt volumes:** `trigger_hurt` (Source, GoldSrc, Quake 3) becomes a Hurt volume; 100+ damage
  kills.
- **Spawns** need ground under them.
- **Stuck spots:** walk-graph spots you can reach but never leave again (no way back to any spawn),
  outside water. They fail the run when there are more than 8, or more than 2 % of the reachable
  area; examples are listed.
- **No near-black faces** in the playable area.
- **Props and stand-ins must be supported.** Anything without map geometry within 48 units below is
  removed before the check. Stand-in boxes are snapped onto the floor, or skipped when there is none.

The first-person check views (`--views`) use 8 well-spread spots from the walk graph.

## Workshop thumbnails

- `thumb-views.json` holds 1-3 camera views, in KovaaK's world coordinates (Unreal cm:
  `x = source_x * MapScale`, `y = -source_y * MapScale`, `z = source_z * MapScale`,
  `yaw = -source_yaw`, pitch positive up). It also holds the ready-to-POST `capture-thumbnail`
  request for the bot-free `Capture/` scenario. The first view is the most open high vantage,
  looking across the main area.
- Until a capture exists, the first view is software-rendered (material colours, sun, sky gradient
  and haze). The AimMod template goes on top: logo, map name, source-game text chip and the
  movement variant. The source game is text only; no game logos are used.
- `python -m mapport.thumbnail <port-dir> --capture shot.png` re-composites the thumbnails from an
  in-game capture.
- The report's `files.preview` points at the 1024² thumbnail, which the Workshop bundle helper uses.
  The top-down check image is `files.preview_check`.

## Preview check

`<map>.preview.png` has a top-down view in radar orientation (+X east, +Y north) with a 256-unit
scale bar. Below it is one zoomed side section per team, looking north through the spawn area.
Each spawn's CS hull (32 x 72) is drawn as a box, and a white bar marks 72 units. Use it to see
that spawns are clear of walls and that doorways and ceilings fit the player before testing
in-game.

## Bots

Target bots use the humanoid `Meso` character model with the `McCree` skin from the Default
character pack, with `MeshHitDetection=true`, so shots register on the mesh's head and body. The
Meso mesh is 184 cm tall, floor-aligned and fitted to the 72 x 32 CS hull. Other free models are
Endo, Ecto, StylizedEcto, StylizedShape, Pill, Pigeon, Witch, Mummy, Ghost, Diver, Medusa,
JackOLantern and Pumpkin. Meso skins are Genji, McCree, Pharah and Tracer. Change `BOT_MODEL` and
`BOT_SKIN` in `mapport/scenario.py` to use another one.

## Not converted yet

- Stock models that are not packed in the map, and exact model shapes (packed models become hulls).
- Lighting and lightmaps: the game lights maps with its own sky.
- Decals, overlays and climbable ladders.
- Texture-accurate UVs. KovaaK's `MI_WA_*` materials are world-aligned, so `uv0` is only a hint.

## Tests

```
python -m unittest discover -s tests
```

The tests build synthetic BSP (v19, v20, v21 and LZMA), GMA, zip, VMF, .phy and .vvd fixtures in code.
