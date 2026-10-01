// Development harness: runs the AimModSteam bridge outside the game, with
// its own SteamAPI_Init under AppID 824270 and its own callback pump, so the
// pipe contract can be exercised with Test-AimModSteamPipe.ps1. Close the
// game first (same AppID). Not shipped.
//
//   aimmod_steam_bridge_harness --steam-api <path\to\steam_api64.dll> [--seconds 120] [--ghost-demo]
#include "Bridge.hpp"

#include <Windows.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <thread>

int wmain(int argc, wchar_t** argv)
{
    std::wstring dll;
    int seconds = 120;
    bool ghost = false;
    std::wstring pipe;
    for (int i = 1; i < argc; ++i)
    {
        const std::wstring a = argv[i];
        if (a == L"--steam-api" && i + 1 < argc) dll = argv[++i];
        else if (a == L"--seconds" && i + 1 < argc) seconds = _wtoi(argv[++i]);
        else if (a == L"--ghost-demo") ghost = true;
        else if (a == L"--pipe" && i + 1 < argc) pipe = std::wstring(L"\\\\.\\pipe\\") + argv[++i];
    }
    if (dll.empty())
    {
        std::fwprintf(stderr, L"usage: --steam-api <steam_api64.dll> [--seconds N] [--ghost-demo]\n");
        return 2;
    }
    SetEnvironmentVariableW(L"SteamAppId", L"824270");
    SetEnvironmentVariableW(L"SteamGameId", L"824270");
    HMODULE module = LoadLibraryW(dll.c_str());
    auto init = module ? reinterpret_cast<bool (*)()>(reinterpret_cast<void*>(GetProcAddress(module, "SteamAPI_Init"))) : nullptr;
    auto run = module ? reinterpret_cast<void (*)()>(reinterpret_cast<void*>(GetProcAddress(module, "SteamAPI_RunCallbacks"))) : nullptr;
    auto shutdown = module ? reinterpret_cast<void (*)()>(reinterpret_cast<void*>(GetProcAddress(module, "SteamAPI_Shutdown"))) : nullptr;
    if (!init || !run || !shutdown || !init())
    {
        std::printf("SteamAPI_Init failed\n");
        return 1;
    }
    auto log = [](const std::string& line) {
        std::printf("[AimModSteam] %s\n", line.c_str());
        std::fflush(stdout);
    };

    // The pump thread plays the game thread: it runs callbacks and jobs.
    std::mutex mutex;
    std::condition_variable cv;
    std::deque<std::pair<std::function<void()>, std::shared_ptr<bool>>> jobs;
    std::atomic<bool> pumping{true};
    std::atomic<DWORD> pumpId{0};
    std::thread pump([&] {
        pumpId = GetCurrentThreadId();
        while (pumping)
        {
            run();
            std::deque<std::pair<std::function<void()>, std::shared_ptr<bool>>> batch;
            {
                std::lock_guard lock(mutex);
                batch.swap(jobs);
            }
            for (auto& [fn, done] : batch)
            {
                fn();
                std::lock_guard lock(mutex);
                *done = true;
            }
            if (!batch.empty()) cv.notify_all();
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
    });
    while (!pumpId) std::this_thread::yield();
    auto onGameThread = [&](const std::function<void()>& fn) {
        if (GetCurrentThreadId() == pumpId || !pumping) return fn();
        auto done = std::make_shared<bool>(false);
        std::unique_lock lock(mutex);
        jobs.emplace_back(fn, done);
        cv.wait(lock, [&] { return *done; });
    };

    {
        bridge::Bridge b(log, onGameThread);
        bridge::Bridge::Options options;
        options.ghostDemo = ghost;
        options.pipeName = pipe;
        b.SetOptions(options);
        if (!b.Start(module, GetCommandLineW())) return 1;
        log("harness: bridge running for " + std::to_string(seconds) + " s");
        std::this_thread::sleep_for(std::chrono::seconds(seconds));
        b.Stop();
    }
    pumping = false;
    pump.join();
    shutdown();
    return 0;
}
