// Tests for the Steam-independent parts of AimModSteam: JSON, base64, ids,
// lobby keys, join strings, launch command lines and the AMP1 wire format.
// Synthetic ids only.
#include "AvatarImage.hpp"
#include "AvatarPath.hpp"
#include "AvatarState.hpp"
#include "Codec.hpp"
#include "GhostMath.hpp"
#include "Json.hpp"
#include "PoseFile.hpp"

#include <cstdio>
#include <cstdlib>
#include <optional>
#include <vector>
#include <cmath>
#include <limits>
#include <string>

namespace
{
    int failures = 0;
    int checks = 0;
    void Check(bool ok, const char* what)
    {
        ++checks;
        if (!ok)
        {
            ++failures;
            std::printf("FAIL: %s\n", what);
        }
    }

    // Synthetic ids with the right universe/type bits.
    constexpr std::uint64_t Person = (1ull << 56) | (1ull << 52) | (1ull << 32) | 1234567ull;
    constexpr std::uint64_t Lobby = (1ull << 56) | (8ull << 52) | (0x40000ull << 32) | 7654321ull;

    // Independent decoder for the PNG the bridge writes: zlib with stored or
    // fixed-Huffman deflate blocks, then the five scanline filters.
    struct BitReader
    {
        const std::vector<std::uint8_t>& in;
        std::size_t pos;
        int bit = 0;
        bool bad = false;
        int Bit()
        {
            if (pos >= in.size()) return bad = true, 0;
            const int b = (in[pos] >> bit) & 1;
            if (++bit == 8) bit = 0, ++pos;
            return b;
        }
        std::uint32_t Get(int n)
        {
            std::uint32_t v = 0;
            for (int i = 0; i < n; ++i) v |= static_cast<std::uint32_t>(Bit()) << i;
            return v;
        }
        std::uint32_t Rev(int n) // Huffman code bits, MSB first
        {
            std::uint32_t v = 0;
            for (int i = 0; i < n; ++i) v = (v << 1) | static_cast<std::uint32_t>(Bit());
            return v;
        }
    };
    std::optional<std::vector<std::uint8_t>> Inflate(const std::vector<std::uint8_t>& z)
    {
        static const int lb[] = {3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258};
        static const int le[] = {0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0};
        static const int db[] = {1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577};
        static const int de[] = {0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13};
        if (z.size() < 6 || ((z[0] << 8) | z[1]) % 31 != 0 || (z[0] & 0x0F) != 8) return std::nullopt;
        BitReader r{z, 2};
        std::vector<std::uint8_t> out;
        for (bool last = false; !last;)
        {
            last = r.Get(1) != 0;
            const auto type = r.Get(2);
            if (type == 0)
            {
                if (r.bit) r.bit = 0, ++r.pos;
                if (r.pos + 4 > z.size()) return std::nullopt;
                const std::size_t len = z[r.pos] | (z[r.pos + 1] << 8);
                r.pos += 4;
                if (r.pos + len > z.size()) return std::nullopt;
                out.insert(out.end(), z.begin() + static_cast<long long>(r.pos), z.begin() + static_cast<long long>(r.pos + len));
                r.pos += len;
                continue;
            }
            if (type != 1) return std::nullopt;
            for (;;)
            {
                int sym;
                std::uint32_t c = r.Rev(7);
                if (c <= 23) sym = 256 + static_cast<int>(c);
                else
                {
                    c = (c << 1) | static_cast<std::uint32_t>(r.Bit());
                    if (c >= 0x30 && c <= 0xBF) sym = static_cast<int>(c - 0x30);
                    else if (c >= 0xC0 && c <= 0xC7) sym = 280 + static_cast<int>(c - 0xC0);
                    else sym = 144 + static_cast<int>(((c << 1) | static_cast<std::uint32_t>(r.Bit())) - 0x190);
                }
                if (r.bad || sym > 285) return std::nullopt;
                if (sym < 256) out.push_back(static_cast<std::uint8_t>(sym));
                else if (sym == 256) break;
                else
                {
                    const int li = sym - 257, len = lb[li] + static_cast<int>(r.Get(le[li]));
                    const int di = static_cast<int>(r.Rev(5));
                    if (di > 29) return std::nullopt;
                    const std::size_t dist = static_cast<std::size_t>(db[di] + static_cast<int>(r.Get(de[di])));
                    if (dist > out.size()) return std::nullopt;
                    for (int k = 0; k < len; ++k) out.push_back(out[out.size() - dist]);
                }
            }
        }
        if (r.bit) ++r.pos;
        if (r.bad || r.pos + 4 != z.size()) return std::nullopt;
        const std::uint32_t adler = (std::uint32_t(z[r.pos]) << 24) | (z[r.pos + 1] << 16) | (z[r.pos + 2] << 8) | z[r.pos + 3];
        if (adler != bridge::avatar::Adler32(out.data(), out.size())) return std::nullopt;
        return out;
    }
    std::uint32_t Be(const std::vector<std::uint8_t>& b, std::size_t i) { return (std::uint32_t(b[i]) << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]; }
    // Returns the RGBA pixels, or nullopt for anything malformed (signature, CRCs, sizes).
    std::optional<std::vector<std::uint8_t>> DecodePng(const std::vector<std::uint8_t>& png, std::uint32_t& w, std::uint32_t& h)
    {
        static const std::uint8_t sig[] = {0x89, 'P', 'N', 'G', '\r', '\n', 0x1A, '\n'};
        if (png.size() < 8 || !std::equal(sig, sig + 8, png.begin())) return std::nullopt;
        std::vector<std::uint8_t> idat;
        bool ended = false;
        for (std::size_t i = 8; i + 12 <= png.size();)
        {
            const std::uint32_t len = Be(png, i);
            if (i + 12 + len > png.size()) return std::nullopt;
            const std::string type(png.begin() + static_cast<long long>(i + 4), png.begin() + static_cast<long long>(i + 8));
            if (Be(png, i + 8 + len) != bridge::avatar::Crc32(png.data() + i + 4, len + 4)) return std::nullopt;
            if (type == "IHDR")
            {
                if (len != 13 || png[i + 16] != 8 || png[i + 17] != 6 || png[i + 20] != 0) return std::nullopt;
                w = Be(png, i + 8);
                h = Be(png, i + 12);
            }
            else if (type == "IDAT") idat.insert(idat.end(), png.begin() + static_cast<long long>(i + 8), png.begin() + static_cast<long long>(i + 8 + len));
            else if (type == "IEND") ended = true;
            i += 12 + len;
        }
        if (!ended || w == 0 || h == 0) return std::nullopt;
        const auto raw = Inflate(idat);
        const std::size_t stride = static_cast<std::size_t>(w) * 4;
        if (!raw || raw->size() != (stride + 1) * h) return std::nullopt;
        std::vector<std::uint8_t> px(stride * h);
        for (std::uint32_t y = 0; y < h; ++y)
        {
            const std::uint8_t f = (*raw)[y * (stride + 1)];
            for (std::size_t x = 0; x < stride; ++x)
            {
                const int a = x >= 4 ? px[y * stride + x - 4] : 0, b = y ? px[(y - 1) * stride + x] : 0, c = y && x >= 4 ? px[(y - 1) * stride + x - 4] : 0;
                int p = 0;
                if (f == 1) p = a;
                else if (f == 2) p = b;
                else if (f == 3) p = (a + b) / 2;
                else if (f == 4)
                {
                    const int q = a + b - c, pa = std::abs(q - a), pb = std::abs(q - b), pc = std::abs(q - c);
                    p = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                }
                else if (f != 0) return std::nullopt;
                px[y * stride + x] = static_cast<std::uint8_t>((*raw)[y * (stride + 1) + 1 + x] + p);
            }
        }
        return px;
    }
} // namespace

int main()
{
    using namespace bridge;

    // JSON
    {
        auto v = json::Parse(R"({"v":1,"cmd":"p2p.send","id":7,"peer":"123","reliable":false,"data":"QUJD"})");
        Check(v && v->IsObject(), "parses an object");
        Check(v && v->Int("v") == 1 && v->Int("id") == 7, "reads integers");
        Check(v && v->Str("cmd", 32) == "p2p.send", "reads strings");
        Check(v && v->Bool("reliable") == false, "reads booleans");
        Check(v && !v->Str("cmd", 3), "enforces string length");
        Check(!json::Parse(R"({"a":1,"a":2})"), "rejects duplicate keys");
        Check(!json::Parse(R"({"a":1} x)"), "rejects trailing data");
        Check(!json::Parse(R"({"a":01})"), "rejects leading zeros");
        Check(!json::Parse("[[[[[[[[[[1]]]]]]]]]]", json::Limits{1024, 4, 64, 8}), "enforces depth");
        Check(!json::Parse(std::string(70 * 1024, ' ')), "enforces size");
        Check(!json::Parse(R"({"a":"\ud800"})"), "rejects lone surrogates");
        auto u = json::Parse(R"({"s":"caf\u00e9 \ud83d\ude00 \"q\""})");
        Check(u && u->Str("s", 64) == "caf\xc3\xa9 \xf0\x9f\x98\x80 \"q\"", "decodes escapes and surrogate pairs");
        auto big = json::Parse(R"({"n":9007199254740993})");
        Check(big && big->Int("n") == 9007199254740993ll, "keeps 64-bit integers exact");
        Check(json::Parse(R"({"n":1.5})")->Int("n") == std::nullopt, "Int rejects fractions");
        const auto out = json::Object().Str("a", "x\"y\n").Int("b", -3).Bool("c", true).Null("d").Done();
        Check(out == R"({"a":"x\"y\n","b":-3,"c":true,"d":null})", "builds escaped objects");
        Check(json::Parse(out).has_value(), "round-trips builder output");
    }

    // Base64
    {
        const std::string text = "AimMod!";
        const auto enc = Base64Encode(reinterpret_cast<const std::uint8_t*>(text.data()), text.size());
        Check(enc == "QWltTW9kIQ==", "base64 encodes");
        const auto dec = Base64Decode(enc, 64);
        Check(dec && std::string(dec->begin(), dec->end()) == text, "base64 round-trips");
        Check(!Base64Decode("QWltTW9kIQ=", 64), "base64 rejects bad length");
        Check(!Base64Decode("QW*t", 64), "base64 rejects bad characters");
        Check(!Base64Decode("QUJDRA==", 3), "base64 enforces the size limit");
        Check(Base64Decode("", 8).has_value() && Base64Decode("", 8)->empty(), "base64 accepts empty");
    }

    // Ids and keys
    {
        Check(ParseId(std::to_string(Person)) == Person, "parses ids");
        Check(!ParseId("12a") && !ParseId("") && !ParseId("99999999999999999999") && !ParseId("0"), "rejects bad ids");
        Check(IsIndividualId(Person) && !IsIndividualId(Lobby), "recognises individual accounts");
        Check(IsLobbyId(Lobby) && !IsLobbyId(Person), "recognises lobbies");
        const auto digits = std::to_string(Person);
        Check(Redact(Person) == "..." + digits.substr(digits.size() - 4), "redacts to the last four digits");
        Check(ValidLobbyKey("aimmod.mode") && ValidLobbyKey("aimmod.scenario_hash"), "accepts aimmod keys");
        Check(!ValidLobbyKey("OWNINGID") && !ValidLobbyKey("aimmod.") && !ValidLobbyKey("aimmod.Mode") && !ValidLobbyKey("aimmodx.a"), "rejects other keys");
        Check(!ValidLobbyKey("aimmod." + std::string(33, 'a')), "limits key length");
    }

    // Join strings and launch command lines
    {
        const auto join = JoinString(Lobby);
        Check(join == "aimmod:1:" + std::to_string(Lobby), "formats join strings");
        Check(ConnectString(Lobby) == "-aimmodjoin=" + join, "formats connect strings");
        auto t = ParseJoinString(ConnectString(Lobby));
        Check(t && t->lobby == Lobby && t->version == 1, "parses connect strings");
        Check(!ParseJoinString("aimmod:1:" + std::to_string(Person)), "rejects non-lobby ids");
        Check(!ParseJoinString("+connect_lobby 1") && !ParseJoinString("aimmod::1") && !ParseJoinString("SteamConnectIP=1.2.3.4"), "rejects foreign strings");
        auto v2 = ParseJoinString("aimmod:2:" + std::to_string(Lobby));
        Check(v2 && v2->version == 2, "keeps the version for compatibility checks");

        const std::wstring lobby = std::to_wstring(Lobby);
        auto a = ParseLaunchCommandLine(L"\"C:\\Game\\FPSAimTrainer-Win64-Shipping.exe\" FPSAimTrainer -aimmodjoin=aimmod:1:" + lobby);
        Check(a && a->target.lobby == Lobby && a->source == "launch-aimmodjoin", "finds -aimmodjoin on the command line");
        auto b = ParseLaunchCommandLine(L"Game.exe -nosplash +connect_lobby " + lobby + L" -log");
        Check(b && b->target.lobby == Lobby && b->source == "launch-connect-lobby", "finds +connect_lobby on the command line");
        Check(!ParseLaunchCommandLine(L"Game.exe -x-aimmodjoin=aimmod:1:" + lobby), "ignores embedded switches");
        Check(!ParseLaunchCommandLine(L"Game.exe +connect_lobby notanumber"), "ignores bad lobby ids");
        Check(!ParseLaunchCommandLine(L"Game.exe +connect_lobbyX " + lobby), "requires the exact switch");
        Check(!ParseLaunchCommandLine(L"Game.exe"), "no join on a normal launch");
    }

    // AMP1 wire format
    {
        WireMessage hello{WireType::Hello};
        hello.lobby = Lobby;
        hello.token = 0x1122334455667788ull;
        const auto bytes = Encode(hello);
        Check(bytes.size() == WireHeader + 16, "hello size");
        auto back = Decode(bytes.data(), bytes.size());
        Check(back && back->type == WireType::Hello && back->lobby == Lobby && back->token == hello.token, "hello round-trips");

        WireMessage data{WireType::Data};
        data.payload.assign(100, 0x5a);
        auto d = Encode(data);
        auto db = Decode(d.data(), d.size());
        Check(db && db->payload.size() == 100, "data round-trips");

        WireMessage ping{WireType::Ping};
        ping.seq = 9;
        ping.time = -5;
        auto p = Encode(ping);
        auto pb = Decode(p.data(), p.size());
        Check(pb && pb->seq == 9 && pb->time == -5, "ping round-trips");

        auto bad = bytes;
        bad[0] = 'X';
        Check(!Decode(bad.data(), bad.size()), "rejects a bad magic");
        bad = bytes;
        bad[4] = 2;
        Check(!Decode(bad.data(), bad.size()), "rejects another wire version");
        bad = bytes;
        bad.push_back(0);
        Check(!Decode(bad.data(), bad.size()), "rejects a wrong body size");
        bad = bytes;
        bad[5] = 99;
        Check(!Decode(bad.data(), bad.size()), "rejects unknown types");
        WireMessage empty{WireType::Data};
        auto e = Encode(empty);
        Check(!Decode(e.data(), e.size()), "rejects empty data");
        WireMessage huge{WireType::Data};
        huge.payload.assign(MaxPayload + 1, 1);
        auto h = Encode(huge);
        Check(!Decode(h.data(), h.size()), "rejects oversized data");
        Check(!Decode(nullptr, 0) && !Decode(bytes.data(), 4), "rejects short frames");

        // Bulk transfer frames
        WireMessage chunk{WireType::Chunk};
        chunk.transfer = 7;
        chunk.index = 123;
        chunk.payload.assign(MaxChunk, 0xab);
        auto ce = Encode(chunk);
        auto cd = Decode(ce.data(), ce.size());
        Check(cd && cd->type == WireType::Chunk && cd->transfer == 7 && cd->index == 123 && cd->payload.size() == MaxChunk, "full chunk round-trips");
        chunk.payload.push_back(1);
        auto big = Encode(chunk);
        Check(!Decode(big.data(), big.size()), "rejects an oversized chunk");
        WireMessage emptyChunk{WireType::Chunk};
        auto ec = Encode(emptyChunk);
        Check(!Decode(ec.data(), ec.size()), "rejects an empty chunk");
        WireMessage ack{WireType::ChunkAck};
        ack.transfer = 7;
        ack.index = 9;
        auto ae = Encode(ack);
        auto ad = Decode(ae.data(), ae.size());
        Check(ad && ad->transfer == 7 && ad->index == 9 && ae.size() == WireHeader + 8, "chunk ack round-trips");
        WireMessage cancel{WireType::Cancel};
        cancel.transfer = 7;
        cancel.code = 2;
        auto xe = Encode(cancel);
        auto xd = Decode(xe.data(), xe.size());
        Check(xd && xd->transfer == 7 && xd->code == 2, "cancel round-trips");
        Check(Base64Encode(chunk.payload.data(), MaxChunk).size() + 200 < MaxPipeFrame, "a chunk's base64 fits one pipe frame");

        // Pose with the sender's capsule half-height; older frames without it stay valid.
        {
            WireMessage hp{WireType::Pose};
            hp.pose.origin = Person;
            hp.pose.seq = 3;
            hp.pose.z = 160.5f;
            hp.pose.vz = 420.f;
            hp.pose.flags = PoseFlagCrouch | PoseFlagHalfHeight;
            hp.pose.halfHeight = 55.f;
            hp.pose.scene = "s";
            auto he = Encode(hp);
            Check(he.size() == WireHeader + 46 + 4 + 1, "pose with half-height size");
            auto hd = Decode(he.data(), he.size());
            Check(hd && hd->pose.halfHeight == 55.f && hd->pose.z == 160.5f && hd->pose.vz == 420.f && (hd->pose.flags & PoseFlagCrouch) && hd->pose.scene == "s",
                  "pose with half-height round-trips (Z, vertical velocity, crouch)");
            WireMessage old = hp;
            old.pose.flags = PoseFlagCrouch; // a sender without the field
            auto oe = Encode(old);
            auto od = Decode(oe.data(), oe.size());
            Check(od && od->pose.halfHeight == 0 && od->pose.scene == "s", "older pose frames without half-height still decode");
        }

        // Spectate frames
        WireMessage sub{WireType::SpectateSub};
        sub.lobby = Person;
        sub.rate = 60;
        auto se = Encode(sub);
        auto sd = Decode(se.data(), se.size());
        Check(sd && sd->type == WireType::SpectateSub && sd->lobby == Person && sd->rate == 60, "spectate subscription round-trips");
        se.back() = 61;
        Check(!Decode(se.data(), se.size()), "rejects a spectate rate above 60 Hz");
        WireMessage cam{WireType::Camera};
        cam.camera.origin = Person;
        cam.camera.seq = 5;
        cam.camera.x = 1;
        cam.camera.y = 2;
        cam.camera.z = 3;
        cam.camera.pitch = -10;
        cam.camera.yaw = 90;
        cam.camera.fov = 103;
        cam.camera.flags = 1;
        cam.camera.ms = 1759300000123;
        auto ce2 = Encode(cam);
        Check(ce2.size() == WireHeader + 49, "camera frame is 57 bytes");
        auto cd2 = Decode(ce2.data(), ce2.size());
        Check(cd2 && cd2->camera.origin == Person && cd2->camera.yaw == 90 && cd2->camera.fov == 103 && cd2->camera.flags == 1 && cd2->camera.ms == 1759300000123, "camera frame round-trips with the sender time");
        WireMessage meta{WireType::CameraMeta};
        meta.lobby = Person;
        meta.camera.fov = 4.f;
        meta.scenario = "AimMod - aim_map (CSS) - CS Movement";
        meta.map = "aimmod_aim_map_css.map";
        auto me = Encode(meta);
        auto md = Decode(me.data(), me.size());
        Check(md && md->lobby == Person && md->camera.fov == 4.f && md->scenario == meta.scenario && md->map == meta.map, "camera meta round-trips");
        me.push_back(0);
        Check(!Decode(me.data(), me.size()), "rejects camera meta with trailing bytes");
        WireMessage badFov = cam;
        badFov.camera.fov = 0;
        auto bf = Encode(badFov);
        Check(!Decode(bf.data(), bf.size()), "rejects an impossible field of view");

        // Lobby-less spectating
        WireMessage hello2{WireType::SpectateHello};
        hello2.rate = 60;
        auto h2 = Encode(hello2);
        auto h2d = Decode(h2.data(), h2.size());
        Check(h2d && h2d->type == WireType::SpectateHello && h2d->rate == 60 && h2.size() == WireHeader + 1, "spectate hello round-trips");
        h2.back() = 0;
        Check(!Decode(h2.data(), h2.size()), "rejects a spectate hello with rate 0");
        WireMessage accept{WireType::SpectateAccept};
        auto ac = Encode(accept);
        Check(Decode(ac.data(), ac.size()).has_value() && ac.size() == WireHeader, "spectate accept round-trips");
        WireMessage refuse{WireType::Reject};
        refuse.code = static_cast<std::uint16_t>(RejectCode::NotFriend);
        auto rf = Encode(refuse);
        auto rfd = Decode(rf.data(), rf.size());
        Check(rfd && rfd->code == 8, "spectate refusals use the reject codes");
        WireMessage score{WireType::Score};
        score.score.origin = Person;
        score.score.flags = 1;
        score.score.score = 1234.5f;
        score.score.seconds = 30;
        score.score.remaining = 30;
        score.score.shots = 100;
        score.score.hits = 87;
        auto sc = Encode(score);
        auto scd = Decode(sc.data(), sc.size());
        Check(scd && scd->score.origin == Person && scd->score.score == 1234.5f && scd->score.hits == 87 && scd->score.kills == 0xFFFFFFFFu,
              "score frame round-trips (unknown kills stay unknown)");
        WireMessage nanScore = score;
        nanScore.score.score = std::numeric_limits<float>::infinity();
        auto ns = Encode(nanScore);
        Check(!Decode(ns.data(), ns.size()), "rejects non-finite scores");

        // Ghost demo pose
        WireMessage pose{WireType::Pose};
        pose.pose.origin = Person;
        pose.pose.seq = 42;
        pose.pose.x = 123.5f;
        pose.pose.y = -9876.25f;
        pose.pose.z = 50.f;
        pose.pose.yaw = 271.f;
        pose.pose.pitch = -12.5f;
        pose.pose.vx = 250.f;
        pose.pose.flags = 1;
        pose.pose.scene = "AimMod - aim_map (CSS) - CS Movement";
        auto pe = Encode(pose);
        Check(pe.size() == WireHeader + 46 + pose.pose.scene.size(), "pose size");
        auto pd = Decode(pe.data(), pe.size());
        Check(pd && pd->type == WireType::Pose && pd->pose.origin == Person && pd->pose.seq == 42 && pd->pose.x == 123.5f && pd->pose.y == -9876.25f &&
                  pd->pose.yaw == 271.f && pd->pose.pitch == -12.5f && pd->pose.vx == 250.f && pd->pose.flags == 1 && pd->pose.scene == pose.pose.scene,
              "pose round-trips");
        auto trunc = pe;
        trunc.pop_back();
        Check(!Decode(trunc.data(), trunc.size()), "rejects a truncated pose");
        WireMessage nan = pose;
        nan.pose.z = std::numeric_limits<float>::quiet_NaN();
        auto ne = Encode(nan);
        Check(!Decode(ne.data(), ne.size()), "rejects non-finite poses");
        WireMessage longScene = pose;
        longScene.pose.scene.assign(200, 'a');
        auto le = Encode(longScene);
        auto ld = Decode(le.data(), le.size());
        Check(ld && ld->pose.scene.size() == MaxPoseScene, "truncates long scene names");
    }

    // Remote transforms come only from remote samples.
    {
        using namespace bridge::ghost;
        auto pose = [](double z, double vz, bool crouch, float half) {
            Pose p;
            p.x = 100;
            p.y = 200;
            p.z = static_cast<float>(z);
            p.vz = static_cast<float>(vz);
            p.yaw = 90;
            p.flags = static_cast<std::uint8_t>((crouch ? PoseFlagCrouch : 0) | (half > 0 ? PoseFlagHalfHeight : 0));
            p.halfHeight = half;
            return p;
        };
        // A jump: Z rises between samples, and interpolation keeps it.
        std::vector<TimedPose> jump = {{10.0, pose(90, 0, false, 88)}, {10.1, pose(150, 400, false, 88)}, {10.2, pose(190, 200, false, 88)}};
        auto mid = Sample(jump, 10.25); // render time 10.15, halfway between samples 2 and 3
        Check(std::fabs(mid.z - 170) < 1e-6, "interpolation keeps Z (jumps show)");
        Check(std::fabs(mid.vz - 300) < 1e-6, "interpolation keeps vertical velocity");
        auto late = Sample(jump, 10.35); // 50 ms past the newest sample: extrapolate with vz
        Check(std::fabs(late.z - (190 + 200 * 0.05)) < 1e-4, "extrapolation continues Z from velocity");
        auto stale = Sample(jump, 20.0);
        Check(std::fabs(stale.z - (190 + 200 * MaxExtrapolation)) < 1e-4, "extrapolation is capped");

        // Crouch and capsule height are the remote player's, whatever the local player does.
        std::vector<TimedPose> standing = {{1.0, pose(90, 0, false, 88)}, {1.1, pose(90, 0, false, 88)}};
        std::vector<TimedPose> crouched = {{1.0, pose(60, 0, true, 55)}, {1.1, pose(60, 0, true, 55)}};
        auto s = Sample(standing, 1.2), c = Sample(crouched, 1.2);
        Check(!s.crouch && s.halfHeight == 88 && c.crouch && c.halfHeight == 55, "crouch and half-height come from the remote pose");
        auto ls = Layout(s), lc = Layout(c);
        Check(lc.head.z < ls.head.z && lc.body.sz < ls.body.sz, "a crouching remote player is drawn lower and shorter");
        Check(ls.body.x == 100 && ls.body.y == 200 && std::fabs(ls.head.z - (90 + 88 * 0.85)) < 1e-9, "shape layout is placed from the remote location only");
        std::vector<TimedPose> legacy = {{1.0, pose(90, 0, false, 0)}};
        Check(Sample(legacy, 2.0).halfHeight == DefaultHalfHeight, "senders without a half-height use the default, not the local capsule");
        auto ah = Sample(jump, 10.15);
        Check(std::fabs(WrapAngle(ah.yaw - 90)) < 1e-9, "yaw comes from the samples");
        std::vector<TimedPose> wrap = {{1.0, pose(0, 0, false, 88)}, {1.1, pose(0, 0, false, 88)}};
        wrap[0].pose.yaw = 170;
        wrap[1].pose.yaw = -170;
        Check(std::fabs(WrapAngle(Sample(wrap, 1.15).yaw - 180)) < 1e-6, "yaw interpolates across +-180 the short way");
    }
    // AimModCore pose format 1
    {
        using namespace bridge::posefile;
        Check(Escape("AimMod - aim_map (CSS) - CS Movement") == "AimMod%20-%20aim_map%20%28CSS%29%20-%20CS%20Movement", "escapes like AimModCore");
        Check(Unescape("a%20b%2Fc") == "a b/c" && !Unescape("a%2") && !Unescape("%zz"), "unescapes strictly");
        const std::string sample = "AIMMOD_POSE_1\t42\nmeta\tAimMod%20-%20aim_map%20%28CSS%29%20-%20CS%20Movement\taimmod_aim_map_css.map\t4\n"
                                   "pose\t1759300000100\t1.5\t-2\t160\t-10\t90\t0\t103\n"
                                   "pose\t1759300000116\t1.6\t-2\t161\t-10\t91\t0\t103\n"
                                   "target\t7\t500\t0\t90\t34\t88\n";
        auto parsed = Parse(sample);
        Check(parsed && parsed->sequence == 42 && parsed->scenario == "AimMod - aim_map (CSS) - CS Movement" && parsed->map == "aimmod_aim_map_css.map" &&
                  parsed->scale == 4 && parsed->rows.size() == 2 && parsed->rows[1].ms == 1759300000116 && parsed->rows[1].v[4] == 91 &&
                  parsed->targets.size() == 1 && parsed->targets[0].v[4] == 88,
              "parses AimModCore's self-pose.tsv");
        auto again = parsed ? Parse(Format(*parsed)) : std::nullopt;
        Check(again && again->rows.size() == 2 && again->rows[0].v[2] == 160 && again->scenario == parsed->scenario && again->targets.size() == 1,
              "pose files round-trip");
        Check(!Parse("AIMMOD_POSE_2\t1\nmeta\ta\tb\t1\npose\t1\t0\t0\t0\t0\t0\t0\t90\n"), "rejects another format version");
        Check(!Parse("AIMMOD_POSE_1\t1\nmeta\ta\tb\t1\npose\t5\t0\t0\t0\t0\t0\t0\t90\npose\t5\t0\t0\t0\t0\t0\t0\t90\n"), "rejects non-increasing times");
        Check(!Parse("AIMMOD_POSE_1\t1\nmeta\ta\tb\t1\npose\t5\t0\t0\t0\t0\t0\t0\t0\n"), "rejects an impossible field of view");
        Check(!Parse("AIMMOD_POSE_1\t1\nmeta\ta\tb\t1\n"), "rejects a file without poses");
        auto live = ScoreFromLiveOverlay(R"({"version":1,"active":true,"paused":false,"scenario":"x","score":512.5,"seconds":12,"shots":40,"hits":30,"remainingSeconds":48})");
        Check(live && (live->flags & 1) && live->score == 512.5f && live->hits == 30 && live->remaining == 48 && live->kills == 0xFFFFFFFFu,
              "reads live-overlay.json into a score frame");
        auto idle = ScoreFromLiveOverlay(R"({"version":1,"active":false,"paused":true})");
        Check(idle && idle->flags == 2 && idle->score == -1, "an idle overlay has no score");
        Check(!ScoreFromLiveOverlay(R"({"version":2})") && !ScoreFromLiveOverlay("nope"), "rejects other overlay versions");
        const auto sid = StreamIdFor(76561197960265729ull - 1);
        Check(ValidStreamId(sid) && sid.size() == 18 && sid.rfind("s-", 0) == 0 && sid.find("7656") == std::string::npos, "stream ids are hashed and well-formed");
        Check(StreamIdFor(5) == StreamIdFor(5) && StreamIdFor(5) != StreamIdFor(6), "stream ids are stable per player");
        File streamed;
        streamed.sequence = 7;
        streamed.stream = sid;
        streamed.rows.push_back(Row{1, {0, 0, 0, 0, 0, 0, 90}});
        const std::string text = Format(streamed);
        Check(text.rfind("AIMMOD_POSE_1\t7\t" + sid + "\n", 0) == 0, "writes the stream id in the header");
        auto sp = Parse(text);
        Check(sp && sp->stream == sid, "reads the stream id back");
        Check(!Parse("AIMMOD_POSE_1\t1\tbad id!\nmeta\ta\tb\t1\npose\t1\t0\t0\t0\t0\t0\t0\t90\n"), "rejects an invalid stream id");
        Check(Parse(sample) && Parse(sample)->stream.empty(), "AimModCore's self-pose.tsv has no stream id");
        File many;
        many.sequence = 1;
        for (int i = 0; i < 100; ++i) many.rows.push_back(Row{1000 + i, {0, 0, 0, 0, 0, 0, 90}});
        auto capped = Parse(Format(many));
        Check(capped && capped->rows.size() == MaxRows && capped->rows.back().ms == 1099, "writes at most the newest 64 rows");
    }
    // Lobby-less spectate decisions
    {
        using K = SpectateDecision::Kind;
        Check(DecideSpectate(SpectatePrivacy::Friends, true, 0).kind == K::Accept, "friends mode accepts a friend");
        Check(DecideSpectate(SpectatePrivacy::Ask, true, 0).kind == K::Ask, "ask mode asks");
        auto off = DecideSpectate(SpectatePrivacy::Off, true, 0);
        Check(off.kind == K::Reject && off.reason == RejectCode::SpectateOff, "off refuses");
        auto stranger = DecideSpectate(SpectatePrivacy::Friends, false, 0);
        Check(stranger.kind == K::Reject && stranger.reason == RejectCode::NotFriend, "non-friends are refused");
        auto full = DecideSpectate(SpectatePrivacy::Friends, true, MaxDirectSpectators);
        Check(full.kind == K::Reject && full.reason == RejectCode::SpectateFull, "the spectator cap holds");
        Check(ParseSpectatePrivacy("ask") == SpectatePrivacy::Ask && !ParseSpectatePrivacy("public"), "parses the privacy setting");
    }
    // Workshop query filter
    Check(UgcMatches("AimMod - aim_map (CSS) - CS Movement", "Scenario, aimmod-port , Movement", "aimmod-port", ""), "matches a tag in the list");
    Check(!UgcMatches("AimMod - aim_map", "Scenario,Movement", "aimmod-port", ""), "requires the tag");
    Check(!UgcMatches("AimMod - aim_map", "aimmod-portx", "aimmod-port", ""), "tags match whole words");
    Check(UgcMatches("AimMod - Dust2 (CSGO) - CS Movement", "", "", "aimmod - "), "text search ignores case");
    Check(!UgcMatches("Pole Long Dodge", "", "", "AimMod - "), "text search filters titles");
    Check(UgcMatches("anything", "", "", ""), "no filter matches everything");
    // Avatar path (offline avatar spike)
    {
        using ghost::AvatarPath;
        const std::string text = "AIMMOD_AVATAR_PATH_1\r\nmeta\tSynthetic%20Arena\tarena.map\t2.5\np\t0\t0\t0\t164\t0\t0\np\t100\t100\t0\t164\t-10\t90\np\t1000\t100\t900\t164\t0\t-170\n";
        std::string error;
        auto path = AvatarPath::Parse(text, &error);
        Check(path && path->scenario == "Synthetic Arena" && path->map == "arena.map" && path->scale == 2.5 && path->rows.size() == 3, "parses an avatar path");
        Check(path && std::fabs(path->Duration() - 1.0) < 1e-9, "path duration is the last row");
        if (path)
        {
            auto a = path->At(0.05, 64);
            Check(std::fabs(a.x - 50) < 1e-6 && std::fabs(a.z - 100) < 1e-6 && std::fabs(a.yaw - 45) < 1e-6 && std::fabs(a.vx - 1000) < 1e-6, "interpolates, lowers the eye to the capsule centre and derives velocity");
            auto wrapped = path->At(1.05, 64);
            Check(std::fabs(wrapped.x - a.x) < 1e-6 && std::fabs(wrapped.y - a.y) < 1e-6, "the path loops");
            auto gap = path->At(0.5, 64);
            Check(gap.vx == 0 && gap.vy == 0 && std::fabs(gap.yaw - (90 + 100.0 * 0.4 / 0.9)) < 1e-6, "long gaps hold velocity at zero and yaw takes the short way");
        }
        Check(!AvatarPath::Parse("AIMMOD_POSE_1\np\t0\t0\t0\t0\t0\t0\n"), "refuses another format");
        Check(!AvatarPath::Parse("AIMMOD_AVATAR_PATH_1\np\t10\t0\t0\t0\t0\t0\np\t5\t0\t0\t0\t0\t0\n"), "refuses rows going back in time");
        Check(!AvatarPath::Parse("AIMMOD_AVATAR_PATH_1\np\t0\t0\t0\t0\t95\t0\np\t5\t0\t0\t0\t0\t0\n"), "refuses impossible pitch");
        Check(!AvatarPath::Parse("AIMMOD_AVATAR_PATH_1\nmeta\tbad%0Aname\tm\t1\np\t0\t0\t0\t0\t0\t0\np\t5\t0\t0\t0\t0\t0\n"), "refuses control characters in meta");
        Check(!AvatarPath::Parse("AIMMOD_AVATAR_PATH_1\np\t0\t0\t0\t0\t0\t0\n"), "needs at least two rows");
        Check(!AvatarPath::Parse("AIMMOD_AVATAR_PATH_1\np\t0\tnan\t0\t0\t0\t0\np\t5\t0\t0\t0\t0\t0\n"), "refuses non-finite numbers");
    }
    // Kick list in lobby data
    {
        const std::uint64_t other = Person + 1;
        const auto text = FormatBanList({Person, Lobby, other});
        Check(text == std::to_string(Person) + "," + std::to_string(other), "ban list keeps individual accounts");
        const auto back = ParseBanList(text + ",x,," + std::to_string(Person));
        Check(back.size() == 2 && back[0] == Person && back[1] == other, "ban list round-trips and ignores junk and duplicates");
        std::vector<std::uint64_t> many;
        for (std::uint64_t i = 0; i < 40; ++i) many.push_back(Person + i);
        Check(FormatBanList(many).size() <= MaxLobbyValue && ParseBanList(FormatBanList(many)).size() >= 10, "ban list fits one lobby value");
        Check(ParseBanList(std::string(MaxLobbyValue + 1, '1')).empty(), "oversized ban list rejected");
    }
    // Remote players never become game bots outside AimMod match scenarios.
    Check(ghost::IsHelperBot("AimMod Hidden Bot") && !ghost::IsHelperBot("AimMod Hidden") && !ghost::IsHelperBot("target") &&
              std::hypot(ghost::HelperParkX, ghost::HelperParkY) > 100000 && std::hypot(ghost::HelperParkX, ghost::HelperParkY) < 1048576,
          "the arena's helper bot is recognised by its bot profile and parked far outside any map, inside the world");
    Check(ghost::AvatarBotsAllowed("AimMod Match - Synthetic Arena - ab12cd34") && !ghost::AvatarBotsAllowed("Synthetic Tracking") &&
              !ghost::AvatarBotsAllowed("") && !ghost::AvatarBotsAllowed("AimMod - Synthetic Map - CS Movement"),
          "avatar bots only in match scenarios");
    // avatar-state.tsv from the service
    {
        const std::string id = std::to_string(Person);
        auto st = bridge::avatarstate::Parse("AIMMOD_AVATARS_1\t9\nmatch\tm-1%20a\npeer\t" + id + "\t0\tfriend\t0\t1759300000000\t1759300003000\n");
        Check(st && st->sequence == 9 && st->match == "m-1 a" && st->peers.count(Person) && !st->peers[Person].alive && st->peers[Person].friendly &&
                  st->peers[Person].respawnAt == 1759300003000,
              "parses avatar-state.tsv");
        Check(!bridge::avatarstate::Parse("AIMMOD_AVATARS_1\t1\npeer\t" + id + "\t2\tfriend\t0\t0\t0\n"), "rejects a bad alive flag");
        Check(!bridge::avatarstate::Parse("AIMMOD_AVATARS_1\t1\npeer\t" + id + "\t1\tally\t0\t0\t0\n"), "rejects an unknown side");
        Check(!bridge::avatarstate::Parse("AIMMOD_AVATARS_1\t1\npeer\t" + std::to_string(Lobby) + "\t1\tenemy\t100\t0\t0\n"), "rejects a non-player id");
        Check(!bridge::avatarstate::Parse("peer\t" + id + "\t1\tenemy\t100\t0\t0\n"), "requires the header");
        Check(!bridge::avatarstate::Parse("AIMMOD_AVATARS_1\t9999999999999999999\n"), "rejects a sequence past INT64_MAX");
        auto empty = bridge::avatarstate::Parse("AIMMOD_AVATARS_1\t2\n");
        Check(empty && empty->peers.empty(), "an empty state file is valid");
    }
    // Tournament lobbies
    {
        WireMessage th{WireType::TournamentHello};
        th.lobby = Lobby;
        th.token = 0x55aa;
        th.matchToken = "tm_9fK2-xQ7Lp";
        auto te = Encode(th);
        auto td = Decode(te.data(), te.size());
        Check(td && td->type == WireType::TournamentHello && td->lobby == Lobby && td->token == 0x55aa && td->matchToken == "tm_9fK2-xQ7Lp", "tournament hello round-trips");
        WireMessage bad = th;
        bad.matchToken = "short";
        auto be = Encode(bad);
        Check(!Decode(be.data(), be.size()), "rejects an invalid match token");
        Check(ValidMatchToken("abcdEFGH_1-2") && !ValidMatchToken("abc") && !ValidMatchToken("has space!") && !ValidMatchToken(std::string(65, 'a')), "validates match tokens");
        Check(ValidProfileName("AimMod Meso Tracer") && ValidProfileName("CS Player (v2)") && !ValidProfileName(" lead") && !ValidProfileName("a/b") &&
                  !ValidProfileName("x\ny") && !ValidProfileName(std::string(65, 'a')),
              "validates character profile names");
        Check(SameToken("abcdefgh", "abcdefgh") && !SameToken("abcdefgh", "abcdefgx") && !SameToken("abcdefgh", "abcdefg"), "compares tokens");
        Check(be.empty(), "encoder refuses an invalid match token");
    }
    {
        // The encoder only produces what the decoder accepts.
        WireMessage data{WireType::Data};
        data.lobby = Lobby;
        Check(Encode(data).empty(), "encoder refuses an empty data payload");
        data.payload.assign(MaxPayload + 1, 1);
        Check(Encode(data).empty(), "encoder refuses an oversized data payload");
        WireMessage chunk{WireType::Chunk};
        chunk.payload.assign(MaxChunk + 1, 1);
        Check(Encode(chunk).empty(), "encoder refuses an oversized chunk");
        WireMessage hello{WireType::SpectateHello};
        hello.rate = 0;
        Check(Encode(hello).empty(), "encoder refuses a zero spectate rate");
        hello.rate = MaxSpectateRate + 1;
        Check(Encode(hello).empty(), "encoder refuses an excessive spectate rate");
        WireMessage pose{WireType::Pose};
        pose.pose.x = std::numeric_limits<float>::quiet_NaN();
        Check(Encode(pose).empty(), "encoder refuses a non-finite pose");
        WireMessage cam{WireType::Camera};
        cam.camera.fov = 90;
        cam.camera.yaw = std::numeric_limits<float>::infinity();
        Check(Encode(cam).empty(), "encoder refuses a non-finite camera");
        cam.camera.yaw = 10;
        const auto ce = Encode(cam);
        Check(!ce.empty() && Decode(ce.data(), ce.size()), "a valid camera frame still round-trips");
    }
    // Avatar PNGs (feature "avatar"): synthetic pictures only.
    {
        Check(avatar::Crc32(reinterpret_cast<const std::uint8_t*>("123456789"), 9) == 0xCBF43926u, "CRC-32 matches the standard check value");
        Check(avatar::Adler32(reinterpret_cast<const std::uint8_t*>("Wikipedia"), 9) == 0x11E60398u, "Adler-32 matches the standard check value");
        // A gradient with a ring and noise, like a real profile picture, and a flat one.
        std::vector<std::uint8_t> picture(64 * 64 * 4), flat(64 * 64 * 4, 0);
        std::uint32_t seed = 12345;
        for (std::uint32_t y = 0; y < 64; ++y)
            for (std::uint32_t x = 0; x < 64; ++x)
            {
                seed = seed * 1103515245u + 12345u;
                const int dx = static_cast<int>(x) - 32, dy = static_cast<int>(y) - 32;
                const bool ring = dx * dx + dy * dy > 600 && dx * dx + dy * dy < 800;
                auto* p = &picture[(y * 64 + x) * 4];
                p[0] = static_cast<std::uint8_t>(ring ? 240 : x * 4);
                p[1] = static_cast<std::uint8_t>(ring ? 200 : y * 4);
                p[2] = static_cast<std::uint8_t>(((seed >> 16) & 15) + 100);
                p[3] = 255;
                flat[(y * 64 + x) * 4 + 1] = 0x80;
                flat[(y * 64 + x) * 4 + 3] = 255;
            }
        for (const auto* source : {&picture, &flat})
        {
            const auto png = avatar::EncodePng(source->data(), source->size(), 64, 64);
            std::uint32_t w = 0, h = 0;
            const auto back = DecodePng(png, w, h);
            Check(back && w == 64 && h == 64 && *back == *source, "PNG round-trips pixel for pixel");
            Check(!png.empty() && png.size() < avatar::MaxPngBytes && png.size() < source->size(), "PNG is smaller than the raw pixels and fits the pipe");
        }
        Check(avatar::EncodePng(flat.data(), flat.size(), 64, 64).size() < 600, "Flat pictures compress to almost nothing");
        std::vector<std::uint8_t> tiny = {1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12};
        std::uint32_t tw = 0, th = 0;
        const auto small = DecodePng(avatar::EncodePng(tiny.data(), tiny.size(), 3, 1), tw, th);
        Check(small && tw == 3 && th == 1 && *small == tiny, "Odd sizes encode too");
        Check(avatar::EncodePng(picture.data(), picture.size(), 65, 64).empty() && avatar::EncodePng(picture.data(), 100, 64, 64).empty() && avatar::EncodePng(nullptr, 0, 0, 0).empty(),
              "Oversized, short or empty pictures are refused");
        const auto hash = avatar::Hash(picture.data(), 64, 64);
        Check(hash.size() == 16 && hash.find_first_not_of("0123456789abcdef") == std::string::npos, "The hash is 16 lowercase hex digits");
        Check(hash == avatar::Hash(picture.data(), 64, 64) && hash != avatar::Hash(flat.data(), 64, 64) && hash != avatar::Hash(picture.data(), 32, 128), "The hash follows pixels and size");
        auto broken = avatar::EncodePng(picture.data(), picture.size(), 64, 64);
        broken[broken.size() / 2] ^= 0x40;
        std::uint32_t bw = 0, bh = 0;
        Check(!DecodePng(broken, bw, bh), "The test decoder notices a damaged PNG");
        // Repetitive data exercises long matches and distances.
        std::vector<std::uint8_t> repeat;
        for (int i = 0; i < 20000; ++i) repeat.push_back(static_cast<std::uint8_t>((i % 7) * 31 + (i / 997)));
        const auto z = avatar::Zlib(repeat.data(), repeat.size());
        const auto inflated = Inflate(z);
        Check(inflated && *inflated == repeat && z.size() < repeat.size() / 4, "zlib streams round-trip and use back-references");
        const auto empty = Inflate(avatar::Zlib(nullptr, 0));
        Check(empty && empty->empty(), "An empty zlib stream is valid");
    }
    std::printf("%d/%d checks passed\n", checks - failures, checks);
    return failures == 0 ? 0 : 1;
}
