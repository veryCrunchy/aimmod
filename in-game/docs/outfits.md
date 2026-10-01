# Outfits

Plan for wearable clothing and outfits on AimMod avatars: tactical vests,
jackets, caps, gloves, balaclavas and armour, in the spirit of team-shooter
agent models. Everything is original AimMod work. Policy is
[cosmetics.md](cosmetics.md#policy): AimMod matches only, curated by the AimMod
team, shipped in hash-pinned AimMod paks, visuals only.

## Licensing

- **No third-party game assets.** Valve's CS:GO and CS2 agent models,
  textures, names and logos are Valve's IP. We don't extract, trace or
  redistribute them. The same goes for KovaaK's and Overwatch assets.
- **Original work only, in a familiar style.** References for silhouettes
  and real-world gear (plate carriers, softshell jackets, ballistic helmets)
  are fine. Copying a specific agent's design, insignia or colour scheme is
  not.
- **Our own licence.** Every outfit is made by or for the AimMod team, under
  terms that let us ship it in the AimMod release. Sources (`.blend`,
  textures) live in the team's asset repository, not in this repository.
- **KovaaK's skeleton, internal reference only.** KovaaK's allows modding,
  so we may read the skeleton (bone names, hierarchy, rest pose) to rig
  against. We never ship the game's mesh, skeleton or textures: the pak
  contains only `/Game/AimModCosmetics/` packages, which refer to the
  game's skeleton by path.
- Catalog entries carry the author and licence in the team's asset
  repository, and the review checklist ([cosmetics.md](cosmetics.md#creator-pipeline-aimmod-team))
  applies.

## Two ways to wear clothing

| | Rigid attachment | Skinned clothing (follower mesh) |
|---|---|---|
| What | a `StaticMeshComponent` on one bone, like the shipped game-mesh accessories | a `SkeletalMeshComponent` skinned to the character skeleton, driven by the character's pose |
| Good for | caps, helmets, visors, balaclava shells, shoulder pads, badges, holsters, knee pads | vests, jackets, gloves, trousers, full outfits |
| Runtime | `AttachFitAccessory` (exists) with a pak mesh | `AddComponentByClass(SkeletalMeshComponent)`, `SetSkeletalMesh`, `SetMasterPoseComponent(CharacterMesh0)`, no collision; attached to `CharacterMesh0` |
| Fit across models | one transform per rig (bone fit) | one mesh per base model (Meso, Endo), because body shapes differ |
| Risk | low | medium: clipping in extreme poses; needs the master-pose check below |

**Full mesh swap** (replacing the body) is a third option. We don't use it:
the game hit-tests some models on the skeletal mesh itself, and policy keeps
`ACharacter.Mesh` untouched. An outfit that covers the whole body is a
follower mesh over the base model. The base model can be hidden with
`SetRenderInMainPass(false)` on avatars only; it keeps its collision, physics
asset and animation.

**Master-pose check (live, first).** A follower with `SetMasterPoseComponent`
copies the master's bone transforms. With the master hidden from the main
pass, it must still tick its pose; if it doesn't, set
`VisibilityBasedAnimTickOption = AlwaysTickPoseAndRefreshBones` on avatar
meshes only. The proof below covers this.

## Tooling

| Step | Tool | Notes |
|---|---|---|
| Model, UV, weight | Blender 4.x | Installed on the build machine. One `.blend` per outfit. |
| Textures | Blender bake, or Substance/Material Maker | BC1 base colour plus packed ORM, at most 1024² for full outfits, 512² for pieces |
| Skeleton reference | FModel / UE Viewer (umodel) on the game's pak, AES not needed (unencrypted) | Export `S_Meso` and `S_Endo` skeletons as glTF/PSK for rigging. Internal reference only. |
| Cook and pak | **Unreal Engine 4.26.2 editor** (Epic Games Launcher) plus UnrealPak | Not installed on this machine: the blocker for a pak proof. |
| Verify | `New-CosmeticsManifest.ps1 -Paks`, `catalog.test.cjs`, AimModCore manifest check | exist |

## Skeleton export

1. In FModel, open `Content/Paks/FPSAimTrainer-WindowsNoEditor.pak` with
   the game's `.usmap` (the UE4SS dump produces one).
2. Export `/Game/SourceArt/Characters/ThirdPerson/S_Meso/S_Meso` and `S_Endo`
   (skeletal mesh, skeleton, rest pose) as glTF or PSK.
3. Record the bone list. The probe prints it: `Rig_Root`, `Rig_Pelvis`,
   `Rig_Spine1..3`, `Rig_Chest`, `Rig_Neck`, `Rig_Head`, `Rig_Arm_L/R_*` and
   so on. Commit the list to the catalog notes, never the export.
4. In Blender, import the reference, model clothing over it, and parent it to
   the imported armature with weights copied from the body (Data Transfer).
   Keep bone names and hierarchy exactly. Delete the reference body before
   export.
5. Export FBX: armature plus clothing mesh, no animation, +X forward / Z up
   as UE expects, scale 1.0 (cm).

## Cook pipeline

1. **Project:** a UE 4.26.2 project named `FPSAimTrainer`, so `/Game/` paths
   resolve as in the game. Content goes under `Content/AimModCosmetics/`.
2. **Placeholder skeletons:** create `Skeleton` assets at the game's exact
   paths (`/Game/SourceArt/Characters/ThirdPerson/S_Meso/S_Meso_Skeleton`,
   checked against the dump) from the exported reference, and exclude them
   from the cook. Our clothing meshes import against them, so the cooked
   package references the game's skeleton by path.
3. **Materials:** our own masters under `/Game/AimModCosmetics/Materials/`,
   opaque or masked, with vector parameters (`Color`, `Accent`) so tints can
   recolour outfits. Cook with "Share Material Shader Code" off.
4. **Cook:** `UE4Editor-Cmd.exe FPSAimTrainer.uproject -run=cook
   -targetplatform=WindowsNoEditor -cookdir=/Game/AimModCosmetics -unversioned`.
5. **Pak:** `UnrealPak.exe AimModCosmetics-<catalog version>.pak
   -create=list.txt`. The response file lists only
   `../../../FPSAimTrainer/Content/AimModCosmetics/...` files. There is no
   `AssetRegistry.bin`, no ini, and no `_P` suffix.
6. **Release:** `Build-AimModPackage.ps1 -CosmeticsPaks <folder>` (exists)
   runs the manifest script. That validates the catalog, pins the pak's
   size and SHA-256 in `catalog-manifest.json` and stages it as
   `Paks\~AimMod\`. The installer places it in `Content\Paks\~AimMod\` and
   records it for repair and uninstall (exists).
7. **Still to add:** the pak index path rule (only
   `/Game/AimModCosmetics/` packages), see
   [cosmetics.md](cosmetics.md#hash-pinning).

## Runtime mounting and loading

- **Mount:** the engine mounts every `.pak` under `Content/Paks` at startup,
  before any mod runs. Mounting only adds the files; nothing references them
  until AimModCore loads one.
- **Load:** AimModCore loads an outfit only when the scope gate is open (an
  AimMod match or spectate session, or the Cosmetics preview), and only when
  its pak matched the manifest (`verifiedPaks`).
- **Change needed:** `Cosmetics::LoadAsset` uses the asset registry. A pak
  without `AssetRegistry.bin` is not in it, so loading must switch to
  `KismetSystemLibrary:LoadAsset_Blocking` (by soft path) for paths under
  `/Game/AimModCosmetics/`.
- **Catalog:** a new kind `outfit` (`parts: ["body"]`, `needsPak`). It has
  `mesh` per base model (`{"Meso": "/Game/AimModCosmetics/Outfits/Vest_A/SK_Vest_A_Meso.SK_Vest_A_Meso", ...}`),
  an optional `material` and tint vectors. There is one outfit slot, and
  outfits combine with head accessories.
- **Applier:** the follower component described above, destroyed when the
  gate closes. The preview wears it the same way (`outfit=` request line).

## Sizes and budgets

| Item | Triangles (LOD0) | LODs | Textures | On disk |
|---|---|---|---|---|
| Cap, helmet, balaclava | ≤ 2,000 | LOD1 50% | 512² BC1 + ORM | ≤ 1 MB |
| Gloves (pair) | ≤ 3,000 | LOD1 50% | 512² | ≤ 1 MB |
| Vest, jacket | ≤ 8,000 | LOD1 50%, LOD2 25% | 1024² BC1 + ORM | ≤ 4 MB |
| Full outfit | ≤ 15,000 | 3 LODs | 2 × 1024² | ≤ 8 MB |
| Whole pak | | | | ≤ 64 MB per catalog version |

Silhouettes stay within the base model's bounds plus 5 cm, so outfits never
read as a different hitbox. There are no emissive surfaces beyond small
trims (no beacons), and no translucency.

## First outfit set (proposal)

1. **Plate carrier** (vest, Meso and Endo): the proof item.
2. **Field cap** (rigid, head).
3. **Softshell jacket** (follower).
4. **Tactical gloves** (follower, hands only).
5. **Balaclava** (rigid shell over the head).

## Proof plan

1. A single original plate carrier, about 3,000 triangles, skinned to
   `S_Meso`, cooked into `AimModCosmetics-4.pak`.
2. AimModCore loads it with `LoadAsset_Blocking`, adds the follower with
   master pose in the Cosmetics preview, and logs the bone count match.
3. Pass: the preview PNG shows the vest following the idle pose; the
   manifest check rejects a modified pak; nothing loads outside the gate.

**Blocked on:** a UE 4.26.2 editor install (Epic Games Launcher, about
40 GB) on the build machine, and the team's sign-off on the art direction.
Blender and FModel suffice for everything before the cook.
