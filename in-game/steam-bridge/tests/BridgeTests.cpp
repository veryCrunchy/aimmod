// Tests for the Steam-independent parts of AimModSteam: JSON, base64, ids,
// lobby keys, join strings, launch command lines and the AMP1 wire format.
// Synthetic ids only.
#include "Codec.hpp"
#include "Json.hpp"

#include <cstdio>
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
    }

    std::printf("%d/%d checks passed\n", checks - failures, checks);
    return failures == 0 ? 0 : 1;
}
