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
        CHECK(shipped && shipped->errors.empty() && shipped->version == 2, "shipped catalog parses");
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
        CHECK(pickable.size() == 12 && std::none_of(pickable.begin(), pickable.end(), [](const Item* i) { return i->draft || i->pak; }),
              "the parameter tints and finishes are offered in the picker, drafts are not");

        ResolveOptions none, drafted{true, {}}, paks{true, {"AimModCosmetics-1.pak"}};
        CHECK(!Resolve(byId, "not-an-item", none) && !Resolve(byId, "", none), "unknown and empty ids fall back");
        CHECK(Resolve(byId, "tint-mint", none) && Resolve(byId, "finish-gold", none), "shipped parameter items resolve");
        CHECK(!Resolve(byId, "meso-pattern-stripes", drafted), "draft pak item needs a verified pak");
        CHECK(!Resolve(byId, "accessory-visor", none), "draft hidden by default");
        CHECK(!Resolve(byId, "accessory-visor", drafted), "pak item needs a verified pak");
        CHECK(Resolve(byId, "accessory-visor", paks) != nullptr, "pak item with a verified pak");
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
        PlanChecks();
    }
} // namespace cosmetics_checks
