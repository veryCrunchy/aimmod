// AimModSteam: Steam lobbies, invites, friends and relay-only P2P for the
// AimMod native service, over \\.\pipe\aimmod-steam-v1. A separate UE4SS C++
// mod; it never touches gameplay, ranked or leaderboard code, never sets UE
// session keys and never initialises, pumps or shuts down Steam.
// See in-game/docs/multiplayer.md.
#include "Bridge.hpp"

#include <DynamicOutput/DynamicOutput.hpp>
#include <Mod/CppUserModBase.hpp>
#include <Unreal/Hooks/Hooks.hpp>

#include <Windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <thread>

namespace
{
    std::wstring Widen(const std::string& utf8)
    {
        if (utf8.empty()) return {};
        const int size = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
        std::wstring out(static_cast<std::size_t>(size > 0 ? size : 0), L'\0');
        if (size > 0) MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), out.data(), size);
        return out;
    }

    void Log(const std::string& line) { RC::Output::send<RC::LogLevel::Default>(STR("[AimModSteam] {}\n"), Widen(line)); }
} // namespace

class AimModSteam final : public RC::CppUserModBase
{
public:
    AimModSteam()
    {
        ModName = STR("AimModSteam");
        ModVersion = STR("0.1.0");
        ModDescription = STR("Steam lobbies, invites and P2P for AimMod multiplayer");
        ModAuthors = STR("AimMod");
    }

    ~AimModSteam() override
    {
        m_stop = true;
        if (m_starter.joinable()) m_starter.join();
        if (m_bridge) m_bridge->Stop(); // leaves the lobby; game-thread jobs fall back inline
        if (m_tickId != 0) RC::Unreal::Hook::UnregisterCallback(m_tickId);
        m_jobDone.notify_all();
    }

    auto on_unreal_init() -> void override
    {
        // Read once: Steam passes "+connect_lobby <id>" or our connect string
        // here when an invite launched the game.
        m_commandLine = GetCommandLineW();

        using namespace RC::Unreal::Hook;
        FCallbackOptions tick{};
        tick.bReadonly = true;
        tick.OwnerModName = STR("AimModSteam");
        tick.HookName = STR("AimModSteam.Tick");
        m_tickId = RegisterEngineTickPostCallback(
            [this](TCallbackIterationData<void>&, RC::Unreal::UEngine*, float, bool) {
                if (m_gameThread.load(std::memory_order_relaxed) == 0) m_gameThread = GetCurrentThreadId();
                DrainGameThreadJobs();
            },
            tick);
        if (m_tickId == ERROR_ID) m_tickId = 0;

        m_starter = std::thread([this] { Start(); });
    }

private:
    struct Job
    {
        std::function<void()> fn;
        bool done = false;
    };

    void Start()
    {
        // Wait for the game to initialise Steam; never load or init it ourselves.
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::minutes(3);
        HMODULE module = nullptr;
        while (!m_stop && std::chrono::steady_clock::now() < deadline)
        {
            module = GetModuleHandleW(L"steam_api64.dll");
            auto user = module ? reinterpret_cast<int (*)()>(reinterpret_cast<void*>(GetProcAddress(module, "SteamAPI_GetHSteamUser"))) : nullptr;
            if (user && user() != 0) break;
            module = nullptr;
            std::this_thread::sleep_for(std::chrono::milliseconds(500));
        }
        if (m_stop) return;
        if (!module)
        {
            Log("Steam was not initialised by the game; AimModSteam is disabled");
            return;
        }
        auto bridge = std::make_unique<bridge::Bridge>(Log, [this](const std::function<void()>& fn) { RunOnGameThread(fn); });
        if (!bridge->Start(module, m_commandLine)) return;
        m_bridge = std::move(bridge);
        Log(std::string("bridge ") + bridge::BridgeVersion + " listening on \\\\.\\pipe\\aimmod-steam-v1 (contract v" + std::to_string(bridge::ContractVersion) + ")");
    }

    void DrainGameThreadJobs()
    {
        std::deque<std::shared_ptr<Job>> jobs;
        {
            std::lock_guard lock(m_jobMutex);
            jobs.swap(m_jobs);
        }
        for (auto& job : jobs)
        {
            job->fn();
            std::lock_guard lock(m_jobMutex);
            job->done = true;
        }
        if (!jobs.empty()) m_jobDone.notify_all();
    }

    // Runs fn on the game thread and waits; falls back to the calling thread
    // after 5 s (for example during shutdown) so cleanup always happens.
    void RunOnGameThread(const std::function<void()>& fn)
    {
        if (GetCurrentThreadId() == m_gameThread.load()) return fn();
        auto job = std::make_shared<Job>(Job{fn});
        std::unique_lock lock(m_jobMutex);
        if (m_tickId != 0)
        {
            m_jobs.push_back(job);
            if (m_jobDone.wait_for(lock, std::chrono::seconds(5), [&] { return job->done; })) return;
            const auto it = std::find(m_jobs.begin(), m_jobs.end(), job);
            if (it == m_jobs.end())
            {
                m_jobDone.wait(lock, [&] { return job->done; });
                return;
            }
            m_jobs.erase(it);
        }
        lock.unlock();
        fn();
    }

    std::wstring m_commandLine;
    std::unique_ptr<bridge::Bridge> m_bridge;
    std::thread m_starter;
    std::atomic<bool> m_stop{false};
    std::atomic<DWORD> m_gameThread{0};
    std::mutex m_jobMutex;
    std::condition_variable m_jobDone;
    std::deque<std::shared_ptr<Job>> m_jobs;
    RC::Unreal::Hook::GlobalCallbackId m_tickId = 0;
};

#define AIMMOD_STEAM_API __declspec(dllexport)
extern "C"
{
    AIMMOD_STEAM_API RC::CppUserModBase* start_mod() { return new AimModSteam(); }
    AIMMOD_STEAM_API void uninstall_mod(RC::CppUserModBase* mod) { delete mod; }
}
