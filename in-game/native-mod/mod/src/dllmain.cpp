// AimModCore: read-only KovaaK's challenge observer for AimMod.
// See in-game/native-mod/DESIGN.md.
#include "Log.hpp"
#include "Observer.hpp"
#include "Output.hpp"
#include "ServiceHost.hpp"

#include <Mod/CppUserModBase.hpp>

#include <Windows.h>

#include <filesystem>
#include <memory>

namespace
{
    constexpr const char* Version = "0.1.0";

    // Mods\AimModCore\dlls\main.dll -> Mods\AimModCore
    std::filesystem::path ModDirectory()
    {
        HMODULE self = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, reinterpret_cast<LPCWSTR>(&ModDirectory),
                           &self);
        wchar_t path[MAX_PATH * 4]{};
        DWORD length = GetModuleFileNameW(self, path, static_cast<DWORD>(std::size(path)));
        if (length == 0 || length >= std::size(path)) return {};
        return std::filesystem::path(path).parent_path().parent_path();
    }

    // <game>\FPSAimTrainer\Binaries\Win64\<exe> -> <game>\FPSAimTrainer\Content\Paks\~AimMod
    std::filesystem::path AimModPaks()
    {
        wchar_t path[MAX_PATH * 4]{};
        DWORD length = GetModuleFileNameW(nullptr, path, static_cast<DWORD>(std::size(path)));
        if (length == 0 || length >= std::size(path)) return {};
        return std::filesystem::path(path).parent_path().parent_path().parent_path() / L"Content" / L"Paks" / L"~AimMod";
    }
} // namespace

class AimModCore final : public RC::CppUserModBase
{
public:
    AimModCore()
    {
        ModName = STR("AimModCore");
        ModVersion = STR("0.1.0");
        ModDescription = STR("Read-only challenge observer for AimMod");
        ModAuthors = STR("AimMod");
    }

    ~AimModCore() override
    {
        if (m_observer) m_observer->Shutdown();
        m_service.Stop();
        m_output.Stop();
    }

    auto on_unreal_init() -> void override
    {
        const auto root = aimmod::Output::DefaultRoot();
        m_output.SetCosmeticsSources(ModDirectory() / L"service" / L"cosmetics", AimModPaks());
        if (!m_output.Start(root, Version))
        {
            aimmod::Warn("output folder unavailable; AimModCore is disabled");
            return;
        }
        m_observer = std::make_unique<aimmod::Observer>(m_output, Version);
        m_observer->Initialize();
        const auto service = ModDirectory() / L"service" / L"AimMod.InGame.exe";
        std::error_code error;
        if (std::filesystem::is_regular_file(service, error))
        {
            m_service.Start(service, root / L"service.log");
            aimmod::Log("native service supervised (Mods\\AimModCore\\service)");
        }
        else aimmod::Log("native service not bundled; not started");
    }

private:
    aimmod::Output m_output;
    aimmod::ServiceHost m_service;
    std::unique_ptr<aimmod::Observer> m_observer;
};

#define AIMMOD_API __declspec(dllexport)
extern "C"
{
    AIMMOD_API RC::CppUserModBase* start_mod() { return new AimModCore(); }
    AIMMOD_API void uninstall_mod(RC::CppUserModBase* mod) { delete mod; }
}
