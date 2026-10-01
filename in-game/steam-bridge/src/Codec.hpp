#pragma once
// Pure helpers shared by the bridge and its tests: base64, the AMP1 P2P wire
// format between AimModSteam instances, join-string parsing and validation.

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace bridge
{
    // Versions. The pipe contract and the P2P wire format move independently.
    constexpr int ContractVersion = 1;
    constexpr std::uint8_t WireVersion = 1;
    constexpr int JoinStringVersion = 1;

    constexpr std::size_t MaxPipeFrame = 64 * 1024; // one JSON message on the pipe
    constexpr std::size_t MaxPayload = 16 * 1024;   // one service frame over P2P (matches the service's Protocol.MaxBytes)
    constexpr int AimModVirtualPort = 0x414D;

    std::string Base64Encode(const std::uint8_t* data, std::size_t size);
    std::optional<std::vector<std::uint8_t>> Base64Decode(std::string_view text, std::size_t maxBytes);

    // SteamID64 / lobby id as decimal string <-> uint64. Rejects anything that
    // is not 1..20 digits or does not fit.
    std::optional<std::uint64_t> ParseId(std::string_view text);
    bool IsIndividualId(std::uint64_t id); // public universe, individual account
    bool IsLobbyId(std::uint64_t id);      // public universe, chat/lobby account
    std::string Redact(std::uint64_t id);  // "...1234"

    // Lobby data keys the service may set: "aimmod." + [a-z0-9._-]{1,32}.
    bool ValidLobbyKey(std::string_view key);
    // Tournament match tokens from the Hub: [A-Za-z0-9_-]{8,64}.
    bool ValidMatchToken(std::string_view token);
    // KovaaK's character profile names (dev.avatar): letters, digits, space and _ - . ( ) ', 1..64, no edge spaces.
    bool ValidProfileName(std::string_view name);
    bool SameToken(std::string_view a, std::string_view b); // constant time for equal lengths
    constexpr std::size_t MaxLobbyValue = 256;
    constexpr std::size_t MaxServiceLobbyKeys = 24;

    // Join strings: rich presence connect / launch switch value
    // "aimmod:<version>:<lobby id>".
    std::string JoinString(std::uint64_t lobby);        // "aimmod:1:<id>"
    std::string ConnectString(std::uint64_t lobby);     // "-aimmodjoin=aimmod:1:<id>"
    struct JoinTarget
    {
        std::uint64_t lobby = 0;
        int version = 0;
    };
    std::optional<JoinTarget> ParseJoinString(std::string_view value); // "aimmod:<v>:<id>" or with "-aimmodjoin=" prefix
    // Scans a process command line for "-aimmodjoin=<join string>" or
    // "+connect_lobby <id>". Returns the first valid target.
    struct LaunchJoin
    {
        JoinTarget target;
        std::string source; // "launch-aimmodjoin" or "launch-connect-lobby"
    };
    std::optional<LaunchJoin> ParseLaunchCommandLine(std::wstring_view commandLine);

    // AMP1 wire format. Header: 'A' 'M' 'P' '1', version, type, 2 reserved bytes.
    enum class WireType : std::uint8_t
    {
        Hello = 1,   // u64 lobby, u64 token
        Welcome = 2, // u64 lobby
        Reject = 3,  // u16 code
        Data = 4,    // payload (1..MaxPayload bytes, opaque service frame)
        Ping = 5,    // u32 seq, i64 local time (us)
        Pong = 6,    // u32 seq, i64 echoed time
        Kick = 7,    // u64 lobby
        Bye = 8,     // no body
        Pose = 9,    // ghost demo: u64 origin, u32 seq, 8 x f32 (x y z yaw pitch vx vy vz), u8 flags, u8 n, [f32 half-height], n bytes scene
        Chunk = 10,  // bulk: u32 transfer, u32 index, 1..MaxChunk bytes (low-priority lane)
        ChunkAck = 11, // bulk: u32 transfer, u32 index
        Cancel = 12, // bulk: u32 transfer, u16 reason
        SpectateSub = 13, // spectate: u64 target, u8 rate Hz (0 = stop)
        Camera = 14,      // spectate: u64 origin, u32 seq, i64 unix ms (sender clock), 7 x f32 (x y z pitch yaw roll fov), u8 flags
        CameraMeta = 15,  // spectate: u64 origin, f32 map scale, u8 n, scenario, u8 m, map name
        SpectateHello = 16,  // lobby-less spectate request: u8 rate Hz (1..60)
        SpectateAccept = 17, // no body
        TournamentHello = 19, // tournament lobby join: u64 lobby, u64 lobby token, u8 n, n bytes Hub match token
        Score = 18,          // live score: u64 origin, u8 flags (1 active, 2 paused), 3 x f32 (score seconds remaining), 3 x u32 (shots hits kills); -1 / 0xFFFFFFFF = unknown
    };

    struct ScoreFrame
    {
        std::uint64_t origin = 0;
        std::uint8_t flags = 0;
        float score = -1, seconds = -1, remaining = -1;
        std::uint32_t shots = 0xFFFFFFFFu, hits = 0xFFFFFFFFu, kills = 0xFFFFFFFFu;
    };
    constexpr std::size_t MaxDirectSpectators = 8;

    constexpr int MaxSpectateRate = 60;
    struct CameraFrame
    {
        std::uint64_t origin = 0;
        std::uint32_t seq = 0;
        std::int64_t ms = 0; // unix ms on the sender's clock
        float x = 0, y = 0, z = 0, pitch = 0, yaw = 0, roll = 0, fov = 90;
        std::uint8_t flags = 0; // bit 0: fired since the last frame
    };

    // Bulk transfers (host-to-joiner file streaming). A chunk's base64 fits a
    // 64 KiB pipe frame; Steam's reliable message limit is 512 KiB.
    constexpr std::size_t MaxChunk = 32 * 1024;
    constexpr std::size_t XferWindow = 4;          // unacknowledged chunks per transfer
    constexpr std::size_t MaxTransfersPerPeer = 4;
    enum class RejectCode : std::uint16_t
    {
        NotMember = 1,
        BadToken = 2,
        Banned = 3,
        Version = 4,
        NotHost = 5,
        SpectateOff = 6,  // the target doesn't allow spectating
        SpectateFull = 7, // too many spectators
        NotFriend = 8,    // only Steam friends may spectate
        Declined = 9,     // the target said no (or didn't answer)
        NotEntrant = 10,  // tournament lobby: not the expected entrant, or a wrong match token
    };
    constexpr std::size_t WireHeader = 8;

    constexpr std::size_t MaxPoseScene = 96;
    constexpr std::uint8_t PoseFlagCrouch = 1;     // the sender is crouching
    constexpr std::uint8_t PoseFlagHalfHeight = 2; // a f32 capsule half-height follows n (frames without it stay valid)
    struct Pose
    {
        std::uint64_t origin = 0;
        std::uint32_t seq = 0;
        float x = 0, y = 0, z = 0, yaw = 0, pitch = 0, vx = 0, vy = 0, vz = 0;
        std::uint8_t flags = 0;
        float halfHeight = 0; // sender's current capsule half-height (with PoseFlagHalfHeight)
        std::string scene; // scenario name (<= MaxPoseScene bytes)
    };

    struct WireMessage
    {
        WireType type{};
        std::uint64_t lobby = 0;
        std::uint64_t token = 0;
        std::uint16_t code = 0;
        std::uint32_t seq = 0;
        std::int64_t time = 0;
        std::vector<std::uint8_t> payload; // Data and Chunk bytes
        std::uint32_t transfer = 0, index = 0; // Chunk / ChunkAck / Cancel (code = reason)
        Pose pose;
        CameraFrame camera;
        ScoreFrame score;
        std::string matchToken; // TournamentHello
        std::string scenario, map; // CameraMeta (<= MaxPoseScene each); origin in lobby, scale in camera.fov
        std::uint8_t rate = 0; // SpectateSub (lobby/target reuse: lobby = target)
    };
    std::vector<std::uint8_t> Encode(const WireMessage& message);
    // Strict: exact sizes per type, version match, payload bounds.
    std::optional<WireMessage> Decode(const std::uint8_t* data, std::size_t size);
    // Lobby-less spectating: what the watched player's bridge does with a request.
    enum class SpectatePrivacy { Off, Friends, Ask };
    std::optional<SpectatePrivacy> ParseSpectatePrivacy(std::string_view text); // off | friends | ask
    const char* SpectatePrivacyName(SpectatePrivacy p);
    struct SpectateDecision
    {
        enum class Kind { Accept, Ask, Reject } kind;
        RejectCode reason = RejectCode::Declined;
    };
    SpectateDecision DecideSpectate(SpectatePrivacy privacy, bool isFriend, std::size_t current);
    // Workshop query filter: the item's comma-separated tag list must contain
    // 	ag (if given) and its title must contain 	ext (if given); both ignore case.
    bool UgcMatches(std::string_view title, std::string_view tags, std::string_view tag, std::string_view text);
} // namespace bridge
