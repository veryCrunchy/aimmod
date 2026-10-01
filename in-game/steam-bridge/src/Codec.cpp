#include "Codec.hpp"

#include <algorithm>
#include <cmath>
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

    std::string FormatBanList(const std::vector<std::uint64_t>& ids)
    {
        std::string out;
        for (const std::uint64_t id : ids)
        {
            if (!IsIndividualId(id)) continue;
            const std::string digits = std::to_string(id);
            if (out.size() + digits.size() + (out.empty() ? 0 : 1) > MaxLobbyValue) break;
            if (!out.empty()) out += ',';
            out += digits;
        }
        return out;
    }

    std::vector<std::uint64_t> ParseBanList(std::string_view text)
    {
        std::vector<std::uint64_t> ids;
        if (text.size() > MaxLobbyValue) return ids;
        while (!text.empty())
        {
            const auto comma = text.find(',');
            if (const auto id = ParseId(text.substr(0, comma)); id && IsIndividualId(*id) && std::find(ids.begin(), ids.end(), *id) == ids.end())
                ids.push_back(*id);
            text = comma == std::string_view::npos ? std::string_view{} : text.substr(comma + 1);
        }
        return ids;
    }

    bool ValidLobbyKey(std::string_view key)
    {
        constexpr std::string_view prefix = "aimmod.";
        if (key.size() <= prefix.size() || key.size() > prefix.size() + 32 || key.substr(0, prefix.size()) != prefix) return false;
        for (const char c : key.substr(prefix.size()))
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-')) return false;
        return true;
    }

    bool ValidMatchToken(std::string_view token)
    {
        if (token.size() < 8 || token.size() > 64) return false;
        for (const char c : token)
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')) return false;
        return true;
    }

    bool ValidProfileName(std::string_view name)
    {
        if (name.empty() || name.size() > 64 || name.front() == ' ' || name.back() == ' ') return false;
        for (const char c : name)
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == ' ' || c == '_' || c == '-' || c == '.' || c == '(' ||
                  c == ')' || c == '\''))
                return false;
        return true;
    }

    bool SameToken(std::string_view a, std::string_view b)
    {
        if (a.size() != b.size()) return false;
        unsigned char diff = 0;
        for (std::size_t i = 0; i < a.size(); ++i) diff |= static_cast<unsigned char>(a[i] ^ b[i]);
        return diff == 0;
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

    namespace
    {
        std::string Lower(std::string_view s)
        {
            std::string out(s);
            for (char& c : out)
                if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
            return out;
        }
        std::string_view Trim(std::string_view s)
        {
            while (!s.empty() && (s.front() == ' ' || s.front() == '\t')) s.remove_prefix(1);
            while (!s.empty() && (s.back() == ' ' || s.back() == '\t')) s.remove_suffix(1);
            return s;
        }
    } // namespace

    bool UgcMatches(std::string_view title, std::string_view tags, std::string_view tag, std::string_view text)
    {
        if (!tag.empty())
        {
            const std::string want = Lower(Trim(tag));
            bool found = false;
            std::size_t start = 0;
            while (!found && start <= tags.size())
            {
                const auto comma = tags.find(',', start);
                const auto piece = tags.substr(start, comma == std::string_view::npos ? std::string_view::npos : comma - start);
                found = Lower(Trim(piece)) == want;
                if (comma == std::string_view::npos) break;
                start = comma + 1;
            }
            if (!found) return false;
        }
        if (!text.empty() && Lower(title).find(Lower(text)) == std::string::npos) return false;
        return true;
    }

    std::optional<SpectatePrivacy> ParseSpectatePrivacy(std::string_view text)
    {
        if (text == "off") return SpectatePrivacy::Off;
        if (text == "friends") return SpectatePrivacy::Friends;
        if (text == "ask") return SpectatePrivacy::Ask;
        return std::nullopt;
    }

    const char* SpectatePrivacyName(SpectatePrivacy p)
    {
        switch (p)
        {
        case SpectatePrivacy::Off: return "off";
        case SpectatePrivacy::Ask: return "ask";
        default: return "friends";
        }
    }

    SpectateDecision DecideSpectate(SpectatePrivacy privacy, bool isFriend, std::size_t current)
    {
        using K = SpectateDecision::Kind;
        if (privacy == SpectatePrivacy::Off) return {K::Reject, RejectCode::SpectateOff};
        if (!isFriend) return {K::Reject, RejectCode::NotFriend};
        if (current >= MaxDirectSpectators) return {K::Reject, RejectCode::SpectateFull};
        return {privacy == SpectatePrivacy::Ask ? K::Ask : K::Accept, RejectCode::Declined};
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
        case WireType::Chunk: Put(out, m.transfer, 4); Put(out, m.index, 4); out.insert(out.end(), m.payload.begin(), m.payload.end()); break;
        case WireType::ChunkAck: Put(out, m.transfer, 4); Put(out, m.index, 4); break;
        case WireType::Cancel: Put(out, m.transfer, 4); Put(out, m.code, 2); break;
        case WireType::SpectateSub: Put(out, m.lobby, 8); out.push_back(m.rate); break;
        case WireType::Camera:
        {
            const CameraFrame& c = m.camera;
            Put(out, c.origin, 8);
            Put(out, c.seq, 4);
            Put(out, static_cast<std::uint64_t>(c.ms), 8);
            for (const float f : {c.x, c.y, c.z, c.pitch, c.yaw, c.roll, c.fov})
            {
                std::uint32_t bits = 0;
                std::memcpy(&bits, &f, 4);
                Put(out, bits, 4);
            }
            out.push_back(c.flags);
            break;
        }
        case WireType::SpectateHello: out.push_back(m.rate); break;
        case WireType::TournamentHello:
        {
            Put(out, m.lobby, 8);
            Put(out, m.token, 8);
            const std::size_t n = std::min<std::size_t>(m.matchToken.size(), 64);
            out.push_back(static_cast<std::uint8_t>(n));
            out.insert(out.end(), m.matchToken.begin(), m.matchToken.begin() + static_cast<std::ptrdiff_t>(n));
            break;
        }
        case WireType::SpectateAccept: break;
        case WireType::Score:
        {
            const ScoreFrame& s = m.score;
            Put(out, s.origin, 8);
            out.push_back(s.flags);
            for (const float f : {s.score, s.seconds, s.remaining})
            {
                std::uint32_t bits = 0;
                std::memcpy(&bits, &f, 4);
                Put(out, bits, 4);
            }
            Put(out, s.shots, 4);
            Put(out, s.hits, 4);
            Put(out, s.kills, 4);
            break;
        }
        case WireType::CameraMeta:
        {
            Put(out, m.lobby, 8);
            std::uint32_t bits = 0;
            std::memcpy(&bits, &m.camera.fov, 4); // map scale
            Put(out, bits, 4);
            for (const std::string* s : {&m.scenario, &m.map})
            {
                const std::size_t n = std::min(s->size(), MaxPoseScene);
                out.push_back(static_cast<std::uint8_t>(n));
                out.insert(out.end(), s->begin(), s->begin() + static_cast<std::ptrdiff_t>(n));
            }
            break;
        }
        case WireType::Pose:
        {
            const Pose& p = m.pose;
            Put(out, p.origin, 8);
            Put(out, p.seq, 4);
            for (const float f : {p.x, p.y, p.z, p.yaw, p.pitch, p.vx, p.vy, p.vz})
            {
                std::uint32_t bits = 0;
                std::memcpy(&bits, &f, 4);
                Put(out, bits, 4);
            }
            out.push_back(p.flags);
            const std::size_t n = std::min(p.scene.size(), MaxPoseScene);
            out.push_back(static_cast<std::uint8_t>(n));
            if (p.flags & PoseFlagHalfHeight)
            {
                std::uint32_t bits = 0;
                std::memcpy(&bits, &p.halfHeight, 4);
                Put(out, bits, 4);
            }
            out.insert(out.end(), p.scene.begin(), p.scene.begin() + static_cast<std::ptrdiff_t>(n));
            break;
        }
        }
        return out;
    }

    std::optional<WireMessage> Decode(const std::uint8_t* data, std::size_t size)
    {
        if (!data || size < WireHeader || size > WireHeader + 8 + std::max(MaxPayload, MaxChunk)) return std::nullopt;
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
        case WireType::Chunk:
            if (n <= 8 || n > 8 + MaxChunk) return std::nullopt;
            m.transfer = static_cast<std::uint32_t>(Get(body, 4));
            m.index = static_cast<std::uint32_t>(Get(body + 4, 4));
            m.payload.assign(body + 8, body + n);
            break;
        case WireType::ChunkAck:
            if (n != 8) return std::nullopt;
            m.transfer = static_cast<std::uint32_t>(Get(body, 4));
            m.index = static_cast<std::uint32_t>(Get(body + 4, 4));
            break;
        case WireType::Cancel:
            if (n != 6) return std::nullopt;
            m.transfer = static_cast<std::uint32_t>(Get(body, 4));
            m.code = static_cast<std::uint16_t>(Get(body + 4, 2));
            break;
        case WireType::SpectateSub:
            if (n != 9 || body[8] > MaxSpectateRate) return std::nullopt;
            m.lobby = Get(body, 8);
            m.rate = body[8];
            break;
        case WireType::Camera:
        {
            if (n != 8 + 4 + 8 + 7 * 4 + 1) return std::nullopt;
            CameraFrame& c = m.camera;
            c.origin = Get(body, 8);
            c.seq = static_cast<std::uint32_t>(Get(body + 8, 4));
            c.ms = static_cast<std::int64_t>(Get(body + 12, 8));
            float* fields[] = {&c.x, &c.y, &c.z, &c.pitch, &c.yaw, &c.roll, &c.fov};
            for (int i = 0; i < 7; ++i)
            {
                const auto bits = static_cast<std::uint32_t>(Get(body + 20 + 4 * i, 4));
                std::memcpy(fields[i], &bits, 4);
                if (!std::isfinite(*fields[i]) || std::fabs(*fields[i]) > 1e7f) return std::nullopt;
            }
            if (c.fov <= 1 || c.fov >= 179) return std::nullopt;
            c.flags = body[48];
            break;
        }
        case WireType::SpectateHello:
            if (n != 1 || body[0] < 1 || body[0] > MaxSpectateRate) return std::nullopt;
            m.rate = body[0];
            break;
        case WireType::SpectateAccept:
            if (n != 0) return std::nullopt;
            break;
        case WireType::TournamentHello:
        {
            if (n < 17) return std::nullopt;
            const std::size_t len = body[16];
            if (len < 1 || len > 64 || n != 17 + len) return std::nullopt;
            m.lobby = Get(body, 8);
            m.token = Get(body + 8, 8);
            m.matchToken.assign(reinterpret_cast<const char*>(body + 17), len);
            if (!ValidMatchToken(m.matchToken)) return std::nullopt;
            break;
        }
        case WireType::Score:
        {
            if (n != 8 + 1 + 12 + 12) return std::nullopt;
            ScoreFrame& s = m.score;
            s.origin = Get(body, 8);
            s.flags = body[8];
            float* fields[] = {&s.score, &s.seconds, &s.remaining};
            for (int i = 0; i < 3; ++i)
            {
                const auto bits = static_cast<std::uint32_t>(Get(body + 9 + 4 * i, 4));
                std::memcpy(fields[i], &bits, 4);
                if (!std::isfinite(*fields[i]) || std::fabs(*fields[i]) > 1e9f) return std::nullopt;
            }
            s.shots = static_cast<std::uint32_t>(Get(body + 21, 4));
            s.hits = static_cast<std::uint32_t>(Get(body + 25, 4));
            s.kills = static_cast<std::uint32_t>(Get(body + 29, 4));
            break;
        }
        case WireType::CameraMeta:
        {
            if (n < 8 + 4 + 2) return std::nullopt;
            m.lobby = Get(body, 8);
            const auto bits = static_cast<std::uint32_t>(Get(body + 8, 4));
            std::memcpy(&m.camera.fov, &bits, 4);
            if (!std::isfinite(m.camera.fov) || m.camera.fov < 0 || m.camera.fov > 1000) return std::nullopt;
            std::size_t at = 12;
            for (std::string* s : {&m.scenario, &m.map})
            {
                if (at >= n) return std::nullopt;
                const std::size_t len = body[at++];
                if (len > MaxPoseScene || at + len > n) return std::nullopt;
                s->assign(reinterpret_cast<const char*>(body + at), len);
                for (const char ch : *s)
                    if (static_cast<unsigned char>(ch) < 0x20) return std::nullopt;
                at += len;
            }
            if (at != n) return std::nullopt;
            break;
        }
        case WireType::Pose:
        {
            constexpr std::size_t fixed = 8 + 4 + 8 * 4 + 2;
            if (n < fixed) return std::nullopt;
            const std::size_t sceneLength = body[fixed - 1];
            const std::size_t extra = (body[fixed - 2] & PoseFlagHalfHeight) ? 4 : 0;
            if (sceneLength > MaxPoseScene || n != fixed + extra + sceneLength) return std::nullopt;
            Pose& p = m.pose;
            p.origin = Get(body, 8);
            p.seq = static_cast<std::uint32_t>(Get(body + 8, 4));
            float* fields[] = {&p.x, &p.y, &p.z, &p.yaw, &p.pitch, &p.vx, &p.vy, &p.vz};
            for (int i = 0; i < 8; ++i)
            {
                const auto bits = static_cast<std::uint32_t>(Get(body + 12 + 4 * i, 4));
                std::memcpy(fields[i], &bits, 4);
                if (!std::isfinite(*fields[i]) || std::fabs(*fields[i]) > 1e7f) return std::nullopt;
            }
            p.flags = body[fixed - 2];
            if (extra)
            {
                const auto bits = static_cast<std::uint32_t>(Get(body + fixed, 4));
                std::memcpy(&p.halfHeight, &bits, 4);
                if (!std::isfinite(p.halfHeight) || p.halfHeight < 0 || p.halfHeight > 10000) return std::nullopt;
            }
            p.scene.assign(reinterpret_cast<const char*>(body + fixed + extra), sceneLength);
            for (const char c : p.scene)
                if (static_cast<unsigned char>(c) < 0x20) return std::nullopt;
            break;
        }
        default: return std::nullopt;
        }
        return m;
    }
} // namespace bridge
