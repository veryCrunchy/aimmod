// Standalone harness for the Steam probe. Unlike the in-game mod, it owns the
// Steam API itself: it loads the steam_api64.dll given on the command line,
// calls SteamAPI_Init under the given AppID, pumps callbacks on its own
// thread, runs the same stage 1 checks and shuts Steam down. Use it to check
// the probe without starting the game. It shows the account as "in game"
// for the AppID while it runs.
//
//   aimmod_steam_probe_harness --steam-api <path\to\steam_api64.dll>
//       [--app-id 824270] [--relay-warmup] [--observe-seconds 10]
//       [--stage2-loopback]   (only honoured by an AIMMOD_PROBE_STAGE2 build)
#include "Probe.hpp"

#include <Windows.h>

#include <atomic>
#include <chrono>
#include <cstdio>
#include <string>
#include <thread>

namespace
{
    void Print(const std::string& line)
    {
        std::printf("[AimModSteamProbe] %s\n", line.c_str());
        std::fflush(stdout);
    }
} // namespace

int wmain(int argc, wchar_t** argv)
{
    std::wstring dll;
    std::wstring appId = L"824270";
    probe::Options options;
    options.observeSeconds = 10;
    for (int i = 1; i < argc; ++i)
    {
        const std::wstring arg = argv[i];
        if (arg == L"--steam-api" && i + 1 < argc) dll = argv[++i];
        else if (arg == L"--app-id" && i + 1 < argc) appId = argv[++i];
        else if (arg == L"--relay-warmup") options.relayWarmup = true;
        else if (arg == L"--observe-seconds" && i + 1 < argc) options.observeSeconds = _wtoi(argv[++i]);
        else if (arg == L"--stage2-loopback") options.stage2Loopback = true;
        else
        {
            std::fwprintf(stderr, L"unknown argument: %s\n", arg.c_str());
            return 2;
        }
    }
    if (dll.empty())
    {
        std::fwprintf(stderr, L"usage: --steam-api <path to steam_api64.dll> [--app-id N] [--relay-warmup] [--observe-seconds N] [--stage2-loopback]\n");
        return 2;
    }

    // SteamAPI_Init reads the AppID from these when no steam_appid.txt is next
    // to the executable.
    SetEnvironmentVariableW(L"SteamAppId", appId.c_str());
    SetEnvironmentVariableW(L"SteamGameId", appId.c_str());

    HMODULE module = LoadLibraryW(dll.c_str());
    if (!module)
    {
        Print("could not load the given steam_api64.dll");
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
        Print("SteamAPI_Init failed (is Steam running and does this account own the AppID?)");
        return 1;
    }
    Print("harness: SteamAPI_Init ok (harness-owned, not the game's instance)");

    probe::SteamApi api;
    api.Resolve(module);

    std::atomic<bool> pump{true};
    std::atomic<DWORD> pumpThread{0};
    std::thread pumper([&] {
        pumpThread = GetCurrentThreadId();
        while (pump)
        {
            runCallbacks();
            std::this_thread::sleep_for(std::chrono::milliseconds(16));
        }
    });

    probe::RunStage1(api, options, Print);
    probe::CallbackObserver observer(api);
    if (options.observeCallbacks)
    {
        while (pumpThread.load() == 0) std::this_thread::yield();
        observer.SetGameThread(pumpThread.load());
        observer.Register();
        std::this_thread::sleep_for(std::chrono::seconds(options.observeSeconds));
        observer.Report(Print);
        Print("callbacks: (harness) \"game thread\" means the harness's RunCallbacks pump thread");
    }
    probe::RunStage2(api, options, Print);

    // Stop dispatching before the listeners go away.
    pump = false;
    pumper.join();
    observer.Unregister();
    shutdown();
    Print("harness: SteamAPI_Shutdown done");
    return 0;
}
