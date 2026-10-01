#include <aimmod/Cosmetics.hpp>

#include <span>
#include <aimmod/Json.hpp>
#include <aimmod/Mesh.hpp>

#include <Windows.h>
#include <bcrypt.h>

#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstdlib>
#include <fstream>

#pragma comment(lib, "bcrypt.lib")

namespace aimmod::cosmetics
{
    namespace
    {
        bool IsLower(char c) { return c >= 'a' && c <= 'z'; }
        bool IsDigit(char c) { return c >= '0' && c <= '9'; }
        bool IsAlpha(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'); }
        bool IsLowerHex(std::string_view s)
        {
            for (char c : s)
                if (!IsDigit(c) && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }
        // ^[a-z0-9][a-z0-9%-]*$, at most 48 bytes.
        bool IsItemId(std::string_view id)
        {
            if (id.empty() || id.size() > 48 || id[0] == '-') return false;
            for (char c : id)
                if (!IsLower(c) && !IsDigit(c) && c != '-') return false;
            return true;
        }
        bool Finite(double n, double lo, double hi) { return std::isfinite(n) && n >= lo && n <= hi; }
        bool Contains(const std::vector<std::string>& list, std::string_view value)
        {
            return std::find(list.begin(), list.end(), value) != list.end();
        }
        // Parameter, bone and model names: FName-safe plain text.
        bool IsPlainName(std::string_view s)
        {
            if (s.empty() || s.size() > 64) return false;
            for (char c : s)
                if (!IsAlpha(c) && !IsDigit(c) && c != '_' && c != ' ' && c != '-' && c != '.') return false;
            return true;
        }
        // Assets only from AimMod's own pak namespace.
        bool IsAimModAsset(std::string_view path)
        {
            constexpr std::string_view root = "/Game/AimModCosmetics/";
            if (path.size() <= root.size() || path.size() > 200 || path.substr(0, root.size()) != root) return false;
            if (path.find("..") != std::string_view::npos || path.find("//") != std::string_view::npos) return false;
            for (char c : path)
                if (!IsAlpha(c) && !IsDigit(c) && c != '_' && c != '/' && c != '.' && c != '-') return false;
            return true;
        }
        bool IsAssetPathText(std::string_view path)
        {
            if (path.size() > 200 || path.find("..") != std::string_view::npos || path.find("//") != std::string_view::npos) return false;
            for (char c : path)
                if (!IsAlpha(c) && !IsDigit(c) && c != '_' && c != '/' && c != '.' && c != '-') return false;
            return true;
        }
        bool IsPakName(std::string_view s)
        {
            if (s.size() < 5 || s.size() > 128 || s.substr(s.size() - 4) != ".pak") return false;
            for (char c : s)
                if (!IsAlpha(c) && !IsDigit(c) && c != '_' && c != '.' && c != '-') return false;
            return true;
        }
        // Lua tonumber() on a marker value: surrounding whitespace, decimal
        // or hex, no inf/nan.
        std::optional<double> LuaNumber(std::string_view s)
        {
            auto space = [](char c) { return c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '\f' || c == '\r'; };
            while (!s.empty() && space(s.front())) s.remove_prefix(1);
            while (!s.empty() && space(s.back())) s.remove_suffix(1);
            if (s.empty() || s.find_first_of("nN") != std::string_view::npos) return std::nullopt;
            const std::string copy(s);
            char* end = nullptr;
            const double v = std::strtod(copy.c_str(), &end);
            if (end != copy.c_str() + copy.size() || !std::isfinite(v)) return std::nullopt;
            return v;
        }
        std::optional<int> RefVersion(std::string_view s)
        {
            if (s.empty() || s.size() > 6) return std::nullopt;
            int v = 0;
            for (char c : s)
            {
                if (!IsDigit(c)) return std::nullopt;
                v = v * 10 + (c - '0');
            }
            if (v < 1 || v > 100000) return std::nullopt;
            return v;
        }
        std::string Hex(const unsigned char* data, std::size_t size)
        {
            static const char digits[] = "0123456789abcdef";
            std::string out;
            for (std::size_t i = 0; i < size; ++i)
            {
                out += digits[data[i] >> 4];
                out += digits[data[i] & 15];
            }
            return out;
        }
        struct Sha256
        {
            BCRYPT_ALG_HANDLE alg{};
            BCRYPT_HASH_HANDLE hash{};
            bool ok{};
            Sha256()
            {
                ok = BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0 &&
                     BCryptCreateHash(alg, &hash, nullptr, 0, nullptr, 0, 0) >= 0;
            }
            ~Sha256()
            {
                if (hash) BCryptDestroyHash(hash);
                if (alg) BCryptCloseAlgorithmProvider(alg, 0);
            }
            bool Add(const void* data, std::size_t size)
            {
                return ok && BCryptHashData(hash, static_cast<PUCHAR>(const_cast<void*>(data)), static_cast<ULONG>(size), 0) >= 0;
            }
            std::optional<std::string> Finish()
            {
                unsigned char digest[32];
                if (!ok || BCryptFinishHash(hash, digest, sizeof(digest), 0) < 0) return std::nullopt;
                return Hex(digest, sizeof(digest));
            }
        };
    } // namespace

    // ------------------------------------------------------------ scope gate

    bool IsMatchScenario(std::string_view name)
    {
        constexpr std::string_view prefix = "AimMod Match - ";
        if (name.substr(0, prefix.size()) != prefix) return false;
        const std::string_view rest = name.substr(prefix.size());
        // Greedy "(.+) %- ([0-9a-f]+)$": the key follows the last " - ".
        const auto separator = rest.rfind(" - ");
        if (separator == std::string_view::npos || separator == 0) return false;
        const std::string_view key = rest.substr(separator + 3);
        return !key.empty() && IsLowerHex(key) && key.size() == 8 && separator <= 120;
    }

    std::optional<Marker> ParseMarker(std::string_view text, std::string* reason)
    {
        auto fail = [&](const char* why) -> std::optional<Marker> {
            if (reason) *reason = why;
            return std::nullopt;
        };
        if (text.empty() || text.size() > 1024) return fail("marker missing or too large");
        std::map<std::string, std::string, std::less<>> fields;
        std::size_t at = 0;
        while (at < text.size())
        {
            const auto end = text.find_first_of("\r\n", at);
            const std::string_view line = text.substr(at, end == std::string_view::npos ? std::string_view::npos : end - at);
            at = end == std::string_view::npos ? text.size() : end + 1;
            std::size_t k = 0;
            while (k < line.size() && IsAlpha(line[k])) ++k;
            if (k == 0 || k >= line.size() || line[k] != '=') continue;
            fields[std::string(line.substr(0, k))] = std::string(line.substr(k + 1));
        }
        auto field = [&](const char* key) -> std::optional<std::string> {
            auto it = fields.find(key);
            if (it == fields.end()) return std::nullopt;
            return it->second;
        };
        if (field("v") != std::optional<std::string>("1")) return fail("unsupported marker version");
        const std::string mode = field("mode").value_or("");
        if (mode != "lobby" && mode != "match" && mode != "spectate") return fail("unknown mode");
        auto expires = LuaNumber(field("expires").value_or(""));
        if (!expires || *expires != std::floor(*expires)) return fail("invalid expiry");
        return Marker{mode, field("scenario").value_or(""), *expires};
    }

    Decision Decide(const ScopeState& state)
    {
        auto off = [](const char* reason) { return Decision{false, false, reason}; };
        if (!state.marker) return off("no AimMod session");
        const Marker& marker = *state.marker;
        if (!state.now) return off("no clock");
        const double now = *state.now;
        if (marker.expires <= now) return off("AimMod session expired");
        if (marker.expires > now + MaxMarkerAge) return off("AimMod session marker out of range");
        if (state.inChallenge != std::optional<bool>(false)) return off("challenge or unknown challenge state");
        if (state.benchmark != std::optional<bool>(false)) return off("benchmark or unknown benchmark state");
        if (state.editor != std::optional<bool>(false)) return off("scenario editor or unknown editor state");
        if (state.loading != std::optional<bool>(false)) return off("scenario loading");
        if (!state.scenario || !IsMatchScenario(*state.scenario)) return off("not an AimMod match scenario");
        if (*state.scenario != marker.scenario) return off("scenario differs from the AimMod session");
        if (marker.mode == "match") return {true, true, "AimMod match"};
        if (marker.mode == "spectate") return {true, false, "AimMod spectating"};
        return off("AimMod lobby: preview only");
    }

    std::optional<bool> CombineChallenge(std::optional<bool> manager, std::optional<bool> scenario)
    {
        if (manager == std::optional<bool>(false) && scenario == std::optional<bool>(false)) return false;
        if (manager.value_or(false) || scenario.value_or(false)) return true;
        return std::nullopt;
    }

    bool IsFreeLook(std::string_view model, std::string_view skin, const std::set<std::string>* freeModels, const std::set<std::string>* freeSkins)
    {
        if (model.empty() || model == "None") return false;
        if (!freeModels || !freeModels->contains(std::string(model))) return false;
        if (skin.empty() || skin == "None" || skin == "Default") return true;
        return freeSkins && freeSkins->contains(std::string(skin));
    }

    // --------------------------------------------------------------- catalog

    const KindInfo* FindKind(std::string_view kind)
    {
        static const KindInfo kinds[] = {
            {"avatar_tint", true, false, false, false},     {"avatar_pattern", true, false, false, true},
            {"weapon_finish", false, true, true, false},    {"weapon_pattern", false, true, true, true},
            {"accessory", true, false, false, true},        {"weapon_model", false, true, false, true},
            {"reload_animation", false, false, true, true}, {"player_model", true, false, false, true},
        };
        for (const KindInfo& k : kinds)
            if (k.name == kind) return &k;
        return nullptr;
    }

    bool Item::HasPart(std::string_view part) const { return Contains(parts, part); }

    bool IsGameAccessoryAsset(std::string_view path, bool material)
    {
        static constexpr std::string_view meshes[] = {"/Engine/BasicShapes/", "/Game/Art/StaticMeshes/KMC/Brushes/"};
        static constexpr std::string_view materials[] = {"/Game/Materials/Instances/Characters/S_Meso/Base/MI_PaintedMetal_",
                                                         "/Game/Materials/Instances/Characters/S_Endo/Base/MI_PaintedMetal_"};
        if (!IsAssetPathText(path)) return false;
        // Flat, per-primitive-data-free materials for AimMod runtime meshes (the
        // character masters dither away on a component without the game's data).
        static constexpr std::string_view flat[] = {"/MapCreator/Materials/MM_G_Basic.MM_G_Basic", "/Engine/BasicShapes/BasicShapeMaterial.BasicShapeMaterial",
                                                    "/Game/Materials/Masters/Environment/MM_Glow.MM_Glow",
                                                    "/MapCreator/Materials/DefaultManipulationMaterial.DefaultManipulationMaterial"};
        if (material)
            for (std::string_view f : flat)
                if (path == f) return true;
        for (std::string_view root : material ? std::span<const std::string_view>(materials) : std::span<const std::string_view>(meshes))
            if (path.size() > root.size() && path.substr(0, root.size()) == root && path.find('/', root.size()) == std::string_view::npos) return true;
        return false;
    }

    bool Item::NeedsPak() const
    {
        const KindInfo* k = FindKind(kind);
        if (!k || !k->needsPak) return false;
        const bool game = kind == "accessory" && !pak && fit && IsGameAccessoryAsset(material, true) &&
                          (IsGameAccessoryAsset(mesh, false) || (mesh.empty() && mesh::IsMeshName(shape)));
        return !game;
    }

    void RotatorAxes(const double rotation[3], double x[3], double y[3], double z[3])
    {
        constexpr double D = 3.14159265358979323846 / 180.0;
        const double sp = std::sin(rotation[0] * D), cp = std::cos(rotation[0] * D), sy = std::sin(rotation[1] * D), cy = std::cos(rotation[1] * D),
                     sr = std::sin(rotation[2] * D), cr = std::cos(rotation[2] * D);
        // FRotationMatrix rows.
        x[0] = cp * cy, x[1] = cp * sy, x[2] = sp;
        y[0] = sr * sp * cy - cr * sy, y[1] = sr * sp * sy + cr * cy, y[2] = -sr * cp;
        z[0] = -(cr * sp * cy + sr * sy), z[1] = cy * sr - cr * sp * sy, z[2] = cr * cp;
    }

    HeadPoints HeadGeometry(double feetZ, double fullHeight, double headDiameter)
    {
        HeadPoints h;
        if (!std::isfinite(feetZ) || !std::isfinite(fullHeight) || !std::isfinite(headDiameter) || fullHeight < 30 || fullHeight > 600 || headDiameter < 5 ||
            headDiameter > 80)
            return h;
        h.valid = true;
        h.top = feetZ + fullHeight;
        h.centre = h.top - headDiameter / 2;
        h.chin = h.top - headDiameter;
        h.scale = std::clamp(headDiameter / 22.0, 0.6, 1.6);
        return h;
    }

    Colours ItemColours(const Item& item)
    {
        Colours c;
        bool main = false;
        for (const auto& [name, v] : item.vector)
        {
            if ((name == "MetalPaint" || name == "AccentColor") && !main) c.main = v, main = true;
            else if (name == "TriangularPaint") c.trim = v, c.hasTrim = true;
            else if (name == "RawMetal") c.metal = v, c.hasMetal = true;
            else if (name == "Emissive") c.glow = v, c.hasGlow = true;
        }
        if (!c.hasTrim) c.trim = c.main;
        if (!c.hasGlow) c.glow = c.main;
        return c;
    }

    std::vector<std::pair<std::string, Color>> MapColours(const Item& item, const std::set<std::string>& names)
    {
        std::vector<std::pair<std::string, Color>> out;
        // The base paint material: the item's own scheme.
        if (names.contains("MetalPaint") && names.contains("TriangularPaint"))
        {
            for (const auto& [name, v] : item.vector)
                if (names.contains(name)) out.push_back({name, v});
            return out;
        }
        const Colours c = ItemColours(item);
        auto lower = [](std::string s) {
            for (char& ch : s) ch = static_cast<char>(std::tolower(static_cast<unsigned char>(ch)));
            return s;
        };
        // Glow capped by luminance so a finish never makes a beacon.
        auto dim = [](Color v, double limit) {
            const double l = 0.2126 * v.r + 0.7152 * v.g + 0.0722 * v.b;
            if (l > limit && l > 0) v.r *= limit / l, v.g *= limit / l, v.b *= limit / l;
            return v;
        };
        static const std::set<std::string> neutral = {"metalblack", "metalwhite", "metalgray", "metalgrey", "metalbrown", "rawmetal"};
        int accents = 0;
        for (const std::string& name : names) // std::set: name order
        {
            const std::string n = lower(name);
            if (n == "accentcolor" || (n.find("accent") != std::string::npos && n.find("emissive") == std::string::npos))
                out.push_back({name, c.main});
            else if (n == "emissive")
                out.push_back({name, dim(c.glow, 0.6)});
            else if (n != "emissivecolor" && n.size() > 13 && n.ends_with("emissivecolor"))
                out.push_back({name, dim(c.main, 0.5)});
            else if ((n.starts_with("metal") && !neutral.contains(n)) || (n.ends_with("paint") && n != "metalpaint") || n == "leathercolor")
                out.push_back({name, accents++ % 2 == 0 ? c.main : c.trim});
        }
        return out;
    }

    std::optional<Placement> PlaceAccessory(const Fit& fit, const double localMin[3], const double localMax[3], const double anchor[3], const double forward[3])
    {
        double extent[3];
        for (int i = 0; i < 3; ++i)
        {
            extent[i] = localMax[i] - localMin[i];
            if (!std::isfinite(extent[i]) || !std::isfinite(anchor[i]) || !std::isfinite(forward[i])) return std::nullopt;
        }
        if (std::max({extent[0], extent[1], extent[2]}) < 0.01) return std::nullopt;
        // An extent of zero (a flat mesh) scales freely on that axis.
        for (double& e : extent) e = std::max(e, 0.01);
        // The character's frame: forward on the ground, right, up.
        const double flat = std::hypot(forward[0], forward[1]);
        if (flat < 1e-6) return std::nullopt;
        const double axes[3][3] = {{forward[0] / flat, forward[1] / flat, 0}, {-forward[1] / flat, forward[0] / flat, 0}, {0, 0, 1}};
        // Local axis for each character axis: the permutation whose proportions
        // best match the wanted size (identity first, so ties keep the mesh's own orientation).
        static constexpr int perms[6][3] = {{0, 1, 2}, {1, 0, 2}, {0, 2, 1}, {2, 1, 0}, {1, 2, 0}, {2, 0, 1}};
        const double wantMax = std::max({fit.size[0], fit.size[1], fit.size[2]}), haveMax = std::max({extent[0], extent[1], extent[2]});
        int best = 0;
        double bestCost = 1e300;
        for (int p = 0; p < (fit.keepAxes ? 1 : 6); ++p)
        {
            double cost = 0;
            for (int c = 0; c < 3; ++c)
            {
                const double d = fit.size[c] / wantMax - extent[perms[p][c]] / haveMax;
                cost += d * d;
            }
            if (cost < bestCost - 1e-9) bestCost = cost, best = p;
        }
        // World direction of each local axis; an odd permutation flips one axis to stay a rotation.
        double local[3][3]{};
        const bool odd = best == 1 || best == 2 || best == 3;
        for (int c = 0; c < 3; ++c)
            for (int k = 0; k < 3; ++k) local[perms[best][c]][k] = axes[c][k] * (odd && c == 2 ? -1 : 1);
        Placement out;
        for (int c = 0; c < 3; ++c) out.scale[perms[best][c]] = fit.size[c] / std::max(extent[perms[best][c]], 0.01);
        // Rotator from the local X, Y, Z world axes (FMatrix::Rotator).
        constexpr double R = 180.0 / 3.14159265358979323846;
        const double* X = local[0];
        out.rotation[0] = std::atan2(X[2], std::hypot(X[0], X[1])) * R;
        out.rotation[1] = std::atan2(X[1], X[0]) * R;
        out.rotation[2] = 0;
        double rx[3], ry[3], rz[3];
        RotatorAxes(out.rotation, rx, ry, rz);
        const double* Y = local[1];
        const double* Z = local[2];
        out.rotation[2] = std::atan2(Z[0] * ry[0] + Z[1] * ry[1] + Z[2] * ry[2], Y[0] * ry[0] + Y[1] * ry[1] + Y[2] * ry[2]) * R;
        // The bounds' centre lands on the anchor plus the offset.
        double target[3];
        for (int k = 0; k < 3; ++k) target[k] = anchor[k] + axes[0][k] * fit.offset[0] + axes[1][k] * fit.offset[1] + axes[2][k] * fit.offset[2];
        for (int k = 0; k < 3; ++k)
        {
            double centre = 0;
            for (int a = 0; a < 3; ++a) centre += local[a][k] * out.scale[a] * (localMin[a] + localMax[a]) / 2;
            out.location[k] = target[k] - centre;
        }
        return out;
    }

    const Attachment* Item::AttachmentFor(std::string_view model) const
    {
        for (const auto& [m, a] : attach)
            if (m == model) return &a;
        return nullptr;
    }

    std::optional<std::string> Validate(const Item& item)
    {
        if (!IsItemId(item.id)) return "bad id";
        const std::string& id = item.id;
        if (!Finite(item.version, 1, 100000) || item.version != std::floor(item.version)) return id + ": bad version";
        const KindInfo* kind = FindKind(item.kind);
        if (!kind) return id + ": unknown kind";
        if (item.parts.empty()) return id + ": no parts";
        for (const std::string& part : item.parts)
            if (!((part == "body" && kind->body) || (part == "weapon" && kind->weapon) || (part == "arms" && kind->arms)))
                return id + ": part " + part + " not allowed for " + item.kind;
        if (kind->body && !kind->weapon && item.models.empty()) return id + ": avatar items need base models";
        for (const auto& [name, c] : item.vector)
        {
            if (!IsPlainName(name)) return id + ": bad vector";
            if (!Finite(c.r, 0, 1) || !Finite(c.g, 0, 1) || !Finite(c.b, 0, 1) || !Finite(c.a, 0, 1)) return id + ": vector " + name + " out of range";
        }
        for (const auto& [name, v] : item.scalar)
            if (!IsPlainName(name) || !Finite(v, -10, 10)) return id + ": bad scalar " + name;
        if (item.NeedsPak() && !item.pak) return id + ": needs a pak";
        if (!kind->needsPak && item.vector.empty() && item.scalar.empty()) return id + ": no parameters";
        // AimModCore additions: assets only from the item's AimMod pak.
        if (item.pak && !IsPakName(*item.pak)) return id + ": bad pak name";
        for (const auto& [name, path] : item.textures)
            if (!IsPlainName(name) || !IsAimModAsset(path) || !item.pak) return id + ": bad texture " + name;
        for (const std::string* path : {&item.mesh, &item.material})
            if (!path->empty() && !(IsAimModAsset(*path) && item.pak) && !(item.kind == "accessory" && !item.NeedsPak()))
                return id + ": bad asset path";
        for (const std::string& model : item.models)
            if (!IsPlainName(model)) return id + ": bad model name";
        if (item.kind == "accessory" && item.attachRole != "head" && item.attachRole != "neck" && item.attachRole != "spine")
            return id + ": accessories attach to head, neck or spine";
        if (!item.shape.empty() && (item.kind != "accessory" || !mesh::IsMeshName(item.shape) || !item.mesh.empty() || !item.fit))
            return id + ": a runtime mesh is an accessory's own shape, with a fit";
        if (item.fit)
        {
            const Fit& f = *item.fit;
            if (!IsPlainName(f.bone) || (f.anchor != "bone" && f.anchor != "top" && f.anchor != "crown" && f.anchor != "chin")) return id + ": bad fit";
            for (int i = 0; i < 3; ++i)
                if (!Finite(f.offset[i], -60, 60) || !Finite(f.size[i], 1, 60)) return id + ": fit out of range";
        }
        for (const auto& [model, a] : item.attach)
        {
            if (!Contains(item.models, model) || !IsPlainName(a.bone)) return id + ": bad attachment for " + model;
            for (int i = 0; i < 3; ++i)
                if (!Finite(a.location[i], -50, 50) || !Finite(a.rotation[i], -360, 360) || !Finite(a.scale[i], 0.1, 4))
                    return id + ": attachment transform for " + model + " out of range";
        }
        return std::nullopt;
    }

    namespace
    {
        bool StringList(const json::Value& v, std::vector<std::string>& out)
        {
            if (!v.isArray() || v.items.size() > 64) return false;
            for (const json::Value& s : v.items)
            {
                if (!s.isString()) return false;
                out.push_back(s.string);
            }
            return true;
        }
        bool Triple(const json::Value* v, double out[3])
        {
            if (!v) return true; // default
            if (!v->isArray() || v->items.size() != 3) return false;
            for (int i = 0; i < 3; ++i)
            {
                if (!v->items[static_cast<std::size_t>(i)].isNumber()) return false;
                out[i] = v->items[static_cast<std::size_t>(i)].number;
            }
            return true;
        }
        // One catalog entry; nullopt + reason when a field has the wrong shape.
        std::optional<Item> ReadItem(const json::Value& v, std::string& error)
        {
            Item item;
            if (!v.isObject()) return (error = "not a table", std::nullopt);
            if (const auto* id = v.find("id"); id && id->isString()) item.id = id->string;
            const std::string who = item.id.empty() ? std::string("item") : item.id;
            auto bad = [&](const std::string& what) -> std::optional<Item> {
                error = who + ": " + what;
                return std::nullopt;
            };
            static const std::set<std::string_view> known = {"id", "version", "kind", "name", "models", "weapons", "parts", "vector", "scalar",
                                                              "textures", "pak", "mesh", "shape", "material", "attach", "draft"};
            for (const auto& [key, value] : v.members)
            {
                if (!known.contains(key)) return bad("unknown field " + key);
                if (key == "id") { if (!value.isString()) return bad("bad id"); }
                else if (key == "version") { if (!value.isNumber()) return bad("bad version"); item.version = value.number; }
                else if (key == "kind") { if (!value.isString()) return bad("unknown kind"); item.kind = value.string; }
                else if (key == "name") { if (!value.isString() || value.string.size() > 64) return bad("bad name"); item.name = value.string; }
                else if (key == "models") { if (!StringList(value, item.models)) return bad("bad models"); }
                else if (key == "weapons")
                {
                    std::vector<std::string> list;
                    if (!StringList(value, list)) return bad("bad weapons");
                    item.weapons = std::move(list);
                }
                else if (key == "parts") { if (!StringList(value, item.parts)) return bad("no parts"); }
                else if (key == "vector")
                {
                    if (!value.isObject() || value.members.size() > 16) return bad("bad vector");
                    for (const auto& [name, c] : value.members)
                    {
                        if (!c.isObject()) return bad("bad vector");
                        auto component = [&](const char* k) {
                            const json::Value* x = c.find(k);
                            return x && x->isNumber() ? x->number : std::nan("");
                        };
                        item.vector.push_back({name, {component("R"), component("G"), component("B"), component("A")}});
                    }
                }
                else if (key == "scalar")
                {
                    if (!value.isObject() || value.members.size() > 16) return bad("bad scalar");
                    for (const auto& [name, x] : value.members) item.scalar.push_back({name, x.isNumber() ? x.number : std::nan("")});
                }
                else if (key == "textures")
                {
                    if (!value.isObject() || value.members.size() > 8) return bad("bad textures");
                    for (const auto& [name, x] : value.members)
                    {
                        if (!x.isString()) return bad("bad texture " + name);
                        item.textures.push_back({name, x.string});
                    }
                }
                else if (key == "pak")
                {
                    // {"file": "...", "sha256": "..."} (the manifest pins the hash) or a file name.
                    if (value.isString()) item.pak = value.string;
                    else if (value.isObject())
                    {
                        const json::Value* file = value.find("file");
                        if (!file || !file->isString()) return bad("needs a pak");
                        for (const auto& [k, x] : value.members)
                            if ((k != "file" && k != "sha256") || !x.isString()) return bad("bad pak");
                        item.pak = file->string;
                    }
                    else return bad("needs a pak");
                }
                else if (key == "mesh") { if (!value.isString()) return bad("bad asset path"); item.mesh = value.string; }
                else if (key == "shape") { if (!value.isString()) return bad("bad shape"); item.shape = value.string; }
                else if (key == "material") { if (!value.isString()) return bad("bad asset path"); item.material = value.string; }
                else if (key == "attach")
                {
                    if (value.isString()) item.attachRole = value.string;
                    else if (value.isObject())
                    {
                        for (const auto& [k, x] : value.members)
                        {
                            if (k == "role" && x.isString()) item.attachRole = x.string;
                            else if (k == "fit" && x.isObject())
                            {
                                Fit f;
                                const json::Value* bone = x.find("bone");
                                const json::Value* anchor = x.find("anchor");
                                if (!bone || !bone->isString() || (anchor && !anchor->isString())) return bad("bad fit");
                                for (const auto& [fk, fv] : x.members)
                                    if (fk != "bone" && fk != "anchor" && fk != "offset" && fk != "size" && fk != "keepAxes") return bad("bad fit");
                                if (const json::Value* keep = x.find("keepAxes"))
                                {
                                    if (!keep->isBool()) return bad("bad fit");
                                    f.keepAxes = keep->boolean;
                                }
                                f.bone = bone->string;
                                if (anchor) f.anchor = anchor->string;
                                if (!Triple(x.find("offset"), f.offset) || !x.find("size") || !Triple(x.find("size"), f.size)) return bad("bad fit");
                                item.fit = f;
                            }
                            else if (k == "models" && x.isObject() && x.members.size() <= 16)
                                for (const auto& [model, a] : x.members)
                                {
                                    Attachment at;
                                    const json::Value* bone = a.isObject() ? a.find("bone") : nullptr;
                                    if (!bone || !bone->isString()) return bad("bad attachment for " + model);
                                    for (const auto& [ak, av] : a.members)
                                        if (ak != "bone" && ak != "location" && ak != "rotation" && ak != "scale") return bad("bad attachment for " + model);
                                    at.bone = bone->string;
                                    if (!Triple(a.find("location"), at.location) || !Triple(a.find("rotation"), at.rotation) || !Triple(a.find("scale"), at.scale))
                                        return bad("bad attachment for " + model);
                                    item.attach.push_back({model, at});
                                }
                            else return bad("bad attach");
                        }
                    }
                    else return bad("bad attach");
                }
                else if (key == "draft") { if (!value.isBool()) return bad("bad draft"); item.draft = value.boolean; }
            }
            return item;
        }
    } // namespace

    std::optional<Catalog> ParseCatalog(std::string_view text, std::string* error)
    {
        std::string why;
        auto root = json::Parse(text, &why);
        auto fail = [&](std::string reason) -> std::optional<Catalog> {
            if (error) *error = std::move(reason);
            return std::nullopt;
        };
        if (!root) return fail("catalog.json: " + why);
        const json::Value* version = root->isObject() ? root->find("version") : nullptr;
        const json::Value* items = root->isObject() ? root->find("items") : nullptr;
        if (!version || !version->isNumber() || !Finite(version->number, 1, 100000) || version->number != std::floor(version->number))
            return fail("catalog.json: bad version");
        if (!items || !items->isArray() || items->items.size() > 1000) return fail("catalog.json: no items");
        for (const auto& [key, value] : root->members)
            if (key != "version" && key != "items") return fail("catalog.json: unknown field " + key);
        Catalog catalog;
        catalog.version = version->number;
        for (const json::Value& v : items->items)
        {
            std::string reason;
            if (auto item = ReadItem(v, reason)) catalog.items.push_back(std::move(*item));
            else catalog.errors.push_back(reason);
        }
        return catalog;
    }

    Index BuildIndex(const std::vector<Item>& items, std::vector<std::string>& errors)
    {
        Index byId;
        std::set<std::string> duplicated;
        for (const Item& item : items)
        {
            if (auto reason = Validate(item))
            {
                errors.push_back(*reason);
                continue;
            }
            if (byId.contains(item.id) || duplicated.contains(item.id))
            {
                duplicated.insert(item.id);
                byId.erase(item.id);
                errors.push_back(item.id + ": duplicate id");
                continue;
            }
            byId.emplace(item.id, item);
        }
        return byId;
    }

    const Item* Resolve(const Index& index, std::string_view id, const ResolveOptions& options, std::string* reason)
    {
        auto fail = [&](std::string why) -> const Item* {
            if (reason) *reason = std::move(why);
            return nullptr;
        };
        if (id.empty()) return fail("no item");
        auto it = index.find(std::string(id));
        if (it == index.end()) return fail("not in the catalog: " + std::string(id));
        const Item& item = it->second;
        if (item.draft && !options.allowDrafts) return fail(std::string(id) + " is a draft");
        if (!item.shape.empty() && !options.verifiedMeshes.contains(item.shape)) return fail(std::string(id) + " needs its mesh file matching the manifest");
        if (item.NeedsPak() && !(item.pak && options.verifiedPaks.contains(*item.pak)))
            return fail(std::string(id) + " needs an AimMod pak matching the manifest");
        return &item;
    }

    std::vector<const Item*> Pickable(const Index& index)
    {
        std::vector<const Item*> list;
        for (const auto& [id, item] : index)
            if (!item.draft) list.push_back(&item);
        return list;
    }

    // -------------------------------------------------------------- manifest

    std::optional<Manifest> ParseManifest(std::string_view text, std::string* error)
    {
        std::string why;
        auto root = json::Parse(text, &why, 1 << 20);
        auto fail = [&](std::string reason) -> std::optional<Manifest> {
            if (error) *error = "catalog-manifest.json: " + reason;
            return std::nullopt;
        };
        if (!root) return fail(why);
        if (!root->isObject()) return fail("not an object");
        Manifest m;
        // "version" is the catalog version the manifest pins (the service reads it the same way).
        const json::Value* version = root->find("version");
        if (!version || !version->isNumber() || !Finite(version->number, 1, 100000) || version->number != std::floor(version->number))
            return fail("bad version");
        m.catalogVersion = version->number;
        const json::Value* files = root->find("files");
        if (!files || !files->isArray() || files->items.size() > 64) return fail("bad files");
        for (const json::Value& f : files->items)
        {
            const json::Value* name = f.isObject() ? f.find("name") : nullptr;
            const json::Value* size = f.isObject() ? f.find("size") : nullptr;
            const json::Value* sha = f.isObject() ? f.find("sha256") : nullptr;
            if (!name || !name->isString() || !size || !size->isNumber() || !sha || !sha->isString()) return fail("bad file entry");
            if (name->string != "catalog.json" && !IsPakName(name->string) && !mesh::IsMeshName(name->string)) return fail("bad file name " + name->string);
            if (!Finite(size->number, 0, 1099511627776.0) || size->number != std::floor(size->number)) return fail("bad size for " + name->string);
            std::string hash = sha->string;
            std::transform(hash.begin(), hash.end(), hash.begin(), [](char c) { return c >= 'A' && c <= 'F' ? static_cast<char>(c - 'A' + 'a') : c; });
            if (hash.size() != 64 || !IsLowerHex(hash)) return fail("bad sha256 for " + name->string);
            for (const ManifestFile& seen : m.files)
                if (seen.name == name->string) return fail("duplicate file " + name->string);
            m.files.push_back({name->string, static_cast<std::uint64_t>(size->number), hash});
        }
        if (std::count_if(m.files.begin(), m.files.end(), [](const ManifestFile& f) { return f.name == "catalog.json"; }) != 1)
            return fail("catalog.json must be listed once");
        // Optional item list (id + version) for a consistency check.
        if (const json::Value* items = root->find("items"))
        {
            if (!items->isArray() || items->items.size() > 1000) return fail("bad items");
            for (const json::Value& i : items->items)
            {
                const json::Value* id = i.isObject() ? i.find("id") : nullptr;
                const json::Value* v = i.isObject() ? i.find("version") : nullptr;
                if (!id || !id->isString() || !IsItemId(id->string) || !v || !v->isNumber() || !Finite(v->number, 1, 100000) || v->number != std::floor(v->number))
                    return fail("bad item entry");
                for (const auto& seen : m.items)
                    if (seen.first == id->string) return fail("duplicate item " + id->string);
                m.items.push_back({id->string, v->number});
            }
            m.listsItems = true;
        }
        return m;
    }

    std::string Sha256Hex(std::string_view data)
    {
        Sha256 h;
        if (!h.Add(data.data(), data.size())) return {};
        return h.Finish().value_or("");
    }

    std::optional<std::string> Sha256File(const std::filesystem::path& path)
    {
        std::ifstream in(path, std::ios::binary);
        if (!in) return std::nullopt;
        Sha256 h;
        std::vector<char> buffer(1 << 20);
        while (in)
        {
            in.read(buffer.data(), static_cast<std::streamsize>(buffer.size()));
            const auto got = in.gcount();
            if (got > 0 && !h.Add(buffer.data(), static_cast<std::size_t>(got))) return std::nullopt;
        }
        if (in.bad()) return std::nullopt;
        return h.Finish();
    }

    ManifestCheck VerifyManifest(const Manifest& manifest, const std::filesystem::path& catalogDir, const std::filesystem::path& paksDir)
    {
        ManifestCheck check;
        std::set<std::string> listed;
        for (const ManifestFile& f : manifest.files)
        {
            if (mesh::IsMeshName(f.name)) continue; // runtime meshes: read, hashed and parsed together by the loader
            const bool catalog = f.name == "catalog.json";
            if (!catalog) listed.insert(f.name);
            const auto path = (catalog ? catalogDir : paksDir) / f.name; // ASCII names only (IsPakName)
            std::error_code error;
            if (!std::filesystem::is_regular_file(path, error))
            {
                check.problems.push_back("missing " + f.name);
                continue;
            }
            const auto size = std::filesystem::file_size(path, error);
            if (error || size != f.size)
            {
                check.problems.push_back("wrong size " + f.name + " (" + std::to_string(error ? 0 : size) + " bytes, manifest " + std::to_string(f.size) + ")");
                continue;
            }
            auto hash = Sha256File(path);
            if (!hash)
            {
                check.problems.push_back("unreadable " + f.name);
                continue;
            }
            if (*hash != f.sha256)
            {
                check.problems.push_back("wrong hash " + f.name);
                continue;
            }
            if (catalog) check.catalogVerified = true;
            else check.verifiedPaks.insert(f.name);
        }
        std::error_code error;
        if (std::filesystem::is_directory(paksDir, error))
        {
            std::size_t seen = 0;
            for (auto it = std::filesystem::recursive_directory_iterator(paksDir, error); !error && it != std::filesystem::recursive_directory_iterator();
                 it.increment(error))
            {
                if (++seen > 4096) break;
                if (!it->is_regular_file(error)) continue;
                std::wstring ext = it->path().extension().wstring();
                std::transform(ext.begin(), ext.end(), ext.begin(), [](wchar_t c) { return c >= L'A' && c <= L'Z' ? static_cast<wchar_t>(c - L'A' + L'a') : c; });
                if (ext != L".pak") continue;
                std::string relative;
                try
                {
                    relative = std::filesystem::relative(it->path(), paksDir, error).generic_string();
                }
                catch (const std::exception&)
                {
                    relative = "(non-ASCII name)"; // never a listed (ASCII) pak
                }
                if (!listed.contains(relative)) check.problems.push_back("extra pak ignored: " + relative);
            }
        }
        return check;
    }

    void ApplyManifestItems(const Manifest& manifest, Catalog& catalog, std::vector<std::string>& problems)
    {
        if (manifest.catalogVersion != catalog.version)
        {
            problems.push_back("catalog version " + std::to_string(static_cast<long long>(catalog.version)) + " is not the manifest's " +
                               std::to_string(static_cast<long long>(manifest.catalogVersion)));
            catalog.items.clear();
            return;
        }
        if (!manifest.listsItems) return;
        std::vector<Item> kept;
        for (Item& item : catalog.items)
        {
            auto it = std::find_if(manifest.items.begin(), manifest.items.end(), [&](const auto& e) { return e.first == item.id; });
            if (it == manifest.items.end()) problems.push_back(item.id + ": not in the manifest");
            else if (it->second != item.version) problems.push_back(item.id + ": version differs from the manifest");
            else kept.push_back(std::move(item));
        }
        for (const auto& [id, version] : manifest.items)
            if (std::none_of(kept.begin(), kept.end(), [&](const Item& i) { return i.id == id; }) &&
                std::none_of(problems.begin(), problems.end(), [&](const std::string& p) { return p.rfind(id + ":", 0) == 0; }))
                problems.push_back(id + ": listed in the manifest but not in the catalog");
        catalog.items = std::move(kept);
    }

    // ----------------------------------------------------------------- looks

    // The service writes Steam ids as 1-20 ASCII digits; a 64-bit value.
    bool IsSteamId64(std::string_view text)
    {
        if (text.empty() || text.size() > 20) return false;
        for (char c : text)
            if (!IsDigit(c)) return false;
        return text.size() < 20 || text <= "18446744073709551615";
    }

    std::optional<std::string> PeerFromTag(std::string_view tag)
    {
        constexpr std::string_view prefix = "AimMod.Peer.";
        if (tag.substr(0, prefix.size()) != prefix) return std::nullopt;
        const std::string_view id = tag.substr(prefix.size());
        if (!IsSteamId64(id)) return std::nullopt;
        return std::string(id);
    }

    const PeerLook* Looks::Find(std::string_view steamId) const
    {
        for (const PeerLook& p : peers)
            if (p.steamId == steamId) return &p;
        return nullptr;
    }

    std::optional<Looks> ParseLooks(std::string_view text, std::string* error)
    {
        auto fail = [&](std::string why) -> std::optional<Looks> {
            if (error) *error = "cosmetic-looks.txt: " + why;
            return std::nullopt;
        };
        if (text.empty() || text.size() > 65536) return fail("missing or too large");
        auto list = [](std::string_view s, std::vector<ItemRef>& out) {
            if (s.empty()) return true;
            std::size_t at = 0;
            while (at <= s.size())
            {
                const auto comma = s.find(',', at);
                const std::string_view ref = s.substr(at, comma == std::string_view::npos ? std::string_view::npos : comma - at);
                const auto sign = ref.find('@');
                if (sign == std::string_view::npos) return false;
                const std::string_view id = ref.substr(0, sign);
                auto version = RefVersion(ref.substr(sign + 1));
                if (!IsItemId(id) || !version || out.size() >= 8) return false;
                for (const ItemRef& seen : out)
                    if (seen.id == id) return false;
                out.push_back({std::string(id), *version});
                if (comma == std::string_view::npos) break;
                at = comma + 1;
            }
            return true;
        };
        Looks looks;
        bool header = false, self = false;
        std::size_t at = 0, number = 0;
        while (at < text.size())
        {
            const auto end = text.find('\n', at);
            std::string_view line = text.substr(at, end == std::string_view::npos ? std::string_view::npos : end - at);
            at = end == std::string_view::npos ? text.size() : end + 1;
            if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
            ++number;
            if (line.empty()) continue;
            const std::string where = "line " + std::to_string(number);
            if (!header)
            {
                if (line != "v=1") return fail("unsupported version");
                header = true;
                continue;
            }
            if (line.substr(0, 5) == "self=")
            {
                if (self) return fail(where + ": second self line");
                if (!list(line.substr(5), looks.self)) return fail(where + ": bad items");
                self = true;
            }
            else if (line.substr(0, 5) == "peer=")
            {
                const auto space = line.find(' ');
                if (space == std::string_view::npos || line.substr(space, 7) != " items=") return fail(where + ": bad peer line");
                const std::string_view id = line.substr(5, space - 5);
                if (!IsSteamId64(id)) return fail(where + ": bad SteamID64");
                if (looks.Find(id)) return fail(where + ": duplicate peer");
                if (looks.peers.size() >= 64) return fail("too many peers");
                PeerLook peer{std::string(id), {}};
                if (!list(line.substr(space + 7), peer.items)) return fail(where + ": bad items");
                looks.peers.push_back(std::move(peer));
            }
            else return fail(where + ": unknown line");
        }
        if (!header) return fail("unsupported version");
        return looks;
    }

    namespace
    {
        const Item* ResolveRef(const Index& index, const ItemRef& ref, const ResolveOptions& options, Plan& plan)
        {
            std::string reason;
            const Item* item = Resolve(index, ref.id, options, &reason);
            if (!item)
            {
                plan.skipped.push_back(reason);
                return nullptr;
            }
            // Shared items resolve only to the same id and version (as the service filters them).
            if (item->version != ref.version)
            {
                plan.skipped.push_back(ref.id + (item->version < ref.version ? ": newer than the installed catalog (update AimMod to see this)"
                                                                             : ": older version than the installed catalog"));
                return nullptr;
            }
            return item;
        }
        void Take(const Item*& slot, const Item* item, Plan& plan, const char* what)
        {
            if (slot) plan.skipped.push_back(item->id + ": one " + what + " only");
            else slot = item;
        }
    } // namespace

    Plan PlanAvatar(const Index& index, const std::vector<ItemRef>& refs, const ResolveOptions& options, std::string_view model)
    {
        Plan plan;
        for (const ItemRef& ref : refs)
        {
            const Item* item = ResolveRef(index, ref, options, plan);
            if (!item) continue;
            if (!item->HasPart("body")) continue; // weapon items are for the wearer's own view
            if (!Contains(item->models, model))
            {
                plan.skipped.push_back(item->id + ": does not fit " + std::string(model));
                continue;
            }
            if (item->kind == "avatar_tint" || (item->kind == "avatar_pattern" && !item->textures.empty()))
                Take(plan.body, item, plan, "body item");
            else if (item->kind == "accessory")
            {
                if ((item->mesh.empty() && item->shape.empty()) || (!item->fit && !item->AttachmentFor(model)))
                {
                    plan.skipped.push_back(item->id + ": no mesh or attachment for " + std::string(model));
                    continue;
                }
                if (item->attachRole == "head") Take(plan.head, item, plan, "head item");
                else if (item->attachRole == "neck") Take(plan.neck, item, plan, "neck item");
                else Take(plan.spine, item, plan, "spine item");
            }
            else plan.skipped.push_back(item->id + ": " + item->kind + " is not supported yet");
        }
        return plan;
    }

    Plan PlanLocal(const Index& index, const std::vector<ItemRef>& refs, const ResolveOptions& options, std::string_view weaponModel)
    {
        Plan plan;
        for (const ItemRef& ref : refs)
        {
            const Item* item = ResolveRef(index, ref, options, plan);
            if (!item) continue;
            if (item->kind != "weapon_finish" && !(item->kind == "weapon_pattern" && !item->textures.empty()))
            {
                if (!item->HasPart("body")) plan.skipped.push_back(item->id + ": " + item->kind + " is not supported yet");
                continue;
            }
            if (item->weapons && !Contains(*item->weapons, weaponModel))
            {
                plan.skipped.push_back(item->id + ": does not fit " + std::string(weaponModel));
                continue;
            }
            if (item->HasPart("weapon")) Take(plan.weapon, item, plan, "weapon finish");
            if (item->HasPart("arms")) Take(plan.arms, item, plan, "arms finish");
        }
        return plan;
    }
} // namespace aimmod::cosmetics
