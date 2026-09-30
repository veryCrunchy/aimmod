#include "Probe.hpp"

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <fstream>
#include <string_view>
#include <thread>

#pragma comment(lib, "version.lib")

namespace probe
{
    namespace
    {
        std::string Trim(std::string_view text)
        {
            const auto first = text.find_first_not_of(" \t\r\n");
            if (first == std::string_view::npos) return {};
            const auto last = text.find_last_not_of(" \t\r\n");
            return std::string(text.substr(first, last - first + 1));
        }

        bool ParseBool(const std::string& value, bool fallback)
        {
            if (value == "1" || value == "true" || value == "yes" || value == "on") return true;
            if (value == "0" || value == "false" || value == "no" || value == "off") return false;
            return fallback;
        }

        int ParseInt(const std::string& value, int fallback, int low, int high)
        {
            try
            {
                return std::clamp(std::stoi(value), low, high);
            }
            catch (...)
            {
                return fallback;
            }
        }

        // Keeps Steam's debug strings printable and short.
        std::string Clean(const char* text, std::size_t max)
        {
            std::string out;
            for (std::size_t i = 0; i < max && text[i] != '\0'; ++i)
            {
                const char c = text[i];
                out.push_back(c >= 0x20 && c < 0x7f ? c : ' ');
            }
            return out.size() > 160 ? out.substr(0, 160) + "..." : out;
        }

        template <typename T> T Export(HMODULE module, const char* name)
        {
            return reinterpret_cast<T>(GetProcAddress(module, name));
        }

        struct InterfaceFamily
        {
            const char* name;
            const char* versions[4];
        };

        // Newest first. The game's own dll and exe reference SteamUser020,
        // SteamFriends017, SteamMatchMaking009, SteamNetworking006 and
        // SteamUtils009; newer versions come from the installed Steam client.
        constexpr InterfaceFamily Families[] = {
            {"SteamUser", {"SteamUser023", "SteamUser021", "SteamUser020", nullptr}},
            {"SteamFriends", {"SteamFriends018", "SteamFriends017", nullptr, nullptr}},
            {"SteamMatchMaking", {"SteamMatchMaking009", nullptr, nullptr, nullptr}},
            {"SteamUtils", {"SteamUtils010", "SteamUtils009", nullptr, nullptr}},
            {"SteamNetworking (legacy P2P)", {"SteamNetworking006", nullptr, nullptr, nullptr}},
            {"SteamNetworkingSockets", {"SteamNetworkingSockets012", "SteamNetworkingSockets009", "SteamNetworkingSockets008", nullptr}},
            {"SteamNetworkingMessages", {"SteamNetworkingMessages002", nullptr, nullptr, nullptr}},
            {"SteamNetworkingUtils", {"SteamNetworkingUtils004", "SteamNetworkingUtils003", nullptr, nullptr}},
        };

        void LogRelay(steamabi::ISteamNetworkingUtils* utils, const char* when, const LogFn& log)
        {
            steamabi::SteamRelayNetworkStatus_t status{};
            const auto avail = utils->GetRelayNetworkStatus(&status);
            char line[512];
            std::snprintf(line, sizeof(line), "relay (%s): avail=%s config=%s anyRelay=%s pinging=%d msg=\"%s\"", when,
                          steamabi::AvailabilityName(avail), steamabi::AvailabilityName(status.m_eAvailNetworkConfig),
                          steamabi::AvailabilityName(status.m_eAvailAnyRelay), status.m_bPingMeasurementInProgress,
                          Clean(status.m_debugMsg, sizeof(status.m_debugMsg)).c_str());
            log(line);
        }
    } // namespace

    Options LoadOptions(const std::filesystem::path& file, const LogFn& log)
    {
        Options options;
        std::ifstream in(file);
        if (!in)
        {
            log("config: none found, using defaults");
            return options;
        }
        std::string raw;
        while (std::getline(in, raw))
        {
            const auto line = Trim(raw);
            if (line.empty() || line[0] == '#' || line[0] == ';') continue;
            const auto eq = line.find('=');
            if (eq == std::string::npos) continue;
            const auto key = Trim(std::string_view(line).substr(0, eq));
            const auto value = Trim(std::string_view(line).substr(eq + 1));
            if (key == "relay_warmup") options.relayWarmup = ParseBool(value, options.relayWarmup);
            else if (key == "relay_wait_seconds") options.relayWaitSeconds = ParseInt(value, options.relayWaitSeconds, 1, 120);
            else if (key == "observe_callbacks") options.observeCallbacks = ParseBool(value, options.observeCallbacks);
            else if (key == "observe_seconds") options.observeSeconds = ParseInt(value, options.observeSeconds, 1, 600);
            else if (key == "stage2_loopback") options.stage2Loopback = ParseBool(value, options.stage2Loopback);
            else if (key == "stage3_role") options.stage3Role = (value == "listen" || value == "connect") ? value : std::string();
            else if (key == "stage3_match") options.stage3Match = value;
            else if (key == "stage3_seconds") options.stage3Seconds = ParseInt(value, options.stage3Seconds, 10, 600);
            else if (key == "stage3_peer")
            {
                try
                {
                    options.stage3Peer = value.empty() ? 0 : std::stoull(value);
                }
                catch (...)
                {
                    options.stage3Peer = 0;
                }
            }
        }
        char summary[200];
        std::snprintf(summary, sizeof(summary), "config: relay_warmup=%d observe_callbacks=%d stage2_loopback=%d stage3_role=%s",
                      options.relayWarmup, options.observeCallbacks, options.stage2Loopback,
                      options.stage3Role.empty() ? "off" : options.stage3Role.c_str());
        log(summary);
        return options;
    }

    bool SteamApi::Resolve(HMODULE steamApi)
    {
        module = steamApi;
        if (!module) return false;
        IsSteamRunning = Export<steamabi::PFN_SteamAPI_IsSteamRunning>(module, "SteamAPI_IsSteamRunning");
        GetHSteamUser = Export<steamabi::PFN_SteamAPI_GetHSteamUser>(module, "SteamAPI_GetHSteamUser");
        GetHSteamPipe = Export<steamabi::PFN_SteamAPI_GetHSteamPipe>(module, "SteamAPI_GetHSteamPipe");
        FindOrCreateUserInterface =
            Export<steamabi::PFN_SteamInternal_FindOrCreateUserInterface>(module, "SteamInternal_FindOrCreateUserInterface");
        RegisterCallback = Export<steamabi::PFN_SteamAPI_RegisterCallback>(module, "SteamAPI_RegisterCallback");
        UnregisterCallback = Export<steamabi::PFN_SteamAPI_UnregisterCallback>(module, "SteamAPI_UnregisterCallback");
        User_GetSteamID = Export<steamabi::PFN_ISteamUser_GetSteamID>(module, "SteamAPI_ISteamUser_GetSteamID");
        User_BLoggedOn = Export<steamabi::PFN_ISteamUser_BLoggedOn>(module, "SteamAPI_ISteamUser_BLoggedOn");
        Utils_GetAppID = Export<steamabi::PFN_ISteamUtils_GetAppID>(module, "SteamAPI_ISteamUtils_GetAppID");
        return GetHSteamUser && GetHSteamPipe && FindOrCreateUserInterface;
    }

    bool SteamApi::Initialised() const
    {
        return GetHSteamUser && GetHSteamPipe && GetHSteamUser() != 0 && GetHSteamPipe() != 0;
    }

    void* SteamApi::Interface(const char* version) const
    {
        if (!Initialised()) return nullptr;
        return FindOrCreateUserInterface(GetHSteamUser(), version);
    }

    HMODULE FindLoadedSteamApi()
    {
        return GetModuleHandleW(L"steam_api64.dll");
    }

    std::string RedactSteamId(std::uint64_t steamId)
    {
        const auto digits = std::to_string(steamId);
        const auto tail = digits.size() > 4 ? digits.substr(digits.size() - 4) : digits;
        const unsigned universe = static_cast<unsigned>(steamId >> 56);
        const unsigned type = static_cast<unsigned>((steamId >> 52) & 0xF);
        char out[96];
        std::snprintf(out, sizeof(out), "...%s (type=%s universe=%s)", tail.c_str(), type == 1 ? "individual" : "other",
                      universe == 1 ? "public" : "other");
        return out;
    }

    std::string FileVersion(HMODULE module)
    {
        wchar_t path[MAX_PATH * 2]{};
        if (!GetModuleFileNameW(module, path, static_cast<DWORD>(std::size(path)))) return "?";
        DWORD ignored = 0;
        const DWORD size = GetFileVersionInfoSizeW(path, &ignored);
        if (size == 0) return "?";
        std::string buffer(size, '\0');
        if (!GetFileVersionInfoW(path, 0, size, buffer.data())) return "?";
        VS_FIXEDFILEINFO* info = nullptr;
        UINT length = 0;
        if (!VerQueryValueW(buffer.data(), L"\\", reinterpret_cast<void**>(&info), &length) || !info) return "?";
        char out[64];
        std::snprintf(out, sizeof(out), "%u.%u.%u.%u", HIWORD(info->dwFileVersionMS), LOWORD(info->dwFileVersionMS),
                      HIWORD(info->dwFileVersionLS), LOWORD(info->dwFileVersionLS));
        return out;
    }

    bool RunStage1(const SteamApi& api, const Options& options, const LogFn& log)
    {
        char line[512];
        log("stage 1 (read-only): begin");
        std::snprintf(line, sizeof(line), "steam_api64.dll: loaded, file version %s, IsSteamRunning=%d", FileVersion(api.module).c_str(),
                      api.IsSteamRunning ? static_cast<int>(api.IsSteamRunning()) : -1);
        log(line);

        const bool modern = GetProcAddress(api.module, "SteamAPI_SteamMatchmaking_v009") != nullptr;
        const bool manualDispatch = GetProcAddress(api.module, "SteamAPI_ManualDispatch_Init") != nullptr;
        const bool flatSockets = GetProcAddress(api.module, "SteamAPI_ISteamNetworkingSockets_CreateSocketPair") != nullptr;
        std::snprintf(line, sizeof(line), "flat api: versioned accessors=%s manual dispatch=%s networking-sockets flat=%s", modern ? "yes" : "no",
                      manualDispatch ? "yes" : "no", flatSockets ? "yes" : "no");
        log(line);

        if (!api.Initialised())
        {
            log("SteamAPI: NOT initialised in this process (HSteamUser or HSteamPipe is 0)");
            return false;
        }
        std::snprintf(line, sizeof(line), "SteamAPI: initialised (user handle %d, pipe %d)", api.GetHSteamUser(), api.GetHSteamPipe());
        log(line);

        for (const auto& family : Families)
        {
            std::string entry = std::string(family.name) + ":";
            for (const char* version : family.versions)
            {
                if (!version) break;
                entry += " ";
                entry += version;
                entry += api.Interface(version) ? "=yes" : "=no";
            }
            log(entry);
        }

        if (auto* user = api.Interface("SteamUser020"); user && api.User_GetSteamID && api.User_BLoggedOn)
        {
            const auto self = reinterpret_cast<std::intptr_t>(user);
            std::snprintf(line, sizeof(line), "user: logged on=%d steamid=%s", static_cast<int>(api.User_BLoggedOn(self)),
                          RedactSteamId(api.User_GetSteamID(self)).c_str());
            log(line);
        }
        else log("user: SteamUser020 or its flat exports unavailable");

        if (auto* utils = api.Interface("SteamUtils009"); utils && api.Utils_GetAppID)
        {
            std::snprintf(line, sizeof(line), "app: AppID=%u", api.Utils_GetAppID(reinterpret_cast<std::intptr_t>(utils)));
            log(line);
        }

        auto* netUtils = static_cast<steamabi::ISteamNetworkingUtils*>(api.Interface("SteamNetworkingUtils004"));
        if (!netUtils)
        {
            log("relay: SteamNetworkingUtils004 unavailable");
            log("stage 1: done");
            return true;
        }
        // GetLocalTimestamp is a harmless call that sanity-checks the vtable
        // layout before anything else relies on it.
        const auto timestamp = netUtils->GetLocalTimestamp();
        std::snprintf(line, sizeof(line), "networking utils: vtable check %s", timestamp > 0 ? "ok" : "unexpected");
        log(line);
        LogRelay(netUtils, "initial", log);

        if (auto* sockets = static_cast<steamabi::ISteamNetworkingSockets*>(api.Interface("SteamNetworkingSockets012")))
        {
            steamabi::SteamNetAuthenticationStatus_t auth{};
            const auto avail = sockets->GetAuthenticationStatus(&auth);
            std::snprintf(line, sizeof(line), "p2p identity cert: %s \"%s\"", steamabi::AvailabilityName(avail),
                          Clean(auth.m_debugMsg, sizeof(auth.m_debugMsg)).c_str());
            log(line);
        }

        if (options.relayWarmup)
        {
            log("relay: warm-up requested (InitRelayNetworkAccess; contacts Valve relays only)");
            netUtils->CheckPingDataUpToDate(1e10f);
            const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(options.relayWaitSeconds);
            steamabi::SteamRelayNetworkStatus_t status{};
            while (std::chrono::steady_clock::now() < deadline)
            {
                if (netUtils->GetRelayNetworkStatus(&status) == 100) break;
                std::this_thread::sleep_for(std::chrono::milliseconds(500));
            }
            LogRelay(netUtils, "after warm-up", log);
            std::snprintf(line, sizeof(line), "relay: %d points of presence known", netUtils->GetPOPCount());
            log(line);
        }
        else log("relay: warm-up disabled (relay_warmup=0); status above is what the host already set up");

        log("stage 1: done");
        return true;
    }

    double ElapsedMs(std::chrono::steady_clock::time_point start)
    {
        return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
    }

    bool WaitForRelayAndCert(const SteamApi& api, int timeoutSeconds, const LogFn& log)
    {
        auto* utils = static_cast<steamabi::ISteamNetworkingUtils*>(api.Interface("SteamNetworkingUtils004"));
        auto* sockets = static_cast<steamabi::ISteamNetworkingSockets*>(api.Interface("SteamNetworkingSockets012"));
        if (!utils || !sockets)
        {
            log("readiness: networking interfaces unavailable");
            return false;
        }
        const auto start = std::chrono::steady_clock::now();
        utils->CheckPingDataUpToDate(1e10f); // InitRelayNetworkAccess
        sockets->InitAuthentication();       // idempotent; requests the cert if missing
        double relayAt = -1, certAt = -1;
        steamabi::SteamRelayNetworkStatus_t relay{};
        steamabi::SteamNetAuthenticationStatus_t cert{};
        const auto deadline = start + std::chrono::seconds(timeoutSeconds);
        while (std::chrono::steady_clock::now() < deadline && (relayAt < 0 || certAt < 0))
        {
            if (relayAt < 0 && utils->GetRelayNetworkStatus(&relay) == 100) relayAt = ElapsedMs(start);
            if (certAt < 0 && sockets->GetAuthenticationStatus(&cert) == 100) certAt = ElapsedMs(start);
            if (relayAt < 0 || certAt < 0) std::this_thread::sleep_for(std::chrono::milliseconds(100));
        }
        const auto relayNow = utils->GetRelayNetworkStatus(&relay);
        const auto certNow = sockets->GetAuthenticationStatus(&cert);
        char line[512];
        std::snprintf(line, sizeof(line), "readiness: relay=%s after %.0f ms (\"%s\")", steamabi::AvailabilityName(relayNow), relayAt,
                      Clean(relay.m_debugMsg, sizeof(relay.m_debugMsg)).c_str());
        log(line);
        std::snprintf(line, sizeof(line), "readiness: p2p cert=%s after %.0f ms (\"%s\")", steamabi::AvailabilityName(certNow), certAt,
                      Clean(cert.m_debugMsg, sizeof(cert.m_debugMsg)).c_str());
        log(line);
        return relayAt >= 0 && certAt >= 0;
    }

    // --- passive callback listeners -------------------------------------

    class CallbackObserver::Listener final : public steamabi::CCallbackBase
    {
    public:
        Listener(int id, int size, const char* name) : m_size(size), m_name(name) { m_iCallback = id; }
        void Run(void*) override
        {
            DWORD expected = 0;
            m_firstThread.compare_exchange_strong(expected, GetCurrentThreadId());
            m_count.fetch_add(1);
        }
        void Run(void* param, bool, steamabi::SteamAPICall_t) override { Run(param); }
        int GetCallbackSizeBytes() override { return m_size; }

        int Id() const { return m_iCallback; }
        const char* Name() const { return m_name; }
        long Count() const { return m_count.load(); }
        DWORD Thread() const { return m_firstThread.load(); }

    private:
        int m_size;
        const char* m_name;
        std::atomic<long> m_count{0};
        std::atomic<DWORD> m_firstThread{0};
    };

    CallbackObserver::CallbackObserver(const SteamApi& api) : m_api(api)
    {
        m_listeners[0] = new Listener(steamabi::k_iPersonaStateChange, sizeof(steamabi::PersonaStateChange_t), "PersonaStateChange");
        m_listeners[1] =
            new Listener(steamabi::k_iSteamRelayNetworkStatus, sizeof(steamabi::SteamRelayNetworkStatus_t), "SteamRelayNetworkStatus");
        // Join requests: counted only, to show that the game's own handlers
        // and ours both receive every broadcast (see the design doc).
        m_listeners[2] = new Listener(steamabi::k_iGameLobbyJoinRequested, 16, "GameLobbyJoinRequested");
        m_listeners[3] = new Listener(steamabi::k_iGameRichPresenceJoinRequested, 264, "GameRichPresenceJoinRequested");
    }

    CallbackObserver::~CallbackObserver()
    {
        Unregister();
        // Listeners are intentionally leaked if they were ever registered and
        // could not be unregistered; freeing memory steam_api may still point
        // at would be worse than a few bytes.
        if (!m_registered)
            for (auto*& listener : m_listeners)
            {
                delete listener;
                listener = nullptr;
            }
    }

    void CallbackObserver::Register()
    {
        if (m_registered || !m_api.RegisterCallback || !m_api.UnregisterCallback) return;
        for (auto* listener : m_listeners) m_api.RegisterCallback(listener, listener->Id());
        m_registered = true;
    }

    void CallbackObserver::Unregister()
    {
        if (!m_registered || !m_api.UnregisterCallback) return;
        for (auto* listener : m_listeners) m_api.UnregisterCallback(listener);
        m_registered = false;
    }

    void CallbackObserver::Report(const LogFn& log) const
    {
        const DWORD game = m_gameThread.load();
        for (const auto* listener : m_listeners)
        {
            char line[256];
            const DWORD thread = listener->Thread();
            const char* where = thread == 0 ? "not dispatched yet" : game == 0 ? "unknown thread" : thread == game ? "game thread" : "another thread";
            std::snprintf(line, sizeof(line), "callbacks: %s x%ld, first dispatch on %s", listener->Name(), listener->Count(), where);
            log(line);
        }
    }
} // namespace probe
