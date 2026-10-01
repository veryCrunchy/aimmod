# Cosmetics

Design and feasibility study for curated AimMod cosmetics in KovaaK's:
tints, patterns, accessories, weapon finishes and models, reload animations
and player models. They are visible only in AimMod's own modes.

Baseline: KovaaK's 3.9.11, Unreal 4.26, UE4SS 3.0.1-1152. Evidence comes from
the UE4SS object dump, the CXX and UHT header dumps of that build, and a
read-only look at the installed game files. Anything that needs a live run is
marked **verify with the probe**.

## Policy

Agreed with the KovaaK's developer ("Should be fine"):

1. **AimMod modes only.** Cosmetics take effect only in AimMod multiplayer:
   - AimMod match scenarios;
   - AimMod avatars;
   - spectating.

   Normal scenarios, challenges, benchmarks, ranked and the scenario editor are
   unchanged. Players without the mod get the same scenarios as players with
   it.
2. **Curated content only.** The AimMod team makes every texture, tint,
   accessory, weapon model, animation and player model. Players cannot upload
   or load their own files, and no code path loads a player-supplied texture
   or mesh. Lobbies share catalog item ids, never files.
3. **Shipped and verified.** Catalog content ships with the AimMod install or
   in AimMod pak files. It is versioned and pinned by SHA-256 and size in a
   catalog manifest that ships with the AimMod install. Only files that match
   the manifest are used.
4. **Paks are allowed.** "Anticheat shouldn't care unless you try to poke it."
   AimMod paks use the engine's normal startup mount and nothing else.
5. **Visuals only.** Cosmetics never change:
   - hit detection, collision, physics assets, bounding boxes or the capsule;
   - `ShotOrigin`;
   - weapon stats, fire rate, spread or reload timing;
   - scoring or input.
6. **No paid content.** Cosmetics never load, apply, preview or expose KovaaK's
   DLC or store items (`Anime_*`, `PrimeWeaponSkinPack`). Avatar items apply
   only over free Default-pack looks.

## Scope enforcement

`CosmeticsScope.decide` is the single gate. Every tick, every condition below
must hold, and any unknown value counts as "off".

| Check | Source |
|---|---|
| AimMod session marker exists, version 1, mode `match`, `spectate` or `lobby`, and expires within the next 120 s | `%LOCALAPPDATA%/AimMod/KovaaksNative/aimmod-session.txt`, written by the service |
| The current scenario is an AimMod generated match scenario: `AimMod Match - <name> - <8 hex>` | `ScenarioManager:GetCurrentScenario():GetName()`, the same pattern as `MatchScenario.FilePattern` |
| It is the scenario named in the marker | marker `scenario=` |
| Not in a challenge | `ScenarioManager:IsInChallenge()` and `Scenario:IsInChallenge()` must both be `false`. Match scenarios run in freeplay (`MatchScenario.SafeMode`), so they submit no scores. |
| Not a benchmark, not the editor, not loading | `IsCurrentlyInBenchmark`, `IsInScenarioEditor` and `IsScenarioLoading` must be `false` |

| Result | Avatars | Your own weapon and arms |
|---|---|---|
| `match` | yes | yes |
| `spectate` | yes | no |
| `lobby` | no (picker and preview only) | no |
| anything else | no | no |

When the gate closes, every slot the applier dressed is set back to the
game's own material. `apply.test.lua` asserts that no material, mesh,
visibility or collision call happens in each of these cases:

- a normal scenario;
- a forged marker;
- a match-named scenario without a session;
- a challenge;
- a benchmark;
- the editor;
- a lobby.

It also asserts the restore on exit. Breaking `decide` fails that test.

The marker format is:

```
v=1
mode=match
scenario=AimMod Match - <name> - <key>
expires=<unix seconds>
```

The service must write it while in a lobby, match or spectate session,
refresh it every 30 s, and delete it on exit. That writer belongs on the
multiplayer branch and doesn't exist yet. Until it does, the prototype stays
off by design.

Any C++ implementation (phase 1+) must port the same gate and test vectors.

## How KovaaK's does looks

### Characters

Looks are data assets in `/Script/GameSkillsTrainer`.

| Type | What it holds |
|---|---|
| `UMetaSkeletalCharacterModel` | `SkeletalMesh`, `PhysicsAsset`, `AnimationBlueprintClass`, `bCanUseSkeletalMeshHitDetection`, `HeadShotBoneName` |
| `UMetaStaticCharacterModel` | the cylinder, sphere and cube "shape" models |
| `UMetaSkeletalCharacterSkin` | `SkeletalMesh`, `Materials[]`, `PhysicsAsset`, plus `Accessories[]` (from `UMetaCharacterSkin`) |
| `UMetaStaticCharacterAccessory` | `Mesh` (`UStaticMesh`) and `AttachmentPoint`; `USkeletalMeshAttachmentPoint` has a `SocketName` |
| Packs: `UCharacterModelPackDataAsset`, `UCharacterSkinPackDataAsset`, `UWeaponSkinPackDataAsset` | a `UDLCPackDataAsset` gated by `UDLCPackBP::IsAvailable()` |

Packs loaded in 3.9.11:

- **Character models:** `Default_CharacterModelPack`, `Anime_CharacterModelPack`
- **Character skins:** `Default_CharacterSkinPack`, `Anime_CharacterSkinPack`
- **Weapon skins:** `DefaultWeaponSkinPack`, `AnimeWeaponSkinPack`, `PrimeWeaponSkinPack`

Skins are applied by:

- `UCharacterSkinBlueprint::ApplyCharacterSkin` (`StaticCharacterSkinBP_C`)
- `USkinFunctionLibrary.ApplySkeletalMesh`, `ApplyMaterials` and `ApplyCustomPrimitiveData*`

These act through `ICharacterModelInterface`, which `AMetaCharacter`
implements.

**Default Meso skins.** One Meso mesh is resident (`S_Meso`). The free skins
are `Meso_CharacterSkin_Genji`, `_McCree`, `_Pharah` and `_Tracer`, with
texture sets TS1 and TS2. That suggests material variants on a shared mesh;
**verify with the probe**, which prints each skin's mesh and materials.

**Meso materials are mask-driven.** The materials are `MI_PaintedMetal_Meso_TS1`
and `TS2`, `MI_PaintedMetal_MesoHead_TS1` and `MI_Player_Meso_TS1`. Their
textures are `T_Meso_TS1_Masks`, `Masks2`, `Normal` and `Materials`; none of
them is a base-colour map. Colour comes from material parameters and custom
primitive data (`SetColors`, `TickColors`, `ColorManagerComponent`).

Consequences:

- **Tints and multi-tone patterns are parameter-only.** They need no pak.
- **New pattern shapes need a curated mask texture in a pak.** It is authored
  on the Meso UV layout and set on the material's mask parameter.

A cooked build can't compile new material permutations, so dynamic instances
are always parented on the slot's existing material.

**Viewer-equipped skins.** Each viewer can equip target skins per bounding
box type (`Set_EquippedCharacterModelIDAtBoundingBoxType`), possibly DLC they
own. Avatar items therefore apply only when `GetCharacterModelName` and
`GetCharacterSkinName` are in the Default packs.

### Avatars and hit detection

The steam bridge spawns remote players as inert `AMetaCharacter` bots:

- `TheMetaAIController:Spawn`
- `SetUseWeapons(false)`
- `OverrideInvulnerable(true)`

It sets their look with `LoadCharacterProfile("AimMod Meso McCree")` from
`aimmod.char.<SteamID>`.

These are never modified:

- `CapsuleComponent`
- `BodyBB` and `ProjBB`
- the hitbox shapes: `CylHead`, `CylBody`, `CylTopSphere`, `CylBottomSphere`, `SphereHead`, `SphereBody`, `CubeHead`, `CubeBody`, and their `P*` projectile variants
- `PhysicsAsset`

Some models hit-test the skeletal mesh itself (`K2_IsUsingMeshHitDetection`,
`bCanUseSkeletalMeshHitDetection`). So `ACharacter.Mesh` (`CharacterMesh0`)
never gets a different `SkeletalMesh`; custom models are separate follower
components.

### First-person weapon and animations

`AMetaCharacter.ViewModel_Native` (`AFPSPlayer_WeaponComponentActor`) holds
the viewmodel:

| Part | Component |
|---|---|
| Arms | `FPSPlayer` (`USkeletalMeshComponent`) |
| Weapons | one mesh component per weapon (`LawBringer`, `Asp`, `MachinePistol`, …) |
| Shot origin | `ShotOrigin`, gameplay: never touched |

Getters:

- `GetSelectWeaponMesh`
- `GetSelectedWeaponModelName`
- `GetFPSPlayerSkeletalMeshComponent`

Weapon skins are material swaps: `FWeaponSkin` holds `WeaponMaterials[]` and
`ArmsMaterials[]`.

Animations live in `WeaponAnimationData`
(`/Game/FirstPersonBP/Blueprints/Bodies/Weapons/WeaponAnimationData`):

- `FWeaponAnimationData.Animations` maps weapon to action to `UAnimMontage`.
- They are played by `PlayReloadAnimation(Duration, Weapon)`, `PlayShootAnimation` and `PlayIncReload*`.
- `AnimInstance:Montage_Play` and `SkeletalMeshComponent:GetAnimInstance` exist.

A curated reload animation therefore never edits the game's data asset. A
post-hook on `PlayReloadAnimation`, behind the scope gate, plays the AimMod
montage on the arms. Its play rate is scaled to the game's `Duration`, so the
visual always matches the game's real reload timing.

### Runtime APIs used

| Need | API in this build |
|---|---|
| Dynamic material | `KismetMaterialLibrary:CreateDynamicMaterialInstance(WorldContext, Parent, OptionalName, CreationFlags)` and `PrimitiveComponent:SetMaterial` |
| Set parameters | `MaterialInstanceDynamic:SetVectorParameterValue`, `SetScalarParameterValue` and `SetTextureParameterValue` |
| List parameters | `MaterialInstance.{Texture,Vector,Scalar}ParameterValues[].ParameterInfo.Name`, `Material.CachedExpressionData.Parameters.RuntimeEntries[]` |
| Accessories | `Actor:AddComponentByClass`, `SceneComponent:K2_AttachToComponent` with a socket, `SetCollisionEnabled(NoCollision)` |
| Player models | `SkinnedMeshComponent:SetMasterPoseComponent`, `PrimitiveComponent:SetRenderInMainPass` |
| Load pak assets | `KismetSystemLibrary:LoadAsset_Blocking`; the UE4SS `LoadAsset` |

## AimMod paks

### What the install shows

- One unsigned pak: `Content/Paks/FPSAimTrainer-WindowsNoEditor.pak`. There is no `.sig` file and no IoStore.
- Its footer is version 11, with an all-zero encryption GUID and an unencrypted index.
- UE4SS here has no `BPModLoaderMod` and there's no `LogicMods` folder.

### How an extra pak mounts

UE 4.26 mounts every `*.pak` found recursively under:

- `<Project>/Content/Paks`
- `<Project>/Saved/Paks`
- `Engine/Content/Paks`

It does this at startup, before UE4SS mods run. A name ending in `_P` gets
patch priority and can override files of the same path. `~mods` is a modding
convention, not an engine feature; `~` only sorts the folder last.

`LogicMods` belongs to UE4SS's `BPModLoaderMod`, which spawns each pak's
`ModActor` in every level. That would run in normal scenarios, so **AimMod
doesn't use it**.

### How it stays inert outside AimMod modes

The plan is `Content/Paks/~AimMod/AimModCosmetics-<catalog version>.pak`, with
**no `_P` suffix**. It contains only new packages under
`/Game/AimModCosmetics/`. Mounting only adds those paths to the virtual file
system. No game asset references them, and AimMod loads them (`LoadAsset`)
only after the scope gate opens. In normal scenarios nothing from the pak is
ever loaded.

Build rules that keep it that way:

- Every path must be new: the build script rejects any path that exists in the game's pak, so nothing can be overridden.
- No `AssetRegistry.bin`, no `.ini`, no plugin descriptor, no blueprint classes that could be spawned implicitly.
- Cook with **"Share Material Shader Code" off**, so shaders live inside the AimMod packages and no global shader library is registered.
- One pak per catalog version. An update means restarting the game (no runtime mount, nothing patched in memory).

### Hash pinning

There is no signing key. The catalog is pinned by hash, and the manifest is
exactly as trusted as the AimMod install that ships it.

**The manifest.**

- `catalog-manifest.json` ships with the AimMod install or update, next to `catalog.json`.
- It lists:
  - the catalog version;
  - for `catalog.json` and each pak: the file name, size and SHA-256;
  - each item's id and version.

**Producing it.**

1. **Catalog source:** `in-game/cosmetics/catalog.json`. `catalog.test.cjs` keeps it in sync with the Lua testbed copy and the AimModCore test copy (`native-mod/cosmetics/catalog.json`). It allows only probed parameter names, and keeps pak items drafts until their pak ships.
2. **Paks:** the team builds them (cooking needs the UE editor). They are not stored in the repository.
3. **Package build:** `Build-AimModPackage.ps1 [-CosmeticsPaks <folder>]` runs `in-game/cosmetics/New-CosmeticsManifest.ps1`. That script:
   - validates the catalog (the same rules as `Catalog.validate` and the service's `Cosmetics.cs`);
   - checks that every pak a non-draft item needs is supplied, and that no supplied pak is unreferenced;
   - checks the pak names (flat, never `_P`);
   - copies `catalog.json` byte-for-byte to `AimModCore\service\cosmetics\`;
   - writes `catalog-manifest.json` there: `{version, files[{name, size, sha256}]}`.

   Paks are copied to `Paks\~AimMod\` in the package.
4. **Not checked yet:** the pak index path rule (only new packages under `/Game/AimModCosmetics/`). It must land before the first pak ships.
5. **Release:** `New-AimModInGameRelease.ps1` stages the paks as `files/paks/~AimMod/<name>.pak`.

**Loading in AimModCore.**

- At startup it reads the manifest from its own installed copy.
- It loads only files whose size and SHA-256 match. Items resolve only when their pak matches.
- Unknown, extra or mismatched files are ignored and logged. A mismatched pak stays mounted but is never referenced.

**Installing and updating.**

- The service's installer and updater (and the developer `Install-AimModCore.ps1`) place paks in the game's `Content\Paks\~AimMod\`.
- They record each pak in `ue4ss\aimmod-install.json` as `paks\~AimMod\<name>.pak`, so repair, update, rollback and uninstall cover the paks like every other file.
- The install is refused unless the game's own `Content\Paks\FPSAimTrainer-WindowsNoEditor.pak` is there. See [install lifecycle](install-lifecycle.md).

A player who edits their own local files changes only what they see
themselves. Every viewer resolves shared ids against their own installed
catalog.

### Creator pipeline (AimMod team)

1. **Project.** A UE **4.26.2** project named `FPSAimTrainer`, so cooked `/Game/` paths resolve. Assets go under `Content/AimModCosmetics/`.
2. **References to game assets.** Reference the game's `S_Meso` skeleton, `FPSPlayer` arms skeleton or materials by path. For that, create placeholder assets at the same paths and exclude them from the cook. With the developer's permission, the team may extract skeletons and UV layouts for **internal reference only**. KovaaK's assets are never redistributed, and the pak's paths never include them.
3. **Accessories.** See [Accessories](#accessories) for sockets and budgets. Each is a rigid static mesh with our own material: no cloth, physics or skinning.
4. **Player models.**
   - Skin the mesh to a Meso-compatible skeleton, with bone names and hierarchy as printed by the probe (`sockets/bones`).
   - Either retarget to the Meso skeleton itself, or build our own skeleton with the same bone names plus an AimMod animation blueprint using *Copy Pose From Mesh*.
   - At runtime the model is a follower `SkeletalMeshComponent` with `SetMasterPoseComponent(Mesh)` and collision off. The game's `Mesh` gets `SetRenderInMainPass(false)` and keeps its collision, physics asset and animation. **Verify** that the hidden master still ticks its pose; otherwise set `VisibilityBasedAnimTickOption` on avatar meshes only.
   - Review rule: the silhouette stays within the stock model's bounds.
5. **Reload and other animations.** Author on the `FPSPlayer` arms skeleton, export an `AnimMontage` with the slot the game's montages use (from the probe or the montage list), and keep the length at or below the game's reload duration.
6. **Weapon models.** Static or skeletal meshes attached to the weapon socket or arms bone. The stock weapon component gets render-off only, and `ShotOrigin` is untouched. Bounds stay close to the stock weapon.
7. **Textures and patterns.** Masks authored on the internal UV reference, set through the material's existing texture parameter.
8. **Cook and package.**
   - Cook for `WindowsNoEditor` with shared shader code off.
   - Run `UnrealPak` (4.26) with a response file listing only `/Game/AimModCosmetics/` packages.
   - Add the pak to the AimMod release build, which checks it and pins its hash in the manifest.
9. **Thumbnails.** Render picker thumbnails in an AimMod match with AimModCore's existing `CaptureThumbnail` game command.
10. **Review checklist:**
    - no KovaaK's or third-party assets;
    - no override paths;
    - budgets and bounds;
    - no gameplay-visible change, such as an emissive shape usable as a crosshair, a beacon, or camouflage;
    - catalog id and version bumped.

## Accessories

All accessories are rigid static meshes on the avatar's `CharacterMesh0`:

- attached with `K2_AttachToComponent` to a bone or socket, with a per-model offset transform from the catalog;
- `NoCollision`, with no physics, cloth or skinning;
- opaque or masked materials only (no translucency);
- one material each;
- one LOD, plus LOD1 at about 50% for items over 1,000 triangles.

They exist only while the scope gate is open, and are destroyed when it
closes.

**Sockets.** The Meso and Endo skeletons' bone and socket names come from the
probe's `sockets/bones` lines. The names below are the expected roles; the
catalog stores the confirmed names per model.

| Candidate | Attach to | Triangles (LOD0) | Texture | Fit work | Notes |
|---|---|---|---|---|---|
| **Halo** | head bone, offset above the head | ≤ 300 | none (colour and emissive parameters) or 128² | lowest: floats, so it never clips any skin; one offset per model | emissive capped (scalar ≤ 2) so it is no beacon; doubles as a tournament reward later |
| **Visor** | head bone | ≤ 800 | 256² base colour + 256² packed ORM | low: one rigid plate per model | must not reach below the chin or past the head bounds |
| **Headband** | head bone | ≤ 400 | 256² | low: a ring around a near-cylindrical head | |
| Cat ears | head bone | ≤ 600 (pair) | 256² | medium: fitted to each head's top shape | |
| Sunglasses | head bone | ≤ 600 | 256² | medium: fitted to each face | lenses opaque or masked, not translucent |
| Crown | head bone | ≤ 1,000 | 512² | medium: sits on the head, so clipping checks per model | tournament reward |
| Small backpack | upper spine bone | ≤ 1,500 | 512² | higher: clipping against arms in animations | larger silhouette, so a bounds review |
| Jetpack | upper spine bone | ≤ 2,500 | 512² (+ optional emissive mask) | highest: as backpack, plus flame or emissive review | |

**Proofs: halo, visor, headband.** These three need no per-skin fitting
beyond one transform per model, and can't clip in animation because the head
moves rigidly. They cover the three patterns we need:

- floating (halo);
- surface-fitted (visor);
- wrapped (headband).

Cat ears, sunglasses and the crown come next. The backpack and jetpack wait
until the head items are proven.

**Shared budgets:**

| Limit | Value |
|---|---|
| On disk | at most 1 MB per accessory |
| Texture maps | at most 2 per item, BC1 base colour, BC5 or packed ORM |
| Head items | inside a 35 × 35 × 30 cm box around the head bone |
| Spine items | inside 45 × 30 × 50 cm |
| Any item | no more than 10 cm beyond the capsule radius |
| Per avatar | at most one head item and one spine item |

## Feasibility (revised)

| Item | How | Feasibility |
|---|---|---|
| Avatar tints, multi-tone patterns | dynamic instance vector/scalar parameters | **High**: prototype in this branch |
| Own weapon and arms finishes | the same, on `GetSelectWeaponMesh` and `GetFPSPlayerSkeletalMeshComponent` | **High**: prototype in this branch |
| Patterns with new shapes | curated mask texture from the AimMod pak, through the existing texture parameter | **High** once the pak hash check exists |
| Accessories | static mesh from the AimMod pak on a bone or socket, `NoCollision` | **High**: the engine path is standard; proofs are the halo, visor and headband |
| Weapon models | pak mesh on the arms or weapon socket, stock weapon render-off | **Medium-High**: static models don't animate weapon parts unless skeletal with matching montages |
| Reload and other animations | pak montage played on the arms after `PlayReloadAnimation`, rate matched to `Duration` | **Medium-High**: needs the game's montage slot name |
| Player models | pak skeletal mesh, follower with master pose | **Medium**: needs a Meso-compatible skeleton and the hidden-master pose check |
| Player-supplied files | none | **Out of scope by policy** |

## Lua testbed (this branch)

`in-game/ue4ss/AimModCosmetics` is a separate UE4SS Lua mod, off unless
`config.txt` sets `enabled=1`. It is the **team testbed only**. The shipped
manifest check and applier live in AimModCore (see [Plan](#plan)). The Lua applier
must never be enabled on the same install as the AimModCore applier.

- **Probe** (`probe=1`, `CosmeticsProbe.lua`): read-only.
  - It logs the mesh components, materials and parameter names, looks, sockets and bones, the viewmodel, the Default packs, and skin and model data.
  - It writes to `UE4SS.log` and `%LOCALAPPDATA%/AimMod/KovaaksNative/cosmetics-probe.txt`.
  - It runs in any scenario, because it changes nothing.
- **Catalog** (`CosmeticsCatalog.lua`): the reference implementation of the item schema, validation and resolution. Unknown ids, drafts (unless `allow_drafts=1`) and pak items whose pak doesn't match the manifest resolve to "base look".
- **Applier** (`cosmetics=1`, `CosmeticsApply.lua`):
  - Behind the scope gate, it applies the team-test items `avatar_item` and `weapon_item` as parameters on dynamic instances parented on the game's materials.
  - It restores the originals when out of scope.
  - It never touches meshes, collision, visibility, `ShotOrigin`, the local character mesh or scenario bots.
  - There's no file or texture setting.
- **Tests:** `util`, `scope`, `catalog`, `probe` and `apply`, all `*.test.lua`. Run them from `tests/` with Lua 5.3/5.4 or `nvim -l`. They are the reference vectors for the C++ port.

### Deploy the probe

1. Close KovaaK's.
2. Copy `in-game/ue4ss/AimModCosmetics/Scripts` and `config.txt` to `<KovaaK's>/Binaries/Win64/ue4ss/Mods/AimModCosmetics/`. Don't copy `tests`.
3. Add `AimModCosmetics : 1` to `Mods/mods.txt`, and `{"mod_name": "AimModCosmetics", "mod_enabled": true}` to `Mods/mods.json`. Keep the other entries.
4. In the deployed `config.txt`, set `enabled=1` and `probe=1`, and keep `cosmetics=0`.
5. Start KovaaK's. Load a scenario with a Meso and an Endo target. If you can, also run an AimMod lobby with an avatar (or the bridge's `avatar_test`), and switch through a few weapons.
6. Collect `%LOCALAPPDATA%/AimMod/KovaaksNative/cosmetics-probe.txt`. It names your local profile and assets, so don't commit it.
7. To undo, set `enabled=0` or remove the mod's lines from `mods.txt` and `mods.json`.

## Sharing

- Each member's look is a list of `{id, version}` pairs, for example `accessory-halo@1`.
- It travels in a lobby protocol frame, `cosmetic.look {member, items}`, which the host relays like `aimmod.char`. Lobby keys aren't used, because the service may write at most 24 keys and `aimmod.char.*` already uses up to 16.
- No files are transferred. Content arrives only with AimMod updates.
- **Viewer settings, applied by the service before it hands looks to AimModCore:**
  - "Show other players' cosmetics": **all (default)**, friends, or off;
  - "Hide this player's cosmetics", per SteamID;
  - a host toggle per AimMod mode.
- Each viewer resolves ids against their own installed catalog. Unknown ids, newer versions or pak items without a matching pak fall back to the base look ("update AimMod to see this").

## UI

A **Cosmetics** page in the AimMod workspace, listing catalog items only:

- base look (the existing `AvatarProfiles`);
- avatar tint or pattern;
- head accessory and spine accessory;
- weapon finish or model;
- reload animation;
- player model.

Items that need a newer catalog or a missing pak show as unavailable.

**Preview:** a live 3D view of your own character (see [Character preview](#character-preview)), plus team-rendered 2D thumbnails for the item cards.

Others only ever see items from the catalog.

## Character preview

The Cosmetics page shows your own character, live, while you customise it. Dragging the picture turns the character.

### Findings

**Gameface live views can't be used from a mod.** The Coherent plugin is compiled into the game exe. Reflection exposes only these:

| API | Takes |
|---|---|
| `UCohtmlBaseComponent` / `UCohtmlWidget:AddPreloadedTexture` | a `UTexture2D` |
| `RemovePreloadedTexture` | a `UTexture2D` |
| `PreloadTextureSync`, `PreloadTextureAsync` | an asset path |
| `UCohtmlWidget:GetRenderTexture` | (returns the view's own output) |

- **Preloading:** a render target isn't a `UTexture2D`. The only conversion, `ConvertRenderTargetToTexture2DEditorOnly`, is editor-only.
- **Live-view hook:** the exe has `CohtmlOnLiveViewSizeRequest__DelegateSignature(compo, Name, Width, Height)`. Nothing in reflection owns or binds it, and the exe contains no live-view URL scheme string.
- **Page origin:** the AimMod workspace is a `UCohtmlWidget` loading `http://127.0.0.1:<port>/<cap>/ui`, so its image URLs resolve over HTTP, not to engine assets.
- **Conclusion:** reaching the plugin's internal live-view code would need signature scanning into its C++, which is fragile and exactly the kind of poking we avoid. **The prototype uses the PNG path.**

**The game already has a preview stage.**

- **The stage actor:** `CharacterSkinPreviewActorUserInterfaceBP_C`, an `ACharacterSkinPreviewActor`. It holds:
  - `SkeletalMesh` and the hitbox shape meshes under `Meshes`;
  - `Floor`, `Wall`, `DirectionalLight`, `PointLight` and `PointLight1`;
  - a `SpringArm` and `Cameras` rig;
  - `SceneCaptureComponent2D`, a `UCharacterSkinPreviewSceneCaptureComponent2D`.
- **The menu's own render target:** `CharacterSkinPreviewRenderTarget2D`, shown by `CharacterSkinPreviewViewport_C`, which also implements drag rotation (`IsDragging`, `OriginalMousePosition`).
- **Applying a look:** the actor implements `ICharacterModelInterface`, so `CharacterModelFunctionLibrary:ApplyCharacterModel` and `CharacterSkinFunctionLibrary:ApplyCharacterSkin` dress it exactly as the game does.
- **Engine support:** `KismetRenderingLibrary:CreateRenderTarget2D`, `ExportRenderTarget`, `SceneCaptureComponent2D:CaptureScene` and `ShowOnlyActorComponents` are all present.

### How the prototype works

| Step | Who | What |
|---|---|---|
| 1 | Cosmetics page (`multiplayer.js`) | While the page is open and the workspace visible, it POSTs `/cosmetic-preview {open, yaw, item?}`: every second, every 250 ms for 2 s after an interaction, and at most every 100 ms while dragging. It sends `{open:false}` when the page closes or the workspace hides. |
| 2 | Service (`MultiplayerService.CosmeticPreview.cs`) | It writes `cosmetics-preview.txt`. The look comes from your chosen avatar profile. Parameters come from equipped or tried-on **catalog** body items without paks, resolved by id. The request expires in 5 s, and `seq` bumps on every change. |
| 3 | AimModCore (`CosmeticsPreview.cpp`) | It reads the request every 0.2 s on the game thread. If `DecidePreview` allows it (not in a challenge, benchmark or the editor, and not loading), it spawns the game's preview stage and dresses its skeletal mesh with the requested look (below). It turns `Meshes` and captures on change only. |
| 4 | AimModCore | It captures a 768×768 RGBA8 render target twice, as final colour and as world normals. `ComposePreview` (core, unit-tested) builds the 384×384 frame from the two. AimModCore writes it through WIC as `cosmetics-preview/preview-0.png` or `preview-1.png` (alternating), then atomically writes `cosmetics-preview-frame.txt` (`v=1, seq, file, width, height`). |
| 5 | Service | It serves the newest PNG at `/cosmetic-preview.png`, and the POST answer carries its frame number, so the page swaps `<img src>` only on a new frame. |

**Dressing the stage.**

- **The look:** the game loads its Default model and skin packs only for the
  character menu, so AimModCore loads them itself (game assets at fixed paths,
  through the asset registry). It finds the requested model and skin by name
  there, so a DLC look is never found. It sets the skin's mesh, the model's
  animation blueprint and the skin's materials straight on the stage's
  `SkeletalMesh`. One animation evaluation gives a standing pose, even while
  the game is paused.
- **Hidden:** the shape models (cylinder, sphere, cube), the weapons, the wall
  and the floor.
- **Catalog parameters:** set on fresh dynamic instances of the look's own
  materials.

**Light and exposure.** The first prototype's frame was almost black. It had
the stage's only key light (its directional light) switched off, and a scene
capture keeps no eye-adaptation history, so auto exposure started from nothing.
Now:

- **Fixed exposure:** the capture's post-process pins the minimum and maximum
  brightness to one value: luminance 1, or EV100 3 when the project extends
  the luminance range (detected from the negative defaults). Vignette, grain,
  motion blur and lens flares are off, and bloom is low.
- **Own rig:** three point lights, in candela converted to each light's unit.
  The key light sits camera-left and above, the fill camera-right and low, and
  a mint rim light behind. They reach 900 cm and cast no shadows. A light
  whose reach can't be set is switched off. When every rig light accepts it,
  rig and character move to lighting channel 1, so the map's lights don't
  change the character.

**Framing.** The camera keeps the stage camera's front view at a 30 degree
field of view. It moves back until the character's bounds fit with room to
turn (`PreviewCameraDistance`).

**Composition (`ComposePreview`).**

1. **Cut-out:** with only the stage rendered, every pixel whose normal matches
   the corners' is background. The mask doesn't depend on the map's sky, fog
   or bloom.
2. **Backdrop:** the page's dark green-grey (`#202d28` centre to `#121a17`
   edge), with a soft floor shadow under the feet.
3. **Brightness:** levelled so that the 97th percentile of the character's
   luminance sits near 0.8. The gain is bounded to 0.7 to 5, so dark finishes
   stay dark.
4. **Framing:** centred on the silhouette and scaled on its height with a
   margin, so turning doesn't zoom.
5. **Downsample:** 2×2 supersampled to 384×384, which anti-aliases the edges.

**Request format (`cosmetics-preview.txt`):**

```
v=1
expires=<unix seconds>   (now, now + 15]
seq=<n>
model=Meso | Endo        Default-pack model
skin=McCree              optional Default-pack skin
yaw=<-180..180>
vector=<Param>:r,g,b,a   up to 8, 0..1
scalar=<Param>:v         up to 8, -10..10
```

`ParsePreviewRequest` validates the whole file (C++ core, unit-tested) and rejects anything else.

**Isolation.** Nothing in the scenario changes:

- **Placement:** the stage spawns 5 km above the origin with collision off.
- **Capture:** its capture writes to an **AimMod render target** (never the game's shared one) and renders only the stage's own components (`PRM_UseShowOnlyList`).
- **Lighting:** the stage's directional light is switched off, so it can't light the map. The rig's point lights reach 900 cm, far short of the 5 km down to the map, and nothing on the stage casts a shadow.
- **Looks:** only names from the free Default packs are applied, even here. A DLC look is never shown.
- **Teardown:** the stage and render target are destroyed as soon as the request is stale or the gate closes. Unknown game state counts as "no".

**Cost.**

- **Idle:** with the page open but no changes, nothing is captured or exported.
- **Per change:**
  - two scene captures and two synchronous read-backs at 768²;
  - two PNG decodes, the composition, and one PNG encode at 384².

  Rotation is capped at about 6 frames a second. A look change triggers
  re-captures at +0.35 s and +1.2 s while meshes stream in.
- **Read-back stall:** expect a few milliseconds per export; **measure live**.
- **When it runs:** only while the page is open, usually from the pause menu.

### Verify in a live run

1. The stage spawns outside the menu level, and `ApplyCharacterModel`/`ApplyCharacterSkin` dress it through the `TScriptInterface` built from the class's interface offset.
2. The stage's `FadeIn` timeline doesn't leave it transparent while the game is paused. If it does, call `FadeIn`, or capture once the timeline finishes.
3. `CaptureScene` works while paused, and `ExportRenderTarget` writes a PNG for `RTF_RGBA8` at `FilePath/FileName`.
4. No visible light or shadow change in the map below.
5. Gameface reloads `<img>` when only the query string changes.
6. The read-back cost at 384².

If step 3 fails, the fallback is reading the render target's pixels from C++ (through the RHI) and encoding the PNG ourselves. The reflected per-pixel reads (`ReadRenderTargetRawPixel`, `ReadRenderTargetPixel`) are far too slow for a whole frame.

## Plan

**Phase 0 (this branch, done).**

- Read-only probe.
- Reference catalog, scope gate and applier in Lua, with tests.
- Session marker spec handed to the multiplayer work.

**Move to AimModCore** (C++; coordinated through the multiplayer coordinator).

The core library (`in-game/native-mod/core`, unit-tested in `tests/CoreTests.cpp`) gains:

1. **`CosmeticsScope`:** an exact port of `CosmeticsScope.lua`, with the `scope.test.lua` vectors as C++ tests.
2. **`Catalog`:** parses the installed `catalog.json`. The item schema is the Lua one, plus accessory `attach` (per-model bone or socket and transform) and `mesh`/`material` paths under `/Game/AimModCosmetics/`. Validation, resolution and the `catalog.test.lua` vectors are ported too.
3. **`CatalogManifest`:** parses the installed `catalog-manifest.json`, and checks the size and SHA-256 (BCrypt) of `catalog.json` and each pak. It reports a matched set and logs every unknown, extra or mismatched file. Tests cover a matching manifest, a wrong hash, a wrong size, a missing file, an extra unlisted pak and a malformed manifest.
4. **`CosmeticLooks`:** parses the looks file the service writes (below). It is validated whole, and never partially accepted.

The mod (`in-game/native-mod/mod/src`) gains:

5. **Game state:** reads `ScenarioManager` (`GetCurrentScenario().GetName()`, `IsInChallenge`, `IsCurrentlyInBenchmark`, `IsInScenarioEditor`, `IsScenarioLoading`) and the session marker every second, on the game thread.
6. **Applier:**
   - parameter items, on dynamic instances parented on the game's materials, with a restore when the gate closes;
   - accessories: AimMod's own `StaticMeshComponent` per item via `AddComponentByClass`, with `SetStaticMesh` on that new component only, `NoCollision`, attached to the bone, and destroyed on gate close;
   - your own weapon and arms finishes in matches.

   It never touches the game's meshes, collision, `ShotOrigin`, scenario bots or DLC looks.
7. **Avatar identity:** AimModSteam adds the actor tag `AimMod.Peer.<SteamID64>` to each avatar it spawns (`AActor.Tags`; a bridge change, one line). AimModCore finds avatars by that tag instead of the profile prefix.
8. **Looks input:**
   - **File and format:** the service writes `%LOCALAPPDATA%/AimMod/KovaaksNative/cosmetic-looks.txt` atomically, in the same style as `core-command.tsv`: `v=1`, then one line per peer, `peer=<SteamID64> items=<id>@<v>,<id>@<v>`, and one `self=` line for the local player's own items.
   - **Content:** the file already reflects the viewer settings (all/friends/off, hidden players).
   - **Lifetime:** it is deleted with the session marker.
9. **Reload animations (phase 3):** a gated post-hook on `PlayReloadAnimation` that plays the item's montage on the arms, with its rate set from `Duration`.

**Service** (multiplayer branch):

- the session marker (spec handed over);
- the looks file;
- the `cosmetic.look` frame;
- viewer settings;
- the Cosmetics page.

**Bridge:** the avatar tag.

**Release build:**

- pak index path check;
- catalog validation;
- manifest generation with hashes.

**Phase 1: parameter items.**

- Run the probe; fill in the real parameter names, bones and sockets; clear `draft` on tints and finishes.
- Land the AimModCore scope, catalog, manifest check (for `catalog.json` alone at first) and the parameter applier.
- Session marker, looks file, `cosmetic.look` frame, viewer settings, thumbnail picker.

**Phase 2: AimMod pak.**

- Pak path rules and hash pinning in the release build.
- Pak hash check in AimModCore.
- Accessories: halo, visor, headband.
- Mask-texture patterns.
- Weapon models.
- In-game 3D preview.

**Phase 3: animation and player models.**

- Reload montages.
- Player models through a follower mesh with master pose.
- More accessories: cat ears, sunglasses, crown, then the backpack and jetpack.

## First curated catalog

The first set works today. It is material parameters only, with no pak, and
uses the names the probe found:

- **Meso and Endo body and head slots** (all `MM_BaseDummy`): vectors
  `MetalPaint` (body paint), `TriangularPaint` (panels), `RawMetal` and
  `Silicone`; scalars `Roughness` and `Metallic`.
- **Viewmodel weapons** (all `M_SingleAssetMaster`): vectors `AccentColor` and
  `Emissive`. Their base colour is a texture, so finishes recolour the accent
  and its glow.

Colours are linear. `catalog.test.cjs` checks that every non-draft item uses
only these names. It also checks that a weapon's emissive is never brighter
than the game's own orange accent (no beacons).

| id | kind | look |
|---|---|---|
| `tint-mint` | avatar tint | AimMod mint paint, off-white panels, satin |
| `tint-carbon` | avatar tint | near-black paint and panels, matte |
| `tint-ivory` | avatar tint | warm ivory, taupe panels |
| `tint-crimson` | avatar tint | black body, crimson panels |
| `tint-gold` | avatar tint | metallic gold, black panels |
| `tint-chrome` | avatar tint | mirror chrome, gunmetal panels |
| `finish-mint`, `finish-crimson`, `finish-gold`, `finish-ice`, `finish-violet`, `finish-ghost` | weapon finish | accent and glow colour on your own weapon |

Tints fit both free models (Meso and Endo). Finishes dress your own selected
weapon in AimMod matches only. The Cosmetics page draws each card as a swatch
from the item's own colours: the service sends them as sRGB hex with the
finish's `Metallic`.

Still drafts:

| id | kind | needs |
|---|---|---|
| `meso-pattern-stripes` | avatar pattern | an AimMod pak with a Meso mask texture, plus the mask parameter name |
| `accessory-halo`, `accessory-visor`, `accessory-headband` | accessory | an AimMod pak with the mesh and our material, plus the Meso and Endo head bone names and per-model offsets |

For the pattern and accessories:

- the 4.26 project, internal UV reference, mask texture, and the halo, visor
  and headband meshes within budget;
- the release build's manifest generation, which fills in the `sha256` values;
- the AimModCore pak check.

## Decisions

Decided:

- The manifest check and the real applier live in AimModCore (C++). The Lua mod is the team testbed only.
- No signing key. The catalog is hash-pinned by a manifest that ships with the AimMod install.
- "Show other players' cosmetics" defaults to **all**, with friends and off as options.
- The proof accessories are the halo, visor and headband. Cat ears, sunglasses and the crown follow, then the backpack and jetpack.
