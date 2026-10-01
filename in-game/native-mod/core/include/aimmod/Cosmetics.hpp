#pragma once
// Curated AimMod cosmetics (in-game/docs/cosmetics.md): the scope gate, the
// installed catalog, its hash-pinned manifest and the looks the service
// hands over. Pure except VerifyManifest/Sha256File (file reads).
//
// The scope gate and catalog rules are exact ports of the Lua testbed
// (AimModCosmetics: CosmeticsScope.lua, CosmeticsCatalog.lua,
// CosmeticsUtil.isFreeLook) and keep its test vectors.
#include <cstdint>
#include <filesystem>
#include <map>
#include <optional>
#include <set>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace aimmod::cosmetics
{
    // ------------------------------------------------------------ scope gate

    inline constexpr double MaxMarkerAge = 120; // seconds; the service rewrites the marker every 30 s

    // "AimMod Match - <name> - <8 lowercase hex>" (MatchScenario.FilePattern without .sce).
    bool IsMatchScenario(std::string_view name);

    // aimmod-session.txt: v=1, mode=match|spectate|lobby, scenario=..., expires=<unix s>.
    struct Marker
    {
        std::string mode, scenario;
        double expires{};
    };
    std::optional<Marker> ParseMarker(std::string_view text, std::string* reason = nullptr);

    // Unknown values (nullopt) count as "off".
    struct ScopeState
    {
        std::optional<Marker> marker;
        std::optional<double> now; // unix seconds
        std::optional<std::string> scenario;
        std::optional<bool> inChallenge, benchmark, editor, loading;
    };
    struct Decision
    {
        bool avatars{}, localPlayer{};
        std::string reason;
    };
    Decision Decide(const ScopeState& state);
    // ScenarioManager:IsInChallenge and Scenario:IsInChallenge: false only
    // when both are false, true when either is true, else unknown.
    std::optional<bool> CombineChallenge(std::optional<bool> manager, std::optional<bool> scenario);

    // Avatar items go only on free looks: the model in the Default model
    // pack, the skin in the Default skin pack (or none / "Default").
    // Null sets (pack not loaded) fail closed.
    bool IsFreeLook(std::string_view model, std::string_view skin, const std::set<std::string>* freeModels, const std::set<std::string>* freeSkins);

    // --------------------------------------------------------------- catalog

    struct KindInfo
    {
        std::string_view name;
        bool body{}, weapon{}, arms{}, needsPak{};
    };
    const KindInfo* FindKind(std::string_view kind);

    struct Color
    {
        double r{}, g{}, b{}, a{};
    };
    struct Attachment
    {
        std::string bone;                 // bone or socket on the model's CharacterMesh0
        double location[3]{}, rotation[3]{}, scale[3]{1, 1, 1};
    };
    // Accessory fit for any rig: the first bone whose name contains `bone`
    // (case-insensitive), an anchor on it, an offset and a size in the
    // character's own frame (forward, right, up; cm). The mesh is turned so
    // its proportions match `size` and scaled to it.
    struct Fit
    {
        std::string bone;          // e.g. "Head", "Neck", "Chest"
        std::string anchor{"bone"}; // "bone"; or from the model's head: "top" (top of the head), "crown" (its centre), "chin" (its bottom)
        double offset[3]{}, size[3]{};
        bool keepAxes{}; // the mesh is authored in the character's frame (X forward, Z up): never turned
    };
    struct Item
    {
        std::string id;
        double version{};
        std::string kind, name;
        std::vector<std::string> models;                  // free base models the item fits
        std::optional<std::vector<std::string>> weapons;  // weapon model names (nullopt = any)
        std::vector<std::string> parts;                   // body, weapon, arms
        std::vector<std::pair<std::string, Color>> vector;
        std::vector<std::pair<std::string, double>> scalar;
        std::vector<std::pair<std::string, std::string>> textures; // parameter -> asset path (pak)
        std::optional<std::string> pak;                   // pak file name
        std::string mesh, material;                       // accessory assets (pak)
        std::string attachRole;                           // accessory: "head", "neck" or "spine"
        std::optional<Fit> fit;                           // accessory from game/engine meshes (no pak)
        std::string shape;                                // accessory from an AimMod runtime mesh ("<name>.amsh", manifest-pinned)
        std::vector<std::pair<std::string, Attachment>> attach; // per base model
        bool draft{};

        bool HasPart(std::string_view part) const;
        // Pak kinds need a verified pak, except accessories built from the
        // game's and engine's own meshes (IsGameAccessoryAsset) with a fit.
        bool NeedsPak() const;
        const Attachment* AttachmentFor(std::string_view model) const;
    };
    // Curated meshes and materials an accessory may use without a pak: the
    // engine's basic shapes, the map editor's brushes and the free Meso
    // material instances (tinted through their probed parameters).
    bool IsGameAccessoryAsset(std::string_view path, bool material);

    // Where an accessory goes: world location, rotation (pitch, yaw, roll in
    // degrees, UE convention) and scale per local axis, from the mesh's local
    // bounds, the anchor point and the character's forward direction.
    struct Placement
    {
        double location[3]{}, rotation[3]{}, scale[3]{1, 1, 1};
    };
    std::optional<Placement> PlaceAccessory(const Fit& fit, const double localMin[3], const double localMax[3], const double anchor[3], const double forward[3]);
    // Unit X, Y and Z axes of an UE rotator (pitch, yaw, roll in degrees).
    void RotatorAxes(const double rotation[3], double x[3], double y[3], double z[3]);

    // The head of a model from its own data (UMetaSkeletalCharacterModel:
    // MeshFullHeight, MeshHeadDiameter) and its feet (the mesh's origin):
    // where "top", "crown" and "chin" anchors sit, and the scale for pieces
    // made for a 22 cm head (clamped 0.6 to 1.6). Invalid on implausible data.
    struct HeadPoints
    {
        bool valid{};
        double top{}, centre{}, chin{}, scale{1};
    };
    HeadPoints HeadGeometry(double feetZ, double fullHeight, double headDiameter);

    // ------------------------------------------------------- colour mapping
    //
    // A tint or finish keeps the skin's identity: it recolours only the
    // parameters a material uses for paint and accents, and leaves neutral
    // ones (black, white, grey and brown metal, silicone, raw metal) alone.
    // Rules by parameter name, so they work on any master:
    //   - the base paint material (MetalPaint and TriangularPaint): the
    //     item's own parameters as they are (it is a full paint scheme);
    //   - skin masters: accent paints (Metal<colour> other than the
    //     neutrals, MetalPattern, *Paint, LeatherColor) take the item's main
    //     colour, then its trim colour, alternating in name order; accent
    //     glows (*EmissiveColor other than EmissiveColor itself) take the main
    //     colour dimmed to at most the item's glow limit;
    //   - weapon masters: AccentColor and Emissive (and any *Accent*) take
    //     the finish's accent and glow.
    // An empty result means the material has nothing to recolour.
    struct Colours
    {
        Color main{}, trim{}, metal{}, glow{};
        bool hasTrim{}, hasMetal{}, hasGlow{};
    };
    // The item's colours: main = MetalPaint or AccentColor, trim = TriangularPaint,
    // metal = RawMetal, glow = Emissive (or main).
    Colours ItemColours(const Item& item);
    std::vector<std::pair<std::string, Color>> MapColours(const Item& item, const std::set<std::string>& materialVectors);

    // Structural check (CosmeticsCatalog.validate): nullopt = valid, else the reason.
    std::optional<std::string> Validate(const Item& item);

    // catalog.json: {"version": <n>, "items": [ ... ]}. Items that cannot be
    // read are reported in `errors` and left out; the file itself is
    // rejected (nullopt) when it is not a catalog at all.
    struct Catalog
    {
        double version{};
        std::vector<Item> items;
        std::vector<std::string> errors;
    };
    std::optional<Catalog> ParseCatalog(std::string_view json, std::string* error = nullptr);

    // Valid items by id; a duplicated id drops every entry of it.
    using Index = std::map<std::string, Item>;
    Index BuildIndex(const std::vector<Item>& items, std::vector<std::string>& errors);

    struct ResolveOptions
    {
        bool allowDrafts{};
        std::set<std::string> verifiedPaks;
        std::set<std::string> verifiedMeshes; // .amsh files that matched the manifest
    };
    // Unknown ids, drafts (unless allowed) and pak items without a verified
    // pak resolve to null; `reason` says why.
    const Item* Resolve(const Index& index, std::string_view id, const ResolveOptions& options, std::string* reason = nullptr);
    std::vector<const Item*> Pickable(const Index& index);

    // -------------------------------------------------------------- manifest

    struct ManifestFile
    {
        std::string name;
        std::uint64_t size{};
        std::string sha256; // 64 lowercase hex
    };
    // catalog-manifest.json: {"version": <catalog version>,
    //   "files": [{"name", "size", "sha256"}...], optional "items": [{"id", "version"}...]}
    // Other members are ignored; hashes are case-insensitive hex.
    struct Manifest
    {
        double catalogVersion{};
        std::vector<ManifestFile> files; // catalog.json exactly once, then paks
        std::vector<std::pair<std::string, double>> items;
        bool listsItems{};
    };
    std::optional<Manifest> ParseManifest(std::string_view json, std::string* error = nullptr);

    std::optional<std::string> Sha256File(const std::filesystem::path& path); // lowercase hex
    std::string Sha256Hex(std::string_view data);

    // Checks catalog.json (in `catalogDir`) and every listed pak (in
    // `paksDir`) by size, then SHA-256. Unlisted paks in `paksDir` are
    // reported and never verified.
    struct ManifestCheck
    {
        bool catalogVerified{};
        std::set<std::string> verifiedPaks;
        std::vector<std::string> problems;
    };
    ManifestCheck VerifyManifest(const Manifest& manifest, const std::filesystem::path& catalogDir, const std::filesystem::path& paksDir);
    // Drops catalog items the manifest does not list with the same version,
    // and reports a catalog version mismatch.
    void ApplyManifestItems(const Manifest& manifest, Catalog& catalog, std::vector<std::string>& problems);

    // ----------------------------------------------------------------- looks

    bool IsSteamId64(std::string_view text);
    // "AimMod.Peer.<SteamID64>" actor tag -> SteamID64.
    std::optional<std::string> PeerFromTag(std::string_view tag);

    struct ItemRef
    {
        std::string id;
        int version{};
    };
    struct PeerLook
    {
        std::string steamId;
        std::vector<ItemRef> items;
    };
    // cosmetic-looks.txt: "v=1", then "peer=<SteamID64> items=<id>@<v>,..."
    // lines (none for a peer without items) and at most one
    // "self=<id>@<v>,..." line; at most 8 items per line. Validated whole.
    struct Looks
    {
        std::vector<PeerLook> peers;
        std::vector<ItemRef> self;
        const PeerLook* Find(std::string_view steamId) const;
    };
    std::optional<Looks> ParseLooks(std::string_view text, std::string* error = nullptr);

    // What one character wears, after resolution and fit: at most one
    // parameter item per part and one head, one neck and one spine accessory.
    struct Plan
    {
        const Item* body{};    // tint/pattern on the avatar mesh
        const Item* head{};    // accessory
        const Item* neck{};    // accessory
        const Item* spine{};   // accessory
        const Item* weapon{};  // finish on the own selected weapon
        const Item* arms{};    // finish on the own arms
        std::vector<std::string> skipped; // id: reason
    };
    // Avatar plan (body and accessories) for a character of `model`.
    Plan PlanAvatar(const Index& index, const std::vector<ItemRef>& refs, const ResolveOptions& options, std::string_view model);
    // Own weapon/arms plan for the selected weapon model.
    Plan PlanLocal(const Index& index, const std::vector<ItemRef>& refs, const ResolveOptions& options, std::string_view weaponModel);
} // namespace aimmod::cosmetics
