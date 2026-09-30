#pragma once
// AimMod Steam probe: read-only checks of the Steam API the host process has
// already initialised. See in-game/docs/multiplayer.md.

#include "SteamAbi.hpp"

#include <Windows.h>

#include <atomic>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <string>

namespace probe
{
    using LogFn = std::function<void(const std::string&)>;

    struct Options
    {
        // Stage 1: calls InitRelayNetworkAccess (CheckPingDataUpToDate) so
        // the Steam client fetches the SDR config and pings Valve relays.
        // Sends nothing to any player. Off by default.
        bool relayWarmup = false;
        int relayWaitSeconds = 20;
        // Stage 1: register passive callback listeners (never consumes a
        // callback; steam_api broadcasts to every listener) to see which
        // thread runs SteamAPI_RunCallbacks.
        bool observeCallbacks = true;
        int observeSeconds = 30;
        // Stage 2 (creates a private, unlisted lobby and loops a message back to
        // ourselves). Needs both the AIMMOD_PROBE_STAGE2 build option and
        // this runtime flag. Off by default.
        bool stage2Loopback = false;
        // Stage 3 (relay-only P2P test between two accounts). Needs the
        // AIMMOD_PROBE_STAGE3 build option and stage3_role=listen|connect.
        std::string stage3Role;       // "", "listen" or "connect"
        std::uint64_t stage3Peer = 0; // the other account's SteamID64 (local config only)
        std::string stage3Match;      // shared token, hashed into the handshake
        int stage3Seconds = 120;
    };

    // AimMod's own SteamNetworkingSockets P2P virtual port ("AM").
    constexpr int AimModVirtualPort = 0x414D;

    // Runs a function on the host's game thread and waits for it. The mod
    // drains it from the engine tick; the harness runs it directly.
    using GameThreadFn = std::function<void(const std::function<void()>&)>;

    // Reads key=value lines (relay_warmup, relay_wait_seconds,
    // observe_callbacks, observe_seconds, stage2_loopback, stage3_role,
    // stage3_peer, stage3_match, stage3_seconds). Missing file or keys keep
    // the defaults.
    Options LoadOptions(const std::filesystem::path& file, const LogFn& log);

    struct SteamApi
    {
        HMODULE module = nullptr;
        steamabi::PFN_SteamAPI_IsSteamRunning IsSteamRunning = nullptr;
        steamabi::PFN_SteamAPI_GetHSteamUser GetHSteamUser = nullptr;
        steamabi::PFN_SteamAPI_GetHSteamPipe GetHSteamPipe = nullptr;
        steamabi::PFN_SteamInternal_FindOrCreateUserInterface FindOrCreateUserInterface = nullptr;
        steamabi::PFN_SteamAPI_RegisterCallback RegisterCallback = nullptr;
        steamabi::PFN_SteamAPI_UnregisterCallback UnregisterCallback = nullptr;
        steamabi::PFN_ISteamUser_GetSteamID User_GetSteamID = nullptr;
        steamabi::PFN_ISteamUser_BLoggedOn User_BLoggedOn = nullptr;
        steamabi::PFN_ISteamUtils_GetAppID Utils_GetAppID = nullptr;

        // Resolves exports from an already-loaded module. Never loads it.
        bool Resolve(HMODULE steamApi);
        // True once the host has called SteamAPI_Init successfully.
        bool Initialised() const;
        void* Interface(const char* version) const;
    };

    // Returns the in-process steam_api64.dll, or nullptr. Never loads it.
    HMODULE FindLoadedSteamApi();

    // Stage 1. Read-only: queries state and interface availability only.
    bool RunStage1(const SteamApi& api, const Options& options, const LogFn& log);

    // Stage 2. See Stage2Loopback.cpp. Logs and returns false when the build
    // or the runtime flag does not enable it.
    bool RunStage2(const SteamApi& api, const Options& options, const LogFn& log);

    // Stage 3. See Stage3P2P.cpp. Same gating as stage 2 with its own build
    // option (AIMMOD_PROBE_STAGE3) and stage3_role.
    struct Stage3Result
    {
        bool ran = false;
        bool pass = false;
        bool connected = false;
        double connectMs = -1;
        bool relayed = false;
        bool authenticated = false; // authenticated and encrypted by Steam
        bool peerMatch = false;
        bool handshake = false;
        std::string pop;            // relay data-centre code, e.g. "fra"
        int pongs = 0;              // connector: pongs received of 20
        int echoed = 0;             // listener: pings echoed
        bool bye = false;           // listener: Bye received
        double rttMinMs = -1, rttMedianMs = -1, rttMaxMs = -1;
        std::string failure;        // first failure reason, empty on pass
    };

    bool RunStage3(const SteamApi& api, const Options& options, const LogFn& log, const GameThreadFn& onGameThread,
                   Stage3Result* result = nullptr);

    // Steam invite test (harness, AIMMOD_PROBE_STAGE3 builds). See InviteTest.cpp.
    struct InviteOptions
    {
        std::string role;    // "send" or "receive"
        std::string variant; // sender: "rp" (rich presence + InviteUserToGame) or "lobby"
        std::uint64_t peer = 0;
        std::string code;
        int seconds = 180;
    };
    struct InviteResult
    {
        std::string role, variant;
        bool sent = false;            // sender: invite call succeeded
        bool received = false;        // receiver: join request arrived
        bool joined = false;          // lobby variant: peer joined / we joined
        bool sawRichPresence = false; // receiver: sender's AimMod connect key visible
        bool pass = false;
        std::string failure;
    };
    bool RunInviteTest(const SteamApi& api, const InviteOptions& options, const LogFn& log, const GameThreadFn& onGameThread,
                       InviteResult* result = nullptr);

    // Waits (up to timeoutSeconds) for the relay network and this account's
    // P2P certificate to be ready, logging both with elapsed times. Asks the
    // Steam client to fetch them first (contacts Valve only).
    bool WaitForRelayAndCert(const SteamApi& api, int timeoutSeconds, const LogFn& log);

    // Milliseconds since `start`, for timing logs.
    double ElapsedMs(std::chrono::steady_clock::time_point start);

    // Passive listeners. Construct, Register() on the thread that owns the
    // host's Steam usage (the game thread), Report() later, and always
    // Unregister() before destruction.
    class CallbackObserver
    {
    public:
        explicit CallbackObserver(const SteamApi& api);
        ~CallbackObserver();
        CallbackObserver(const CallbackObserver&) = delete;
        CallbackObserver& operator=(const CallbackObserver&) = delete;

        void Register();
        void Unregister();
        void SetGameThread(DWORD threadId) { m_gameThread.store(threadId); }
        void Report(const LogFn& log) const;

    private:
        class Listener;
        const SteamApi& m_api;
        Listener* m_listeners[4]{};
        bool m_registered = false;
        std::atomic<DWORD> m_gameThread{0};
    };

    // SteamID64 -> "...1234" plus its account type and universe, for logs.
    std::string RedactSteamId(std::uint64_t steamId);
    std::string FileVersion(HMODULE module);
} // namespace probe
