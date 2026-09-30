#pragma once
#include <DynamicOutput/DynamicOutput.hpp>

#include <Windows.h>

#include <string>

namespace aimmod
{
    inline std::wstring Widen(const std::string& utf8)
    {
        if (utf8.empty()) return {};
        int size = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
        std::wstring out(static_cast<std::size_t>(size > 0 ? size : 0), L'\0');
        if (size > 0) MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), out.data(), size);
        return out;
    }

    // One line in UE4SS.log, prefixed. Never log private payloads (names of
    // other players, paths under the user profile).
    inline void Log(const std::string& line)
    {
        RC::Output::send<RC::LogLevel::Default>(STR("[AimModCore] {}\n"), Widen(line));
    }
    inline void Warn(const std::string& line)
    {
        RC::Output::send<RC::LogLevel::Warning>(STR("[AimModCore] {}\n"), Widen(line));
    }
} // namespace aimmod
