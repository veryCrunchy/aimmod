#include "Codec.hpp"

#include <cstring>

namespace bridge
{
    namespace
    {
        constexpr char Alphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

        int Sextet(char c)
        {
            if (c >= 'A' && c <= 'Z') return c - 'A';
            if (c >= 'a' && c <= 'z') return c - 'a' + 26;
            if (c >= '0' && c <= '9') return c - '0' + 52;
            if (c == '+') return 62;
            if (c == '/') return 63;
            return -1;
        }

        void Put(std::vector<std::uint8_t>& out, std::uint64_t value, int bytes)
        {
            for (int i = 0; i < bytes; ++i) out.push_back(static_cast<std::uint8_t>(value >> (8 * i)));
        }
        std::uint64_t Get(const std::uint8_t* p, int bytes)
        {
            std::uint64_t value = 0;
            for (int i = 0; i < bytes; ++i) value |= static_cast<std::uint64_t>(p[i]) << (8 * i);
            return value;
        }
    } // namespace

    std::string Base64Encode(const std::uint8_t* data, std::size_t size)
    {
        std::string out;
        out.reserve((size + 2) / 3 * 4);
        for (std::size_t i = 0; i < size; i += 3)
        {
            const std::uint32_t n = (static_cast<std::uint32_t>(data[i]) << 16) | (i + 1 < size ? static_cast<std::uint32_t>(data[i + 1]) << 8 : 0) |
                                    (i + 2 < size ? data[i + 2] : 0);
            out.push_back(Alphabet[(n >> 18) & 63]);
            out.push_back(Alphabet[(n >> 12) & 63]);
            out.push_back(i + 1 < size ? Alphabet[(n >> 6) & 63] : '=');
            out.push_back(i + 2 < size ? Alphabet[n & 63] : '=');
        }
        return out;
    }

    std::optional<std::vector<std::uint8_t>> Base64Decode(std::string_view text, std::size_t maxBytes)
    {
        if (text.size() % 4 != 0 || text.size() / 4 * 3 > maxBytes + 2) return std::nullopt;
        std::vector<std::uint8_t> out;
        out.reserve(text.size() / 4 * 3);
        for (std::size_t i = 0; i < text.size(); i += 4)
        {
            const int a = Sextet(text[i]), b = Sextet(text[i + 1]);
            const bool last = i + 4 == text.size();
            const bool pad2 = last && text[i + 2] == '=' && text[i + 3] == '=';
            const bool pad1 = last && !pad2 && text[i + 3] == '=';
            const int c = pad2 ? 0 : Sextet(text[i + 2]);
            const int d = (pad1 || pad2) ? 0 : Sextet(text[i + 3]);
            if (a < 0 || b < 0 || c < 0 || d < 0) return std::nullopt;
            const std::uint32_t n = (static_cast<std::uint32_t>(a) << 18) | (static_cast<std::uint32_t>(b) << 12) | (static_cast<std::uint32_t>(c) << 6) |
                                    static_cast<std::uint32_t>(d);
            out.push_back(static_cast<std::uint8_t>(n >> 16));
            if (!pad2) out.push_back(static_cast<std::uint8_t>(n >> 8));
            if (!pad1 && !pad2) out.push_back(static_cast<std::uint8_t>(n));
        }
        if (out.size() > maxBytes) return std::nullopt;
        return out;
    }

    std::optional<std::uint64_t> ParseId(std::string_view text)
    {
        if (text.empty() || text.size() > 20) return std::nullopt;
        std::uint64_t value = 0;
        for (const char c : text)
        {
            if (c < '0' || c > '9') return std::nullopt;
            const std::uint64_t digit = static_cast<std::uint64_t>(c - '0');
            if (value > (UINT64_MAX - digit) / 10) return std::nullopt;
            value = value * 10 + digit;
        }
        return value == 0 ? std::nullopt : std::optional<std::uint64_t>(value);
    }

    bool IsIndividualId(std::uint64_t id) { return (id >> 56) == 1 && ((id >> 52) & 0xF) == 1 && (id & 0xFFFFFFFFull) != 0; }
    bool IsLobbyId(std::uint64_t id) { return (id >> 56) == 1 && ((id >> 52) & 0xF) == 8 && (id & 0xFFFFFFFFull) != 0; }

    std::string Redact(std::uint64_t id)
    {
        const std::string digits = std::to_string(id);
        return "..." + (digits.size() > 4 ? digits.substr(digits.size() - 4) : digits);
    }

    bool ValidLobbyKey(std::string_view key)
    {
        constexpr std::string_view prefix = "aimmod.";
        if (key.size() <= prefix.size() || key.size() > prefix.size() + 32 || key.substr(0, prefix.size()) != prefix) return false;
        for (const char c : key.substr(prefix.size()))
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-')) return false;
        return true;
    }

    std::string JoinString(std::uint64_t lobby) { return "aimmod:" + std::to_string(JoinStringVersion) + ":" + std::to_string(lobby); }
    std::string ConnectString(std::uint64_t lobby) { return "-aimmodjoin=" + JoinString(lobby); }

    std::optional<JoinTarget> ParseJoinString(std::string_view value)
    {
        constexpr std::string_view sw = "-aimmodjoin=";
        if (value.substr(0, sw.size()) == sw) value.remove_prefix(sw.size());
        constexpr std::string_view head = "aimmod:";
        if (value.size() > 64 || value.substr(0, head.size()) != head) return std::nullopt;
        value.remove_prefix(head.size());
        const auto colon = value.find(':');
        if (colon == std::string_view::npos || colon == 0 || colon > 4) return std::nullopt;
        int version = 0;
        for (const char c : value.substr(0, colon))
        {
            if (c < '0' || c > '9') return std::nullopt;
            version = version * 10 + (c - '0');
        }
        const auto lobby = ParseId(value.substr(colon + 1));
        if (!lobby || !IsLobbyId(*lobby)) return std::nullopt;
        return JoinTarget{*lobby, version};
    }

    std::optional<LaunchJoin> ParseLaunchCommandLine(std::wstring_view line)
    {
        auto narrow = [](std::wstring_view w) {
            std::string s;
            for (const wchar_t c : w) s.push_back(c < 0x80 ? static_cast<char>(c) : '?');
            return s;
        };
        auto token = [&](std::size_t from) {
            std::size_t end = from;
            while (end < line.size() && line[end] != L' ' && line[end] != L'\t' && line[end] != L'"') ++end;
            return line.substr(from, end - from);
        };
        constexpr std::wstring_view sw = L"-aimmodjoin=";
        for (auto at = line.find(sw); at != std::wstring_view::npos; at = line.find(sw, at + 1))
        {
            if (at > 0 && line[at - 1] != L' ' && line[at - 1] != L'"' && line[at - 1] != L'\t') continue;
            if (auto target = ParseJoinString(narrow(token(at + sw.size()))))
                return LaunchJoin{*target, "launch-aimmodjoin"};
        }
        constexpr std::wstring_view cl = L"+connect_lobby";
        for (auto at = line.find(cl); at != std::wstring_view::npos; at = line.find(cl, at + 1))
        {
            std::size_t p = at + cl.size();
            if (p >= line.size() || (line[p] != L' ' && line[p] != L'\t')) continue;
            while (p < line.size() && (line[p] == L' ' || line[p] == L'\t')) ++p;
            const auto id = ParseId(narrow(token(p)));
            if (id && IsLobbyId(*id)) return LaunchJoin{JoinTarget{*id, JoinStringVersion}, "launch-connect-lobby"};
        }
        return std::nullopt;
    }

    std::vector<std::uint8_t> Encode(const WireMessage& m)
    {
        std::vector<std::uint8_t> out = {'A', 'M', 'P', '1', WireVersion, static_cast<std::uint8_t>(m.type), 0, 0};
        switch (m.type)
        {
        case WireType::Hello: Put(out, m.lobby, 8); Put(out, m.token, 8); break;
        case WireType::Welcome:
        case WireType::Kick: Put(out, m.lobby, 8); break;
        case WireType::Reject: Put(out, m.code, 2); break;
        case WireType::Data: out.insert(out.end(), m.payload.begin(), m.payload.end()); break;
        case WireType::Ping:
        case WireType::Pong: Put(out, m.seq, 4); Put(out, static_cast<std::uint64_t>(m.time), 8); break;
        case WireType::Bye: break;
        }
        return out;
    }

    std::optional<WireMessage> Decode(const std::uint8_t* data, std::size_t size)
    {
        if (!data || size < WireHeader || size > WireHeader + MaxPayload) return std::nullopt;
        if (std::memcmp(data, "AMP1", 4) != 0 || data[4] != WireVersion || data[6] != 0 || data[7] != 0) return std::nullopt;
        WireMessage m;
        m.type = static_cast<WireType>(data[5]);
        const std::uint8_t* body = data + WireHeader;
        const std::size_t n = size - WireHeader;
        switch (m.type)
        {
        case WireType::Hello:
            if (n != 16) return std::nullopt;
            m.lobby = Get(body, 8);
            m.token = Get(body + 8, 8);
            break;
        case WireType::Welcome:
        case WireType::Kick:
            if (n != 8) return std::nullopt;
            m.lobby = Get(body, 8);
            break;
        case WireType::Reject:
            if (n != 2) return std::nullopt;
            m.code = static_cast<std::uint16_t>(Get(body, 2));
            break;
        case WireType::Data:
            if (n == 0 || n > MaxPayload) return std::nullopt;
            m.payload.assign(body, body + n);
            break;
        case WireType::Ping:
        case WireType::Pong:
            if (n != 12) return std::nullopt;
            m.seq = static_cast<std::uint32_t>(Get(body, 4));
            m.time = static_cast<std::int64_t>(Get(body + 4, 8));
            break;
        case WireType::Bye:
            if (n != 0) return std::nullopt;
            break;
        default: return std::nullopt;
        }
        return m;
    }
} // namespace bridge
