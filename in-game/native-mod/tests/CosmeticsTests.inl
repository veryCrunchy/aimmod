// Cosmetics checks (included by CoreTests.cpp). The scope and catalog
// vectors are those of the Lua testbed (scope.test.lua, catalog.test.lua,
// util.test.lua) so the two implementations cannot drift apart.
#include <aimmod/Cosmetics.hpp>
#include <aimmod/Json.hpp>

#include <cctype>
#include <iterator>
#include <process.h>
#include <set>

namespace cosmetics_checks
{
    using namespace aimmod::cosmetics;

    std::string ReadText(const std::filesystem::path& path)
    {
        std::ifstream in(path, std::ios::binary);
        return std::string(std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>());
    }

    void Scope()
    {
        const std::string match = "AimMod Match - Tile Frenzy - Duel - 0a1b2c3d";
        CHECK(IsMatchScenario(match), "match scenario");
        CHECK(IsMatchScenario("AimMod Match - x - deadbeef"), "short match scenario");
        for (const char* name : {"Tile Frenzy", "AimMod Match - Tile Frenzy", "AimMod Match - Tile - 0A1B2C3D", "AimMod Match - Tile - 0a1b2c3",
                                 "AimMod Match - Tile - 0a1b2c3d9", "aimmod match - t - 0a1b2c3d", "VT AimMod Match - Tile - 0a1b2c3d",
                                 "AimMod Match -  - 0a1b2c3d x", ""})
            CHECK(!IsMatchScenario(name), "not a match scenario");
        CHECK(!IsMatchScenario("AimMod Match - " + std::string(121, 'x') + " - 0a1b2c3d") && IsMatchScenario("AimMod Match - " + std::string(120, 'x') + " - 0a1b2c3d"),
              "match name length limit");

        auto marker = [&](const std::string& mode, long long expires, const std::string& scenario) {
            return "v=1\nmode=" + mode + "\nscenario=" + scenario + "\nexpires=" + std::to_string(expires) + "\n";
        };
        CHECK(ParseMarker(marker("match", 1000, match))->mode == "match", "marker parses");
        CHECK(!ParseMarker("") && !ParseMarker("v=2\nmode=match\nexpires=1000"), "missing marker, unknown version");
        CHECK(!ParseMarker("v=1\nmode=ranked\nexpires=1000") && !ParseMarker("v=1\nmode=match\nexpires=soon"), "unknown mode, bad expiry");
        CHECK(!ParseMarker(std::string(2000, 'x')), "oversized marker");
        CHECK(ParseMarker("v=1\r\nmode=match\r\nexpires= 1e3 \r\n")->expires == 1000 && !ParseMarker("v=1\nmode=match\nexpires=1000.5") &&
                  !ParseMarker("v=1\nmode=match\nexpires=inf"),
              "expiry follows Lua tonumber, integral only");

        const double now = 1000;
        auto state = [&]() {
            ScopeState s;
            s.marker = ParseMarker(marker("match", 1060, match));
            s.now = now;
            s.scenario = match;
            s.inChallenge = s.benchmark = s.editor = s.loading = false;
            return s;
        };
        auto off = [&](ScopeState s, const char* why) {
            Decision d = Decide(s);
            CHECK(!d.avatars && !d.localPlayer, why);
        };
        Decision on = Decide(state());
        CHECK(on.avatars && on.localPlayer && on.reason == "AimMod match", "the one fully on case");
        ScopeState s;
        s = state(); s.scenario = "Tile Frenzy"; off(s, "normal scenario even with an AimMod session");
        s = state(); s.inChallenge = true; off(s, "challenge in a match scenario");
        s = state(); s.inChallenge.reset(); off(s, "unknown challenge state");
        s = state(); s.benchmark = true; off(s, "benchmark");
        s = state(); s.benchmark.reset(); off(s, "unknown benchmark state");
        s = state(); s.editor = true; off(s, "scenario editor");
        s = state(); s.loading = true; off(s, "loading");
        s = state(); s.scenario.reset(); off(s, "unknown scenario");
        s = state(); s.marker.reset(); off(s, "no session");
        s = state(); s.marker = ParseMarker(marker("match", 1000, match)); off(s, "expired session");
        s = state(); s.marker = ParseMarker(marker("match", 1000 + 3600, match)); off(s, "marker too far in the future");
        s = state(); s.marker = ParseMarker(marker("match", 1060, "AimMod Match - Other - 0a1b2c3e")); off(s, "different match");
        s = state(); s.now.reset(); off(s, "no clock");
        s = state(); s.marker = ParseMarker(marker("lobby", 1060, match)); off(s, "lobby is preview only");
        off(ScopeState{}, "no state");
        s = state(); s.marker = ParseMarker(marker("spectate", 1060, match));
        Decision spectate = Decide(s);
        CHECK(spectate.avatars && !spectate.localPlayer, "spectating: avatars only");

        CHECK(CombineChallenge(false, false) == std::optional<bool>(false) && CombineChallenge(true, false) == std::optional<bool>(true) &&
                  CombineChallenge(std::nullopt, true) == std::optional<bool>(true) && !CombineChallenge(false, std::nullopt),
              "challenge flags combine");

        const std::set<std::string> models{"Meso", "Endo"}, skins{"McCree", "Genji"};
        CHECK(IsFreeLook("Meso", "McCree", &models, &skins), "free skin");
        CHECK(IsFreeLook("Endo", "Default", &models, &skins) && IsFreeLook("Endo", "", &models, &skins), "default skin");
        CHECK(!IsFreeLook("AnimeGirl", "Default", &models, &skins), "DLC model rejected");
        CHECK(!IsFreeLook("Meso", "Variant_1", &models, &skins), "unknown skin rejected");
        CHECK(!IsFreeLook("Meso", "McCree", nullptr, nullptr), "packs unavailable fails closed");
        CHECK(!IsFreeLook("", "McCree", &models, &skins) && !IsFreeLook("None", "", &models, &skins), "no model");
    }

    Item Good()
    {
        Item item;
        item.id = "t-1";
        item.version = 1;
        item.kind = "avatar_tint";
        item.models = {"Meso"};
        item.parts = {"body"};
        item.vector = {{"Tint", {1, 0, 0, 1}}};
        return item;
    }

    void CatalogChecks()
    {
        // The shipped catalog is valid: parameter items are pickable today,
        // pak items stay drafts until the pak ships.
        std::string error;
        auto shipped = ParseCatalog(ReadText(std::filesystem::path(AIMMOD_SOURCE_DIR) / "cosmetics" / "catalog.json"), &error);
        CHECK(shipped && shipped->errors.empty() && shipped->version == 5, "shipped catalog parses");
        std::vector<std::string> errors;
        Index byId = BuildIndex(shipped ? shipped->items : std::vector<Item>{}, errors);
        CHECK(errors.empty() && byId.size() == 16 && shipped && byId.size() == shipped->items.size(), "shipped catalog validates");
        bool pakDrafts = true, freeModels = true;
        for (const auto& [id, item] : byId)
        {
            if (item.pak) pakDrafts &= item.draft;
            for (const std::string& m : item.models) freeModels &= m == "Meso" || m == "Endo";
        }
        CHECK(pakDrafts && freeModels, "pak items are drafts; every item is on free base models");
        const auto pickable = Pickable(byId);
        CHECK(pickable.size() == 15 && std::none_of(pickable.begin(), pickable.end(), [](const Item* i) { return i->draft || i->pak; }),
              "the tints, finishes and game-mesh accessories are offered in the picker, drafts are not");

        ResolveOptions none, drafted{true, {}}, paks{true, {"AimModCosmetics-1.pak"}};
        CHECK(!Resolve(byId, "not-an-item", none) && !Resolve(byId, "", none), "unknown and empty ids fall back");
        CHECK(Resolve(byId, "tint-mint", none) && Resolve(byId, "finish-gold", none), "shipped parameter items resolve");
        CHECK(!Resolve(byId, "meso-pattern-stripes", drafted), "draft pak item needs a verified pak");
        CHECK(!Resolve(byId, "meso-pattern-stripes", none), "draft hidden by default");
        CHECK(Resolve(byId, "meso-pattern-stripes", paks) != nullptr, "pak item with a verified pak");
        ResolveOptions meshes;
        meshes.verifiedMeshes = {"halo.amsh", "visor.amsh", "collar.amsh"};
        Plan dressed = PlanAvatar(byId, {{"accessory-halo", 3}, {"accessory-collar", 3}, {"accessory-visor", 2}}, meshes, "Endo");
        CHECK(dressed.head && dressed.head->id == "accessory-halo" && dressed.neck && dressed.skipped.size() == 1,
              "shipped accessories: one per head and neck");
        CHECK(!PlanAvatar(byId, {{"accessory-halo", 3}}, none, "Endo").head, "a runtime-mesh accessory without its verified mesh is not worn");
        Plan meso = PlanAvatar(byId, {{"tint-gold", 1}}, none, "Meso"), endo = PlanAvatar(byId, {{"tint-gold", 1}}, none, "Endo");
        CHECK(meso.body && meso.body->id == "tint-gold" && endo.body && endo.body->id == "tint-gold", "tints fit both free models");
        Plan own = PlanLocal(byId, {{"finish-ice", 1}}, none, "Rifle");
        CHECK(own.weapon && own.weapon->id == "finish-ice" && !own.arms, "weapon finishes dress the own weapon only");

        CHECK(!Validate(Good()), "good item");
        auto bad = [&](auto change, const char* why) {
            Item item = Good();
            change(item);
            CHECK(Validate(item).has_value(), why);
        };
        bad([](Item& i) { i.id = "Bad Id"; }, "id format");
        bad([](Item& i) { i.id = "../x"; }, "id format (path)");
        bad([](Item& i) { i.version = 0; }, "version");
        bad([](Item& i) { i.kind = "user_texture"; }, "unknown kind");
        bad([](Item& i) { i.parts = {"weapon"}; }, "part not allowed for kind");
        bad([](Item& i) { i.models.clear(); }, "avatar items need models");
        bad([](Item& i) { i.vector = {{"Tint", {2, 0, 0, 1}}}; }, "colour range");
        bad([](Item& i) { i.vector.clear(); }, "no parameters");
        bad([](Item& i) { i.vector.clear(); i.scalar = {{"Rough", std::nan("")}}; }, "NaN scalar");
        bad([](Item& i) { i.pak = "x.pak"; i.mesh = "/Game/Other/SM_X.SM_X"; }, "assets only from AimMod's own namespace");
        Item accessory;
        accessory.id = "p";
        accessory.version = 1;
        accessory.kind = "accessory";
        accessory.models = {"Meso"};
        accessory.parts = {"body"};
        CHECK(Validate(accessory).has_value(), "pak kinds need a pak");
        std::vector<std::string> dupErrors;
        Index dup = BuildIndex({Good(), Good()}, dupErrors);
        CHECK(dupErrors.size() == 1 && !dup.contains("t-1"), "duplicate ids are dropped");

        auto parsed = ParseCatalog(R"({"version":1,"items":[{"id":"x","version":1,"kind":"avatar_tint","models":["Meso"],"parts":["body"],
            "vector":{"Tint":{"R":0,"G":1,"B":0,"A":1}},"texture":"C:/file.png"}]})");
        CHECK(parsed && parsed->items.empty() && parsed->errors.size() == 1, "file references are rejected");
        CHECK(!ParseCatalog("{\"version\":1}") && !ParseCatalog("[]") && !ParseCatalog("{\"version\":1,\"items\":[],\"extra\":1}"), "not a catalog");
        auto halo = ParseCatalog(R"({"version":2,"items":[{"id":"accessory-halo","version":1,"kind":"accessory","models":["Meso"],"parts":["body"],
            "pak":{"file":"AimModCosmetics-2.pak"},"mesh":"/Game/AimModCosmetics/Halo/SM_Halo.SM_Halo",
            "attach":{"role":"head","models":{"Meso":{"bone":"head","location":[0,0,22],"rotation":[0,0,0],"scale":[1,1,1]}}}}]})");
        CHECK(halo && halo->items.size() == 1 && !Validate(halo->items[0]) && halo->items[0].AttachmentFor("Meso") &&
                  halo->items[0].AttachmentFor("Meso")->location[2] == 22 && !halo->items[0].AttachmentFor("Endo"),
              "accessory attachment per model");
    }

    void ManifestChecks()
    {
        const auto root = std::filesystem::temp_directory_path() / ("aimmod-cosmetics-" + std::to_string(_getpid()));
        std::filesystem::remove_all(root);
        const auto catalogDir = root / "service" / "cosmetics", paksDir = root / "Paks" / "~AimMod";
        std::filesystem::create_directories(catalogDir);
        std::filesystem::create_directories(paksDir / "sub");
        const std::string catalog = "{\"version\":1,\"items\":[]}";
        const std::string pak = "synthetic pak bytes";
        std::ofstream(catalogDir / "catalog.json", std::ios::binary) << catalog;
        std::ofstream(paksDir / "AimModCosmetics-1.pak", std::ios::binary) << pak;
        std::ofstream(paksDir / "sub" / "Unlisted.pak", std::ios::binary) << "x";
        CHECK(Sha256Hex("abc") == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "sha256 known answer");
        CHECK(Sha256File(paksDir / "AimModCosmetics-1.pak") == Sha256Hex(pak), "file hash");
        auto manifestText = [&](const std::string& pakHash, std::size_t pakSize) {
            std::string upper = Sha256Hex(catalog);
            for (char& c : upper) c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
            return "{\"version\":1,\"files\":[{\"name\":\"catalog.json\",\"size\":" + std::to_string(catalog.size()) + ",\"sha256\":\"" + upper +
                   "\"},{\"name\":\"AimModCosmetics-1.pak\",\"size\":" + std::to_string(pakSize) + ",\"sha256\":\"" + pakHash + "\"}]}";
        };
        auto m = ParseManifest(manifestText(Sha256Hex(pak), pak.size()));
        CHECK(m && m->files.size() == 2 && m->catalogVersion == 1, "manifest parses (upper-case hash accepted)");
        auto check = VerifyManifest(*m, catalogDir, paksDir);
        CHECK(check.catalogVerified && check.verifiedPaks.contains("AimModCosmetics-1.pak"), "matching manifest verifies");
        CHECK(check.problems.size() == 1 && check.problems[0] == "extra pak ignored: sub/Unlisted.pak", "extra unlisted pak reported, never verified");
        auto wrongHash = VerifyManifest(*ParseManifest(manifestText(std::string(64, 'a'), pak.size())), catalogDir, paksDir);
        CHECK(wrongHash.catalogVerified && wrongHash.verifiedPaks.empty() && wrongHash.problems[0] == "wrong hash AimModCosmetics-1.pak", "wrong hash");
        auto wrongSize = VerifyManifest(*ParseManifest(manifestText(Sha256Hex(pak), pak.size() + 1)), catalogDir, paksDir);
        CHECK(wrongSize.verifiedPaks.empty() && wrongSize.problems[0].rfind("wrong size AimModCosmetics-1.pak", 0) == 0, "wrong size");
        std::filesystem::remove(paksDir / "AimModCosmetics-1.pak");
        auto missing = VerifyManifest(*m, catalogDir, paksDir);
        CHECK(missing.catalogVerified && missing.verifiedPaks.empty() && missing.problems[0] == "missing AimModCosmetics-1.pak", "missing file");
        std::ofstream(catalogDir / "catalog.json", std::ios::binary) << catalog << " ";
        CHECK(!VerifyManifest(*m, catalogDir, paksDir).catalogVerified, "edited catalog is not verified");
        CHECK(!ParseManifest("") && !ParseManifest("{\"version\":1}") && !ParseManifest("{\"version\":1,\"files\":[]}") &&
                  !ParseManifest("{\"version\":1,\"files\":[{\"name\":\"../x.pak\",\"size\":1,\"sha256\":\"" + std::string(64, 'a') + "\"}]}") &&
                  !ParseManifest("{\"version\":1,\"files\":[{\"name\":\"catalog.json\",\"size\":1,\"sha256\":\"zz\"}]}") && !ParseManifest("{\"version\":1,"),
              "malformed manifests");
        std::filesystem::remove_all(root);

        Catalog c{1, {Good()}, {}};
        Manifest listed{1, {}, {{"t-1", 2}}, true};
        std::vector<std::string> problems;
        ApplyManifestItems(listed, c, problems);
        CHECK(c.items.empty() && problems.size() == 1, "item version must match the manifest");
        Catalog c2{2, {Good()}, {}};
        problems.clear();
        ApplyManifestItems(Manifest{1, {}, {}, false}, c2, problems);
        CHECK(c2.items.empty() && problems.size() == 1, "catalog version must match the manifest");
    }

    void LooksChecks()
    {
        const std::string a = "76561198000000001", b = "76561198000000002";
        auto looks = ParseLooks("v=1\npeer=" + a + " items=meso-tint-ember@1,accessory-halo@1\npeer=" + b + " items=weapon-finish-sand@1\nself=weapon-finish-aimmod@1\n");
        CHECK(looks && looks->peers.size() == 2 && looks->Find(a)->items.size() == 2 && looks->Find(a)->items[1].id == "accessory-halo" &&
                  looks->self.size() == 1 && !looks->Find("76561198000000003"),
              "looks parse");
        CHECK(ParseLooks("v=1\r\nself=\r\n") && ParseLooks("v=1\n") && ParseLooks("v=1\n")->self.empty(), "no items, CRLF, optional self");
        const std::vector<std::string> bads = {std::string(""), std::string("v=2\nself=\n"), "peer=" + a + " items=x@1\nv=1\n", "v=1\nself=\nself=\n",
                                       "v=1\npeer=" + a + " items=x@1\npeer=" + a + " items=y@1\n", "v=1\npeer=abc items=x@1\n",
                                       "v=1\npeer=" + a + " items=X@1\n", "v=1\npeer=" + a + " items=x@0\n", "v=1\npeer=" + a + " items=x\n",
                                       "v=1\npeer=" + a + " items=x@1,x@1\n", "v=1\npeer=" + a + "\n", "v=1\nself=a@1,b@1,c@1,d@1,e@1,f@1,g@1,h@1,i@1\n",
                                       "v=1\nfriend=" + a + "\n"};
        for (const std::string& bad : bads) CHECK(!ParseLooks(bad), "malformed looks are rejected whole");
        CHECK(PeerFromTag("AimMod.Peer." + a) == a && !PeerFromTag("AimMod.Peer.") && !PeerFromTag("AimMod.Peer.12a") && !PeerFromTag("Other." + a),
              "avatar tags");
    }

    void PlanChecks()
    {
        std::vector<std::string> errors;
        auto catalog = ParseCatalog(R"({"version":1,"items":[
            {"id":"tint","version":1,"kind":"avatar_tint","models":["Meso"],"parts":["body"],"vector":{"Tint":{"R":0,"G":1,"B":0,"A":1}}},
            {"id":"tint-2","version":1,"kind":"avatar_tint","models":["Meso"],"parts":["body"],"scalar":{"Rough":0.5}},
            {"id":"finish","version":2,"kind":"weapon_finish","parts":["weapon","arms"],"scalar":{"Roughness":0.2}},
            {"id":"pistol-only","version":1,"kind":"weapon_finish","weapons":["Pistol"],"parts":["weapon"],"scalar":{"Metallic":1}},
            {"id":"halo","version":1,"kind":"accessory","models":["Meso"],"parts":["body"],"pak":{"file":"A.pak"},
             "mesh":"/Game/AimModCosmetics/SM_Halo.SM_Halo","attach":{"role":"head","models":{"Meso":{"bone":"head"}}}},
            {"id":"visor","version":1,"kind":"accessory","models":["Meso"],"parts":["body"],"pak":{"file":"A.pak"},
             "mesh":"/Game/AimModCosmetics/SM_Visor.SM_Visor","attach":{"role":"head","models":{"Meso":{"bone":"head"}}}}]})");
        Index index = BuildIndex(catalog->items, errors);
        CHECK(errors.empty() && index.size() == 6, "plan catalog");
        ResolveOptions options{false, {"A.pak"}};
        Plan avatar = PlanAvatar(index, {{"tint", 1}, {"tint-2", 1}, {"halo", 1}, {"visor", 1}, {"finish", 2}, {"gone", 1}}, options, "Meso");
        CHECK(avatar.body && avatar.body->id == "tint" && avatar.head && avatar.head->id == "halo" && !avatar.spine && !avatar.weapon,
              "one body item and one head item per avatar");
        CHECK(avatar.skipped.size() == 3, "second tint, second head item and unknown id skipped");
        Plan endo = PlanAvatar(index, {{"tint", 1}}, options, "Endo");
        CHECK(!endo.body && endo.skipped.size() == 1, "items fit their base models only");
        Plan noPak = PlanAvatar(index, {{"halo", 1}}, ResolveOptions{}, "Meso");
        CHECK(!noPak.head, "accessory without a verified pak");
        Plan newer = PlanAvatar(index, {{"tint", 2}}, options, "Meso");
        CHECK(!newer.body && newer.skipped[0].find("update AimMod") != std::string::npos, "newer shared version falls back");
        Plan local = PlanLocal(index, {{"finish", 2}, {"pistol-only", 1}, {"tint", 1}}, options, "Rifle");
        CHECK(local.weapon && local.weapon->id == "finish" && local.arms && local.arms->id == "finish" && !local.body, "own weapon and arms finish");
        Plan pistol = PlanLocal(index, {{"pistol-only", 1}}, options, "Pistol");
        CHECK(pistol.weapon && pistol.weapon->id == "pistol-only" && !pistol.arms, "weapon-specific finish");
    }

    bool Near(double a, double b, double tolerance = 1e-6) { return std::abs(a - b) <= tolerance; }

    void AccessoryChecks()
    {
        // A flat torus brush (100 x 100 x 20, centred) as a halo 20 cm above the head.
        Fit halo{"Head", "top", {0, 0, 20}, {26, 26, 3}};
        const double flatMin[3] = {-50, -50, -10}, flatMax[3] = {50, 50, 10}, head[3] = {10, 20, 170}, ahead[3] = {1, 0, 0};
        auto p = PlaceAccessory(halo, flatMin, flatMax, head, ahead);
        CHECK(p && Near(p->scale[0], 0.26) && Near(p->scale[1], 0.26) && Near(p->scale[2], 0.15) && Near(p->rotation[0], 0) && Near(p->rotation[1], 0) &&
                  Near(p->rotation[2], 0) && Near(p->location[0], 10) && Near(p->location[1], 20) && Near(p->location[2], 190),
              "a flat ring is scaled to size and centred over the anchor");
        // The same torus standing upright (thin along Y) is laid flat: its local Y points up.
        const double uprightMin[3] = {-50, -10, -50}, uprightMax[3] = {50, 10, 50};
        p = PlaceAccessory(halo, uprightMin, uprightMax, head, ahead);
        double x[3], y[3], z[3];
        if (p) RotatorAxes(p->rotation, x, y, z);
        CHECK(p && Near(std::abs(y[2]), 1, 1e-6) && Near(p->scale[1], 0.15) && Near(p->scale[0], 0.26) && Near(p->scale[2], 0.26) && Near(p->location[2], 190),
              "an upright mesh is turned so its thin axis is vertical");
        // A back disc behind the chest of a character facing +Y, from a mesh with its pivot at the bottom.
        Fit disc{"Chest", "bone", {-16, 0, 0}, {2, 22, 22}};
        const double cylMin[3] = {-50, -50, 0}, cylMax[3] = {50, 50, 4}, chest[3] = {0, 0, 120}, facingY[3] = {0, 3, 0};
        p = PlaceAccessory(disc, cylMin, cylMax, chest, facingY);
        if (p) RotatorAxes(p->rotation, x, y, z);
        // The thin local Z axis now points along the character's forward (+Y); the bounds' centre sits 16 cm behind.
        double centre[3]{};
        if (p)
            for (int k = 0; k < 3; ++k) centre[k] = p->location[k] + x[k] * p->scale[0] * 0 + y[k] * p->scale[1] * 0 + z[k] * p->scale[2] * 2;
        CHECK(p && Near(std::abs(z[1]), 1, 1e-6) && Near(p->scale[2], 0.5) && Near(centre[0], 0, 1e-6) && Near(centre[1], -16, 1e-6) && Near(centre[2], 120, 1e-6),
              "a disc faces forward behind the anchor, pivot corrected");
        for (const double r : {0.0, 37.0, -120.0})
        {
            const double rot[3] = {r / 3, r, r / 2};
            RotatorAxes(rot, x, y, z);
            // Orthonormal and right-handed in UE's sense: X x Y = Z.
            CHECK(Near(x[0] * y[0] + x[1] * y[1] + x[2] * y[2], 0) && Near(x[0] * z[0] + x[1] * z[1] + x[2] * z[2], 0) &&
                      Near(x[0] * x[0] + x[1] * x[1] + x[2] * x[2], 1) && Near(x[1] * y[2] - x[2] * y[1], z[0]) && Near(x[2] * y[0] - x[0] * y[2], z[1]),
                  "rotator axes are orthonormal");
        }
        const double zero[3] = {0, 0, 0};
        CHECK(!PlaceAccessory(halo, flatMin, flatMax, head, zero) && !PlaceAccessory(halo, zero, zero, head, ahead), "no forward or empty bounds: no placement");

        // Game-mesh accessories need no pak, but only from the curated folders and with a fit.
        CHECK(IsGameAccessoryAsset("/Game/Art/StaticMeshes/KMC/Brushes/SM_Torus.SM_Torus", false) && IsGameAccessoryAsset("/Engine/BasicShapes/Cone.Cone", false) &&
                  !IsGameAccessoryAsset("/Game/Art/StaticMeshes/KMC/Props/Anime/SM_Bell.SM_Bell", false) &&
                  !IsGameAccessoryAsset("/Game/Art/StaticMeshes/KMC/Brushes/../Props/X.X", false) &&
                  IsGameAccessoryAsset("/Game/Materials/Instances/Characters/S_Meso/Base/MI_PaintedMetal_Meso_TS1.MI_PaintedMetal_Meso_TS1", true) &&
                  !IsGameAccessoryAsset("/Game/Materials/Instances/Characters/S_Meso/Base/MI_PaintedMetal_Meso_TS1.MI_PaintedMetal_Meso_TS1", false),
              "game accessory asset allow-list");
        std::vector<std::string> errors;
        auto catalog = ParseCatalog(R"({"version":1,"items":[
            {"id":"ring","version":1,"kind":"accessory","models":["Meso"],"parts":["body"],"mesh":"/Game/Art/StaticMeshes/KMC/Brushes/SM_Torus.SM_Torus",
             "material":"/Game/Materials/Instances/Characters/S_Meso/Base/MI_PaintedMetal_Meso_TS1.MI_PaintedMetal_Meso_TS1",
             "vector":{"MetalPaint":{"R":1,"G":0.8,"B":0.2,"A":1}},"attach":{"role":"head","fit":{"bone":"Head","anchor":"top","offset":[0,0,20],"size":[26,26,3]}}},
            {"id":"collar","version":1,"kind":"accessory","models":["Meso"],"parts":["body"],"mesh":"/Engine/BasicShapes/Cylinder.Cylinder",
             "material":"/Game/Materials/Instances/Characters/S_Meso/Base/MI_PaintedMetal_Meso_TS1.MI_PaintedMetal_Meso_TS1","attach":{"role":"neck","fit":{"bone":"Neck","size":[20,20,4]}}},
            {"id":"no-fit","version":1,"kind":"accessory","models":["Meso"],"parts":["body"],"mesh":"/Engine/BasicShapes/Cube.Cube",
             "material":"/Game/Materials/Instances/Characters/S_Meso/Base/MI_PaintedMetal_Meso_TS1.MI_PaintedMetal_Meso_TS1","attach":"head"},
            {"id":"prop","version":1,"kind":"accessory","models":["Meso"],"parts":["body"],"mesh":"/Game/Art/StaticMeshes/KMC/Props/Anime/SM_Bell.SM_Bell",
             "material":"/Game/Materials/Instances/Characters/S_Meso/Base/MI_PaintedMetal_Meso_TS1.MI_PaintedMetal_Meso_TS1","attach":{"role":"head","fit":{"bone":"Head","size":[20,20,20]}}},
            {"id":"huge","version":1,"kind":"accessory","models":["Meso"],"parts":["body"],"mesh":"/Engine/BasicShapes/Cube.Cube",
             "material":"/Game/Materials/Instances/Characters/S_Meso/Base/MI_PaintedMetal_Meso_TS1.MI_PaintedMetal_Meso_TS1","attach":{"role":"head","fit":{"bone":"Head","size":[200,20,20]}}}]})");
        Index index = BuildIndex(catalog ? catalog->items : std::vector<Item>{}, errors);
        CHECK(catalog && index.size() == 2 && index.contains("ring") && index.contains("collar") && errors.size() == 3,
              "game accessories validate; missing fit, props outside the brush folder and oversize fits are dropped");
        CHECK(index.contains("ring") && !index.at("ring").NeedsPak() && Resolve(index, "ring", ResolveOptions{}), "game accessories resolve without a pak");
        // An AimMod runtime mesh: an accessory's own shape, usable only when its file matched the manifest.
        auto shaped = ParseCatalog(R"({"version":1,"items":[
            {"id":"visor","version":1,"kind":"accessory","models":["Meso"],"parts":["body"],"shape":"visor.amsh",
             "material":"/Game/Materials/Instances/Characters/S_Meso/Base/MI_PaintedMetal_Meso_TS1.MI_PaintedMetal_Meso_TS1",
             "attach":{"role":"head","fit":{"bone":"Head","anchor":"crown","size":[13,26,5],"keepAxes":true}}},
            {"id":"bad-shape","version":1,"kind":"accessory","models":["Meso"],"parts":["body"],"shape":"../x.amsh",
             "material":"/Game/Materials/Instances/Characters/S_Meso/Base/MI_PaintedMetal_Meso_TS1.MI_PaintedMetal_Meso_TS1","attach":{"role":"head","fit":{"bone":"Head","size":[10,10,10]}}},
            {"id":"tint-shape","version":1,"kind":"avatar_tint","models":["Meso"],"parts":["body"],"shape":"halo.amsh","vector":{"MetalPaint":{"R":1,"G":1,"B":1,"A":1}}}]})");
        std::vector<std::string> shapeErrors;
        Index shapes = BuildIndex(shaped ? shaped->items : std::vector<Item>{}, shapeErrors);
        CHECK(shapes.size() == 1 && shapes.contains("visor") && shapes.at("visor").fit->keepAxes && !shapes.at("visor").NeedsPak() && shapeErrors.size() == 2,
              "runtime-mesh accessories validate; bad names and shapes on other kinds are dropped");
        ResolveOptions withMesh;
        withMesh.verifiedMeshes = {"visor.amsh"};
        CHECK(!Resolve(shapes, "visor", ResolveOptions{}) && Resolve(shapes, "visor", withMesh), "a runtime mesh needs its verified file");
        const double boxMin[3] = {0, -13, -2.5}, boxMax[3] = {13, 13, 2.5}, at[3] = {0, 0, 160}, sideways[3] = {0, 1, 0};
        auto kept = PlaceAccessory(shapes.at("visor").fit.value(), boxMin, boxMax, at, sideways);
        double kx[3], ky[3], kz[3];
        if (kept) RotatorAxes(kept->rotation, kx, ky, kz);
        CHECK(kept && Near(kx[1], 1, 1e-6) && Near(kz[2], 1, 1e-6) && Near(kept->scale[0], 1) && Near(kept->scale[1], 1), "keepAxes: the mesh's X follows the character's forward, never turned");
        Plan plan = PlanAvatar(index, {{"ring", 1}, {"collar", 1}}, ResolveOptions{}, "Meso");
        CHECK(plan.head && plan.head->id == "ring" && plan.neck && plan.neck->id == "collar" && plan.skipped.empty(), "one head and one neck accessory");
    }

    void ColourChecks()
    {
        std::string error;
        auto shipped = ParseCatalog(ReadText(std::filesystem::path(AIMMOD_SOURCE_DIR) / "cosmetics" / "catalog.json"), &error);
        std::vector<std::string> errors;
        Index byId = BuildIndex(shipped ? shipped->items : std::vector<Item>{}, errors);
        const Item& mint = byId.at("tint-mint");
        const Item& gold = byId.at("tint-gold");
        auto find = [](const std::vector<std::pair<std::string, Color>>& m, const std::string& name) -> const Color* {
            for (const auto& [n, c] : m)
                if (n == name) return &c;
            return nullptr;
        };
        // Skin masters from the live log: McCree, Genji, Tracer.
        const std::set<std::string> mccree = {"BodyColor", "EmissiveColor", "HeadColor", "MetalBlack", "MetalRed", "MetalYellow", "RawMetal", "SiliconeBlack", "SiliconeBrown", "SiliconeWhite", "SiliconeYellow"};
        const std::set<std::string> genji = {"BodyColor", "EmissiveColor", "HeadColor", "HexEmissiveColor", "HexPaint", "MetalBlack", "MetalBrown", "RawMetal", "Silicone"};
        const std::set<std::string> tracer = {"BodyColor", "EmissiveColor", "HeadColor", "MetalBlack", "MetalBrown", "MetalGray", "MetalOrange", "MetalPattern", "MetalWhite", "MetalYellow", "RawMetal"};
        const std::set<std::string> base = {"AccentColor", "BodyColor", "Color", "EmissiveColor", "HeadColor", "MetalPaint", "RawMetal", "Silicone", "TriangularPaint"};
        const std::set<std::string> pistol = {"AccentColor", "Emissive"};
        auto m = MapColours(mint, mccree);
        CHECK(m.size() == 2 && find(m, "MetalRed") && find(m, "MetalYellow") && find(m, "MetalRed")->g == 0.6 && !find(m, "MetalBlack") && !find(m, "BodyColor") && !find(m, "RawMetal"),
              "McCree: the tint recolours the red and yellow paint only; black, silicone and body colour stay the skin's");
        auto g = MapColours(gold, mccree);
        CHECK(g.size() == 2 && find(g, "MetalRed")->r != find(m, "MetalRed")->r, "McCree in Mint and in Gold differ");
        auto h = MapColours(mint, genji);
        CHECK(h.size() == 2 && find(h, "HexPaint") && find(h, "HexEmissiveColor") && !find(h, "EmissiveColor") &&
                  0.2126 * find(h, "HexEmissiveColor")->r + 0.7152 * find(h, "HexEmissiveColor")->g + 0.0722 * find(h, "HexEmissiveColor")->b <= 0.5 + 1e-9,
              "Genji: hex paint and a dimmed hex glow");
        auto t = MapColours(mint, tracer);
        CHECK(t.size() == 3 && find(t, "MetalOrange") && find(t, "MetalPattern") && find(t, "MetalYellow") && find(t, "MetalPattern")->r == 0.86, "Tracer: orange, pattern and yellow alternate main and trim");
        auto b = MapColours(mint, base);
        CHECK(b.size() == 4 && find(b, "MetalPaint") && find(b, "TriangularPaint") && find(b, "Silicone"), "the base paint material takes the tint's own scheme");
        auto w = MapColours(byId.at("finish-ice"), pistol);
        CHECK(w.size() == 2 && find(w, "AccentColor")->b == 1 && find(w, "Emissive"), "weapon finishes recolour the accent and its glow");
        CHECK(MapColours(mint, {"BodyColor", "MetalBlack", "Silicone"}).empty(), "nothing to recolour: the skin stays as it is");
    }

    void Json()
    {
        CHECK(aimmod::json::Parse(R"({"a":[1,-2.5e1,true,null,"\u00e9\ud83d\ude00"]})").has_value(), "json values");
        auto v = aimmod::json::Parse(R"({"a":"\u00e9"})");
        CHECK(v && v->find("a")->string == "\xc3\xa9", "json unicode escape");
        for (const char* bad : {"", "{", "{\"a\":1,}", "{\"a\":1,\"a\":2}", "[01]", "[1.]", "\"\\x\"", "{\"a\":1} x", "[\"\x01\"]", "[NaN]", "[1e999]"})
            CHECK(!aimmod::json::Parse(bad), "json rejects malformed input");
        std::string deep(40, '[');
        deep += std::string(40, ']');
        CHECK(!aimmod::json::Parse(deep), "json depth limit");
    }

    void Run()
    {
        Json();
        Scope();
        CatalogChecks();
        ManifestChecks();
        LooksChecks();
        AccessoryChecks();
        ColourChecks();
        PlanChecks();
    }
} // namespace cosmetics_checks
