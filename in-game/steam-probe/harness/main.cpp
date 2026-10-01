// Standalone harness for the Steam probe. Unlike the in-game mod, it owns the
// Steam API itself: it loads steam_api64.dll, calls SteamAPI_Init under the
// AppID (824270 by default), pumps callbacks on its own thread, runs the
// probe stages and shuts Steam down. Use it without starting the game. Steam
// shows the account as playing the AppID while it runs, and SteamAPI_Init
// only succeeds when the account owns the AppID.
//
//   aimmod_steam_probe_harness [--steam-api <path\to\steam_api64.dll>]
//       [--app-id 824270] [--print-steamid] [--relay-warmup] [--observe-seconds 10]
//       [--stage2-loopback]   (only honoured by an AIMMOD_PROBE_STAGE2 build)
//       [--stage3-role listen|connect --stage3-peer <SteamID64> --stage3-match <code> [--stage3-seconds 300]]
//                             (only honoured by an AIMMOD_PROBE_STAGE3 build)
//       [--invite-test send|receive [--invite-variant rp|lobby] --stage3-peer <SteamID64> --stage3-match <code> [--invite-seconds 180]]
//
// Without --steam-api it uses steam_api64.dll next to the exe, or else the
// copy inside the local KovaaK's install (found through the Steam library
// folders). The dll is only loaded, never copied or modified.
#include "Probe.hpp"

#include <Windows.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <deque>
#include <filesystem>
#include <fstream>
#include <functional>
#include <mutex>
#include <regex>
#include <string>
#include <thread>
#include <vector>

namespace
{
    void Print(const std::string& line)
    {
        std::printf("[AimModSteamProbe] %s\n", line.c_str());
        std::fflush(stdout);
    }

    std::filesystem::path ExeDirectory()
    {
        wchar_t path[MAX_PATH * 4]{};
        const DWORD length = GetModuleFileNameW(nullptr, path, static_cast<DWORD>(std::size(path)));
        if (length == 0 || length >= std::size(path)) return {};
        return std::filesystem::path(path).parent_path();
    }

    std::wstring RegString(HKEY root, const wchar_t* key, const wchar_t* name, DWORD flags = 0)
    {
        wchar_t value[MAX_PATH * 2]{};
        DWORD size = sizeof(value);
        if (RegGetValueW(root, key, name, RRF_RT_REG_SZ | flags, nullptr, value, &size) != ERROR_SUCCESS) return {};
        return value;
    }

    std::string ReadText(const std::filesystem::path& file)
    {
        std::ifstream in(file, std::ios::binary);
        std::string text((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
        return text.size() > 4 * 1024 * 1024 ? std::string() : text;
    }

    std::string Unescape(const std::string& p)
    {
        std::string out;
        for (std::size_t i = 0; i < p.size(); ++i)
        {
            if (p[i] == '\\' && i + 1 < p.size() && p[i + 1] == '\\') ++i;
            out.push_back(p[i]);
        }
        return out;
    }

    std::filesystem::path Utf8Path(const std::string& utf8)
    {
        const int n = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
        std::wstring w(static_cast<std::size_t>(n > 0 ? n : 0), L'\0');
        if (n > 0) MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), w.data(), n);
        return w;
    }

    // Finds the game's steam_api64.dll: Steam roots from the registry, every
    // library in libraryfolders.vdf (old "1" "path" and new "path" formats),
    // appmanifest_824270.acf installdir, then the known folder name.
    std::filesystem::path FindGameSteamApi(std::vector<std::wstring>& tried)
    {
        std::vector<std::filesystem::path> roots;
        auto addRoot = [&](const std::filesystem::path& p) {
            if (p.empty()) return;
            std::error_code e;
            auto norm = std::filesystem::weakly_canonical(p, e);
            if (e) norm = p;
            for (const auto& r : roots)
                if (_wcsicmp(r.c_str(), norm.c_str()) == 0) return;
            roots.push_back(norm);
        };
        addRoot(RegString(HKEY_CURRENT_USER, L"Software\\Valve\\Steam", L"SteamPath"));
        addRoot(RegString(HKEY_LOCAL_MACHINE, L"SOFTWARE\\WOW6432Node\\Valve\\Steam", L"InstallPath"));
        addRoot(RegString(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Valve\\Steam", L"InstallPath"));
        addRoot(L"C:\\Program Files (x86)\\Steam");

        std::vector<std::filesystem::path> libraries;
        auto addLibrary = [&](const std::filesystem::path& p) {
            for (const auto& l : libraries)
                if (_wcsicmp(l.c_str(), p.c_str()) == 0) return;
            libraries.push_back(p);
        };
        const std::regex entry(R"re("(path|\d+)"\s+"([^"]+)")re");
        const auto rootCount = roots.size();
        for (std::size_t r = 0; r < rootCount; ++r)
        {
            addLibrary(roots[r]);
            for (const auto* vdfName : {L"steamapps\\libraryfolders.vdf", L"config\\libraryfolders.vdf"})
            {
                const std::string text = ReadText(roots[r] / vdfName);
                for (auto it = std::sregex_iterator(text.begin(), text.end(), entry); it != std::sregex_iterator(); ++it)
                {
                    const std::string value = Unescape((*it)[2].str());
                    // Old format: "1" "D:\\Games\\Steam"; new: "path" "...". Skip numeric values like sizes.
                    if (value.find(':') == std::string::npos && value.rfind("\\\\", 0) != 0) continue;
                    addLibrary(Utf8Path(value));
                }
            }
        }
        for (const auto& library : libraries)
        {
            std::vector<std::wstring> folders{L"FPSAimTrainer"};
            const std::string acf = ReadText(library / "steamapps" / "appmanifest_824270.acf");
            std::smatch m;
            const std::regex installdir(R"re("installdir"\s+"([^"]+)")re");
            if (std::regex_search(acf, m, installdir))
            {
                const auto dir = Utf8Path(Unescape(m[1].str())).wstring();
                if (_wcsicmp(dir.c_str(), L"FPSAimTrainer") != 0) folders.insert(folders.begin(), dir);
            }
            for (const auto& folder : folders)
            {
                const auto candidate = library / "steamapps" / "common" / folder / "FPSAimTrainer" / "Binaries" / "Win64" / "steam_api64.dll";
                tried.push_back(candidate.wstring());
                std::error_code error;
                if (std::filesystem::is_regular_file(candidate, error)) return candidate;
            }
        }
        return {};
    }
    std::string YesNo(bool value) { return value ? "yes" : "no"; }

    // Short, shareable result. No SteamIDs and no addresses.
    std::string Summary(const probe::Options& options, const probe::Stage3Result& r)
    {
        char buffer[1024];
        const bool listen = options.stage3Role == "listen";
        char rtt[128];
        if (listen) std::snprintf(rtt, sizeof(rtt), "n/a on the listen side (echoed %d pings, bye=%s)", r.echoed, r.bye ? "yes" : "no");
        else if (r.pongs > 0) std::snprintf(rtt, sizeof(rtt), "min %.1f / median %.1f / max %.1f ms (%d/20 pongs)", r.rttMinMs, r.rttMedianMs, r.rttMaxMs, r.pongs);
        else std::snprintf(rtt, sizeof(rtt), "none (0/20 pongs)");
        std::snprintf(buffer, sizeof(buffer),
                      "==== AimMod P2P test result ====\n"
                      "RESULT: %s\n"
                      "role: %s\n"
                      "connected: %s%s\n"
                      "relayed: %s\n"
                      "encrypted/authenticated: %s\n"
                      "peer identity check: %s\n"
                      "handshake: %s\n"
                      "relay POP: %s\n"
                      "RTT: %s\n"
                      "%s%s%s"
                      "================================\n",
                      r.pass ? "PASS" : "FAIL", options.stage3Role.c_str(), YesNo(r.connected).c_str(),
                      r.connected ? (" (after " + std::to_string(static_cast<long long>(r.connectMs)) + " ms)").c_str() : "",
                      YesNo(r.relayed).c_str(), YesNo(r.authenticated).c_str(), YesNo(r.peerMatch).c_str(), YesNo(r.handshake).c_str(),
                      r.pop.empty() ? "none" : r.pop.c_str(), rtt, r.failure.empty() ? "" : "reason: ", r.failure.c_str(),
                      r.failure.empty() ? "" : "\n");
        return buffer;
    }

    std::string InviteSummary(const probe::InviteResult& r)
    {
        std::string s = "==== AimMod invite test result ====\n";
        s += std::string("RESULT: ") + (r.pass ? "PASS" : "FAIL") + "\n";
        s += "role: " + r.role + "\n";
        s += "variant: " + (r.variant.empty() ? std::string("-") : r.variant) + "\n";
        if (r.role == "send") s += std::string("invite sent: ") + YesNo(r.sent) + "\n";
        else
        {
            s += std::string("join request received: ") + YesNo(r.received) + "\n";
            s += std::string("sender's AimMod rich presence visible: ") + YesNo(r.sawRichPresence) + "\n";
        }
        if (r.variant == "lobby") s += std::string("lobby joined: ") + YesNo(r.joined) + "\n";
        if (!r.failure.empty()) s += "reason: " + r.failure + "\n";
        s += "===================================\n";
        return s;
    }

    // Jobs that must run on the callback pump thread (the harness's "game
    // thread"), so registering listeners never races RunCallbacks.
    class PumpJobs
    {
    public:
        void Run(const std::function<void()>& fn)
        {
            if (GetCurrentThreadId() == m_pumpThread.load()) return fn();
            auto job = std::make_shared<Job>(Job{fn});
            std::unique_lock lock(m_mutex);
            if (!m_running)
            {
                lock.unlock();
                return fn();
            }
            m_jobs.push_back(job);
            m_done.wait(lock, [&] { return job->done; });
        }
        void Drain()
        {
            std::deque<std::shared_ptr<Job>> jobs;
            {
                std::lock_guard lock(m_mutex);
                jobs.swap(m_jobs);
            }
            for (auto& job : jobs)
            {
                job->fn();
                std::lock_guard lock(m_mutex);
                job->done = true;
            }
            if (!jobs.empty()) m_done.notify_all();
        }
        void Start(DWORD thread)
        {
            m_pumpThread = thread;
            std::lock_guard lock(m_mutex);
            m_running = true;
        }
        void Stop()
        {
            {
                std::lock_guard lock(m_mutex);
                m_running = false;
            }
            Drain();
        }

    private:
        struct Job
        {
            std::function<void()> fn;
            bool done = false;
        };
        std::mutex m_mutex;
        std::condition_variable m_done;
        std::deque<std::shared_ptr<Job>> m_jobs;
        std::atomic<DWORD> m_pumpThread{0};
        bool m_running = false;
    };
} // namespace

int wmain(int argc, wchar_t** argv)
{
    std::wstring dll;
    std::wstring appId = L"824270";
    probe::Options options;
    options.observeSeconds = 10;
    options.stage3Seconds = 300;
    bool printOnly = false;
    probe::InviteOptions invite;
    for (int i = 1; i < argc; ++i)
    {
        const std::wstring arg = argv[i];
        if (arg == L"--steam-api" && i + 1 < argc) dll = argv[++i];
        else if (arg == L"--app-id" && i + 1 < argc) appId = argv[++i];
        else if (arg == L"--relay-warmup") options.relayWarmup = true;
        else if (arg == L"--observe-seconds" && i + 1 < argc) options.observeSeconds = _wtoi(argv[++i]);
        else if (arg == L"--stage2-loopback") options.stage2Loopback = true;
        else if (arg == L"--print-steamid") printOnly = true;
        else if (arg == L"--invite-test" && i + 1 < argc)
        {
            const std::wstring role = argv[++i];
            invite.role = role == L"send" ? "send" : role == L"receive" ? "receive" : "";
            if (invite.role.empty())
            {
                std::fwprintf(stderr, L"--invite-test must be send or receive\n");
                return 2;
            }
        }
        else if (arg == L"--invite-variant" && i + 1 < argc)
        {
            const std::wstring variant = argv[++i];
            invite.variant = variant == L"rp" ? "rp" : variant == L"lobby" ? "lobby" : "";
        }
        else if (arg == L"--invite-seconds" && i + 1 < argc) invite.seconds = _wtoi(argv[++i]);
        else if (arg == L"--stage3-role" && i + 1 < argc)
        {
            const std::wstring role = argv[++i];
            options.stage3Role = role == L"listen" ? "listen" : role == L"connect" ? "connect" : "";
            if (options.stage3Role.empty())
            {
                std::fwprintf(stderr, L"--stage3-role must be listen or connect\n");
                return 2;
            }
        }
        else if (arg == L"--stage3-peer" && i + 1 < argc) options.stage3Peer = _wcstoui64(argv[++i], nullptr, 10);
        else if (arg == L"--stage3-match" && i + 1 < argc)
        {
            const std::wstring match = argv[++i];
            for (const wchar_t c : match) options.stage3Match.push_back(c < 0x80 ? static_cast<char>(c) : '?');
        }
        else if (arg == L"--stage3-seconds" && i + 1 < argc) options.stage3Seconds = _wtoi(argv[++i]);
        else
        {
            std::fwprintf(stderr, L"unknown argument: %s\n", arg.c_str());
            return 2;
        }
    }
    const bool inviteTest = !invite.role.empty();
    if (inviteTest)
    {
        invite.peer = options.stage3Peer;
        invite.code = options.stage3Match;
        options.stage3Role.clear(); // the invite test replaces the P2P test
    }
    const bool stage3 = !options.stage3Role.empty();
    if (stage3 || inviteTest) options.observeCallbacks = false; // keep the tests short

    std::vector<std::wstring> tried;
    if (dll.empty())
    {
        const auto local = ExeDirectory() / L"steam_api64.dll";
        tried.push_back(local.wstring());
        std::error_code error;
        if (std::filesystem::is_regular_file(local, error)) dll = local.wstring();
        else if (const auto game = FindGameSteamApi(tried); !game.empty())
        {
            dll = game.wstring();
            Print("using steam_api64.dll from the local KovaaK's install");
        }
    }
    if (dll.empty())
    {
        Print("steam_api64.dll not found. Looked in:");
        for (const auto& path : tried) std::wprintf(L"    %s\n", path.c_str());
        Print("Copy steam_api64.dll from <your KovaaK's folder>\\FPSAimTrainer\\Binaries\\Win64 next to this program, or pass --steam-api <path>.");
        return 1;
    }

    // SteamAPI_Init reads the AppID from these (or from steam_appid.txt in the
    // working directory).
    SetEnvironmentVariableW(L"SteamAppId", appId.c_str());
    SetEnvironmentVariableW(L"SteamGameId", appId.c_str());

    HMODULE module = LoadLibraryW(dll.c_str());
    if (!module)
    {
        Print("could not load steam_api64.dll");
        return 1;
    }
    auto init = reinterpret_cast<steamabi::PFN_SteamAPI_Init>(GetProcAddress(module, "SteamAPI_Init"));
    auto shutdown = reinterpret_cast<steamabi::PFN_SteamAPI_Shutdown>(GetProcAddress(module, "SteamAPI_Shutdown"));
    auto runCallbacks = reinterpret_cast<steamabi::PFN_SteamAPI_RunCallbacks>(GetProcAddress(module, "SteamAPI_RunCallbacks"));
    if (!init || !shutdown || !runCallbacks)
    {
        Print("steam_api64.dll lacks SteamAPI_Init/Shutdown/RunCallbacks");
        return 1;
    }
    if (!init())
    {
        Print("SteamAPI_Init failed. Is Steam running and logged in, and does this account own KovaaK's?");
        return 1;
    }
    Print("harness: SteamAPI_Init ok (harness-owned, not the game's instance)");

    probe::SteamApi api;
    api.Resolve(module);
    if (auto* user = api.Interface("SteamUser020"); user && api.User_GetSteamID)
    {
        // Console only, so the two people can exchange IDs. Logs and the
        // result summary stay redacted.
        std::printf("\n  Your SteamID64 (give this to the other person): %llu\n\n",
                    static_cast<unsigned long long>(api.User_GetSteamID(reinterpret_cast<std::intptr_t>(user))));
        std::fflush(stdout);
    }
    if (printOnly)
    {
        shutdown();
        return 0;
    }

    PumpJobs jobs;
    std::atomic<bool> pump{true};
    std::atomic<DWORD> pumpThread{0};
    std::thread pumper([&] {
        pumpThread = GetCurrentThreadId();
        jobs.Start(GetCurrentThreadId());
        while (pump)
        {
            runCallbacks();
            jobs.Drain();
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
    });
    while (pumpThread.load() == 0) std::this_thread::yield();

    probe::RunStage1(api, options, Print);
    probe::CallbackObserver observer(api);
    if (options.observeCallbacks)
    {
        observer.SetGameThread(pumpThread.load());
        jobs.Run([&] { observer.Register(); });
        std::this_thread::sleep_for(std::chrono::seconds(options.observeSeconds));
        observer.Report(Print);
        Print("callbacks: (harness) \"game thread\" means the harness's RunCallbacks pump thread");
    }
    probe::RunStage2(api, options, Print);

    probe::Stage3Result result;
    probe::RunStage3(api, options, Print, [&](const std::function<void()>& fn) { jobs.Run(fn); }, &result);
    probe::InviteResult inviteResult;
    if (inviteTest) probe::RunInviteTest(api, invite, Print, [&](const std::function<void()>& fn) { jobs.Run(fn); }, &inviteResult);

    // Stop dispatching before the listeners go away.
    jobs.Stop();
    pump = false;
    pumper.join();
    observer.Unregister();
    shutdown();
    Print("harness: SteamAPI_Shutdown done");

    if (inviteTest)
    {
        const std::string summary = InviteSummary(inviteResult);
        std::printf("\n%s\n", summary.c_str());
        std::ofstream file(ExeDirectory() / L"aimmod-invite-result.txt", std::ios::trunc);
        file << summary;
        std::printf("The result above was also saved to aimmod-invite-result.txt next to this program.\n");
        std::printf("Send only that result (or the block above). It contains no SteamIDs or IP addresses.\n");
        std::fflush(stdout);
        return inviteResult.pass ? 0 : 1;
    }
    if (stage3)
    {
        if (!result.ran) result.failure = "stage 3 is not compiled into this build";
        const std::string summary = Summary(options, result);
        std::printf("\n%s\n", summary.c_str());
        std::ofstream file(ExeDirectory() / L"aimmod-p2p-result.txt", std::ios::trunc);
        file << summary;
        std::printf("The result above was also saved to aimmod-p2p-result.txt next to this program.\n");
        std::printf("Send only that result (or the block above). It contains no SteamIDs or IP addresses.\n");
        std::fflush(stdout);
        return result.pass ? 0 : 1;
    }
    return 0;
}
