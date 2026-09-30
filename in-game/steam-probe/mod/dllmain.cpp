// AimModSteamProbe: a separate, read-only UE4SS C++ mod that reports what the
// game's already-initialised Steam API offers AimMod. It never initialises,
// shuts down or pumps Steam, never touches gameplay objects, and (unless the
// separately compiled stage 2 is enabled) creates nothing and sends nothing.
// See in-game/docs/multiplayer.md.
#include "Probe.hpp"

#include <DynamicOutput/DynamicOutput.hpp>
#include <Mod/CppUserModBase.hpp>
#include <Unreal/Hooks/Hooks.hpp>

#include <Windows.h>

#include <atomic>
#include <chrono>
#include <filesystem>
#include <memory>
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

    void Log(const std::string& line)
    {
        RC::Output::send<RC::LogLevel::Default>(STR("[AimModSteamProbe] {}\n"), Widen(line));
    }

    // Mods\AimModSteamProbe\dlls\main.dll -> Mods\AimModSteamProbe
    std::filesystem::path ModDirectory()
    {
        HMODULE self = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, reinterpret_cast<LPCWSTR>(&ModDirectory),
                           &self);
        wchar_t path[MAX_PATH * 4]{};
        const DWORD length = GetModuleFileNameW(self, path, static_cast<DWORD>(std::size(path)));
        if (length == 0 || length >= std::size(path)) return {};
        return std::filesystem::path(path).parent_path().parent_path();
    }
} // namespace

class AimModSteamProbe final : public RC::CppUserModBase
{
public:
    AimModSteamProbe()
    {
        ModName = STR("AimModSteamProbe");
        ModVersion = STR("0.1.0");
        ModDescription = STR("Read-only Steam networking feasibility probe for AimMod");
        ModAuthors = STR("AimMod");
    }

    ~AimModSteamProbe() override
    {
        m_stop = true;
        if (m_tickId != 0) RC::Unreal::Hook::UnregisterCallback(m_tickId);
        if (m_worker.joinable()) m_worker.join();
        if (m_observer) m_observer->Unregister();
    }

    auto on_unreal_init() -> void override
    {
        m_options = probe::Options{};
        m_options = probe::LoadOptions(ModDirectory() / L"config.txt", Log);

        // Engine tick (game thread, read-only): records the game thread id
        // and registers the passive callback listeners from that thread.
        using namespace RC::Unreal::Hook;
        FCallbackOptions tick{};
        tick.bReadonly = true;
        tick.OwnerModName = STR("AimModSteamProbe");
        tick.HookName = STR("AimModSteamProbe.Tick");
        m_tickId = RegisterEngineTickPostCallback(
            [this](TCallbackIterationData<void>&, RC::Unreal::UEngine*, float, bool) {
                if (m_stop.load(std::memory_order_relaxed)) return;
                if (m_gameThread.load(std::memory_order_relaxed) == 0) m_gameThread = GetCurrentThreadId();
                if (m_wantRegister.exchange(false) && m_observer)
                {
                    m_observer->SetGameThread(GetCurrentThreadId());
                    m_observer->Register();
                    m_registered = true;
                }
            },
            tick);
        if (m_tickId == ERROR_ID) m_tickId = 0;

        m_worker = std::thread([this] { Run(); });
    }

private:
    void Run()
    {
        // Wait for the game to load and initialise steam_api64.dll. The probe
        // never loads or initialises it itself.
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::minutes(3);
        while (!m_stop && std::chrono::steady_clock::now() < deadline)
        {
            if (HMODULE module = probe::FindLoadedSteamApi(); module && m_api.Resolve(module) && m_api.Initialised()) break;
            std::this_thread::sleep_for(std::chrono::milliseconds(500));
        }
        if (m_stop) return;
        if (!m_api.module)
        {
            Log("steam_api64.dll is not loaded in this process; nothing to probe");
            return;
        }
        probe::RunStage1(m_api, m_options, Log);

        if (m_options.observeCallbacks && m_api.Initialised() && m_tickId != 0)
        {
            m_observer = std::make_unique<probe::CallbackObserver>(m_api);
            m_wantRegister = true;
            Sleep(m_options.observeSeconds);
            if (m_stop) return;
            if (!m_registered) Log("callbacks: listeners were not registered (no engine tick yet)");
            else
            {
                m_observer->Report(Log);
                Log("callbacks: listeners stay registered until the mod unloads (passive, never consume)");
            }
        }
        else Log("callbacks: observation disabled");

        probe::RunStage2(m_api, m_options, Log);
    }

    void Sleep(int seconds)
    {
        const auto until = std::chrono::steady_clock::now() + std::chrono::seconds(seconds);
        while (!m_stop && std::chrono::steady_clock::now() < until) std::this_thread::sleep_for(std::chrono::milliseconds(200));
    }

    probe::Options m_options;
    probe::SteamApi m_api;
    std::unique_ptr<probe::CallbackObserver> m_observer;
    std::thread m_worker;
    std::atomic<bool> m_stop{false};
    std::atomic<bool> m_wantRegister{false};
    std::atomic<bool> m_registered{false};
    std::atomic<DWORD> m_gameThread{0};
    RC::Unreal::Hook::GlobalCallbackId m_tickId = 0;
};

#define AIMMOD_PROBE_API __declspec(dllexport)
extern "C"
{
    AIMMOD_PROBE_API RC::CppUserModBase* start_mod() { return new AimModSteamProbe(); }
    AIMMOD_PROBE_API void uninstall_mod(RC::CppUserModBase* mod) { delete mod; }
}
