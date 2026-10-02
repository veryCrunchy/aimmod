// Tests for the Steam-independent parts of AimModSteam: JSON, base64, ids,
// lobby keys, join strings, launch command lines and the AMP1 wire format.
// Synthetic ids only.
#include "AvatarImage.hpp"
#include "AvatarPath.hpp"
#include "AvatarState.hpp"
#include "BotOrders.hpp"
#include "Codec.hpp"
#include "GhostMath.hpp"
#include "GrenadePhysics.hpp"
#include "Json.hpp"
#include "PoseFile.hpp"
#include "SiteSpots.hpp"
#include "Walker.hpp"

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
        // Friends lists are sent in parts that each fit the pipe frame.
        {
            std::vector<std::string> friends;
            for (int i = 0; i < 500; ++i)
                friends.push_back(json::Object().Str("peer", std::to_string(Person + static_cast<std::uint64_t>(i))).Str("name", "Synthetic Friend " + std::to_string(i)).Str("detail", std::string(120, 'x')).Done());
            std::size_t skipped = 99, total = 0;
            const auto parts = json::Chunks(friends, 48 * 1024, skipped);
            bool fit = parts.size() > 1 && skipped == 0;
            for (const auto& part : parts)
            {
                const auto parsed = json::Parse("{\"f\":" + part + "}", json::Limits{64 * 1024, 8, 32 * 1024, 4096});
                fit = fit && part.size() <= 48 * 1024 && parsed && parsed->Get("f")->type == json::Value::Type::Array;
                if (parsed) total += parsed->Get("f")->array.size();
            }
            Check(fit && total == friends.size(), "a long friends list splits into valid parts under the frame, keeping every entry");
            const auto one = json::Chunks({R"({"a":1})", std::string(200, 'y'), R"({"b":2})"}, 100, skipped);
            Check(one.size() == 1 && one[0] == R"([{"a":1},{"b":2}])" && skipped == 1, "an entry too large on its own is counted, not sent");
            const auto none = json::Chunks({}, 100, skipped);
            Check(none.size() == 1 && none[0] == "[]" && skipped == 0, "no friends still sends one empty part");
        }
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
        const std::string match = sample + "self\t1759300000116\t1.6\t-2\t96\t34\t96\t0\nfire\t1759300000116\t12\t1\nweapon\t1759300000116\t1\ntag\t7\ts-0011223344556677\n"
                                         "seen\t1759300000083\t7\t498\t0\t90\t34\t88\n";
        Check(Parse(match) && Parse(match)->rows.size() == 2 && Parse(match)->targets.size() == 1, "AimModCore's match rows (self, fire, weapon, tag, seen) don't break the pose file");
        Check(!Parse(sample + "teleport\t1\n"), "an unknown row still rejects the file");
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
    {
        // The simulated player walks on the floor between spawns, never through a wall or off a ledge.
        const double half = ghost::DefaultHalfHeight;
        auto flat = [](double x, double y, double) -> std::optional<double> { return std::fabs(x) < 2000 && std::fabs(y) < 2000 ? std::optional<double>(0.0) : std::nullopt; };
        auto open = [](double, double, double, double, double, double) { return true; };
        ghost::Walker w;
        w.spawns = {{-1000, 0, 120}, {1000, 0, 120}, {0, 1000, 120}};
        Check(w.Place(1, half, flat) && w.x == 1000 && w.z == half, "a placed walker stands on the floor below its spawn");
        bool grounded = true, inside = true, moved = false, crouched = false, jumped = false;
        for (int i = 0; i < 60 * 40; ++i)
        {
            const auto s = w.Step(i / 60.0, 1 / 60.0, half, flat, open);
            grounded &= s.z - s.halfHeight >= -0.01 && s.z - s.halfHeight <= ghost::Walker::JumpHeight + 0.01; // feet on the floor, crouched or not
            inside &= std::fabs(s.x) < 2000 && std::fabs(s.y) < 2000;
            moved |= std::hypot(s.x - 1000, s.y) > 500;
            crouched |= s.crouch;
            jumped |= s.z > half + 1;
        }
        Check(grounded && inside && moved, "the walker walks between spawns with its feet on the floor");
        Check(crouched && jumped, "it crouches now and then and jumps rarely");
        // A wall at x = 0: spawns on both sides, it never crosses.
        auto wall = [](double ax, double, double, double bx, double, double) { return (ax < 0) == (bx < 0); };
        ghost::Walker v;
        v.spawns = {{-1000, 0, 0}, {-1000, 800, 0}, {1000, 0, 0}};
        v.Place(0, half, flat);
        bool crossed = false;
        for (int i = 0; i < 60 * 40; ++i) crossed |= v.Step(i / 60.0, 1 / 60.0, half, flat, wall).x >= 0;
        Check(!crossed, "the walker never walks through a wall");
        // Floor only where x < 300: a ledge it never steps off.
        auto ledge = [](double x, double, double) -> std::optional<double> { return x < 300 ? std::optional<double>(0.0) : std::nullopt; };
        ghost::Walker l;
        l.spawns = {{-800, 0, 0}, {900, 0, 0}, {-800, 600, 0}};
        l.Place(0, half, ledge);
        bool fell = false;
        for (int i = 0; i < 60 * 30; ++i) fell |= l.Step(i / 60.0, 1 / 60.0, half, ledge, open).x >= 300;
        Check(!fell, "the walker never steps off a ledge");
        // A 3 m wall at x = 0 whose top the floor trace sees: the walker never stands on top of it.
        auto wallTop = [](double x, double, double z) -> std::optional<double> { return std::fabs(x) < 20 && z > 300 ? 300.0 : 0.0; };
        auto footWall = [](double ax, double, double az, double bx, double, double bz) { return (ax < -20) == (bx < -20) || (az > 300 && bz > 300); };
        ghost::Walker top;
        top.spawns = {{-1000, 0, 0}, {-1000, 600, 0}, {1000, 0, 0}, {1000, 600, 0}};
        top.Place(0, half, wallTop);
        bool onWall = false;
        for (int i = 0; i < 60 * 40; ++i) onWall |= top.Step(i / 60.0, 1 / 60.0, half, wallTop, footWall).z > half + ghost::Walker::JumpHeight + 1;
        Check(!onWall, "the walker never climbs onto a wall top");
        // Placed while no trace finds the floor (spawn height 500): it stands still, then snaps down
        // as soon as one does, and is never shown walking in the air.
        int calls = 0;
        auto late = [&](double, double, double) -> std::optional<double> { return ++calls > 5 ? std::optional<double>(0.0) : std::nullopt; };
        ghost::Walker f;
        f.spawns = {{0, 0, 500}, {1000, 0, 500}};
        f.Place(0, half, late);
        bool airWalk = false;
        double lastZ = 0;
        for (int i = 0; i < 60 * 5; ++i)
        {
            const auto s = f.Step(i / 60.0, 1 / 60.0, half, late, open);
            airWalk |= s.z > 400 && (std::fabs(s.x) > 0.01 || std::fabs(s.y) > 0.01);
            lastZ = s.z;
        }
        Check(!airWalk && lastZ <= half + ghost::Walker::JumpHeight + 0.01, "without a floor yet the walker waits at its spawn, then stands on the floor");

        // Bots: a goal behind a wall is reached through a waypoint that sees both sides.
        auto corner = [](double ax, double ay, double, double bx, double by, double) {
            // A wall along x = 0 for y < 500: crossing it below y = 500 is blocked.
            if ((ax < 0) == (bx < 0)) return true;
            const double t = ax == bx ? 0 : (0 - ax) / (bx - ax);
            return ay + (by - ay) * t >= 500;
        };
        ghost::Walker b;
        b.spawns = {{-1000, 0, 0}, {-1000, 200, 0}, {-600, 900, 0}, {600, 900, 0}};
        b.own = 2;
        b.Place(0, half, flat);
        b.goal = std::array<double, 3>{1000, 0, 0};
        bool reached = false, throughWall = false;
        double px = b.x, py = b.y;
        for (int i = 0; i < 60 * 40 && !reached; ++i)
        {
            const auto s = b.Step(i / 60.0, 1 / 60.0, half, flat, corner);
            throughWall |= !corner(px, py, 0, s.x, s.y, 0);
            px = s.x; py = s.y;
            reached |= std::hypot(s.x - 1000, s.y) < ghost::Walker::Arrive + 10;
        }
        Check(reached && !throughWall, "a bot walks to its goal around a wall by way of its waypoints");
        // Arrived: it stays at the goal.
        double drift = 0;
        for (int i = 0; i < 60 * 5; ++i) { const auto s = b.Step(40 + i / 60.0, 1 / 60.0, half, flat, corner); drift = std::max(drift, std::hypot(s.x - 1000, s.y)); }
        Check(drift < ghost::Walker::Arrive + 60, "at its goal the bot stays there");
        // Hold: it stands still and faces the point it's given (yaw toward it, pitch down to a lower eye).
        b.hold = true;
        b.face = std::array<double, 3>{1000, 1000, half + 20};
        const double hx = b.x, hy = b.y;
        ghost::RemoteTransform held;
        for (int i = 0; i < 60; ++i) held = b.Step(50 + i / 60.0, 1 / 60.0, half, flat, corner);
        Check(std::hypot(held.x - hx, held.y - hy) < 0.01 && std::fabs(ghost::WrapAngle(held.yaw - 90)) < 3 && held.pitch < 0,
              "holding, the bot stands still and aims at the point it faces");
        // Placed only on its own spawns (the first `own` points), and at a given point on its floor.
        bool ownOnly = true;
        for (int i = 0; i < 40; ++i) { b.PlaceRandom(half, flat); ownOnly &= b.x == -1000; }
        Check(ownOnly, "a bot is placed only on its own spawns, never on a waypoint");
        b.PlaceAt(300, 400, 150, 45, half, flat);
        Check(b.x == 300 && b.y == 400 && b.z == half && b.grounded && b.yaw == 45, "a bot stands at its round's spawn, on the floor below it");

        // A goal only reachable by first walking away from it (around a long wall): a route over the
        // waypoints, worked out a few checks at a time and shared by the walkers on the map.
        auto big = [](double x, double y, double) -> std::optional<double> { return std::fabs(x) < 3000 && std::fabs(y) < 3000 ? std::optional<double>(0.0) : std::nullopt; };
        auto longWall = [](double ax, double ay, double, double bx, double by, double) {
            if ((ax < 0) == (bx < 0)) return true;
            const double t = (0 - ax) / (bx - ax);
            return ay + (by - ay) * t > 1500; // the wall x = 0 runs up to y = 1500
        };
        const auto cache = std::make_shared<ghost::LinkCache>();
        ghost::Walker r;
        r.links = cache;
        r.spawns = {{-1000, 0, 0}, {-1000, -1500, 0}, {-1000, 2000, 0}, {1000, 2000, 0}, {1600, -1500, 0}};
        r.own = 2;
        r.Tune(1100, 79);
        Check(r.speed > 2.5 * ghost::Walker::Speed && r.stepUp == 79 && r.arrive > ghost::Walker::Arrive, "bots move like the player on a scaled map (run speed, stairs)");
        r.Place(0, half, big);
        r.goal = std::array<double, 3>{1000, 0, 0};
        bool there = false, crossedWall = false;
        double rx = r.x, ry = r.y, at = -1;
        for (int i = 0; i < 60 * 30 && !there; ++i)
        {
            const auto s = r.Step(i / 60.0, 1 / 60.0, half, big, longWall);
            crossedWall |= !longWall(rx, ry, 0, s.x, s.y, 0);
            rx = s.x; ry = s.y;
            if (std::hypot(s.x - 1000, s.y) < r.arrive + 10) { there = true; at = i / 60.0; }
        }
        Check(there && !crossedWall && r.plansFound >= 1 && r.plansPending >= 1, "a bot plans a route around a long wall over its waypoints, a few checks at a time");
        Check(at > 0 && at < 12, "and walks it at its tuned speed");
        // A second bot on the same map reuses what the first learnt.
        ghost::Walker r2;
        r2.links = cache;
        r2.spawns = r.spawns;
        r2.Tune(1100, 79);
        r2.Place(0, half, big);
        r2.goal = std::array<double, 3>{1000, 0, 0};
        int traces = 0;
        auto countingWall = [&](double ax, double ay, double az, double bx, double by, double bz) { ++traces; return longWall(ax, ay, az, bx, by, bz); };
        for (int i = 0; i < 3; ++i) r2.Step(i / 60.0, 1 / 60.0, half, big, countingWall);
        Check(r2.plansFound == 1 && r2.plansPending == 0 && !r2.route.empty(), "the route is found at once from the shared link cache");
        // The goal moves (a chase): the route is planned again for the new goal.
        r2.goal = std::array<double, 3>{-1000, -1500, 0};
        for (int i = 3; i < 60 * 10; ++i) r2.Step(i / 60.0, 1 / 60.0, half, big, longWall);
        Check(std::hypot(r2.x + 1000, r2.y + 1500) < r2.arrive + 10, "a moved goal is walked to");
        // The goal is cleared mid-walk: no stale goal target.
        r2.goal = std::array<double, 3>{1000, 0, 0};
        for (int i = 0; i < 30; ++i) r2.Step(20 + i / 60.0, 1 / 60.0, half, big, longWall);
        r2.goal.reset();
        r2.Step(21, 1 / 60.0, half, big, longWall);
        Check(r2.target != ghost::Walker::GoalTarget && r2.route.empty(), "without a goal the bot drops its route");
    }
    // The nav grid: ported maps have spawns and bomb sites only, so the bots' way between them is
    // found over the floor itself (the live test: every bot stayed in its spawn, no route).
    {
        const double half = 145;
        // Two rooms, a wall at x = 50 with one door (y 800..1100); no waypoint anywhere near the door.
        auto floor = [](double x, double y, double) -> std::optional<double> { return std::fabs(x) < 3000 && std::fabs(y) < 3000 ? std::optional<double>(0.0) : std::nullopt; };
        auto door = [](double ax, double ay, double, double bx, double by, double) {
            if ((ax < 50) == (bx < 50)) return true;
            const double t = (50 - ax) / (bx - ax), yy = ay + (by - ay) * t;
            return yy > 800 && yy < 1100;
        };
        const auto grid = std::make_shared<ghost::NavGrid>();
        grid->stepUp = 79; grid->stepDown = 126; grid->halfHeight = half;
        Check(grid->Seed(-2000, -2000, 150, floor) >= 0 && grid->Seed(2000, -2000, 150, floor) >= 0, "the grid is seeded at spawns and goals");
        const int used = grid->Grow(400, floor, door);
        Check(used <= 404 && !grid->Done(), "the grid grows a trace budget at a time");
        int rounds = 0;
        while (!grid->Done() && rounds++ < 10000) grid->Grow(2000, floor, door);
        Check(grid->Done() && grid->nodes.size() > 1000 && grid->nodes.size() < 3000, "the whole floor is covered, once per grid point");
        bool reached = false;
        const auto path = grid->Path({-2000, -2000, 145}, {2000, -2000, 145}, reached);
        bool viaDoor = false, crosses = true;
        for (std::size_t i = 1; i < path.size(); ++i)
        {
            crosses &= door(path[i - 1][0], path[i - 1][1], 0, path[i][0], path[i][1], 0);
            viaDoor |= path[i][1] > 700 && std::fabs(path[i][0]) < 200;
        }
        Check(reached && viaDoor && crosses, "the path between the rooms goes through the door");
        ghost::Walker w;
        w.nav = grid;
        w.spawns = {{-2000, -2000, 150}, {-1800, -2000, 150}};
        w.own = 2;
        w.Tune(1100, 79);
        w.Place(0, half, floor);
        w.goal = std::array<double, 3>{2000, -2000, 576};
        bool there = false, wallHit = false;
        double px = w.x, py = w.y;
        int traces = 0;
        auto counted = [&](double ax, double ay, double az, double bx, double by, double bz) { ++traces; return door(ax, ay, az, bx, by, bz); };
        for (int i = 0; i < 60 * 20 && !there; ++i)
        {
            const auto s = w.Step(i / 60.0, 1 / 60.0, half, floor, counted);
            wallHit |= !door(px, py, 0, s.x, s.y, 0);
            px = s.x; py = s.y;
            there |= std::hypot(s.x - 2000, s.y + 2000) < w.arrive + 10;
        }
        Check(there && !wallHit, "a bot walks from its spawn to a goal in the other room along the grid");
        Check(traces < 60 * 20 * 3, "following a path costs a few traces a step, not a re-plan every frame");
        // Stairs: 40 cm steps every 60 cm up to a 400 cm platform; a 79 cm step height climbs them.
        auto stairs = [](double x, double y, double) -> std::optional<double> {
            if (std::fabs(x) > 3000 || std::fabs(y) > 600) return std::nullopt;
            return x < 0 ? 0.0 : std::min(400.0, std::floor(x / 60) * 40);
        };
        auto open = [](double, double, double, double, double, double) { return true; };
        ghost::NavGrid up;
        up.stepUp = 79; up.stepDown = 126; up.halfHeight = half;
        up.Seed(-1500, 0, 150, stairs);
        while (!up.Done()) up.Grow(5000, stairs, open);
        bool climbed = false;
        const auto upPath = up.Path({-1500, 0, 145}, {1500, 0, 545}, climbed);
        Check(climbed && !upPath.empty() && upPath.back()[2] == 400, "the grid climbs stairs no higher than a step");

        // String-pulling: across the open room the path is a few straight legs, not a grid staircase.
        std::vector<int> raw;
        {
            bool ok = false;
            raw = grid->PathNodes({-2000, -2000, 145}, {-200, -400, 145}, ok);
            const auto pulled = grid->Smooth(raw);
            Check(ok && raw.size() > 12 && pulled.size() <= 3 && pulled.front() == raw.front() && pulled.back() == raw.back(), "paths are string-pulled into straight legs");
            // Diagonal steps across open floor: about as many grid points as the longer side, not both sides.
            Check(raw.size() <= 18, "across open floor the grid path steps diagonally, not in a staircase");
        }
        // Stuck recovery: a pillar the grid never saw (the traces missed it) blocks the way. The bot
        // hops, backs off, takes the link out of the grid and goes around.
        auto pillar = [&](double ax, double ay, double az, double bx, double by, double bz) {
            for (int k = 0; k <= 10; ++k)
            {
                const double px = ax + (bx - ax) * k / 10.0, py = ay + (by - ay) * k / 10.0;
                if (std::hypot(px + 1000, py + 1000) < 100) return false;
            }
            return door(ax, ay, az, bx, by, bz);
        };
        auto pillarGrid = std::make_shared<ghost::NavGrid>(*grid); // grown without the pillar
        ghost::Walker st;
        st.nav = pillarGrid;
        st.spawns = {{-1600, -1000, 150}};
        st.Tune(1100, 79);
        st.Place(0, half, floor);
        st.goal = std::array<double, 3>{-400, -1000, 145};
        bool passed = false, insidePillar = false;
        for (int i = 0; i < 60 * 30 && !passed; ++i)
        {
            const auto s = st.Step(i / 60.0, 1 / 60.0, half, floor, pillar);
            insidePillar |= std::hypot(s.x + 1000, s.y + 1000) < 60;
            passed |= std::hypot(s.x + 400, s.y + 1000) < st.arrive + 10;
        }
        Check(passed && st.stuckRecoveries >= 1 && !insidePillar, "a bot stuck on an unseen obstacle recovers and goes around it");
        Check(pillarGrid->blocked >= 1 || st.navBlocked >= 1, "and the way it couldn't pass is marked");
        // Turning: never faster than its turn rate.
        ghost::Walker tr;
        tr.nav = grid;
        tr.spawns = {{0, -2000, 150}};
        tr.Place(0, half, floor);
        tr.turnRate = 180;
        tr.hold = true;
        tr.face = std::array<double, 3>{-1000, -2000, 200}; // straight behind (yaw 180)
        double lastYaw = tr.yaw, maxTurn = 0;
        for (int i = 0; i < 90; ++i)
        {
            const auto s = tr.Step(i / 60.0, 1 / 60.0, half, floor, open);
            maxTurn = std::max(maxTurn, std::fabs(ghost::WrapAngle(s.yaw - lastYaw)));
            lastYaw = s.yaw;
        }
        Check(maxTurn <= 180.0 / 60 + 0.01 && std::fabs(ghost::WrapAngle(lastYaw - 180)) < 1, "a bot turns no faster than its turn rate, then faces its target");
        // Stopping short (a default position): it walks part of the way and stays there.
        ghost::Walker sh;
        sh.nav = grid;
        sh.spawns = {{-2000, -2000, 150}};
        sh.Tune(1100, 79);
        sh.Place(0, half, floor);
        sh.goal = std::array<double, 3>{2000, -2000, 145};
        sh.goalStop = 0.5;
        for (int i = 0; i < 60 * 20; ++i) sh.Step(i / 60.0, 1 / 60.0, half, floor, door);
        const double leftover = std::hypot(sh.x - 2000, sh.y + 2000);
        Check(sh.target == -1 && leftover > 1500 && std::hypot(sh.x + 2000, sh.y + 2000) > 1000, "told to go part of the way, a bot stops there and stays");
        sh.goalStop = 1;
        for (int i = 0; i < 60 * 20; ++i) sh.Step(20 + i / 60.0, 1 / 60.0, half, floor, door);
        Check(std::hypot(sh.x - 2000, sh.y + 2000) < sh.arrive + 10, "then told to go all the way, it goes on");
        // A detour first (a split): by way of the far corner of its room, then the goal.
        ghost::Walker vi;
        vi.nav = grid;
        vi.spawns = {{-2000, -2000, 150}};
        vi.Tune(1100, 79);
        vi.Place(0, half, floor);
        vi.goal = std::array<double, 3>{2000, -2000, 145};
        vi.via = std::array<double, 4>{-2000, 2400, 145, 1.0};
        bool corner = false, viaThere = false;
        for (int i = 0; i < 60 * 40 && !viaThere; ++i)
        {
            const auto s = vi.Step(i / 60.0, 1 / 60.0, half, floor, door);
            corner |= s.y > 2000;
            viaThere |= corner && std::hypot(s.x - 2000, s.y + 2000) < vi.arrive + 10;
        }
        Check(corner && viaThere, "a bot told to go by way of a point goes there first, then to its goal");
        // In a fight it strafes across the line to the enemy, near where it stood.
        ghost::Walker fi;
        fi.nav = grid;
        fi.spawns = {{-1500, -1500, 150}};
        fi.Tune(1100, 79);
        fi.Place(0, half, floor);
        fi.hold = true;
        fi.fight = 1;
        fi.face = std::array<double, 3>{-1500, 0, 200};
        double minX = 1e9, maxX = -1e9, maxY = -1e9, minY = 1e9;
        for (int i = 0; i < 60 * 4; ++i)
        {
            const auto s = fi.Step(i / 60.0, 1 / 60.0, half, floor, open);
            minX = std::min(minX, s.x); maxX = std::max(maxX, s.x); minY = std::min(minY, s.y); maxY = std::max(maxY, s.y);
        }
        Check(maxX - minX > 100 && maxX - minX <= 2 * ghost::Walker::StrafeRange + 20 && maxY - minY < 30, "fighting, a bot strafes side to side across the enemy's line, within a short range");
        // Two bots sent to the same spot end up side by side, not inside each other.
        ghost::Walker a1, a2;
        for (auto* w2 : {&a1, &a2})
        {
            w2->nav = grid;
            w2->Tune(1100, 79);
            w2->goal = std::array<double, 3>{-500, -500, 145};
        }
        a1.spawns = {{-2000, -1900, 150}};
        a2.spawns = {{-1900, -2000, 150}};
        a1.Place(0, half, floor);
        a2.Place(0, half, floor);
        for (int i = 0; i < 60 * 15; ++i)
        {
            a1.others = {{a2.x, a2.y}};
            a1.Step(i / 60.0, 1 / 60.0, half, floor, open);
            a2.others = {{a1.x, a1.y}};
            a2.Step(i / 60.0, 1 / 60.0, half, floor, open);
        }
        Check(std::hypot(a1.x - a2.x, a1.y - a2.y) > 80 && std::hypot(a1.x + 500, a1.y + 500) < 400 && std::hypot(a2.x + 500, a2.y + 500) < 400, "two bots sent to one spot stand side by side");

        // Eased turning: the view's angular velocity changes no faster than its acceleration, no step
        // turns faster than the turn rate, it settles on the target, and overshoots only a little.
        {
            const double rate = 540, accel = 3240, dt = 1 / 60.0;
            const auto turn = [&](double over, double& overshot, double& maxDv, double& maxStep, double& settled) {
                double a = 0, v = 0;
                overshot = 0; maxDv = 0; maxStep = 0; settled = -1;
                for (int i = 0; i < 240; ++i)
                {
                    const double before = a, vb = v;
                    a = ghost::Walker::Ease(a, 179, v, rate, accel, over, dt, false);
                    maxDv = std::max(maxDv, std::fabs(v - vb));
                    maxStep = std::max(maxStep, std::fabs(a - before));
                    overshot = std::max(overshot, a - 179);
                    if (std::fabs(a - 179) < 1) { if (settled < 0) settled = i * dt; }
                    else settled = -1;
                }
            };
            double over5 = 0, over0 = 0, dv = 0, step = 0, settled = 0, dv0 = 0, step0 = 0, settled0 = 0;
            turn(0.05, over5, dv, step, settled);
            turn(0, over0, dv0, step0, settled0);
            Check(dv <= accel * dt + 1e-9 && dv0 <= accel * dt + 1e-9, "eased turning never changes its turning speed faster than its acceleration");
            Check(step <= rate * 1.05 * dt + 1e-9 && step0 <= rate * dt + 1e-9, "eased turning never snaps: no step faster than the turn rate");
            Check(settled > 0 && settled < 1.5 && settled0 > 0 && settled0 < 1.5, "a 180 degree turn settles within a degree of its target");
            Check(over5 > 0.5 && over5 < 15 && over0 < 0.5, "it overshoots a little with overshoot, barely without");
            // A series of random targets, across +-180: the same limits on every step.
            double a = 0, v = 0, target = 0, worstDv = 0, worstStep = 0;
            std::uint32_t r = 12345;
            for (int i = 0; i < 60 * 30; ++i)
            {
                if (i % 30 == 0) { r = r * 1664525u + 1013904223u; target = (r % 36000) / 100.0 - 180; }
                const double before = a, vb = v;
                a = ghost::Walker::Ease(a, target, v, rate, accel, 0.2, dt, true);
                worstDv = std::max(worstDv, std::fabs(v - vb));
                worstStep = std::max(worstStep, std::fabs(ghost::WrapAngle(a - before)));
            }
            Check(worstDv <= accel * dt + 1e-9 && worstStep <= rate * 1.2 * dt + 1e-9, "random flicks across +-180 keep to the acceleration and turn rate");
        }

        // Gaits: shift-walking at 52% of the run speed, crouch-walking at 34% (the body crouched), and
        // neither hops nor crouches at random on the way.
        {
            const auto topSpeed = [&](bool walkGait, bool crouchStance, bool& alwaysCrouched, bool& idled) {
                ghost::Walker g;
                g.nav = grid;
                g.spawns = {{-2000, -2000, 150}};
                g.Tune(1100, 79);
                g.Place(0, half, floor);
                g.walkGait = walkGait;
                g.crouchStance = crouchStance;
                g.goal = std::array<double, 3>{-2000, 2500, 145};
                double top = 0;
                alwaysCrouched = true;
                idled = false;
                for (int i = 0; i < 60 * 20; ++i)
                {
                    const auto s = g.Step(i / 60.0, 1 / 60.0, half, floor, open);
                    top = std::max(top, std::hypot(s.vx, s.vy));
                    alwaysCrouched &= s.crouch;
                    idled |= (!crouchStance && s.crouch) || s.z > half + 1;
                }
                return top / g.speed;
            };
            bool crouchedRun = false, crouchedWalk = false, crouchedCrouch = false, idleRun = false, idleWalk = false, idleCrouch = false;
            const double run = topSpeed(false, false, crouchedRun, idleRun), walk = topSpeed(true, false, crouchedWalk, idleWalk), crouch = topSpeed(false, true, crouchedCrouch, idleCrouch);
            Check(std::fabs(run - 1) < 0.03 && std::fabs(walk - 0.52) < 0.02 && std::fabs(crouch - 0.34) < 0.02, "a bot runs, shift-walks at 52% and crouch-walks at 34% of its speed");
            Check(crouchedCrouch && !crouchedWalk && !idleWalk && !idleCrouch, "crouch-walking it stays crouched; walking silently it never hops or crouches at random");
        }

        // Smooth walking round the corner of an L-shaped corridor: the heading turns gradually, it slows
        // for the corner and arrives, never leaving the floor.
        {
            const auto inL = [](double x, double y) { return (x > -200 && x < 3180 && std::fabs(y) < 180) || (std::fabs(x - 3000) < 180 && y > -180 && y < 3000); };
            auto lFloor = [inL](double x, double y, double) -> std::optional<double> { return inL(x, y) ? std::optional<double>(0.0) : std::nullopt; };
            auto lClear = [inL](double ax, double ay, double, double bx, double by, double) {
                const int n = std::max(1, static_cast<int>(std::hypot(bx - ax, by - ay) / 10));
                for (int k = 0; k <= n; ++k)
                    if (!inL(ax + (bx - ax) * k / n, ay + (by - ay) * k / n)) return false;
                return true;
            };
            const auto lGrid = std::make_shared<ghost::NavGrid>();
            lGrid->stepUp = 79; lGrid->stepDown = 126; lGrid->halfHeight = half;
            lGrid->Seed(0, 0, 150, lFloor);
            lGrid->Seed(3000, 2760, 150, lFloor);
            while (!lGrid->Done()) lGrid->Grow(5000, lFloor, lClear);
            const auto walkL = [&](bool preaim, double& maxHeadingStep, double& cornerSpeed, double& maxYawStep, bool& left, double& yawBefore) {
                ghost::Walker c;
                c.nav = lGrid;
                c.spawns = {{0, 0, 150}};
                c.Tune(1100, 79);
                c.Place(0, half, lFloor);
                c.preaim = preaim;
                c.goal = std::array<double, 3>{3000, 2760, 145};
                double lastHeading = 1e9, lastYaw = c.yaw, at = -1;
                maxHeadingStep = 0; cornerSpeed = 1e9; maxYawStep = 0; left = false; yawBefore = 0;
                for (int i = 0; i < 60 * 20 && at < 0; ++i)
                {
                    const auto s = c.Step(i / 60.0, 1 / 60.0, half, lFloor, lClear);
                    const double sp = std::hypot(s.vx, s.vy);
                    if (sp > c.speed * 0.5)
                    {
                        const double heading = std::atan2(s.vy, s.vx) * 180 / 3.14159265358979;
                        if (lastHeading < 1e8) maxHeadingStep = std::max(maxHeadingStep, std::fabs(ghost::WrapAngle(heading - lastHeading)));
                        lastHeading = heading;
                    }
                    else lastHeading = 1e9;
                    maxYawStep = std::max(maxYawStep, std::fabs(ghost::WrapAngle(s.yaw - lastYaw)));
                    lastYaw = s.yaw;
                    if (std::hypot(s.x - 3000, s.y) < 300) cornerSpeed = std::min(cornerSpeed, sp);
                    if (s.x > 2300 && s.x < 2400 && std::fabs(s.y) < 150) yawBefore = s.yaw;
                    left |= !inL(s.x, s.y);
                    if (std::hypot(s.x - 3000, s.y - 2760) < c.arrive + 10) at = i / 60.0;
                }
                return at;
            };
            double headingStep = 0, cornerSpeed = 0, yawStep = 0, yawBefore = 0, h2 = 0, c2 = 0, y2 = 0, yawPreaim = 0;
            bool left = false, left2 = false;
            const double at = walkL(false, headingStep, cornerSpeed, yawStep, left, yawBefore);
            Check(at > 0 && !left, "a bot walks round the corner of an L-shaped corridor and arrives, never off the floor");
            Check(headingStep < 12 && yawStep <= 540.0 / 60 + 1e-6, "its heading turns gradually through the corner (no step change), its view no faster than the turn rate");
            Check(cornerSpeed < 935 * 0.85, "and it slows down for the corner");
            const double at2 = walkL(true, h2, c2, y2, left2, yawPreaim);
            Check(at2 > 0 && !left2 && yawPreaim > 20 && std::fabs(yawBefore) < 10, "pre-aiming, it looks round the corner ahead while it walks up to it");
        }

        // Counter-strafing: strafe, a dead stop (under 0.1 s), still for the shooting window, the other way.
        {
            ghost::Walker cs;
            cs.nav = grid;
            cs.spawns = {{-1500, -1500, 150}};
            cs.Tune(1100, 79);
            cs.Place(0, half, floor);
            cs.hold = true;
            cs.fight = 1;
            cs.counterStrafe = true;
            cs.face = std::array<double, 3>{-1500, 0, 200};
            double stopAt = -1, topSpeed = 0, leftX = 1e9, rightX = -1e9;
            int windows = 0, slowStops = 0, movedInWindow = 0, shortWindows = 0;
            int lastPhase = 0;
            for (int i = 0; i < 60 * 8; ++i)
            {
                const double t = i / 60.0;
                const auto s = cs.Step(t, 1 / 60.0, half, floor, open);
                const double sp = std::hypot(s.vx, s.vy);
                topSpeed = std::max(topSpeed, sp);
                leftX = std::min(leftX, s.x); rightX = std::max(rightX, s.x);
                if (cs.strafePhase == 1 && lastPhase == 0) stopAt = t;
                if (cs.strafePhase == 0 && lastPhase == 1)
                {
                    ++windows;
                    if (t - stopAt < 0.3) ++shortWindows;
                }
                if (cs.strafePhase == 1 && stopAt >= 0)
                {
                    if (t - stopAt >= 0.1 && sp > 5) ++movedInWindow;
                    if (t - stopAt >= 0.1 && t - stopAt < 0.1 + 1 / 60.0 && sp > 5) ++slowStops;
                }
                lastPhase = cs.strafePhase;
            }
            Check(windows >= 4 && topSpeed > cs.speed * 0.5 && rightX - leftX > 100 && rightX - leftX <= 2 * ghost::Walker::StrafeRange + 20, "counter-strafing, a bot strafes both ways across the enemy's line, within its range");
            Check(slowStops == 0 && movedInWindow == 0 && shortWindows == 0, "it stops dead within 0.1 s and stands still for the shooting window");
        }

        // Zones to avoid (a smoke): around one when there is room, through it when there is no other way.
        {
            const std::array<double, 5> smoke{-2000, 0, 0, 500, 50};
            bool ok = false;
            const auto around = grid->PathNodes({-2000, -2000, 145}, {-2000, 2000, 145}, ok, {smoke});
            const auto legs = grid->Smooth(around, {smoke});
            bool inside = false, cutsAcross = false;
            for (const int n : around) inside |= std::hypot(grid->nodes[n].x - smoke[0], grid->nodes[n].y - smoke[1]) < smoke[3];
            for (std::size_t i = 1; i < legs.size(); ++i)
            {
                const auto& p = grid->nodes[legs[i - 1]];
                const auto& q = grid->nodes[legs[i]];
                cutsAcross |= ghost::NavGrid::CrossesZone({p.x, p.y, p.z}, {q.x, q.y, q.z}, smoke);
            }
            bool direct = false;
            const auto plain = grid->Smooth(grid->PathNodes({-2000, -2000, 145}, {-2000, 2000, 145}, direct));
            bool plainCrosses = false;
            for (std::size_t i = 1; i < plain.size(); ++i)
            {
                const auto& p = grid->nodes[plain[i - 1]];
                const auto& q = grid->nodes[plain[i]];
                plainCrosses |= ghost::NavGrid::CrossesZone({p.x, p.y, p.z}, {q.x, q.y, q.z}, smoke);
            }
            Check(ok && direct && !inside && !cutsAcross && plainCrosses, "a path goes round a zone to avoid when there is room, and its shortcuts never cut across it");
            // The walker plans around it too, and plans again once it is gone.
            ghost::Walker av;
            av.nav = grid;
            av.spawns = {{-2000, -2000, 150}};
            av.Tune(1100, 79);
            av.Place(0, half, floor);
            av.avoid = {smoke};
            av.goal = std::array<double, 3>{-2000, 2000, 145};
            double closest = 1e9;
            bool arrived = false;
            for (int i = 0; i < 60 * 20 && !arrived; ++i)
            {
                const auto s = av.Step(i / 60.0, 1 / 60.0, half, floor, open);
                closest = std::min(closest, std::hypot(s.x - smoke[0], s.y - smoke[1]));
                arrived = std::hypot(s.x + 2000, s.y - 2000) < av.arrive + 10;
            }
            Check(arrived && closest > smoke[3] * 0.85, "a bot walks round a smoke on its way");
            av.goal = std::array<double, 3>{-2000, -2000, 145};
            av.Step(30, 1 / 60.0, half, floor, open);
            av.avoid.clear();
            for (int i = 1; i < 10; ++i) av.Step(30 + i / 60.0, 1 / 60.0, half, floor, open);
            Check(av.planAvoid.empty(), "a smoke gone, it plans without it");
        }
        {
            // The only way (a corridor) through the zone: walked through all the same, even at a cost meant to be never.
            auto corridorFloor = [](double x, double y, double) -> std::optional<double> { return x > -200 && x < 3200 && std::fabs(y) < 100 ? std::optional<double>(0.0) : std::nullopt; };
            auto anywhere = [](double, double, double, double, double, double) { return true; };
            ghost::NavGrid corridor;
            corridor.stepUp = 79; corridor.stepDown = 126; corridor.halfHeight = 145;
            corridor.Seed(0, 0, 150, corridorFloor);
            while (!corridor.Done()) corridor.Grow(5000, corridorFloor, anywhere);
            bool through = false, never = false;
            const auto p1 = corridor.PathNodes({0, 0, 145}, {3000, 0, 145}, through, {{1500, 0, 0, 300, 50}});
            const auto p2 = corridor.PathNodes({0, 0, 145}, {3000, 0, 145}, never, {{1500, 0, 0, 300, 5000}});
            Check(through && never && p1.size() == p2.size() && p1.size() >= 25, "with no other way it goes through the zone");
        }

        // Peeks: a jiggle peek steps out 60-90 cm to one side and back, quickly, facing the point; a wide
        // peek swings out and stays.
        {
            ghost::Walker pk;
            pk.nav = grid;
            pk.spawns = {{-1500, -1500, 150}};
            pk.Tune(1100, 79);
            pk.Place(0, half, floor);
            pk.hold = true;
            pk.peek = ghost::Walker::Peek::Jiggle;
            pk.peekAt = {-1500, -500, half + 64};
            double maxOut = 0, maxAlong = 0, worstAim = 0;
            int swings = 0;
            bool out = false;
            for (int i = 0; i < 60 * 4; ++i)
            {
                const auto s = pk.Step(i / 60.0, 1 / 60.0, half, floor, open);
                const double off = std::fabs(s.x + 1500);
                maxOut = std::max(maxOut, off);
                maxAlong = std::max(maxAlong, std::fabs(s.y + 1500));
                if (!out && off > 45) { out = true; ++swings; }
                if (out && off < 15) out = false;
                const double bearing = std::atan2(-500 - s.y, -1500 - s.x) * 180 / 3.14159265358979;
                if (i > 60) worstAim = std::max(worstAim, std::fabs(ghost::WrapAngle(s.yaw - bearing)));
            }
            Check(maxOut > 55 && maxOut < 100 && maxAlong < 15 && swings >= 5, "a jiggle peek steps out 60-90 cm to the side and back, again and again");
            Check(worstAim < 10, "and keeps facing the peeked point");
            pk.peek = ghost::Walker::Peek::Wide;
            pk.peekAt = {-1500, -400, half + 64};
            for (int i = 0; i < 60 * 2; ++i) pk.Step(5 + i / 60.0, 1 / 60.0, half, floor, open);
            Check(std::fabs(std::fabs(pk.x + 1500) - ghost::Walker::WideOut) < 20 && std::hypot(pk.velX, pk.velY) < 5, "a wide peek swings out and holds there");
        }
    }
    // Avatar placement: the actor follows the simulated position every tick.
    {
        ghost::RemoteTransform s;
        s.x = 1792; s.y = -9536; s.z = 657; s.halfHeight = 145; // a bot walker on a floor at z = 512
        Check(std::fabs(ghost::AvatarActorZ(s, 145, 160) - 657) < 0.01, "an avatar stands on its own capsule, not the padded mesh bounds");
        s.halfHeight = 168; s.z = 682; // a CS player (capsule 168) on a floor at z = 514
        Check(std::fabs(ghost::AvatarActorZ(s, 145) - (514 + 145)) < 0.01, "a remote player's floor carries over to the avatar's own capsule");
        Check(std::fabs(ghost::AvatarActorZ(s, -1, 150) - (514 + 150)) < 0.01 && std::fabs(ghost::AvatarActorZ(s, -1, -1) - 682) < 0.01, "mesh bounds, then the sample, as fallbacks");
        // Where the live test found the bots: near the local spawn, floating, while their walkers were at the other spawn.
        const double stuck[3]{-2364.6, 2798.3, 977.9}, close[3]{1792.5, -9536, 657}, exact[3]{1792, -9536, 657};
        Check(ghost::PlaceAfterDrive(stuck, 1792, -9536, 657) == ghost::Placement::Teleport, "a body the drive left across the map is teleported to its position");
        Check(ghost::PlaceAfterDrive(close, 1792, -9536, 657) == ghost::Placement::Driven && ghost::PlaceAfterDrive(exact, 1792, -9536, 657) == ghost::Placement::Driven,
              "a body where it was sent is left to the drive (its animation)");
        const double short_[3]{1792, -9436, 657};
        Check(ghost::PlaceAfterDrive(short_, 1792, -9536, 657) == ghost::Placement::Correct, "a body a wall stopped short is placed directly");
    }
    // spectate-view.tsv: who to watch while dead, and the chase camera behind them.
    {
        const std::int64_t now = 1790891335653;
        const auto bot = bridge::view::Parse("AIMMOD_VIEW_1\t1790891335000\nview\t3\n", now);
        const auto player = bridge::view::Parse("AIMMOD_VIEW_1\t1790891335000\r\nview\t" + std::to_string(Person) + "\r\n", now);
        Check(bot && *bot == 3 && player && *player == Person, "parses the avatar to watch (a bot stand-in or a player)");
        Check(!bridge::view::Parse("AIMMOD_VIEW_1\t1790891300000\nview\t3\n", now) && !bridge::view::Parse("AIMMOD_VIEW_1\t1790891335000\n", now) &&
                  !bridge::view::Parse("AIMMOD_VIEW_1\t1790891335000\nview\t0\n", now) && !bridge::view::Parse("AIMMOD_VIEW_1\t1790891335000\nview\tx1\n", now) &&
                  !bridge::view::Parse("AIMMOD_POSE_1\t1790891335000\nview\t3\n", now),
              "a stale file, no view row, a bad peer or another file: the view is the player's own");
        ghost::RemoteTransform s;
        s.x = 100; s.y = 200; s.z = 657; s.yaw = 90;
        const auto v = ghost::ChaseCamera(s, 145);
        Check(std::fabs(v.eye[2] - (657 + 145 * 0.75)) < 0.01 && std::fabs(v.camera[0] - 100) < 0.01 && v.camera[1] < 200 - 250 && v.camera[2] > v.eye[2] &&
                  v.yaw == 90 && v.pitch < 0 && v.pitch > -20,
              "the spectator camera sits behind the watched avatar at eye height, looking slightly down its way");
    }
    // Holding spots (SiteSpots.hpp): a site in a walled room with a door north and a door east and
    // a pillar inside; the defenders come from the north. Two ways in (one round to the east door),
    // spots that really see the bomb or an entrance (checked by the same traces), lurk spots off the
    // ways, all worked out a budget at a time.
    {
        const double half = 145;
        struct Box { double x0, y0, x1, y1; };
        const std::vector<Box> solid{
            {-1000, 990, -150, 1010}, {150, 990, 1000, 1010},   // north wall, door x -150..150
            {-1000, -1010, 1000, -990},                          // south wall
            {-1010, -1000, -990, 1000},                          // west wall
            {990, -1000, 1010, -150}, {990, 150, 1010, 1000},   // east wall, door y -150..150
            {300, 300, 600, 600},                                // a pillar on the site
        };
        auto inside = [&](double x, double y) {
            for (const auto& b : solid)
                if (x >= b.x0 && x <= b.x1 && y >= b.y0 && y <= b.y1) return true;
            return false;
        };
        auto floor = [&](double x, double y, double) -> std::optional<double> {
            return std::fabs(x) < 3000 && std::fabs(y) < 3000 && !inside(x, y) ? std::optional<double>(0.0) : std::nullopt;
        };
        int sightTraces = 0;
        auto clear = [&](double ax, double ay, double, double bx, double by, double) {
            ++sightTraces;
            const double len = std::hypot(bx - ax, by - ay);
            const int n = std::max(1, static_cast<int>(len / 10));
            for (int i = 0; i <= n; ++i)
                if (inside(ax + (bx - ax) * i / n, ay + (by - ay) * i / n)) return false;
            return true;
        };
        ghost::NavGrid grid;
        grid.stepUp = 79; grid.stepDown = 126; grid.halfHeight = half;
        grid.Seed(0, 0, 150, floor);
        grid.Seed(0, 2800, 150, floor);
        for (int i = 0; i < 100000 && !grid.Done(); ++i) grid.Grow(4000, floor, clear);
        ghost::SpotArea area;
        area.Start({"post", {0, 0, 0}, 250, 2400, 1300, {{0, 2800, 0}}});
        sightTraces = 0;
        int steps = 0, most = 0;
        while (!area.Done() && steps < 1000)
        {
            const int before = sightTraces;
            area.Step(grid, 300, clear);
            most = std::max(most, sightTraces - before);
            ++steps;
        }
        Check(area.Done() && !area.noGrid && steps > 3 && most <= 300 + 8, "holding spots are worked out a trace budget at a time");
        bool north = false, east = false;
        for (const auto& e : area.entrances)
        {
            north |= std::fabs(e.at[0]) < 400 && e.at[1] > 900 && e.at[1] < 1800;
            east |= std::fabs(e.at[1]) < 400 && e.at[0] > 900 && e.at[0] < 1800;
        }
        Check(area.entrances.size() == 2 && north && east, "two ways in from the defenders' side: the north door, and round to the east door");
        bool truthful = true, ranged = true, bomb = false, pillarHides = true, outsideSeesDoor = false;
        int checked = 0;
        for (const auto& sp : area.spots)
        {
            const double eye = sp.at[2] + ghost::SpotArea::EyeAbove(grid);
            int used = 0;
            const bool sees = ghost::SpotArea::Sees(clear, {sp.at[0], sp.at[1], eye}, {0, 0, ghost::SpotArea::BombAbove}, used);
            truthful &= sees == sp.bomb;
            ranged &= std::hypot(sp.at[0], sp.at[1]) >= 250 && std::hypot(sp.at[0], sp.at[1]) <= 2400;
            bomb |= sp.bomb;
            if (sp.at[0] > 650 && sp.at[1] > 650 && sp.at[0] < 980 && sp.at[1] < 980 && std::fabs(sp.at[0] - sp.at[1]) < 150) pillarHides &= !sp.bomb;
            outsideSeesDoor |= sp.at[1] > 1100 && sp.mask != 0;
            ++checked;
        }
        Check(checked >= 8 && truthful && ranged && bomb, "every spot is in range and sees the bomb exactly when a trace says so");
        Check(pillarHides && outsideSeesDoor, "a spot behind the pillar doesn't see the bomb; one outside the north door watches a way in");
        bool lurkOff = true;
        int lurks = 0;
        for (const auto& e : area.entrances)
            if (e.lurk)
            {
                ++lurks;
                for (const int w : e.way) lurkOff &= std::hypot(grid.nodes[w].x - (*e.lurk)[0], grid.nodes[w].y - (*e.lurk)[1]) >= grid.spacing * 1.7;
            }
        Check(lurks >= 1 && lurkOff, "lurk spots by the ways in, off the ways themselves");
        std::map<std::string, ghost::SpotArea> areas{{"post", area}};
        const auto text = ghost::FormatSpots(5, areas);
        Check(text.rfind("AIMMOD_SPOTS_1\t5\narea\tpost\tdone\t2\t", 0) == 0 && text.find("\nentrance\tpost\t1\t") != std::string::npos && text.find("\nspot\tpost\t") != std::string::npos,
              "bot-spots.tsv lists each area's entrances and spots");
        ghost::SpotArea nowhere;
        nowhere.Start({"far", {50000, 50000, 0}, 250, 2400, 1300, {{0, 2800, 0}}});
        nowhere.Step(grid, 300, clear);
        Check(nowhere.Done() && nowhere.noGrid && ghost::FormatSpots(1, {{"far", nowhere}}).find("area\tfar\tnogrid\t0\t0") != std::string::npos, "an area off the grid says so");
        const auto asked = bridge::bots::Parse("AIMMOD_BOTS_1\t3\narea\tb-12\t10\t20\t30\t250\t2400\t1300\nfrom\tb-12\t0\t2800\t0\nfrom\tb-12\t1\t2\t3\nfrom\tnone\t1\t2\t3\n"
                                               "area\tbad key\t1\t2\t3\t1\t200\t300\narea\tx\t1\t2\t3\t500\t400\t300\narea\tb-12\t1\t2\t3\t1\t200\t300\n");
        Check(asked && asked->areas.size() == 1 && asked->areas[0].key == "b-12" && asked->areas[0].sources.size() == 2 && asked->areas[0].rmax == 2400 && asked->areas[0].centre[1] == 20 && asked->bots.empty(),
              "area and from rows: keys checked, radii in range, one request per key");
        bridge::bots::Report lr;
        lr.peer = 3; lr.look = 87.4;
        Check(bridge::bots::Format(1, {lr}).find("\nlook\t3\t87\n") != std::string::npos, "bot-sight.tsv says how far each bot sees straight ahead");
    }
    // bot-orders.tsv and bot-sight.tsv
    {
        const auto o = bridge::bots::Parse("AIMMOD_BOTS_1\t7\nbot\t1\tgoal\t100\t-200.5\t30\nface\t1\t5\t6\t7\nplace\t1\tcs3\t1\t2\t3\t90\n"
                                          "sight\t1\t4\t10\t20\t30\nsight\t1\t9\t11\t21\t31\nbot\t2\thold\npose\t3\t1\t2\t3\t180\nbot\t17\troam\nbot\tx\troam\n");
        Check(o && o->sequence == 7 && o->bots.size() == 3, "parses bot orders (bad peers skipped)");
        if (o && o->bots.size() == 3)
        {
            const auto& a = o->bots.at(1);
            Check(a.mode == bridge::bots::Order::Mode::Goal && a.goal && (*a.goal)[1] == -200.5 && a.face && (*a.face)[2] == 7 && a.placeToken == "cs3" && a.placeAt[3] == 90,
                  "a bot's goal, facing and round placement");
            Check(a.sight.size() == 2 && a.sight[1].tag == 9 && a.sight[1].at[0] == 11, "a bot's sight targets");
            Check(o->bots.at(2).mode == bridge::bots::Order::Mode::Hold && o->bots.at(3).mode == bridge::bots::Order::Mode::Pose && o->bots.at(3).pose[3] == 180,
                  "hold, and a client's pose from the host");
        }
        Check(!bridge::bots::Parse("bot\t1\troam\n") && !bridge::bots::Parse("AIMMOD_BOTS_2\t1\n"), "bot orders need their header");
        Check(bridge::bots::Parse("AIMMOD_BOTS_1\t1\nsight\t1\t2\tnan\t0\t0\nbot\t1\tgoal\t1e9\t0\t0\n")->bots.at(1).sight.empty(), "refuses non-finite and huge numbers");
        bridge::bots::Report r;
        r.peer = 2; r.x = 1; r.y = 2.25; r.z = -3; r.yaw = 90; r.floor = -148; r.seen = {{4, true}, {9, false}};
        r.speed = 412.34; r.crouch = true;
        Check(bridge::bots::Format(1759300000000, {r}) == "AIMMOD_BOTSIGHT_1\t1759300000000\nbot\t2\t1.0\t2.2\t-3.0\t90.0\t-148.0\nvel\t2\t412.3\t1\nseen\t2\t4\t1\nseen\t2\t9\t0\n" ||
                  bridge::bots::Format(1759300000000, {r}) == "AIMMOD_BOTSIGHT_1\t1759300000000\nbot\t2\t1.0\t2.3\t-3.0\t90.0\t-148.0\nvel\t2\t412.3\t1\nseen\t2\t4\t1\nseen\t2\t9\t0\n",
              "formats bot-sight.tsv, with the floor under each bot, its speed and crouch");
        // How the bots move: gait and stance, peeks, the fight's style, eased aim, zones to avoid.
        std::string rows = "AIMMOD_BOTS_1\t9\nmove\t1\twalk\tcrouch\t1\npeek\t1\tjiggle\t10\t20\t30\nfight\t1\t0.7\tcounter\naim\t1\t2500\t0.1\n"
                           "avoid\t1\t1\t2\t3\t300\t50\navoid\t1\t4\t5\t6\t200\t5000\nfight\t4\t0.5\nfight\t5\t0.5\tad\n"
                           "move\t2\tsprint\tstand\t0\nmove\t2\twalk\tstand\t2\nmove\t2\twalk\npeek\t2\tlean\t1\t2\t3\npeek\t2\twide\t1\t2\nfight\t2\t0.5\tzigzag\n"
                           "aim\t2\t50\t0.1\naim\t2\t2000\t0.9\navoid\t2\t0\t0\t0\t5\t10\navoid\t2\t0\t0\t0\t100\t-1\navoid\t2\t0\t0\tnan\t100\t1\n";
        for (int i = 0; i < 10; ++i) rows += "avoid\t3\t" + std::to_string(i) + "\t0\t0\t100\t10\n";
        const auto moves = bridge::bots::Parse(rows);
        Check(moves && moves->bots.size() == 5, "parses the movement rows");
        if (moves && moves->bots.size() == 5)
        {
            const auto& m1 = moves->bots.at(1);
            using O = bridge::bots::Order;
            Check(m1.gait == O::Gait::Walk && m1.crouch && m1.preaim && m1.peek == O::Peek::Jiggle && m1.peekAt[2] == 30, "move (walk, crouched, pre-aiming) and peek rows");
            Check(m1.fight == 0.7 && m1.counterStrafe && moves->bots.at(4).fight == 0.5 && !moves->bots.at(4).counterStrafe && !moves->bots.at(5).counterStrafe && moves->bots.at(5).fight == 0.5,
                  "a fight's style: counter, ad, or none (side to side)");
            Check(m1.aimAccel == 2500 && m1.aimOvershoot == 0.1 && m1.avoid.size() == 2 && m1.avoid[1][3] == 200 && m1.avoid[1][4] == 5000, "aim and avoid rows");
            const auto& m2 = moves->bots.at(2);
            Check(m2.gait == O::Gait::Run && !m2.crouch && !m2.preaim && m2.peek == O::Peek::None && m2.fight == 0 && m2.aimAccel == 0 && m2.avoid.empty(),
                  "bad movement rows are skipped (unknown gaits, peeks and styles, out-of-range aim and zones)");
            Check(moves->bots.at(3).avoid.size() == 8, "at most 8 zones to avoid per bot");
        }
        // The overhaul's orders: stop short, a detour, fight strafing, turn rate, the debug overlay.
        const auto more = bridge::bots::Parse("AIMMOD_BOTS_1\t8\ndebug\t1\nbot\t1\tgoal\t100\t200\t30\t0.6\nvia\t1\t-50\t60\t30\t0.5\nfight\t1\t0.8\nturn\t1\t420\n"
                                              "bot\t2\tgoal\t1\t2\t3\t7\nvia\t2\t1\t2\t3\t2\nfight\t2\t5\nturn\t2\t1\n");
        Check(more && more->debug && more->bots.at(1).stop == 0.6 && more->bots.at(1).via && (*more->bots.at(1).via)[3] == 0.5 && more->bots.at(1).fight == 0.8 && more->bots.at(1).turn == 420,
              "parses stop-short goals, detours, fight strafing, turn rates and the debug switch");
        Check(more && more->bots.at(2).stop == 1 && !more->bots.at(2).via && more->bots.at(2).fight == 0 && more->bots.at(2).turn == 0, "out-of-range values are ignored");
    }
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
        // Bots and simulated players are the bridge's stand-in avatars 1..16: their rows must not
        // void the whole file (that left every avatar alive, on the enemy team and unarmed).
        auto bots = bridge::avatarstate::Parse("AIMMOD_AVATARS_1\t21\nmatch\tm-1\npeer\t1\t0\tenemy\t0\t1759300000000\t0\tPistol\npeer\t2\t1\tfriend\t100\t0\t0\tAK47\npeer\t" + id + "\t1\tenemy\t100\t0\t0\n");
        Check(bots && bots->peers.size() == 3 && !bots->peers[1].alive && bots->peers[2].friendly && bots->peers[2].weapon == "AK47" && bots->peers.count(Person),
              "parses stand-in avatar rows (bots) next to players");
        Check(!bridge::avatarstate::Parse("AIMMOD_AVATARS_1\t1\npeer\t17\t1\tenemy\t100\t0\t0\n") && !bridge::avatarstate::Parse("AIMMOD_AVATARS_1\t1\npeer\t0\t1\tenemy\t100\t0\t0\n"),
              "stand-ins are peers 1 to 16 only");
        Check(!bridge::avatarstate::Parse("AIMMOD_AVATARS_1\t9999999999999999999\n"), "rejects a sequence past INT64_MAX");
        auto empty = bridge::avatarstate::Parse("AIMMOD_AVATARS_1\t2\n");
        Check(empty && empty->peers.empty(), "an empty state file is valid");
        // CS: the weapon in their hands, by KovaaK's third-person model name.
        auto armed = bridge::avatarstate::Parse("AIMMOD_AVATARS_1\t3\npeer\t" + id + "\t1\tenemy\t100\t0\t0\tSix Shooter\n");
        Check(armed && armed->peers[Person].weapon == "Six Shooter" && st->peers[Person].weapon.empty(), "parses the held weapon (none without the column)");
        auto bare = bridge::avatarstate::Parse("AIMMOD_AVATARS_1\t3\npeer\t" + id + "\t1\tenemy\t100\t0\t0\t-\n");
        Check(bare && bare->peers[Person].weapon.empty(), "\"-\": nothing in their hands to show");
        Check(!bridge::avatarstate::Parse("AIMMOD_AVATARS_1\t3\npeer\t" + id + "\t1\tenemy\t100\t0\t0\t/Game/Other\n") &&
                  !bridge::avatarstate::Parse("AIMMOD_AVATARS_1\t3\npeer\t" + id + "\t1\tenemy\t100\t0\t0\tAK47\textra\n"),
              "only KovaaK's own weapon models, and no further columns");
        Check(bridge::avatarstate::ThirdPersonMesh("AK47") == L"/Game/SourceArt/Weapons/FN_AK47/FN_AK47.FN_AK47" &&
                  bridge::avatarstate::ThirdPersonMesh("Bolt Action Sniper") == L"/Game/SourceArt/Weapons/FN_Sniper_BoltAction/FN_Sniper_BoltAction.FN_Sniper_BoltAction" &&
                  bridge::avatarstate::ThirdPersonMesh("Knife").empty(),
              "third-person models map to KovaaK's FN_ weapon meshes");
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
    {
        // CS grenades (GrenadePhysics.hpp): the same flight as the service's GrenadePhysics.
        using namespace bridge::grenades;
        const Vec full = ThrowVelocity(0, 0, 1);
        Check(std::fabs(Len(full[0], full[1], full[2]) - ThrowSpeed) < 1e-6 && std::fabs(std::atan2(full[2], full[0]) * 180 / 3.14159265358979323846 - 10) < 1e-6 &&
                  std::fabs(Len(ThrowVelocity(0, 0, 0)[0], 0, ThrowVelocity(0, 0, 0)[2]) - ThrowSpeed * 0.3) < 1e-6,
              "Grenade throws: full speed on fire, 30 % underhand, aimed 10 degrees up at level");
        const auto flat = Simulate({0, 0, 180}, full, Floor(0));
        // The service's MultiplayerChecks.GoldenRestMs / GoldenRestX for the same throw.
        Check(flat.back().motion == Rest && std::fabs(flat.back().t - 2206) < 0.5 && std::fabs(flat.back().x - 3852.8) < 0.5 &&
                  std::any_of(flat.begin(), flat.end(), [](const Key& k) { return k.motion == Slide; }),
              "A grenade on a level floor bounces, slides and rests where the service's physics says");
        Check(Simulate({0, 0, 180}, full, Floor(0)) == flat, "The same throw flies the same path every time");
        // A floor at 0 and a wall at x = 800 facing back.
        const Trace room = [](const Vec& a, const Vec& b) -> std::optional<Hit> {
            std::optional<double> best;
            Vec normal{};
            if (a[2] >= 0 && b[2] < 0) best = a[2] / (a[2] - b[2]), normal = {0, 0, 1};
            if (a[0] <= 800 && b[0] > 800)
            {
                const double f = (800 - a[0]) / (b[0] - a[0]);
                if (!best || f < *best) best = f, normal = {-1, 0, 0};
            }
            if (!best) return std::nullopt;
            return Hit{{a[0] + (b[0] - a[0]) * *best, a[1] + (b[1] - a[1]) * *best, a[2] + (b[2] - a[2]) * *best}, normal};
        };
        const auto keys = Simulate({0, 0, 180}, full, room);
        bool inside = true, ordered = true, exact = true, wall = false;
        for (std::size_t i = 0; i < keys.size(); ++i)
        {
            inside = inside && keys[i].x <= 800 && keys[i].z >= -0.01;
            wall = wall || keys[i].impact == WallImpact;
            if (i == 0) continue;
            ordered = ordered && keys[i].t >= keys[i - 1].t;
            const Vec at = Pos(keys[i - 1], (keys[i].t - keys[i - 1].t) / 1000);
            exact = exact && Len(at[0] - keys[i].x, at[1] - keys[i].y, at[2] - keys[i].z) < 3;
        }
        Check(wall && inside && ordered && exact && keys.back().motion == Rest, "A grenade bounces off a wall, stays in the room and each key follows from the last");
        const auto lost = Simulate({0, 0, 100}, {100, 0, 0}, [](const Vec&, const Vec&) { return std::optional<Hit>{}; });
        Check(lost.back().motion == Rest && std::fabs(lost.back().t - MaxSeconds * 1000) < 20, "With nothing to hit, a path ends after 8 s");
        const auto r = ParseSim("AIMMOD_GRENADESIM_1\t7\nthrow\t3\the\t1\t2\t3\t10\t0\t-5\nthrow\tx\the\t1\t2\t3\t4\t5\t6\nlos\t9\t0\t0\t0\t5\t5\t5\nnoise\n");
        Check(r && r->sequence == 7 && r->throws.size() == 1 && r->throws[0].id == 3 && r->throws[0].kind == "he" && r->throws[0].velocity[2] == -5 && r->los.size() == 1 && r->los[0].tag == 9 &&
                  r->los[0].to[1] == 5 && !ParseSim("AIMMOD_BOTS_1\t1\n"),
              "grenade-sim.tsv: throws and line-of-sight checks; bad rows skipped, a foreign header refused");
        const std::string out = FormatPaths(1234, {{3, {{0, 1, 2, 3, 4, 5, 6, Flight, NoImpact}, {500, 7, 8, 9, 0, 0, 0, Rest, FloorImpact}}}}, {{9, false}, {10, true}});
        Check(out == "AIMMOD_GRENADEPATHS_1\t1234\npath\t3\t2\t0.000\t1.000\t2.000\t3.000\t4.000\t5.000\t6.000\t0\t0\t500.000\t7.000\t8.000\t9.000\t0.000\t0.000\t0.000\t2\t2\nlos\t9\t0\nlos\t10\t1\n",
              "grenade-paths.tsv: each path's keys and each line of sight");
        // The service's GrenadeThrowChecks: 1.25 times the thrower's run or jump, at most 15 m/s of it; underhand 12 units lower.
        const Vec run = ThrowVelocity(0, 0, 1, {1000, 0, 0}), wild = ThrowVelocity(0, 0, 1, {1e6, 0, 0});
        Check(std::fabs(run[0] - full[0] - 1250) < 1e-6 && run[2] == full[2] && std::fabs(wild[0] - full[0] - 1.25 * MaxInheritCm) < 1e-6 &&
                  ThrowOrigin({0, 0, 180}, 0)[2] == 180 - 12 * Unit && ThrowOrigin({0, 0, 180}, 1)[2] == 180 &&
                  Simulate({0, 0, 180}, run, Floor(0)).back().x > flat.back().x + 500,
              "A throw carries the thrower's velocity (CS: 1.25 times it) and goes further on the run");
        // Floors: the first surface down if it faces up; a wall or nothing is no floor.
        const Trace step = [](const Vec& a, const Vec& b) -> std::optional<Hit> {
            if (a[0] > 100) return Hit{{a[0], a[1], 0}, {1, 0, 0}}; // a wall face
            const double z = a[1] > 150 ? -60 : 0;
            if (a[2] < z || b[2] >= z) return std::nullopt;
            return Hit{{a[0], a[1], z}, {0, 0, 1}};
        };
        Check(FloorHeight(step, 0, 0, 80, -200) == 0.0 && FloorHeight(step, 0, 300, 80, -200) == -60.0 && !FloorHeight(step, 200, 0, 80, -200) && !FloorHeight(step, 0, 300, 80, -50) &&
                  !FloorHeight(step, 0, 0, -10, 10),
              "Floor heights: a step down, none beyond the trace, a wall isn't a floor");
        const auto fr = ParseSim("AIMMOD_GRENADESIM_1\t8\nfloor\t12\t100\t200\t260\t-150\nfloor\tx\t1\t2\t3\t4\nfloor\t13\t1\t2\t3\n");
        Check(fr && fr->floors.size() == 1 && fr->floors[0].tag == 12 && fr->floors[0].y == 200 && fr->floors[0].bottom == -150 && fr->throws.empty(), "grenade-sim.tsv: floor requests");
        Check(FormatPaths(1, {}, {}, {{12, -60.5}, {13, std::nullopt}}) == "AIMMOD_GRENADEPATHS_1\t1\nfloor\t12\t-60.5\nfloor\t13\t-\n", "grenade-paths.tsv: a floor's height, or none");
    }
    std::printf("%d/%d checks passed\n", checks - failures, checks);
    return failures == 0 ? 0 : 1;
}
