// Replay format 2 measurement tool (development only; not shipped).
//   aimmod_replay_tool convert <format1.amreplay> <out.amreplay>
// Converts a format-1 JSON-lines replay into format 2 and reports size,
// orientation reconstruction error and hit reproduction.
#define _USE_MATH_DEFINES
#include <aimmod/ReplayV2.hpp>

#include <array>
#include <cstring>
#include <optional>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <fstream>
#include <map>
#include <memory>
#include <sstream>
#include <string>
#include <variant>
#include <vector>

using namespace aimmod;
using namespace aimmod::replay2;

namespace
{
    // Minimal JSON reader for the known format-1 records.
    struct Json;
    using JsonPtr = std::shared_ptr<Json>;
    struct Json
    {
        std::variant<std::nullptr_t, bool, double, std::string, std::vector<JsonPtr>, std::map<std::string, JsonPtr>> v;
        const Json* operator[](const std::string& k) const
        {
            auto* m = std::get_if<std::map<std::string, JsonPtr>>(&v);
            if (!m) return nullptr;
            auto it = m->find(k);
            return it == m->end() ? nullptr : it->second.get();
        }
        double num() const { return std::get<double>(v); }
        const std::string& str() const { return std::get<std::string>(v); }
        const std::vector<JsonPtr>& arr() const { return std::get<std::vector<JsonPtr>>(v); }
    };
    struct Parser
    {
        const char* p;
        void ws()
        {
            while (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n') ++p;
        }
        JsonPtr parse()
        {
            ws();
            auto j = std::make_shared<Json>();
            if (*p == '{')
            {
                ++p;
                std::map<std::string, JsonPtr> m;
                ws();
                if (*p == '}') { ++p; j->v = m; return j; }
                for (;;)
                {
                    ws();
                    auto key = parse();
                    ws();
                    ++p; // ':'
                    m[key->str()] = parse();
                    ws();
                    if (*p == ',') { ++p; continue; }
                    ++p; // '}'
                    break;
                }
                j->v = std::move(m);
            }
            else if (*p == '[')
            {
                ++p;
                std::vector<JsonPtr> a;
                ws();
                if (*p == ']') { ++p; j->v = a; return j; }
                for (;;)
                {
                    a.push_back(parse());
                    ws();
                    if (*p == ',') { ++p; continue; }
                    ++p;
                    break;
                }
                j->v = std::move(a);
            }
            else if (*p == '"')
            {
                ++p;
                std::string s;
                while (*p != '"')
                {
                    if (*p == '\\')
                    {
                        ++p;
                        if (*p == 'u') { s += '?'; p += 5; continue; }
                        s += *p == 'n' ? '\n' : *p == 't' ? '\t' : *p;
                        ++p;
                        continue;
                    }
                    s += *p++;
                }
                ++p;
                j->v = s;
            }
            else if (!strncmp(p, "true", 4)) { p += 4; j->v = true; }
            else if (!strncmp(p, "false", 5)) { p += 5; j->v = false; }
            else if (!strncmp(p, "null", 4)) { p += 4; j->v = nullptr; }
            else
            {
                char* end;
                j->v = std::strtod(p, &end);
                p = end;
            }
            return j;
        }
    };

    struct V1Frame
    {
        double t;
        double cam[7];
        std::vector<std::array<double, 6>> actors;
        std::optional<double> hits;
    };

    // Ray/capsule test: does the view ray from `cam` pass through the capsule?
    bool RayHits(const double cam[7], const double pitch, const double yaw, const std::array<double, 6>& a)
    {
        const double cp = std::cos(pitch * M_PI / 180), sp = std::sin(pitch * M_PI / 180);
        const double cy = std::cos(yaw * M_PI / 180), sy = std::sin(yaw * M_PI / 180);
        const double d[3] = {cp * cy, cp * sy, sp};
        // Closest approach between the ray and the capsule's vertical segment.
        const double half = a[5] - a[4];
        const double o[3] = {cam[0] - a[1], cam[1] - a[2], cam[2] - a[3]};
        double best = 1e30;
        for (int i = 0; i <= 20; ++i)
        {
            const double z = -half + 2 * half * i / 20.0;
            const double w[3] = {o[0], o[1], o[2] - z};
            const double t = std::max(0.0, -(w[0] * d[0] + w[1] * d[1] + w[2] * d[2]));
            const double q[3] = {w[0] + t * d[0], w[1] + t * d[1], w[2] + t * d[2]};
            best = std::min(best, std::sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2]));
        }
        return best <= a[4];
    }
} // namespace

int main(int argc, char** argv)
{
    if (argc != 4 || std::string(argv[1]) != "convert")
    {
        std::printf("usage: aimmod_replay_tool convert <format1.amreplay> <out.amreplay>\n");
        return 2;
    }
    std::ifstream in(argv[2], std::ios::binary);
    std::string line;
    Capture c;
    std::vector<std::pair<double, InputEvent>> inputs;
    std::vector<V1Frame> frames;
    std::vector<ActorSample> actorRows;
    std::vector<std::pair<double, StatsSample>> stats;
    std::vector<double> times;
    std::size_t inputBytes = 0, frameBytes = 0, v1Bytes = 0;
    while (std::getline(in, line))
    {
        v1Bytes += line.size() + 1;
        Parser parser{line.c_str()};
        JsonPtr row = parser.parse();
        const std::string kind = (*row)["kind"]->str();
        if (kind == "header")
        {
            c.header.id = (*row)["id"]->str();
            c.header.scenario = (*row)["scenario"]->str();
            if ((*row)["mapName"]) c.header.mapName = (*row)["mapName"]->str();
            if ((*row)["mapScale"]) c.header.mapScale = (*row)["mapScale"]->num();
            c.header.startEvent = "native";
        }
        else if (kind == "input")
        {
            inputBytes += line.size() + 1;
            const double t = (*row)["t"]->num();
            InputEvent e{0, static_cast<std::uint8_t>(ActionIndex((*row)["action"]->str())), static_cast<float>((*row)["value"]->num())};
            inputs.push_back({t, e});
            times.push_back(t);
        }
        else if (kind == "frame")
        {
            frameBytes += line.size() + 1;
            V1Frame f{(*row)["t"]->num()};
            const auto& cam = (*row)["camera"]->arr();
            for (int i = 0; i < 7; ++i) f.cam[i] = cam[static_cast<std::size_t>(i)]->num();
            for (const auto& a : (*row)["actors"]->arr())
            {
                std::array<double, 6> v;
                for (int i = 0; i < 6; ++i) v[static_cast<std::size_t>(i)] = a->arr()[static_cast<std::size_t>(i)]->num();
                f.actors.push_back(v);
            }
            StatsSample s;
            if (const Json* st = (*row)["stats"])
            {
                auto field = [&](const char* k) -> std::optional<double> { const Json* x = (*st)[k]; return x ? std::optional<double>(x->num()) : std::nullopt; };
                s.score = field("score"); s.shots = field("shots"); s.hits = field("hits"); s.kills = field("kills"); s.damage = field("damage"); s.seconds = field("seconds");
                f.hits = s.hits;
            }
            std::map<double, double> health;
            if (const Json* h = (*row)["health"])
                for (const auto& x : h->arr()) health[(*x)["id"]->num()] = (*x)["percent"]->num();
            std::map<double, std::array<double, 3>> rotation;
            if (const Json* ap = (*row)["appearance"])
                for (const auto& x : ap->arr())
                {
                    const auto& r = (*x)["rotation"]->arr();
                    rotation[(*x)["id"]->num()] = {r[0]->num(), r[1]->num(), r[2]->num()};
                    c.profiles[static_cast<std::uint32_t>((*x)["id"]->num())] = (*x)["profile"]->str();
                }
            for (const auto& a : f.actors)
            {
                ActorSample s2{0, static_cast<std::uint32_t>(a[0]), a[1], a[2], a[3], a[4], a[5]};
                if (health.contains(a[0])) s2.health = health[a[0]];
                if (rotation.contains(a[0]))
                {
                    s2.hasRotation = true;
                    s2.pitch = rotation[a[0]][0]; s2.yaw = rotation[a[0]][1]; s2.roll = rotation[a[0]][2];
                }
                actorRows.push_back(s2);
            }
            stats.push_back({f.t, s});
            times.push_back(f.t);
            frames.push_back(std::move(f));
        }
        else if (kind == "end")
        {
            c.reason = (*row)["reason"]->str();
            if ((*row)["score"]) c.score = (*row)["score"]->num();
        }
    }
    std::sort(times.begin(), times.end());
    times.erase(std::unique(times.begin(), times.end()), times.end());
    c.frameTimes = times;
    auto frameOf = [&](double t) { return static_cast<std::uint32_t>(std::lower_bound(times.begin(), times.end(), t) - times.begin()); };
    for (auto& [t, e] : inputs)
    {
        e.frame = frameOf(t);
        c.inputs.push_back(e);
    }
    std::size_t row = 0;
    for (const V1Frame& f : frames)
    {
        const std::uint32_t fr = frameOf(f.t);
        c.camera.push_back({fr, f.cam[0], f.cam[1], f.cam[2], f.cam[3], f.cam[4], f.cam[5], f.cam[6]});
        for (std::size_t i = 0; i < f.actors.size(); ++i) { actorRows[row].frame = fr; c.actors.push_back(actorRows[row++]); }
    }
    for (auto& [t, s] : stats) { s.frame = frameOf(t); c.stats.push_back(s); }

    EncodeOptions options;
    for (std::uint32_t algorithm : {0u, 4u, 5u})
    {
        options.compression = algorithm;
        EncodeReport r;
        auto bytes = Encode(c, options, &r);
        std::printf("compression=%u: %zu bytes (%.1f KB/min), body %zu bytes\n", algorithm, bytes.size(), bytes.size() / 1024.0 / (r.duration / 60.0), r.bodyBytes);
    }
    options.compression = 5;
    EncodeReport r;
    auto bytes = Encode(c, options, &r);
    std::ofstream(argv[3], std::ios::binary).write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    const double minutes = r.duration / 60.0;
    std::printf("format 1: %zu bytes (%.1f KB/min; frames %zu B, inputs %zu B)\n", v1Bytes, v1Bytes / 1024.0 / minutes, frameBytes, inputBytes);
    std::printf("format 2: %zu bytes (%.1f KB/min), %.1fx smaller\n", bytes.size(), bytes.size() / 1024.0 / minutes, double(v1Bytes) / double(bytes.size()));
    std::printf("engine frames %u, inputs %u, camera samples %u, keyframes %u (%.2f/s), target samples %u -> points %u\n", r.engineFrames, r.inputEvents,
                r.cameraSamples, r.keyframes, r.keyframes / r.duration, r.actorSamples, r.actorPoints);
    std::printf("quantum %.9g, yaw/unit %.9g, pitch/unit %.9g\n", r.quantum, r.yawPerUnit, r.pitchPerUnit);
    std::printf("drift at keyframes before snap: max %.4f deg, rms %.4f deg; encoder max sample error %.4f deg\n", r.keyframeErrorMax, r.keyframeErrorRms, r.sampleErrorMax);

    auto decoded = Decode(bytes.data(), bytes.size());
    if (!decoded) { std::printf("DECODE FAILED\n"); return 1; }
    // Reconstruction error at every recorded camera sample, after decoding.
    double maxErr = 0, sq = 0;
    for (const CameraSample& s : c.camera)
    {
        double rot[3];
        decoded->Rotation(s.frame, rot);
        double dy = std::fmod(rot[1] - s.yaw + 540.0, 360.0) - 180.0;
        const double e = std::max(std::fabs(dy), std::fabs(rot[0] - s.pitch));
        maxErr = std::max(maxErr, e);
        sq += e * e;
    }
    std::printf("decoded orientation vs recorded at %zu samples: max %.4f deg, rms %.5f deg\n", c.camera.size(), maxErr, std::sqrt(sq / c.camera.size()));
    // Hit reproduction: at samples where the game counted a hit, does the
    // view ray intersect the target (recorded vs reconstructed orientation)?
    std::size_t hitSamples = 0, recordedOnTarget = 0, reconstructedOnTarget = 0, agree = 0;
    for (std::size_t i = 1; i < frames.size(); ++i)
    {
        const V1Frame& f = frames[i];
        if (!f.hits || !frames[i - 1].hits || *f.hits <= *frames[i - 1].hits || f.actors.empty()) continue;
        ++hitSamples;
        double rot[3];
        decoded->Rotation(c.camera[i].frame, rot);
        bool a = false, b = false;
        for (const auto& actor : f.actors)
        {
            a |= RayHits(f.cam, f.cam[3], f.cam[4], actor);
            b |= RayHits(f.cam, rot[0], rot[1], actor);
        }
        recordedOnTarget += a;
        reconstructedOnTarget += b;
        agree += a == b;
    }
    std::printf("hit samples %zu: view ray on target recorded %zu, reconstructed %zu, agreement %.2f%%\n", hitSamples, recordedOnTarget, reconstructedOnTarget,
                hitSamples ? 100.0 * agree / hitSamples : 100.0);
    return 0;
}
