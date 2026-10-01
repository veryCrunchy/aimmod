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
   in AimMod pak files. It is versioned and hash-checked against a manifest
   signed by AimMod, and only verified content is used.
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

### Signing

UE's built-in pak signing needs keys compiled into the game, so it doesn't
apply. AimMod signs its own content instead.

1. An AimMod release key (ECDSA P-256) signs `catalog-manifest.json`. The manifest lists:
   - the catalog version;
   - each pak's file name, size and SHA-256;
   - the catalog's item ids and versions.
2. The public key is compiled into AimModCore. At startup, AimModCore verifies the manifest signature (Windows CNG `BCryptVerifySignature`) and the SHA-256 of each pak. Only then does it mark a pak verified.
3. Pak items resolve only for verified paks (`Catalog.resolve`, `verifiedPaks`). A modified or unknown pak stays mounted but is never referenced.
4. The installer verifies the same manifest before copying a pak.

A player who edits their own local catalog changes only what they see
themselves. Every viewer resolves shared ids against their own verified
catalog.

### Creator pipeline (AimMod team)

1. **Project.** A UE **4.26.2** project named `FPSAimTrainer`, so cooked `/Game/` paths resolve. Assets go under `Content/AimModCosmetics/`.
2. **References to game assets.** Our assets can reference the game's `S_Meso` skeleton, `FPSPlayer` arms skeleton or materials by path. For that, create placeholder assets at the same paths and exclude them from the cook. With the developer's permission, the team may extract skeletons and UV layouts for **internal reference only**. KovaaK's assets are never redistributed, and the pak's paths never include them.
3. **Player models.**
   - Skin the mesh to a Meso-compatible skeleton, with bone names and hierarchy as printed by the probe (`sockets/bones`).
   - Either retarget to the Meso skeleton itself, or build our own skeleton with the same bone names plus an AimMod animation blueprint using *Copy Pose From Mesh*.
   - At runtime the model is a follower `SkeletalMeshComponent` with `SetMasterPoseComponent(Mesh)` and collision off. The game's `Mesh` gets `SetRenderInMainPass(false)` and keeps its collision, physics asset and animation. **Verify** that the hidden master still ticks its pose; otherwise set `VisibilityBasedAnimTickOption` on avatar meshes only.
   - Review rule: the silhouette stays within the stock model's bounds.
4. **Reload and other animations.** Author on the `FPSPlayer` arms skeleton, export an `AnimMontage` with the slot the game's montages use (from the probe or the montage list), and keep the length at or below the game's reload duration.
5. **Weapon models.** Static or skeletal meshes attached to the weapon socket or arms bone. The stock weapon component gets render-off only, and `ShotOrigin` is untouched. Bounds stay close to the stock weapon.
6. **Textures and patterns.** Masks authored on the internal UV reference, set through the material's existing texture parameter. Accessories use static meshes with our own materials, attached to sockets from the probe.
7. **Cook and package.**
   - Cook for `WindowsNoEditor` with shared shader code off.
   - Run `UnrealPak` (4.26) with a response file listing only `/Game/AimModCosmetics/` packages.
   - The build script checks the path rules, writes the manifest, and signs it with the release key on the release machine.
8. **Review checklist:**
   - no KovaaK's or third-party assets;
   - no override paths;
   - bounds checks;
   - no gameplay-visible change, such as an emissive weapon shape usable as a crosshair, or camouflage;
   - catalog id and version bumped.

## Feasibility (revised)

| Item | How | Feasibility |
|---|---|---|
| Avatar tints, multi-tone patterns | dynamic instance vector/scalar parameters | **High**: prototype in this branch |
| Own weapon and arms finishes | the same, on `GetSelectWeaponMesh` and `GetFPSPlayerSkeletalMeshComponent` | **High**: prototype in this branch |
| Patterns with new shapes | curated mask texture from the AimMod pak, through the existing texture parameter | **High** once the pak verifier exists |
| Accessories | static mesh from the AimMod pak on a socket, `NoCollision` | **High**: the engine path is standard; the proof item is the visor |
| Weapon models | pak mesh on the arms or weapon socket, stock weapon render-off | **Medium-High**: static models don't animate weapon parts unless skeletal with matching montages |
| Reload and other animations | pak montage played on the arms after `PlayReloadAnimation`, rate matched to `Duration` | **Medium-High**: needs the game's montage slot name |
| Player models | pak skeletal mesh, follower with master pose | **Medium**: needs a Meso-compatible skeleton and the hidden-master pose check |
| Player-supplied files | none | **Out of scope by policy** |

## Prototype in this branch

`in-game/ue4ss/AimModCosmetics` is a separate UE4SS Lua mod, off unless
`config.txt` sets `enabled=1`.

- **Probe** (`probe=1`, `CosmeticsProbe.lua`): unchanged and read-only.
  - It logs the mesh components, materials and parameter names, looks, sockets and bones, the viewmodel, the Default packs, and skin and model data.
  - It writes to `UE4SS.log` and `%LOCALAPPDATA%/AimMod/KovaaksNative/cosmetics-probe.txt`.
  - It runs in any scenario, because it changes nothing.
- **Catalog** (`CosmeticsCatalog.lua`): the curated item list, with validation (id format, kinds, parts, value ranges, required pak) and resolution. Unknown ids, drafts (unless `allow_drafts=1` for team tests) and pak items without a verified pak all resolve to "base look".
- **Applier** (`cosmetics=1`, `CosmeticsApply.lua`):
  - Behind the scope gate, it applies the team-test items `avatar_item` (to AimMod avatars with a free look and a matching model) and `weapon_item` (to your own weapon and arms in a match).
  - It creates a dynamic instance parented on the slot's material, sets only the item's parameters, and assigns it with `SetMaterial`. It restores the original when out of scope.
  - It skips materials missing any of the item's parameters.
  - It never touches meshes, collision, visibility, `ShotOrigin`, the local character mesh or scenario bots.
  - There's no file or texture setting at all. Per-player items come in phase 1 via the bridge.
- **Tests:** `util`, `scope`, `catalog`, `probe` and `apply`, all `*.test.lua`. Run them from `tests/` with Lua 5.3/5.4 or `nvim -l`. They use a synthetic object model.

### Deploy the probe

1. Close KovaaK's.
2. Copy `in-game/ue4ss/AimModCosmetics/Scripts` and `config.txt` to `<KovaaK's>/Binaries/Win64/ue4ss/Mods/AimModCosmetics/`. Don't copy `tests`.
3. Add `AimModCosmetics : 1` to `Mods/mods.txt`, and `{"mod_name": "AimModCosmetics", "mod_enabled": true}` to `Mods/mods.json`. Keep the other entries.
4. In the deployed `config.txt`, set `enabled=1` and `probe=1`, and keep `cosmetics=0`.
5. Start KovaaK's. Load a scenario with a Meso and an Endo target. If you can, also run an AimMod lobby with an avatar (or the bridge's `avatar_test`), and switch through a few weapons.
6. Collect `%LOCALAPPDATA%/AimMod/KovaaksNative/cosmetics-probe.txt`. It names your local profile and assets, so don't commit it.
7. To undo, set `enabled=0` or remove the mod's lines from `mods.txt` and `mods.json`.

## Sharing

- Each member's look is a list of `{id, version}` pairs, for example `meso-tint-ember@1`.
- It travels in a lobby protocol frame, `cosmetic.look {member, items}`, which the host relays like `aimmod.char`. Lobby keys aren't used, because the service may write at most 24 keys and `aimmod.char.*` already uses up to 16.
- No files are transferred. Content arrives only with AimMod updates.
- The service passes each peer's items to AimModSteam (new pipe command `cosmetics.set {peer, items}`), which owns the SteamID-to-avatar map.
- Each viewer resolves ids against their own verified catalog. Unknown ids, newer versions or unverified pak items fall back to the base look ("update AimMod to see this").
- Viewer settings:
  - "show others' cosmetics": all / friends / off;
  - "hide this player's cosmetics";
  - a host toggle per AimMod mode.

## UI

A **Cosmetics** page in the AimMod workspace, listing catalog items only:

- base look (the existing `AvatarProfiles`);
- avatar tint or pattern;
- accessory;
- weapon finish or model;
- reload animation;
- player model.

Items that need a newer catalog or a missing pak show as unavailable.

**Preview:**

- **Phase 1:** team-rendered 2D thumbnails shipped with the catalog.
- **Phase 2:** an in-game 3D preview. AimMod spawns its own preview actor with a scene capture into a render target, the way the game's `ACharacterSkinPreviewActor` and `UCharacterSkinPreviewSceneCaptureComponent2D` do, and shows it in a UMG image beside the Gameface view.

Others only ever see items from the catalog.

## Plan

**Phase 0 (this branch).**

- Read-only probe.
- Catalog and scope gate, with tests.
- Parameter-only applier for team tests.

**Phase 1: parameter items.**

- Probe; fill in the real parameter names; clear `draft`.
- The service writes the session marker.
- AimModSteam ports the gate and applies per-player items after `LoadCharacterProfile`.
- Local weapon and arms finishes.
- The `cosmetic.look` frame and viewer settings.
- Cosmetics page with thumbnails.

**Phase 2: AimMod pak.**

- Signed manifest and verifier in AimModCore.
- Pak build script with the path rules.
- Mask-texture patterns, the visor accessory, weapon models.
- In-game 3D preview.

**Phase 3: animation and player models.**

- Reload montages through the gated post-hook.
- Player models through a follower mesh with master pose.

## First curated catalog

The draft entries in `CosmeticsCatalog.lua` are:

| id | kind | needs |
|---|---|---|
| `meso-tint-ember`, `meso-tint-glacier`, `meso-tint-graphite` | avatar tint | Meso body/head vector (and scalar) parameter names |
| `endo-tint-verdant` | avatar tint | Endo parameter names |
| `meso-pattern-stripes` | avatar pattern | AimMod pak with a Meso mask texture, plus the mask parameter name |
| `weapon-finish-gunmetal`, `weapon-finish-sand`, `weapon-finish-aimmod` | weapon finish | weapon and arms material parameter names, for the common weapons |
| `accessory-visor` | accessory | AimMod pak with the visor mesh and our material, plus the Meso head socket name |

To make them real:

1. From the probe:
   - **Character parameters:** vector and scalar parameter names on the Meso and Endo `CharacterMesh0` slots, and which slot is body or head.
   - **Weapon parameters:** weapon and arms parameter names for 2–3 common weapons.
   - **Sockets and bones:** Meso's socket and bone list (head socket).
   - **Skin mesh check:** whether the four Default skins share `S_Meso`.
2. Replace the placeholder parameter names (`PrimaryColor`, `Roughness`, `Metallic`), check the colours in game in an AimMod match, render thumbnails, and clear `draft`.
3. For the pattern and the visor:
   - the 4.26 project, internal UV reference, mask texture and visor mesh;
   - a pak build and the signing key;
   - the AimModCore verifier, filling in the `sha256` values.
4. Service: the session marker writer and the `cosmetic.look` frame. Bridge: `cosmetics.set`.

## Open decisions

1. Which machine holds the release signing key, and who signs catalog releases?
2. Should AimModCore (C++) host the verifier and the phase 1+ applier, with the Lua prototype kept only as a team testbed?
3. Default for "show others' cosmetics": all, friends or off.
4. Should the visor be the proof accessory, or would you prefer another first item?
