#include <DynamicOutput/Output.hpp>
#include <Mod/CppUserModBase.hpp>
#include <Unreal/FText.hpp>
#include <Unreal/FProperty.hpp>
#include <Unreal/Core/Containers/ScriptArray.hpp>
#include <Unreal/Property/FArrayProperty.hpp>
#include <Unreal/Property/FBoolProperty.hpp>
#include <Unreal/Property/FNumericProperty.hpp>
#include <Unreal/Property/FObjectProperty.hpp>
#include <Unreal/Property/FStrProperty.hpp>
#include <Unreal/Property/FStructProperty.hpp>
#include <Unreal/Property/FTextProperty.hpp>
#include <Unreal/UClass.hpp>
#include <Unreal/UFunction.hpp>
#include <Unreal/UObject.hpp>
#include <Unreal/UObjectGlobals.hpp>

#include <algorithm>
#include <array>
#include <atomic>
#include <cctype>
#include <cmath>
#include <cstdlib>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <initializer_list>
#include <limits>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#ifdef TEXT
#undef TEXT
#endif
#include <windows.h>

#include "rust_bridge.hpp"

namespace {

bool is_likely_readable_region(const void* ptr, size_t bytes) {
    if (!ptr || bytes == 0) {
        return false;
    }
    MEMORY_BASIC_INFORMATION mbi{};
    const auto q = VirtualQuery(ptr, &mbi, sizeof(mbi));
    if (q == 0 || mbi.State != MEM_COMMIT) {
        return false;
    }
    if (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) {
        return false;
    }
    const DWORD p = mbi.Protect & 0xFF;
    const bool readable =
        p == PAGE_READONLY ||
        p == PAGE_READWRITE ||
        p == PAGE_WRITECOPY ||
        p == PAGE_EXECUTE_READ ||
        p == PAGE_EXECUTE_READWRITE ||
        p == PAGE_EXECUTE_WRITECOPY;
    if (!readable) {
        return false;
    }
    const auto start = reinterpret_cast<uintptr_t>(ptr);
    const auto end = start + bytes;
    const auto region_start = reinterpret_cast<uintptr_t>(mbi.BaseAddress);
    const auto region_end = region_start + mbi.RegionSize;
    return start >= region_start && end <= region_end;
}

bool is_likely_valid_object_ptr(const void* ptr) {
    if (!is_likely_readable_region(ptr, sizeof(void*))) {
        return false;
    }
    const auto vtable = *reinterpret_cast<const uintptr_t*>(ptr);
    if (vtable < 0x10000ull) {
        return false;
    }
    return is_likely_readable_region(reinterpret_cast<const void*>(vtable), sizeof(void*));
}

std::wstring game_bin_dir() {
    wchar_t buffer[MAX_PATH] = {};
    if (!GetModuleFileNameW(nullptr, buffer, MAX_PATH)) {
        return L".\\";
    }
    std::wstring path(buffer);
    const auto pos = path.find_last_of(L"\\/");
    if (pos == std::wstring::npos) {
        return L".\\";
    }
    return path.substr(0, pos + 1);
}

bool env_flag_enabled(const char* name) {
    char value[16] = {};
    const auto len = GetEnvironmentVariableA(name, value, static_cast<DWORD>(sizeof(value)));
    if (len == 0 || len >= sizeof(value)) {
        return false;
    }
    return value[0] == '1' || value[0] == 'y' || value[0] == 'Y' || value[0] == 't' || value[0] == 'T';
}

std::string utf8_from_wide(const std::wstring& input) {
    if (input.empty()) {
        return {};
    }
    const int required = WideCharToMultiByte(
        CP_UTF8,
        0,
        input.c_str(),
        static_cast<int>(input.size()),
        nullptr,
        0,
        nullptr,
        nullptr
    );
    if (required <= 0) {
        return {};
    }

    std::string output(static_cast<size_t>(required), '\0');
    const int written = WideCharToMultiByte(
        CP_UTF8,
        0,
        input.c_str(),
        static_cast<int>(input.size()),
        output.data(),
        required,
        nullptr,
        nullptr
    );
    if (written <= 0) {
        return {};
    }
    return output;
}

RC::StringType string_type_from_utf8(const char* input) {
    if (!input || !*input) {
        return RC::StringType{};
    }

    int required = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, input, -1, nullptr, 0);
    UINT code_page = CP_UTF8;
    DWORD flags = MB_ERR_INVALID_CHARS;
    if (required <= 0) {
        code_page = CP_ACP;
        flags = 0;
        required = MultiByteToWideChar(code_page, flags, input, -1, nullptr, 0);
        if (required <= 0) {
            return RC::StringType{};
        }
    }

    std::wstring wide(static_cast<size_t>(required), L'\0');
    const int written = MultiByteToWideChar(code_page, flags, input, -1, wide.data(), required);
    if (written <= 0) {
        return RC::StringType{};
    }
    if (!wide.empty() && wide.back() == L'\0') {
        wide.pop_back();
    }
    return RC::StringType(wide);
}

void runtime_log_line(const char* line) {
    RC::Output::send<RC::LogLevel::Warning>(
        STR("[kmod-prod] {}\n"),
        string_type_from_utf8(line)
    );
}

// Logs a comma-separated name list, split into chunks so long lists stay readable.
void log_name_list(const char* label, const std::string& names) {
    constexpr size_t k_chunk = 900;
    if (names.empty()) {
        runtime_log_line((std::string(label) + ": (none)").c_str());
        return;
    }
    size_t start = 0;
    uint32_t part = 1;
    while (start < names.size()) {
        size_t end = names.size();
        if (end - start > k_chunk) {
            const auto comma = names.rfind(',', start + k_chunk);
            end = (comma != std::string::npos && comma > start) ? comma : start + k_chunk;
        }
        std::string line = label;
        if (start > 0 || end < names.size()) {
            line += " [" + std::to_string(part) + "]";
        }
        line += ": ";
        line.append(names, start, end - start);
        runtime_log_line(line.c_str());
        start = (end < names.size() && names[end] == ',') ? end + 1 : end;
        ++part;
    }
}

// user_mgmt kill switch: env AIMMOD_DISABLE_USER_MGMT=1, or a file named
// "aimmod_disable_user_mgmt" next to the game exe (Binaries\Win64).
bool user_mgmt_disabled_by_switch() {
    if (env_flag_enabled("AIMMOD_DISABLE_USER_MGMT")) {
        return true;
    }
    const auto flag_path = game_bin_dir() + L"aimmod_disable_user_mgmt";
    return GetFileAttributesW(flag_path.c_str()) != INVALID_FILE_ATTRIBUTES;
}

void events_log_line(const char* line) {
    RC::Output::send<RC::LogLevel::Warning>(
        STR("[kmod-prod-ev] {}\n"),
        string_type_from_utf8(line)
    );
}

void* safe_property_value_ptr(RC::Unreal::FProperty* property, void* container, int32_t array_index = 0) {
    if (!property || !container || !is_likely_valid_object_ptr(property)) {
        return nullptr;
    }
    const int32_t array_dim = property->GetArrayDim();
    if (array_index < 0 || (array_dim > 0 && array_index >= array_dim)) {
        return nullptr;
    }
    const int32_t offset = property->GetOffset_Internal();
    const int32_t element_size = property->GetElementSize();
    if (offset < 0 || element_size <= 0 || element_size > 0x100000) {
        return nullptr;
    }
    auto* value_ptr = reinterpret_cast<uint8_t*>(container)
        + static_cast<size_t>(offset)
        + static_cast<size_t>(element_size) * static_cast<size_t>(array_index);
    if (!is_likely_readable_region(value_ptr, static_cast<size_t>(element_size))) {
        return nullptr;
    }
    return value_ptr;
}

RC::StringType object_path_from_full_name(const RC::StringType& full_name) {
    const auto split = full_name.find(STR(" "));
    if (split == RC::StringType::npos) {
        return full_name;
    }
    if (split + 1 >= full_name.size()) {
        return RC::StringType{};
    }
    return full_name.substr(split + 1);
}

std::string normalize_ascii(const RC::StringType& input) {
    std::string out;
    out.reserve(input.size());
    for (auto c : input) {
        const auto ch = static_cast<unsigned int>(c);
        if (ch > 0x7F) {
            continue;
        }
        out.push_back(static_cast<char>(std::tolower(static_cast<unsigned char>(ch))));
    }
    return out;
}

bool try_parse_int_text(const RC::StringType& text, int32_t& out_value) {
    if (text.empty()) {
        return false;
    }
    bool seen_digit = false;
    bool negative = false;
    int64_t accum = 0;
    for (size_t i = 0; i < text.size(); ++i) {
        const auto ch = text[i];
        if (!seen_digit && ch == STR('-')) {
            negative = true;
            continue;
        }
        if (ch >= STR('0') && ch <= STR('9')) {
            seen_digit = true;
            accum = (accum * 10) + static_cast<int64_t>(ch - STR('0'));
            if (accum > 2147483647LL) {
                accum = 2147483647LL;
            }
        }
    }
    if (!seen_digit) {
        return false;
    }
    if (negative) {
        accum = -accum;
    }
    out_value = static_cast<int32_t>(accum);
    return true;
}

bool try_parse_float_text(const RC::StringType& text, float& out_value) {
    std::string buffer;
    buffer.reserve(text.size());
    bool seen_digit = false;
    bool seen_dot = false;
    bool seen_sign = false;
    for (size_t i = 0; i < text.size(); ++i) {
        const auto ch = static_cast<char>(text[i]);
        if (ch >= '0' && ch <= '9') {
            buffer.push_back(ch);
            seen_digit = true;
            continue;
        }
        if (ch == '.' && !seen_dot) {
            buffer.push_back(ch);
            seen_dot = true;
            continue;
        }
        if (ch == ',' || ch == ' ') {
            continue;
        }
        if ((ch == '-' || ch == '+') && !seen_sign && !seen_digit) {
            buffer.push_back(ch);
            seen_sign = true;
            continue;
        }
    }
    if (!seen_digit || buffer.empty()) {
        return false;
    }
    char* end_ptr = nullptr;
    const float parsed = std::strtof(buffer.c_str(), &end_ptr);
    if (end_ptr == buffer.c_str() || (end_ptr && *end_ptr != '\0')) {
        return false;
    }
    out_value = parsed;
    return std::isfinite(out_value);
}

bool try_parse_time_to_seconds(const RC::StringType& text, float& out_seconds) {
    std::vector<int32_t> parts{};
    parts.reserve(3);
    int32_t accum = 0;
    bool seen_digit = false;
    bool any_digit = false;
    for (size_t i = 0; i < text.size(); ++i) {
        const auto ch = static_cast<char>(text[i]);
        if (ch >= '0' && ch <= '9') {
            seen_digit = true;
            any_digit = true;
            accum = (accum * 10) + static_cast<int32_t>(ch - '0');
            continue;
        }
        if (ch == ':') {
            parts.push_back(accum);
            accum = 0;
            seen_digit = false;
            continue;
        }
        if (seen_digit) {
            break;
        }
    }
    if (seen_digit || !parts.empty()) {
        parts.push_back(accum);
    }
    if (parts.empty() || !any_digit) {
        return false;
    }
    int64_t total_seconds = 0;
    if (parts.size() == 3) {
        total_seconds = static_cast<int64_t>(parts[0]) * 3600LL
            + static_cast<int64_t>(parts[1]) * 60LL
            + static_cast<int64_t>(parts[2]);
    } else if (parts.size() == 2) {
        total_seconds = static_cast<int64_t>(parts[0]) * 60LL
            + static_cast<int64_t>(parts[1]);
    } else {
        total_seconds = static_cast<int64_t>(parts[0]);
    }
    if (total_seconds < 0) {
        return false;
    }
    out_seconds = static_cast<float>(total_seconds);
    return std::isfinite(out_seconds);
}

std::string trim_ascii_token(const RC::StringType& input) {
    std::string out;
    out.reserve(input.size());
    bool prev_space = true;
    for (auto c : input) {
        const auto raw = static_cast<unsigned int>(c);
        if (raw > 0x7F) {
            continue;
        }
        char ch = static_cast<char>(raw);
        if (ch == '\r' || ch == '\n' || ch == '\t') {
            ch = ' ';
        }
        if (ch == ' ') {
            if (prev_space || out.empty()) {
                continue;
            }
            out.push_back(' ');
            prev_space = true;
            continue;
        }
        if (ch < 0x20) {
            continue;
        }
        out.push_back(ch);
        prev_space = false;
    }
    while (!out.empty() && out.back() == ' ') {
        out.pop_back();
    }
    return out;
}

bool looks_like_real_scenario_name(const std::string& value) {
    if (value.size() < 3 || value.size() > 160) {
        return false;
    }
    bool has_alnum = false;
    for (char ch : value) {
        if (std::isalnum(static_cast<unsigned char>(ch))) {
            has_alnum = true;
            break;
        }
    }
    if (!has_alnum) {
        return false;
    }
    std::string lower;
    lower.reserve(value.size());
    for (char ch : value) {
        lower.push_back(static_cast<char>(std::tolower(static_cast<unsigned char>(ch))));
    }
    if (lower == "scenario" || lower == "scenariotitle" || lower == "none") {
        return false;
    }
    return true;
}

std::string derive_scenario_name_from_id(const std::string& scenario_id) {
    if (scenario_id.empty()) {
        return {};
    }
    size_t start = scenario_id.find_last_of('/');
    if (start == std::string::npos) {
        start = 0;
    } else {
        ++start;
    }
    size_t end = scenario_id.find_last_of('.');
    if (end == std::string::npos || end <= start) {
        end = scenario_id.size();
    }
    if (start >= scenario_id.size() || end <= start) {
        return {};
    }
    return scenario_id.substr(start, end - start);
}

std::string escape_json_ascii(std::string_view value) {
    std::string out;
    out.reserve(value.size() + 8);
    for (char ch : value) {
        switch (ch) {
            case '\\': out += "\\\\"; break;
            case '"': out += "\\\""; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (static_cast<unsigned char>(ch) < 0x20) {
                    continue;
                }
                out.push_back(ch);
                break;
        }
    }
    return out;
}

const char* classify_session_ui_field(const RC::StringType& ctx_name) {
    const auto n = normalize_ascii(ctx_name);
    if (n.find("scenariotitle") != std::string::npos
        || n.find("scenarioheader") != std::string::npos
        || n.find("challengeheader") != std::string::npos) {
        return "scenario_name";
    }
    if (n.find("distscore") != std::string::npos
        || n.find("palettedsumscore") != std::string::npos
        || n.find("challengescore") != std::string::npos) {
        return "session_score";
    }
    if (n.find("sessionstatistics") == std::string::npos) {
        return nullptr;
    }
    if (n.find("sessionshots") != std::string::npos || n.find("shotsfired") != std::string::npos) return "session_shots";
    if (n.find("sessionhits") != std::string::npos || n.find("shotshit") != std::string::npos) return "session_hits";
    if (n.find("killcounter") != std::string::npos || n.find("sessionkills") != std::string::npos) return "session_kills";
    if (n.find("damagedone") != std::string::npos) return "session_damage_done";
    if (n.find("damagepossible") != std::string::npos) return "session_damage_possible";
    if (n.find("damageeff") != std::string::npos || n.find("damageefficiency") != std::string::npos) return "session_damage_eff";
    if (n.find("kps") != std::string::npos || n.find("killspersecond") != std::string::npos) return "session_kps";
    if (n.find("sessiontime") != std::string::npos || n.find("gametime") != std::string::npos) return "session_time";
    if (n.find("spm") != std::string::npos || n.find("scoreperminute") != std::string::npos) return "session_spm";
    if (n.find("averagettk") != std::string::npos || n.find("ttk") != std::string::npos) return "session_avg_ttk";
    return nullptr;
}

void append_unique_objects(std::vector<RC::Unreal::UObject*>& dst, const std::vector<RC::Unreal::UObject*>& src) {
    std::unordered_set<RC::Unreal::UObject*> seen{};
    seen.reserve(dst.size() + src.size());
    for (auto* obj : dst) {
        if (obj) {
            seen.insert(obj);
        }
    }
    for (auto* obj : src) {
        if (!obj) {
            continue;
        }
        if (seen.insert(obj).second) {
            dst.push_back(obj);
        }
    }
}

bool is_rejected_runtime_object_name(const RC::StringType& full_name) {
    if (full_name.empty()) {
        return true;
    }
    if (full_name.find(STR("None.None:None.None")) != RC::StringType::npos) {
        return true;
    }
    if (full_name.find(STR("Default__")) != RC::StringType::npos) {
        return true;
    }
    if (full_name.find(STR("/Script/")) != RC::StringType::npos) {
        return true;
    }
    return false;
}

bool is_rejected_runtime_function_name(const RC::StringType& full_name) {
    if (full_name.empty()) {
        return true;
    }
    if (full_name.find(STR("Function None.")) != RC::StringType::npos) {
        return true;
    }
    if (full_name.find(STR("None.None:None.None")) != RC::StringType::npos) {
        return true;
    }
    return false;
}

void collect_objects_by_class(RC::Unreal::UClass* target_class, std::vector<RC::Unreal::UObject*>& out) {
    if (!target_class || !is_likely_valid_object_ptr(target_class)) {
        return;
    }
    const auto class_name = target_class->GetName();
    if (class_name.empty()) {
        return;
    }
    RC::Unreal::UObjectGlobals::FindAllOf(class_name, out);
}

#include "kmod/replay/replay_types.inl"
#include "kmod/replay/replay_state_machine.inl"
#include "kmod/replay/replay_sources.inl"
#include "kmod/replay/replay_sampler.inl"
#include "kmod/replay/replay_delta_codec.inl"
#include "kmod/replay/replay_emit.inl"
#include "kmod/replay/replay_ingame_playback.inl"
#include "kmod/replay/bridge_command_bus.inl"

} // namespace

class KovaaksBridgeModProduction final : public RC::CppUserModBase {
public:
    enum class LiveMetricHookKind : uintptr_t {
        Seconds = 1,
        Score = 2,
        ShotsFired = 3,
        ShotsHit = 4,
        ChallengeTickCount = 5,
        Kills = 6,
        Accuracy = 7,
        ScorePerMinute = 8,
        KillsPerSecond = 9,
        DamageDone = 10,
        DamagePossible = 11,
        DamageEfficiency = 12,
        ChallengeAverageFps = 13,
    };

    KovaaksBridgeModProduction(const KovaaksBridgeModProduction&) = delete;
    KovaaksBridgeModProduction& operator=(const KovaaksBridgeModProduction&) = delete;

    KovaaksBridgeModProduction() {
        ModName = STR("KovaaksBridgeMod");
        ModVersion = STR("0.1.0");
        ModDescription = STR("Stripped production direct-pull bridge.");
        ModAuthors = STR("veryCrunchy");

        verbose_logs_ = false;
        http_trace_enabled_ = env_flag_enabled("AIMMOD_HTTP_TRACE");
        user_mgmt_enabled_ = !user_mgmt_disabled_by_switch();
        s_instance_ = this;
        runtime_log_line(user_mgmt_enabled_
            ? "[user_mgmt] enabled"
            : "[user_mgmt] DISABLED by switch (AIMMOD_DISABLE_USER_MGMT or aimmod_disable_user_mgmt file)");

        if (kovaaks::RustBridge::startup()) {
            rust_ready_ = true;
            emit_bridge_ready_banner();
        } else {
            RC::Output::send<RC::LogLevel::Error>(
                STR("[KovaaksBridgeMod] Failed to load Rust bridge DLL. path={} win32_error={}\n"),
                RC::StringType(kovaaks::RustBridge::last_dll_path()),
                kovaaks::RustBridge::last_win32_error()
            );
            next_rust_startup_retry_ms_ = GetTickCount64() + 1000;
        }
    }

    ~KovaaksBridgeModProduction() override {
        updates_disabled_ = true;
        unregister_live_metric_hooks();
        unregister_http_trace_hooks();
        if (text_set_hook_registered_ && text_set_fn_ && is_likely_valid_object_ptr(text_set_fn_)) {
            text_set_fn_->UnregisterHook(text_set_hook_id_);
            text_set_hook_registered_ = false;
            text_set_hook_id_ = 0;
        }
        const bool had_rust = rust_ready_;
        rust_ready_ = false;
        if (had_rust) {
            kovaaks::RustBridge::shutdown();
        }
        if (s_instance_ == this) {
            s_instance_ = nullptr;
        }
    }

    auto on_unreal_init() -> void override {
#if defined(_MSC_VER)
        __try {
#endif
            resolve_targets(true);
            if (verbose_logs_) {
                RC::Output::send<RC::LogLevel::Warning>(STR("[kmod-prod] on_unreal_init complete\n"));
            }
#if defined(_MSC_VER)
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            handle_runtime_fault("on_unreal_init");
            RC::Output::send<RC::LogLevel::Error>(
                STR("[kmod-prod] on_unreal_init crashed; runtime fault trapped\n")
            );
        }
#endif
    }

    auto on_ui_init() -> void override {}

    auto on_cpp_mods_loaded() -> void {
    }

    auto on_lua_start(
        RC::StringViewType,
        RC::LuaMadeSimple::Lua&,
        RC::LuaMadeSimple::Lua&,
        RC::LuaMadeSimple::Lua&,
        RC::LuaMadeSimple::Lua*
    ) -> void {
    }

    auto on_lua_start(
        RC::LuaMadeSimple::Lua&,
        RC::LuaMadeSimple::Lua&,
        RC::LuaMadeSimple::Lua&,
        RC::LuaMadeSimple::Lua*
    ) -> void {
    }

    auto on_lua_stop(
        RC::StringViewType,
        RC::LuaMadeSimple::Lua&,
        RC::LuaMadeSimple::Lua&,
        RC::LuaMadeSimple::Lua&,
        RC::LuaMadeSimple::Lua*
    ) -> void {
    }

    auto on_lua_stop(
        RC::LuaMadeSimple::Lua&,
        RC::LuaMadeSimple::Lua&,
        RC::LuaMadeSimple::Lua&,
        RC::LuaMadeSimple::Lua*
    ) -> void {
    }

    auto on_update() -> void override {
        const uint64_t now_ms = GetTickCount64();
        if (!rust_ready_) {
            if (now_ms >= next_rust_startup_retry_ms_) {
                next_rust_startup_retry_ms_ = now_ms + 1000;
                if (kovaaks::RustBridge::startup()) {
                    rust_ready_ = true;
                    emit_bridge_ready_banner();
                    pending_bridge_recovered_snapshot_ = true;
                    runtime_log_line("[bridge_reconnect] rust bridge startup recovered");
                } else if (now_ms >= next_rust_reconnect_log_ms_) {
                    next_rust_reconnect_log_ms_ = now_ms + 5000;
                    std::array<char, 256> log_buf{};
                    std::snprintf(
                        log_buf.data(),
                        log_buf.size(),
                        "[bridge_reconnect] startup pending win32=%lu transport=%lu",
                        static_cast<unsigned long>(kovaaks::RustBridge::last_win32_error()),
                        static_cast<unsigned long>(kovaaks::RustBridge::last_transport_error())
                    );
                    runtime_log_line(log_buf.data());
                }
            }
            if (!rust_ready_) {
                return;
            }
        }

        kovaaks::RustBridgeReconnectEvent reconnect_event{};
        if (kovaaks::RustBridge::read_async_reconnect_event(reconnect_event)
            && reconnect_event.sequence != last_rust_reconnect_event_seq_) {
            last_rust_reconnect_event_seq_ = reconnect_event.sequence;
            if (reconnect_event.kind == kovaaks::RustBridgeReconnectEventKind::Connected) {
                pending_bridge_recovered_snapshot_ = true;
                runtime_log_line("[bridge_reconnect] reconnect worker observed connected");
            } else if (reconnect_event.kind == kovaaks::RustBridgeReconnectEventKind::Disconnected) {
                if (now_ms >= next_rust_reconnect_log_ms_) {
                    next_rust_reconnect_log_ms_ = now_ms + 5000;
                    runtime_log_line("[bridge_reconnect] reconnect worker observed disconnect");
                }
            } else if (now_ms >= next_rust_reconnect_log_ms_) {
                next_rust_reconnect_log_ms_ = now_ms + 5000;
                std::array<char, 256> log_buf{};
                std::snprintf(
                    log_buf.data(),
                    log_buf.size(),
                    "[bridge_reconnect] reconnect worker failed win32=%lu transport=%lu",
                    static_cast<unsigned long>(reconnect_event.win32_error),
                    static_cast<unsigned long>(reconnect_event.transport_error)
                );
                runtime_log_line(log_buf.data());
            }
        }

        if (updates_disabled_) {
            if (pending_bridge_recovered_snapshot_ && rust_ready_ && kovaaks::RustBridge::is_connected()) {
                refresh_live_runtime_hooks_after_reconnect(now_ms);
                if (emit_requested_state_snapshot_safe(now_ms, "bridge_recovered_disabled")) {
                    pending_bridge_recovered_snapshot_ = false;
                    runtime_log_line("[bridge_reconnect] emitted recovery snapshot while updates disabled");
                }
            }
            if (now_ms >= next_updates_disabled_log_ms_) {
                next_updates_disabled_log_ms_ = now_ms + 10000;
                runtime_log_line("[bridge_reconnect] updates disabled; reconnect-only mode active");
            }
            return;
        }
        LARGE_INTEGER update_start{};
        QueryPerformanceCounter(&update_start);
        on_update_impl_safe();
        note_update_cost(update_start, now_ms);
    }

private:
    // Logs the mod's own update cost every 30s so CPU regressions are visible.
    void note_update_cost(const LARGE_INTEGER& start, uint64_t now_ms) {
        LARGE_INTEGER end{};
        LARGE_INTEGER freq{};
        QueryPerformanceCounter(&end);
        QueryPerformanceFrequency(&freq);
        if (freq.QuadPart <= 0) {
            return;
        }
        const uint64_t us = static_cast<uint64_t>(
            (end.QuadPart - start.QuadPart) * 1000000 / freq.QuadPart);
        update_cost_total_us_ += us;
        update_cost_max_us_ = (std::max)(update_cost_max_us_, us);
        ++update_cost_calls_;
        if (update_cost_window_start_ms_ == 0) {
            update_cost_window_start_ms_ = now_ms;
            return;
        }
        const uint64_t window_ms = now_ms - update_cost_window_start_ms_;
        if (window_ms < 30000) {
            return;
        }
        std::array<char, 200> line{};
        std::snprintf(
            line.data(),
            line.size(),
            "[perf] update busy=%.1f%% of one core calls=%llu avg_us=%llu max_us=%llu in_challenge=%d",
            static_cast<double>(update_cost_total_us_) / 10.0 / static_cast<double>(window_ms),
            static_cast<unsigned long long>(update_cost_calls_),
            static_cast<unsigned long long>(update_cost_total_us_ / (std::max<uint64_t>)(1, update_cost_calls_)),
            static_cast<unsigned long long>(update_cost_max_us_),
            static_cast<int>(last_is_in_challenge_)
        );
        runtime_log_line(line.data());
        // ms of work per second, per section: targets, receiver, pulls,
        // meta+scenario_mgr, scenario_state, state/emit, overlay.
        std::array<char, 400> sections{};
        const double ms_per_tick = 1000.0 / static_cast<double>(freq.QuadPart);
        const double per_second = 1000.0 / static_cast<double>(window_ms);
        auto sec = [&](size_t i) { return perf_section_ticks_[i] * ms_per_tick * per_second; };
        std::snprintf(
            sections.data(),
            sections.size(),
            "[perf] ms/s targets=%.1f receiver=%.1f pulls=%.1f meta_scenario=%.1f scenario_state=%.1f state_emit=%.1f overlay=%.1f lifecycle_shots=%.1f replay_playback=%.1f replay_capture=%.1f",
            sec(0), sec(1), sec(2), sec(3), sec(4), sec(5), sec(6), sec(7), sec(8), sec(9)
        );
        runtime_log_line(sections.data());
        for (auto& ticks : perf_section_ticks_) {
            ticks = 0;
        }
        update_cost_window_start_ms_ = now_ms;
        update_cost_total_us_ = 0;
        update_cost_max_us_ = 0;
        update_cost_calls_ = 0;
    }

    // Section timings inside on_update_impl (QPC ticks), reported with [perf].
    static constexpr size_t k_perf_sections = 10;
    int64_t perf_lap_last_qpc_{0};
    int64_t perf_section_ticks_[k_perf_sections]{};

    void perf_lap_begin() {
        LARGE_INTEGER t{};
        QueryPerformanceCounter(&t);
        perf_lap_last_qpc_ = t.QuadPart;
    }

    void perf_lap(size_t section) {
        LARGE_INTEGER t{};
        QueryPerformanceCounter(&t);
        if (perf_lap_last_qpc_ != 0 && section < k_perf_sections) {
            perf_section_ticks_[section] += t.QuadPart - perf_lap_last_qpc_;
        }
        perf_lap_last_qpc_ = t.QuadPart;
    }

    uint64_t update_cost_window_start_ms_{0};
    uint64_t update_cost_total_us_{0};
    uint64_t update_cost_max_us_{0};
    uint64_t update_cost_calls_{0};

    void emit_bridge_ready_banner() {
        kovaaks::RustBridge::emit_json("{\"ev\":\"ue4ss_mod_loaded\"}");
        kovaaks::RustBridge::emit_json("{\"ev\":\"ue4ss_mode\",\"mode\":\"production_stripped\"}");
        kovaaks::RustBridge::emit_json("{\"ev\":\"ue4ss_prod_diag\",\"enabled\":0}");
        kovaaks::RustBridge::emit_json("{\"ev\":\"ui_textupdate_hook\",\"enabled\":0,\"reason\":\"disabled_in_prod_stripped\"}");
        RC::Output::send<RC::LogLevel::Warning>(STR("[KovaaksBridgeMod] Rust bridge loaded (production stripped).\n"));
    }

    static auto enumerate_properties(RC::Unreal::UStruct* owner) -> std::vector<RC::Unreal::FProperty*> {
        std::vector<RC::Unreal::FProperty*> out{};
        if (!owner || !is_likely_valid_object_ptr(owner)) {
            return out;
        }
        std::unordered_set<RC::Unreal::FProperty*> seen{};
        auto* property = owner->GetPropertyLink();
        while (property && is_likely_valid_object_ptr(property)) {
            if (!seen.insert(property).second) {
                break;
            }
            out.emplace_back(property);
            auto* next = property->GetPropertyLinkNext();
            if (next == property) {
                break;
            }
            property = next;
        }
        return out;
    }

    static auto enumerate_properties_in_chain(RC::Unreal::UStruct* owner) -> std::vector<RC::Unreal::FProperty*> {
        std::vector<RC::Unreal::FProperty*> out{};
        if (!owner || !is_likely_valid_object_ptr(owner)) {
            return out;
        }
        std::unordered_set<RC::Unreal::UStruct*> seen_structs{};
        std::unordered_set<RC::Unreal::FProperty*> seen_properties{};
        for (auto* current = owner; current && is_likely_valid_object_ptr(current); current = current->GetSuperStruct()) {
            if (!seen_structs.insert(current).second) {
                break;
            }
            for (auto* property : enumerate_properties(current)) {
                if (!property || !is_likely_valid_object_ptr(property)) {
                    continue;
                }
                if (seen_properties.insert(property).second) {
                    out.emplace_back(property);
                }
            }
        }
        return out;
    }

    template <typename TFlags>
    static auto property_has_any_flags(
        const RC::Unreal::FProperty* property,
        TFlags flags
    ) -> bool {
        if (!property || !is_likely_valid_object_ptr(property)) {
            return false;
        }
        const auto current = static_cast<uint64_t>(property->GetPropertyFlags());
        const auto wanted = static_cast<uint64_t>(flags);
        return (current & wanted) != 0;
    }

    struct InGameHttpTraceRequest {
        uint64_t id{0};
        uint64_t created_ms{0};
        bool has_auth_token{false};
        std::string auth_token{};
        std::string context_name{};
        std::string url_path{};
        std::string verb{};
        std::string request_body{};
    };

    static auto resolve_struct_property_script_struct(
        RC::Unreal::FStructProperty* struct_property
    ) -> RC::Unreal::UScriptStruct* {
        if (!struct_property || !is_likely_valid_object_ptr(struct_property)) {
            return nullptr;
        }
        const auto it = RC::Unreal::FStructProperty::MemberOffsets.find(STR("Struct"));
        if (it == RC::Unreal::FStructProperty::MemberOffsets.end()) {
            return nullptr;
        }
        const int32_t offset = it->second;
        if (offset <= 0 || offset > 0x400) {
            return nullptr;
        }
        auto** script_struct_ptr = reinterpret_cast<RC::Unreal::UScriptStruct**>(
            reinterpret_cast<uint8_t*>(struct_property) + static_cast<size_t>(offset)
        );
        if (!script_struct_ptr || !is_likely_readable_region(script_struct_ptr, sizeof(void*))) {
            return nullptr;
        }
        auto* script_struct = *script_struct_ptr;
        if (!script_struct || !is_likely_valid_object_ptr(script_struct)) {
            return nullptr;
        }
        return script_struct;
    }

    static auto is_runtime_object_usable(RC::Unreal::UObject* obj) -> bool {
        if (!obj || !is_likely_valid_object_ptr(obj)) {
            return false;
        }
        return !is_rejected_runtime_object_name(obj->GetFullName());
    }

    static auto is_runtime_function_usable(RC::Unreal::UFunction* fn) -> bool {
        if (!fn || !is_likely_valid_object_ptr(fn)) {
            return false;
        }
        return !is_rejected_runtime_function_name(fn->GetFullName());
    }

    static auto ui_widget_probe_object_usable(RC::Unreal::UObject* obj) -> bool {
        if (!is_runtime_object_usable(obj)) {
            return false;
        }
        const auto full_name = obj->GetFullName();
        return full_name.find(STR("None.None:None.None")) == RC::StringType::npos;
    }

    static auto set_direct_fault_context(
        const char*,
        RC::Unreal::UObject*,
        RC::Unreal::UFunction*
    ) -> void {
    }

    static auto should_quarantine_invoke_fault(
        const char*,
        RC::Unreal::UFunction*
    ) -> bool {
        return false;
    }

    static auto quarantine_faulted_function(
        RC::Unreal::UFunction*,
        uint64_t
    ) -> uint32_t {
        return 0;
    }

    static auto is_function_quarantined(
        RC::Unreal::UFunction*,
        uint64_t
    ) -> bool {
        return false;
    }

    auto sync_in_game_overlay_pull_cache(
        int32_t current_is_in_challenge,
        int32_t current_is_in_scenario,
        int32_t current_is_in_scenario_editor,
        int32_t current_scenario_is_paused,
        float current_queue_time_remaining,
        float current_score_total,
        float current_score_total_derived,
        int32_t current_kills_total,
        float current_score_per_minute,
        float current_seconds,
        float current_challenge_average_fps,
        int32_t current_challenge_tick_count,
        float current_time_remaining
    ) -> void {
        const auto sanitize_state = [](int32_t current, int32_t fallback) -> int32_t {
            if (current >= 0) {
                return current;
            }
            if (fallback != std::numeric_limits<int32_t>::min()) {
                return fallback;
            }
            return -1;
        };
        const auto sanitize_metric = [](float current, float fallback) -> float {
            if (std::isfinite(current) && current >= 0.0f) {
                return current;
            }
            if (std::isfinite(fallback) && fallback >= 0.0f) {
                return fallback;
            }
            return -1.0f;
        };
        const bool has_active_runtime_context =
            current_is_in_challenge > 0
            || current_is_in_scenario > 0
            || (std::isfinite(current_time_remaining) && current_time_remaining > 0.05f)
            || (current_challenge_tick_count > 0)
            || (std::isfinite(current_seconds) && current_seconds > 0.05f);
        const auto sanitize_temporal_metric = [has_active_runtime_context](float current, float fallback) -> float {
            if (std::isfinite(current) && current >= 0.0f) {
                return current;
            }
            if (!has_active_runtime_context) {
                return 0.0f;
            }
            if (std::isfinite(fallback) && fallback >= 0.0f) {
                return fallback;
            }
            return -1.0f;
        };

        s_last_pull_is_in_challenge = sanitize_state(current_is_in_challenge, last_is_in_challenge_);
        s_last_pull_is_in_scenario = sanitize_state(current_is_in_scenario, last_is_in_scenario_);
        s_last_pull_is_in_scenario_editor = sanitize_state(current_is_in_scenario_editor, last_is_in_scenario_editor_);
        s_last_pull_scenario_is_in_editor = s_last_pull_is_in_scenario_editor;
        s_last_pull_scenario_is_paused = sanitize_state(current_scenario_is_paused, s_last_pull_scenario_is_paused);

        s_last_pull_queue_time_remaining = sanitize_temporal_metric(
            current_queue_time_remaining,
            last_queue_time_remaining_
        );
        s_last_pull_score = sanitize_metric(current_score_total, last_score_total_);
        s_last_pull_score_derived = sanitize_metric(current_score_total_derived, last_score_total_derived_);
        s_last_pull_kills = sanitize_state(current_kills_total, last_kills_total_);
        s_last_pull_spm = sanitize_metric(current_score_per_minute, last_score_per_minute_);
        s_last_pull_challenge_seconds = sanitize_metric(current_seconds, last_challenge_seconds_total_);
        s_last_pull_challenge_average_fps = sanitize_metric(current_challenge_average_fps, last_challenge_average_fps_);
        s_last_pull_challenge_tick_count = sanitize_state(current_challenge_tick_count, last_challenge_tick_count_);
        s_last_pull_time_remaining = sanitize_temporal_metric(current_time_remaining, last_time_remaining_);
        s_last_run_scenario_name = last_scenario_name_;
    }

    auto try_resolve_current_scenario_identity(
        uint64_t now_ms,
        std::string& out_scenario_name,
        std::string& out_scenario_id,
        std::string& out_scenario_manager_id
    ) -> bool {
        out_scenario_name.clear();
        out_scenario_id.clear();
        out_scenario_manager_id.clear();

        auto* scenario_manager = resolve_scenario_manager_instance(now_ms);
        if (!scenario_manager || !is_likely_valid_object_ptr(scenario_manager)) {
            return false;
        }

        out_scenario_manager_id = utf8_from_wide(object_path_from_full_name(scenario_manager->GetFullName()));
        auto* owner_class = *reinterpret_cast<RC::Unreal::UClass**>(
            reinterpret_cast<uint8_t*>(scenario_manager) + 0x10
        );
        if (!owner_class || !is_likely_valid_object_ptr(owner_class)) {
            return !out_scenario_manager_id.empty();
        }

        auto read_object_property_by_name = [&](const char* wanted_name) -> RC::Unreal::UObject* {
            if (!wanted_name || !*wanted_name) {
                return nullptr;
            }
            for (RC::Unreal::FProperty* property : enumerate_properties_in_chain(owner_class)) {
                if (!property || !is_likely_valid_object_ptr(property)) {
                    continue;
                }
                auto* object_property = RC::Unreal::CastField<RC::Unreal::FObjectPropertyBase>(property);
                if (!object_property || !is_likely_valid_object_ptr(object_property)) {
                    continue;
                }
                if (normalize_ascii(property->GetName()) != wanted_name) {
                    continue;
                }
                void* value_ptr = safe_property_value_ptr(property, scenario_manager);
                if (!value_ptr || !is_likely_readable_region(value_ptr, sizeof(void*))) {
                    continue;
                }
                auto* value = object_property->GetObjectPropertyValue(value_ptr);
                if (!value || !is_likely_valid_object_ptr(value)) {
                    continue;
                }
                const auto full_name = value->GetFullName();
                if (is_rejected_runtime_object_name(full_name)) {
                    continue;
                }
                return value;
            }
            return nullptr;
        };

        RC::Unreal::UObject* current_scenario = nullptr;
        current_scenario = read_object_property_by_name("currentscenario");
        if (!current_scenario) {
            current_scenario = read_object_property_by_name("selectedscenario");
        }
        if (!current_scenario) {
            current_scenario = read_object_property_by_name("activescenario");
        }
        if (!current_scenario) {
            current_scenario = read_object_property_by_name("currentchallenge");
        }

        if (current_scenario && is_likely_valid_object_ptr(current_scenario)) {
            const auto full_name = current_scenario->GetFullName();
            if (!is_rejected_runtime_object_name(full_name)) {
                out_scenario_id = utf8_from_wide(object_path_from_full_name(full_name));
            }
            out_scenario_name = utf8_from_wide(current_scenario->GetName());
            if (!looks_like_real_scenario_name(out_scenario_name)) {
                out_scenario_name.clear();
            }
        }
        if (out_scenario_name.empty() && !out_scenario_id.empty()) {
            out_scenario_name = derive_scenario_name_from_id(out_scenario_id);
        }
        return !out_scenario_name.empty() || !out_scenario_id.empty() || !out_scenario_manager_id.empty();
    }

    auto emit_requested_state_snapshot(uint64_t now_ms, const std::string& request_reason) -> bool {
        const auto reason = kmod_replay::sanitize_state_request_reason(request_reason);
        bool emitted_ok = true;
        std::string scenario_name{};
        std::string scenario_id{};
        std::string scenario_manager_id{};
        (void)try_resolve_current_scenario_identity(now_ms, scenario_name, scenario_id, scenario_manager_id);

        if (scenario_name.empty()) {
            scenario_name = last_scenario_name_;
        }
        if (scenario_name.empty()) {
            scenario_name = s_last_run_scenario_name;
        }
        if (scenario_name.empty() && !scenario_id.empty()) {
            scenario_name = derive_scenario_name_from_id(scenario_id);
        }
        if (!scenario_name.empty()) {
            s_last_run_scenario_name = scenario_name;
        }
        if (scenario_id.empty()) {
            scenario_id = s_last_run_scenario_id;
        } else {
            s_last_run_scenario_id = scenario_id;
        }
        if (scenario_manager_id.empty()) {
            scenario_manager_id = s_last_run_scenario_manager_id;
        } else {
            s_last_run_scenario_manager_id = scenario_manager_id;
        }

        const auto game_state_code = kmod_replay::derive_game_state_code(
            last_is_in_scenario_editor_,
            s_last_pull_scenario_is_paused,
            last_is_in_challenge_,
            last_is_in_scenario_,
            last_is_in_trainer_,
            s_last_pull_queue_time_remaining,
            s_last_pull_time_remaining,
            0
        );
        const auto game_state = kmod_replay::game_state_code_to_string(game_state_code);

        const auto scenario_name_escaped = escape_json_ascii(scenario_name);
        const auto scenario_id_escaped = escape_json_ascii(scenario_id);
        const auto scenario_manager_escaped = escape_json_ascii(scenario_manager_id);
        std::array<char, 1024> metadata{};
        std::snprintf(
            metadata.data(),
            metadata.size(),
            "{\"ev\":\"scenario_metadata\",\"trigger\":\"state_request:%s\",\"run_id\":0,\"ts_ms\":%llu,\"scenario_name\":\"%s\",\"scenario_id\":\"%s\",\"scenario_manager\":\"%s\",\"scenario_play_type\":-1,\"is_in_trainer\":%d,\"is_in_challenge\":%d,\"is_in_scenario\":%d,\"is_in_scenario_editor\":%d,\"is_currently_in_benchmark\":%d,\"challenge_time_length\":-1.0,\"queue_time_remaining\":%.6f,\"game_seconds\":-1.0,\"game_state_code\":%d,\"game_state\":\"%s\",\"source\":\"production_state_sync\"}",
            reason.c_str(),
            static_cast<unsigned long long>(now_ms),
            scenario_name_escaped.c_str(),
            scenario_id_escaped.c_str(),
            scenario_manager_escaped.c_str(),
            last_is_in_trainer_,
            last_is_in_challenge_,
            last_is_in_scenario_,
            last_is_in_scenario_editor_,
            last_is_currently_in_benchmark_,
            static_cast<double>(s_last_pull_queue_time_remaining),
            static_cast<int>(game_state_code),
            game_state
        );
        emitted_ok = kovaaks::RustBridge::emit_json(metadata.data()) && emitted_ok;

        if (!scenario_name.empty()) {
            std::array<char, 512> scenario_msg{};
            std::snprintf(
                scenario_msg.data(),
                scenario_msg.size(),
                "{\"ev\":\"ui_scenario_name\",\"field\":\"%s\",\"source\":\"state_manager\"}",
                scenario_name_escaped.c_str()
            );
            emitted_ok = kovaaks::RustBridge::emit_json(scenario_msg.data()) && emitted_ok;
        }
        std::array<char, 2048> snapshot{};
        std::snprintf(
            snapshot.data(),
            snapshot.size(),
            "{\"ev\":\"pull_snapshot\",\"ts_ms\":%llu,\"is_in_challenge\":%d,\"is_in_scenario\":%d,\"is_in_scenario_editor\":%d,\"is_in_trainer\":%d,\"is_currently_in_benchmark\":%d,\"scenario_is_paused\":%d,\"kills\":%d,\"shots_fired\":%d,\"shots_hit\":%d,\"challenge_tick_count_total\":%d,\"session_time_secs\":%.6f,\"challenge_seconds_total\":%.6f,\"score_total\":%.6f,\"score_total_derived\":%.6f,\"spm\":%.6f,\"kps\":%.6f,\"accuracy_pct\":%.6f,\"challenge_average_fps\":%.6f,\"damage_done\":%.6f,\"damage_possible\":%.6f,\"damage_efficiency\":%.6f,\"time_remaining\":%.6f,\"queue_time_remaining\":%.6f,\"game_state_code\":%d,\"game_state\":\"%s\",\"method\":\"state_request\",\"origin_flag\":\"state_snapshot\",\"source\":\"production_state_sync\"}",
            static_cast<unsigned long long>(now_ms),
            last_is_in_challenge_,
            last_is_in_scenario_,
            last_is_in_scenario_editor_,
            last_is_in_trainer_,
            last_is_currently_in_benchmark_,
            s_last_pull_scenario_is_paused,
            last_kills_total_,
            last_shots_fired_,
            last_shots_hit_,
            last_challenge_tick_count_,
            static_cast<double>(last_seconds_),
            static_cast<double>(last_challenge_seconds_total_),
            static_cast<double>(last_score_total_),
            static_cast<double>(last_score_total_derived_),
            static_cast<double>(last_score_per_minute_),
            static_cast<double>(last_kills_per_second_),
            static_cast<double>(last_accuracy_),
            static_cast<double>(last_challenge_average_fps_),
            static_cast<double>(last_damage_done_),
            static_cast<double>(last_damage_possible_),
            static_cast<double>(last_damage_efficiency_),
            static_cast<double>(last_time_remaining_),
            static_cast<double>(last_queue_time_remaining_),
            static_cast<int>(game_state_code),
            game_state
        );
        emitted_ok = kovaaks::RustBridge::emit_json(snapshot.data()) && emitted_ok;

        std::array<char, 320> ack{};
        std::snprintf(
            ack.data(),
            ack.size(),
            "{\"ev\":\"state_snapshot\",\"source\":\"state_request\",\"reason\":\"%s\",\"ts_ms\":%llu}",
            reason.c_str(),
            static_cast<unsigned long long>(now_ms)
        );
        emitted_ok = kovaaks::RustBridge::emit_json(ack.data()) && emitted_ok;
        maybe_emit_user_management_build_tag();
        maybe_emit_user_management_snapshot_safe(now_ms, true);
        return emitted_ok;
    }

    auto emit_requested_state_snapshot_safe(uint64_t now_ms, const std::string& request_reason) -> bool {
#if defined(_MSC_VER)
        __try {
            return emit_requested_state_snapshot(now_ms, request_reason);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            ++replay_fault_count_;
            replay_fault_backoff_until_ms_ = now_ms + 1500;
            if (rust_ready_) {
                std::array<char, 256> json{};
                std::snprintf(
                    json.data(),
                    json.size(),
                    "{\"ev\":\"ue4ss_prod_fault\",\"where\":\"state_snapshot\",\"count\":%u,\"disabled\":0}",
                    static_cast<unsigned int>(replay_fault_count_)
                );
                kovaaks::RustBridge::emit_json(json.data());
            }
            reset_runtime_resolvers();
            return false;
        }
#else
        return emit_requested_state_snapshot(now_ms, request_reason);
#endif
    }

    void on_update_impl_safe() {
#if defined(_MSC_VER)
        __try {
            on_update_impl();
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            handle_runtime_fault("on_update");
        }
#else
        on_update_impl();
#endif
    }

    void maybe_emit_user_management_snapshot_safe(uint64_t now_ms, bool force) {
        if (!user_mgmt_enabled_ || now_ms < user_management_fault_backoff_until_ms_) {
            return;
        }
#if defined(_MSC_VER)
        __try {
            maybe_emit_user_management_snapshot(now_ms, force);
            user_management_fault_count_ = 0;
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            ++user_management_fault_count_;
            user_management_fault_backoff_until_ms_ = now_ms + 15000;
            std::array<char, 256> line{};
            std::snprintf(
                line.data(),
                line.size(),
                "[user_mgmt] fault trapped; backoff active count=%u",
                static_cast<unsigned int>(user_management_fault_count_)
            );
            runtime_log_line(line.data());
        }
#else
        maybe_emit_user_management_snapshot(now_ms, force);
        user_management_fault_count_ = 0;
#endif
    }

    auto note_replay_runtime_fault(uint64_t now_ms, const char* where) -> void {
        ++replay_fault_count_;
        replay_fault_backoff_until_ms_ = now_ms + 3000;
        if (replay_fault_count_ >= 6) {
            replay_fault_latched_ = true;
        }
        if (rust_ready_) {
            std::array<char, 320> json{};
            std::snprintf(
                json.data(),
                json.size(),
                "{\"ev\":\"ue4ss_prod_replay_fault\",\"where\":\"%s\",\"count\":%u,\"latched\":%u}",
                where ? where : "unknown",
                static_cast<unsigned int>(replay_fault_count_),
                replay_fault_latched_ ? 1u : 0u
            );
            kovaaks::RustBridge::emit_json(json.data());
        }
        // Replay faults must not poison the live state pipeline. Keep replay isolated
        // and let normal state resolvers continue emitting.
    }

    auto replay_playback_tick_safe(uint64_t now_ms) -> bool {
        if (replay_fault_latched_ || now_ms < replay_fault_backoff_until_ms_) {
            return false;
        }
#if defined(_MSC_VER)
        __try {
            kmod_replay::replay_ingame_playback_tick(now_ms);
            return true;
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            note_replay_runtime_fault(now_ms, "replay_playback_tick");
            return false;
        }
#else
        kmod_replay::replay_ingame_playback_tick(now_ms);
        return true;
#endif
    }

    auto replay_tick_safe(uint64_t now_ms, const kmod_replay::ReplayTickInput& input) -> bool {
        if (replay_capture_disabled_) {
            return false;
        }
        if (replay_fault_latched_ || now_ms < replay_fault_backoff_until_ms_) {
            return false;
        }
#if defined(_MSC_VER)
        __try {
            kmod_replay::replay_tick(input);
            return true;
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            replay_capture_disabled_ = true;
            note_replay_runtime_fault(now_ms, "replay_tick");
            return false;
        }
#else
        kmod_replay::replay_tick(input);
        return true;
#endif
    }

    auto replay_playback_command_safe(
        uint64_t now_ms,
        const kmod_replay::BridgeCommand& command
    ) -> bool {
        if (replay_fault_latched_ || now_ms < replay_fault_backoff_until_ms_) {
            return false;
        }
#if defined(_MSC_VER)
        __try {
            kmod_replay::replay_ingame_playback_handle_command(command, now_ms);
            return true;
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            note_replay_runtime_fault(now_ms, "replay_playback_command");
            return false;
        }
#else
        kmod_replay::replay_ingame_playback_handle_command(command, now_ms);
        return true;
#endif
    }

    #include "kmod/in_game_overlay.inl"

    struct EmitTagScope {
        KovaaksBridgeModProduction& self;
        const char* prev_method{nullptr};
        const char* prev_origin_flag{nullptr};

        EmitTagScope(KovaaksBridgeModProduction& owner, const char* method, const char* origin_flag)
            : self(owner) {
            prev_method = self.emit_method_;
            prev_origin_flag = self.emit_origin_flag_;
            self.emit_method_ = method ? method : "unknown";
            self.emit_origin_flag_ = origin_flag ? origin_flag : "unknown";
        }

        ~EmitTagScope() {
            self.emit_method_ = prev_method ? prev_method : "unknown";
            self.emit_origin_flag_ = prev_origin_flag ? prev_origin_flag : "unknown";
        }
    };

    static auto try_read_hook_param_i32(void* params, size_t offset, int32_t& out_value) -> bool {
        if (!params) {
            return false;
        }
        auto* ptr = reinterpret_cast<const uint8_t*>(params) + offset;
        if (!is_likely_readable_region(ptr, sizeof(int32_t))) {
            return false;
        }
        std::memcpy(&out_value, ptr, sizeof(int32_t));
        return true;
    }

    static auto try_read_hook_param_f32(void* params, size_t offset, float& out_value) -> bool {
        if (!params) {
            return false;
        }
        auto* ptr = reinterpret_cast<const uint8_t*>(params) + offset;
        if (!is_likely_readable_region(ptr, sizeof(float))) {
            return false;
        }
        std::memcpy(&out_value, ptr, sizeof(float));
        return std::isfinite(out_value);
    }

    static auto derive_accuracy_pct(int32_t shots_hit, int32_t shots_fired) -> float {
        if (shots_fired <= 0 || shots_hit < 0) {
            return -1.0f;
        }
        return (static_cast<float>(shots_hit) * 100.0f) / static_cast<float>(shots_fired);
    }

    static auto normalize_accuracy_pct(float value, int32_t shots_hit, int32_t shots_fired) -> float {
        if (!std::isfinite(value) || value < 0.0f) {
            return -1.0f;
        }

        if (shots_fired > 0 && shots_hit >= 0) {
            const float derived = derive_accuracy_pct(shots_hit, shots_fired);
            if (std::isfinite(derived) && derived >= 0.0f && value <= 1.0f) {
                const float direct_delta = std::fabs(value - derived);
                const float ratio_delta = std::fabs((value * 100.0f) - derived);
                const float tolerance = std::max(0.35f, derived * 0.08f);
                if (ratio_delta + 0.0001f < direct_delta && ratio_delta <= tolerance) {
                    return value * 100.0f;
                }
            }
        }

        return value;
    }

    static auto safe_process_event_call(
        RC::Unreal::UObject* caller,
        RC::Unreal::UFunction* fn,
        void* params
    ) -> bool {
        if (!caller || !fn) {
            return false;
        }
#if defined(_MSC_VER)
        __try {
            caller->ProcessEvent(fn, params);
            return true;
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            return false;
        }
#else
        caller->ProcessEvent(fn, params);
        return true;
#endif
    }

    void handle_live_metric_hook(LiveMetricHookKind kind, RC::Unreal::UnrealScriptFunctionCallableContext& call_ctx) {
        if (updates_disabled_ || !rust_ready_) {
            return;
        }
        auto* frame = reinterpret_cast<RC::Unreal::FFrame_50_AndBelow*>(&call_ctx.TheStack);
        void* params = frame ? static_cast<void*>(frame->Locals) : nullptr;
        if (!params) {
            return;
        }

        const uint64_t now = GetTickCount64();
        EmitTagScope hook_scope(*this, "receiver_hook", "non_ui_live_hook");

        if (call_ctx.Context && is_likely_valid_object_ptr(call_ctx.Context)) {
            // This hook fires on the game thread for every metric update; the
            // receiver object is stable within a run, so classify it once.
            if (call_ctx.Context != hook_context_last_) {
                const auto context_name = call_ctx.Context->GetFullName();
                hook_context_last_ = call_ctx.Context;
                hook_context_is_receiver_ =
                    !is_rejected_runtime_object_name(context_name)
                    && context_name.find(STR("PerformanceIndicatorsStateReceiver")) != RC::StringType::npos;
            }
            if (hook_context_is_receiver_) {
                live_metric_receiver_hint_ = call_ctx.Context;
                live_metric_receiver_hint_ms_ = now;
                state_receiver_instance_ = call_ctx.Context;
                next_receiver_resolve_ms_ = now + 100;
            }
        }

        switch (kind) {
        case LiveMetricHookKind::Seconds: {
            float value = -1.0f;
            if (!try_read_hook_param_f32(params, 0x20, value) || value < 0.0f) {
                return;
            }
            bool changed = false;
            changed |= cache_pull_f32("pull_seconds_total", last_seconds_, value, last_nonzero_seconds_ms_, now);
            changed |= cache_pull_f32(
                "pull_challenge_seconds_total",
                last_challenge_seconds_total_,
                value,
                last_nonzero_challenge_seconds_ms_,
                now
            );
            if (changed) {
                live_pull_snapshot_dirty_ = true;
            }
            last_runtime_progress_ms_ = now;
            last_observed_seconds_value_ = value;
            break;
        }
        case LiveMetricHookKind::Score: {
            float value = -1.0f;
            if (!try_read_hook_param_f32(params, 0x20, value) || value < 0.0f) {
                return;
            }
            if (cache_pull_f32("pull_score_total", last_score_total_, value, last_nonzero_score_total_ms_, now)) {
                live_pull_snapshot_dirty_ = true;
            }
            last_runtime_progress_ms_ = now;
            break;
        }
        case LiveMetricHookKind::ShotsFired: {
            int32_t value = -1;
            if (!try_read_hook_param_i32(params, 0x20, value) || value < 0) {
                return;
            }
            if (cache_pull_i32("pull_shots_fired_total", last_shots_fired_, value, last_nonzero_shots_fired_ms_, now)) {
                live_pull_snapshot_dirty_ = true;
            }
            last_runtime_progress_ms_ = now;
            break;
        }
        case LiveMetricHookKind::ShotsHit: {
            int32_t value = -1;
            if (!try_read_hook_param_i32(params, 0x20, value) || value < 0) {
                return;
            }
            if (cache_pull_i32("pull_shots_hit_total", last_shots_hit_, value, last_nonzero_shots_hit_ms_, now)) {
                live_pull_snapshot_dirty_ = true;
            }
            last_runtime_progress_ms_ = now;
            break;
        }
        case LiveMetricHookKind::ChallengeTickCount: {
            int32_t value = -1;
            if (!try_read_hook_param_i32(params, 0x20, value) || value < 0) {
                return;
            }
            if (cache_pull_i32(
                "pull_challenge_tick_count_total",
                last_challenge_tick_count_,
                value,
                last_nonzero_challenge_tick_count_ms_,
                now
            )) {
                live_pull_snapshot_dirty_ = true;
            }
            last_runtime_progress_ms_ = now;
            last_observed_challenge_tick_count_ = value;
            break;
        }
        case LiveMetricHookKind::Kills: {
            int32_t value = -1;
            if (!try_read_hook_param_i32(params, 0x20, value) || value < 0) {
                return;
            }
            if (cache_pull_i32("pull_kills_total", last_kills_total_, value, last_nonzero_kills_total_ms_, now)) {
                live_pull_snapshot_dirty_ = true;
            }
            last_runtime_progress_ms_ = now;
            break;
        }
        case LiveMetricHookKind::Accuracy: {
            float value = -1.0f;
            if (!try_read_hook_param_f32(params, 0x20, value) || value < 0.0f) {
                return;
            }
            value = normalize_accuracy_pct(value, last_shots_hit_, last_shots_fired_);
            const bool have_authoritative_shot_counts = last_shots_fired_ > 0 && last_shots_hit_ >= 0;
            if (!have_authoritative_shot_counts
                && cache_pull_f32("pull_accuracy", last_accuracy_, value, last_nonzero_accuracy_ms_, now)) {
                live_pull_snapshot_dirty_ = true;
            }
            last_runtime_progress_ms_ = now;
            break;
        }
        case LiveMetricHookKind::ScorePerMinute: {
            float value = -1.0f;
            if (!try_read_hook_param_f32(params, 0x20, value) || value < 0.0f) {
                return;
            }
            if (cache_pull_f32("pull_score_per_minute", last_score_per_minute_, value, last_nonzero_spm_ms_, now)) {
                live_pull_snapshot_dirty_ = true;
            }
            last_runtime_progress_ms_ = now;
            break;
        }
        case LiveMetricHookKind::KillsPerSecond: {
            float value = -1.0f;
            if (!try_read_hook_param_f32(params, 0x20, value) || value < 0.0f) {
                return;
            }
            if (cache_pull_f32(
                "pull_kills_per_second",
                last_kills_per_second_,
                value,
                last_nonzero_kills_per_second_ms_,
                now
            )) {
                live_pull_snapshot_dirty_ = true;
            }
            last_runtime_progress_ms_ = now;
            break;
        }
        case LiveMetricHookKind::DamageDone: {
            float value = -1.0f;
            if (!try_read_hook_param_f32(params, 0x20, value) || value < 0.0f) {
                return;
            }
            if (cache_pull_f32("pull_damage_done", last_damage_done_, value, last_nonzero_damage_done_ms_, now)) {
                live_pull_snapshot_dirty_ = true;
            }
            last_runtime_progress_ms_ = now;
            break;
        }
        case LiveMetricHookKind::DamagePossible: {
            float value = -1.0f;
            if (!try_read_hook_param_f32(params, 0x20, value) || value < 0.0f) {
                return;
            }
            if (cache_pull_f32(
                "pull_damage_possible",
                last_damage_possible_,
                value,
                last_nonzero_damage_possible_ms_,
                now
            )) {
                live_pull_snapshot_dirty_ = true;
            }
            last_runtime_progress_ms_ = now;
            break;
        }
        case LiveMetricHookKind::DamageEfficiency: {
            float value = -1.0f;
            if (!try_read_hook_param_f32(params, 0x20, value) || value < 0.0f) {
                return;
            }
            if (cache_pull_f32(
                "pull_damage_efficiency",
                last_damage_efficiency_,
                value,
                last_nonzero_damage_efficiency_ms_,
                now
            )) {
                live_pull_snapshot_dirty_ = true;
            }
            last_runtime_progress_ms_ = now;
            break;
        }
        case LiveMetricHookKind::ChallengeAverageFps: {
            float value = -1.0f;
            if (!try_read_hook_param_f32(params, 0x20, value) || value < 0.0f) {
                return;
            }
            if (cache_pull_f32(
                "pull_challenge_average_fps",
                last_challenge_average_fps_,
                value,
                last_nonzero_challenge_average_fps_ms_,
                now
            )) {
                live_pull_snapshot_dirty_ = true;
            }
            last_runtime_progress_ms_ = now;
            break;
        }
        default:
            break;
        }
    }

    void register_live_metric_hooks() {
        std::string newly_registered{};
        uint32_t new_count = 0;
        auto bind = [&](RC::Unreal::UFunction* fn, LiveMetricHookKind kind, const char* name) {
            if (!fn || !is_likely_valid_object_ptr(fn)) {
                return;
            }
            for (const auto& existing : live_metric_hook_bindings_) {
                if (existing.first == fn) {
                    return;
                }
            }
            const auto callback_id = fn->RegisterPostHook(
                [](RC::Unreal::UnrealScriptFunctionCallableContext& call_ctx, void* custom_data) {
                    auto* self = s_instance_;
                    if (!self) {
                        return;
                    }
                    const auto raw = reinterpret_cast<uintptr_t>(custom_data);
                    self->handle_live_metric_hook(static_cast<LiveMetricHookKind>(raw), call_ctx);
                },
                reinterpret_cast<void*>(static_cast<uintptr_t>(kind))
            );
            live_metric_hook_bindings_.emplace_back(fn, callback_id);
            if (!newly_registered.empty()) {
                newly_registered += ',';
            }
            newly_registered += name;
            ++new_count;
        };

        bind(targets_.receive_seconds, LiveMetricHookKind::Seconds, "receive_seconds");
        bind(targets_.receive_seconds_single, LiveMetricHookKind::Seconds, "receive_seconds_single");
        bind(targets_.receive_seconds_value_else, LiveMetricHookKind::Seconds, "receive_seconds_value_else");
        bind(targets_.receive_seconds_value_or, LiveMetricHookKind::Seconds, "receive_seconds_value_or");
        bind(targets_.receive_kills, LiveMetricHookKind::Kills, "receive_kills");
        bind(targets_.receive_kills_single, LiveMetricHookKind::Kills, "receive_kills_single");
        bind(targets_.receive_kills_value_else, LiveMetricHookKind::Kills, "receive_kills_value_else");
        bind(targets_.receive_kills_value_or, LiveMetricHookKind::Kills, "receive_kills_value_or");
        bind(targets_.receive_score, LiveMetricHookKind::Score, "receive_score");
        bind(targets_.receive_score_single, LiveMetricHookKind::Score, "receive_score_single");
        bind(targets_.receive_score_value_else, LiveMetricHookKind::Score, "receive_score_value_else");
        bind(targets_.receive_score_value_or, LiveMetricHookKind::Score, "receive_score_value_or");
        bind(targets_.receive_accuracy, LiveMetricHookKind::Accuracy, "receive_accuracy");
        bind(targets_.receive_accuracy_single, LiveMetricHookKind::Accuracy, "receive_accuracy_single");
        bind(targets_.receive_accuracy_value_else, LiveMetricHookKind::Accuracy, "receive_accuracy_value_else");
        bind(targets_.receive_accuracy_value_or, LiveMetricHookKind::Accuracy, "receive_accuracy_value_or");
        bind(targets_.receive_shots_fired, LiveMetricHookKind::ShotsFired, "receive_shots_fired");
        bind(targets_.receive_shots_fired_single, LiveMetricHookKind::ShotsFired, "receive_shots_fired_single");
        bind(targets_.receive_shots_fired_value_else, LiveMetricHookKind::ShotsFired, "receive_shots_fired_value_else");
        bind(targets_.receive_shots_fired_value_or, LiveMetricHookKind::ShotsFired, "receive_shots_fired_value_or");
        bind(targets_.receive_shots_hit, LiveMetricHookKind::ShotsHit, "receive_shots_hit");
        bind(targets_.receive_shots_hit_single, LiveMetricHookKind::ShotsHit, "receive_shots_hit_single");
        bind(targets_.receive_shots_hit_value_else, LiveMetricHookKind::ShotsHit, "receive_shots_hit_value_else");
        bind(targets_.receive_shots_hit_value_or, LiveMetricHookKind::ShotsHit, "receive_shots_hit_value_or");
        bind(targets_.receive_score_per_minute, LiveMetricHookKind::ScorePerMinute, "receive_score_per_minute");
        bind(targets_.receive_score_per_minute_value_else, LiveMetricHookKind::ScorePerMinute, "receive_score_per_minute_value_else");
        bind(targets_.receive_score_per_minute_value_or, LiveMetricHookKind::ScorePerMinute, "receive_score_per_minute_value_or");
        bind(targets_.receive_kills_per_second, LiveMetricHookKind::KillsPerSecond, "receive_kills_per_second");
        bind(targets_.receive_kills_per_second_value_else, LiveMetricHookKind::KillsPerSecond, "receive_kills_per_second_value_else");
        bind(targets_.receive_kills_per_second_value_or, LiveMetricHookKind::KillsPerSecond, "receive_kills_per_second_value_or");
        bind(targets_.receive_damage_done, LiveMetricHookKind::DamageDone, "receive_damage_done");
        bind(targets_.receive_damage_done_value_else, LiveMetricHookKind::DamageDone, "receive_damage_done_value_else");
        bind(targets_.receive_damage_possible, LiveMetricHookKind::DamagePossible, "receive_damage_possible");
        bind(targets_.receive_damage_possible_value_else, LiveMetricHookKind::DamagePossible, "receive_damage_possible_value_else");
        bind(targets_.receive_damage_efficiency, LiveMetricHookKind::DamageEfficiency, "receive_damage_efficiency");
        bind(targets_.receive_damage_efficiency_value_else, LiveMetricHookKind::DamageEfficiency, "receive_damage_efficiency_value_else");
        bind(targets_.receive_challenge_average_fps, LiveMetricHookKind::ChallengeAverageFps, "receive_challenge_average_fps");
        bind(targets_.receive_challenge_average_fps_single, LiveMetricHookKind::ChallengeAverageFps, "receive_challenge_average_fps_single");
        bind(targets_.receive_challenge_average_fps_value_else, LiveMetricHookKind::ChallengeAverageFps, "receive_challenge_average_fps_value_else");
        bind(targets_.receive_challenge_average_fps_value_or, LiveMetricHookKind::ChallengeAverageFps, "receive_challenge_average_fps_value_or");
        bind(targets_.receive_challenge_tick_count, LiveMetricHookKind::ChallengeTickCount, "receive_challenge_tick_count");
        bind(targets_.receive_challenge_tick_count_single, LiveMetricHookKind::ChallengeTickCount, "receive_challenge_tick_count_single");
        bind(targets_.receive_challenge_tick_count_value_else, LiveMetricHookKind::ChallengeTickCount, "receive_challenge_tick_count_value_else");
        bind(targets_.receive_challenge_tick_count_value_or, LiveMetricHookKind::ChallengeTickCount, "receive_challenge_tick_count_value_or");

        live_metric_hooks_registered_ = !live_metric_hook_bindings_.empty();
        if (new_count > 0) {
            std::array<char, 128> summary{};
            std::snprintf(
                summary.data(),
                summary.size(),
                "[hooks] live_metric registered new=%u total=%u",
                static_cast<unsigned int>(new_count),
                static_cast<unsigned int>(live_metric_hook_bindings_.size())
            );
            runtime_log_line(summary.data());
            log_name_list("[hooks] live_metric new", newly_registered);
        }
    }

    void unregister_live_metric_hooks() {
        for (auto& binding : live_metric_hook_bindings_) {
            if (binding.first && is_likely_valid_object_ptr(binding.first)) {
                binding.first->UnregisterHook(binding.second);
            }
        }
        live_metric_hook_bindings_.clear();
        live_metric_hooks_registered_ = false;
    }

    static auto truncate_http_trace_field(std::string value, size_t max_len) -> std::string {
        if (value.empty()) {
            return {};
        }
        value.erase(
            std::remove_if(
                value.begin(),
                value.end(),
                [](unsigned char ch) { return ch < 0x20; }
            ),
            value.end()
        );
        while (!value.empty() && std::isspace(static_cast<unsigned char>(value.front()))) {
            value.erase(value.begin());
        }
        while (!value.empty() && std::isspace(static_cast<unsigned char>(value.back()))) {
            value.pop_back();
        }
        if (value.size() <= max_len) {
            return value;
        }
        value.resize(max_len);
        value += "...(truncated)";
        return value;
    }

    bool read_object_param_full_name(
        RC::Unreal::UFunction* fn,
        void* params,
        const char* wanted_name,
        std::string& out
    ) {
        out.clear();
        if (!fn || !params || !wanted_name || !*wanted_name) {
            return false;
        }
        const auto wanted_key = canonicalize_property_name(wanted_name);
        for (RC::Unreal::FProperty* property : enumerate_properties(fn)) {
            if (!property || !is_likely_valid_object_ptr(property)) {
                continue;
            }
            if (!property_has_any_flags(property, RC::Unreal::CPF_Parm)) {
                continue;
            }
            if (canonicalize_property_name(normalize_ascii(property->GetName())) != wanted_key) {
                continue;
            }
            auto* object_property = RC::Unreal::CastField<RC::Unreal::FObjectPropertyBase>(property);
            if (!object_property || !is_likely_valid_object_ptr(object_property)) {
                continue;
            }
            void* value_ptr = safe_property_value_ptr(property, params);
            if (!value_ptr || !is_likely_readable_region(value_ptr, sizeof(void*))) {
                continue;
            }
            auto* object_value = object_property->GetObjectPropertyValue(value_ptr);
            if (!object_value || !is_likely_valid_object_ptr(object_value)) {
                continue;
            }
            out = trim_ascii_token(object_value->GetFullName());
            return !out.empty();
        }
        return false;
    }

    bool read_struct_param_value_ptr(
        RC::Unreal::UFunction* fn,
        void* params,
        const char* wanted_name,
        RC::Unreal::UStruct*& out_struct,
        void*& out_ptr
    ) {
        out_struct = nullptr;
        out_ptr = nullptr;
        if (!fn || !params || !wanted_name || !*wanted_name) {
            return false;
        }
        const auto wanted_key = canonicalize_property_name(wanted_name);
        for (RC::Unreal::FProperty* property : enumerate_properties(fn)) {
            if (!property || !is_likely_valid_object_ptr(property)) {
                continue;
            }
            if (!property_has_any_flags(property, RC::Unreal::CPF_Parm)) {
                continue;
            }
            if (canonicalize_property_name(normalize_ascii(property->GetName())) != wanted_key) {
                continue;
            }
            auto* struct_property = RC::Unreal::CastField<RC::Unreal::FStructProperty>(property);
            if (!struct_property || !is_likely_valid_object_ptr(struct_property)) {
                continue;
            }
            void* value_ptr = safe_property_value_ptr(property, params);
            if (!value_ptr) {
                continue;
            }
            auto* script_struct = resolve_struct_property_script_struct(struct_property);
            if (!script_struct || !is_likely_valid_object_ptr(script_struct)) {
                continue;
            }
            out_struct = reinterpret_cast<RC::Unreal::UStruct*>(script_struct);
            out_ptr = value_ptr;
            return true;
        }
        return false;
    }

    bool read_nested_struct_value_ptr(
        RC::Unreal::UStruct* owner,
        void* container,
        const char* wanted_name,
        RC::Unreal::UStruct*& out_struct,
        void*& out_ptr
    ) {
        out_struct = nullptr;
        out_ptr = nullptr;
        if (!owner || !container || !wanted_name || !*wanted_name) {
            return false;
        }
        const auto wanted_key = canonicalize_property_name(wanted_name);
        for (RC::Unreal::FProperty* property : enumerate_properties_in_chain(owner)) {
            if (!property || !is_likely_valid_object_ptr(property)) {
                continue;
            }
            if (canonicalize_property_name(normalize_ascii(property->GetName())) != wanted_key) {
                continue;
            }
            auto* struct_property = RC::Unreal::CastField<RC::Unreal::FStructProperty>(property);
            if (!struct_property || !is_likely_valid_object_ptr(struct_property)) {
                continue;
            }
            void* value_ptr = safe_property_value_ptr(property, container);
            if (!value_ptr) {
                continue;
            }
            auto* script_struct = resolve_struct_property_script_struct(struct_property);
            if (!script_struct || !is_likely_valid_object_ptr(script_struct)) {
                continue;
            }
            out_struct = reinterpret_cast<RC::Unreal::UStruct*>(script_struct);
            out_ptr = value_ptr;
            return true;
        }
        return false;
    }

    bool read_http_trace_request_params(
        RC::Unreal::UFunction* fn,
        void* params,
        InGameHttpTraceRequest& out_trace
    ) {
        RC::Unreal::UStruct* params_struct = nullptr;
        void* params_ptr = nullptr;
        if (!read_struct_param_value_ptr(fn, params, "params", params_struct, params_ptr)) {
            return false;
        }

        RC::Unreal::UStruct* network_request_struct = nullptr;
        void* network_request_ptr = nullptr;
        read_nested_struct_value_ptr(
            params_struct,
            params_ptr,
            "networkrequest",
            network_request_struct,
            network_request_ptr
        );

        std::string steam_auth_token{};
        read_string_property_named(params_struct, params_ptr, "steamauthtoken", steam_auth_token);
        out_trace.has_auth_token = !steam_auth_token.empty();
        out_trace.auth_token = std::move(steam_auth_token);

        if (network_request_struct && network_request_ptr) {
            read_string_property_named(network_request_struct, network_request_ptr, "urlpath", out_trace.url_path);
            read_string_property_named(network_request_struct, network_request_ptr, "verb", out_trace.verb);
            read_string_property_named(network_request_struct, network_request_ptr, "messagebody", out_trace.request_body);
        }

        out_trace.url_path = truncate_http_trace_field(std::move(out_trace.url_path), 320);
        out_trace.verb = truncate_http_trace_field(std::move(out_trace.verb), 32);
        out_trace.request_body = truncate_http_trace_field(std::move(out_trace.request_body), 1024);
        return !out_trace.url_path.empty() || !out_trace.request_body.empty();
    }

    bool should_trace_http_request_url(std::string_view url_path) const {
        if (url_path.empty()) {
            return false;
        }
        if (verbose_logs_ || http_trace_enabled_) {
            return true;
        }
        return url_path.find("DownloadTopAdjacentLeaderboardEntries") != std::string_view::npos
            || url_path.find("leaderboard") != std::string_view::npos;
    }

    RC::Unreal::UObject* read_http_trace_return_object(RC::Unreal::UFunction* fn, void* params) {
        if (!fn || !params) {
            return nullptr;
        }
        for (RC::Unreal::FProperty* property : enumerate_properties(fn)) {
            if (!property || !is_likely_valid_object_ptr(property)) {
                continue;
            }
            if (!property_has_any_flags(property, RC::Unreal::CPF_ReturnParm)) {
                continue;
            }
            auto* object_property = RC::Unreal::CastField<RC::Unreal::FObjectPropertyBase>(property);
            if (!object_property || !is_likely_valid_object_ptr(object_property)) {
                continue;
            }
            void* value_ptr = safe_property_value_ptr(property, params);
            if (!value_ptr || !is_likely_readable_region(value_ptr, sizeof(void*))) {
                continue;
            }
            auto* object_value = object_property->GetObjectPropertyValue(value_ptr);
            if (object_value && is_likely_valid_object_ptr(object_value)) {
                return object_value;
            }
        }
        return nullptr;
    }

    void emit_http_trace_request(const InGameHttpTraceRequest& trace) {
        char summary[512]{};
        std::snprintf(
            summary,
            sizeof(summary),
            "[http_trace] request id=%llu verb=%s auth=%u ctx=%s url=%s body_len=%zu",
            static_cast<unsigned long long>(trace.id),
            trace.verb.empty() ? "?" : trace.verb.c_str(),
            trace.has_auth_token ? 1u : 0u,
            trace.context_name.empty() ? "?" : trace.context_name.c_str(),
            trace.url_path.empty() ? "?" : trace.url_path.c_str(),
            trace.request_body.size()
        );
        runtime_log_line(summary);

        std::string json;
        json.reserve(512 + trace.request_body.size());
        json += "{\"ev\":\"kovaaks_ingame_http_request\",\"id\":";
        json += std::to_string(trace.id);
        json += ",\"context\":\"";
        json += escape_json_ascii(trace.context_name);
        json += "\",\"url\":\"";
        json += trace.url_path;
        json += "\",\"verb\":\"";
        json += trace.verb;
        json += "\",\"has_auth\":";
        json += trace.has_auth_token ? "true" : "false";
        json += ",\"body\":\"";
        json += trace.request_body;
        json += "\"}";
        kovaaks::RustBridge::emit_json(json.c_str());
    }

    void emit_http_trace_response(
        const InGameHttpTraceRequest& trace,
        const char* phase,
        int32_t response_code,
        const std::string& response_contents
    ) {
        char summary[512]{};
        std::snprintf(
            summary,
            sizeof(summary),
            "[http_trace] %s id=%llu code=%d ctx=%s url=%s resp_len=%zu",
            phase ? phase : "response",
            static_cast<unsigned long long>(trace.id),
            response_code,
            trace.context_name.empty() ? "?" : trace.context_name.c_str(),
            trace.url_path.empty() ? "?" : trace.url_path.c_str(),
            response_contents.size()
        );
        runtime_log_line(summary);

        std::string json;
        json.reserve(512 + response_contents.size());
        json += "{\"ev\":\"kovaaks_ingame_http_response\",\"phase\":\"";
        json += escape_json_ascii(phase ? phase : "response");
        json += "\",\"id\":";
        json += std::to_string(trace.id);
        json += ",\"context\":\"";
        json += escape_json_ascii(trace.context_name);
        json += "\",\"url\":\"";
        json += trace.url_path;
        json += "\",\"verb\":\"";
        json += trace.verb;
        json += "\",\"code\":";
        json += std::to_string(response_code);
        json += ",\"body\":\"";
        json += trace.request_body;
        json += "\",\"response\":\"";
        json += response_contents;
        json += "\"}";
        kovaaks::RustBridge::emit_json(json.c_str());
    }

    void handle_http_trace_hook_impl(
        RC::Unreal::UFunction* fn,
        RC::Unreal::UnrealScriptFunctionCallableContext& call_ctx
    ) {
        if (!fn || !is_likely_valid_object_ptr(fn)) {
            return;
        }
        auto* frame = reinterpret_cast<RC::Unreal::FFrame_50_AndBelow*>(&call_ctx.TheStack);
        void* params = frame ? static_cast<void*>(frame->Locals) : nullptr;
        if (!params) {
            return;
        }

        if (fn == targets_.send_sa_http_request) {
            if (!http_trace_enabled_) {
                return;
            }
            InGameHttpTraceRequest trace{};
            trace.id = next_http_trace_request_id_++;
            trace.created_ms = GetTickCount64();
            read_object_param_full_name(fn, params, "worldcontextobject", trace.context_name);
            read_http_trace_request_params(fn, params, trace);
            if (!should_trace_http_request_url(trace.url_path)) {
                return;
            }
            if (!trace.auth_token.empty()) {
                last_sa_http_auth_token_ = trace.auth_token;
            }

            auto* request_object = read_http_trace_return_object(fn, params);
            if (request_object && is_likely_valid_object_ptr(request_object)) {
                pending_http_trace_requests_[request_object] = trace;
            }
            emit_http_trace_request(trace);
            return;
        }

        if (!call_ctx.Context || !is_likely_valid_object_ptr(call_ctx.Context)) {
            return;
        }
        auto it = pending_http_trace_requests_.find(call_ctx.Context);
        if (it == pending_http_trace_requests_.end()) {
            return;
        }

        std::string response_contents{};
        read_string_property_named(fn, params, "contents", response_contents);
        response_contents = truncate_http_trace_field(std::move(response_contents), 2048);

        if (fn == targets_.sa_http_invoke_success) {
            if (user_mgmt_enabled_) {
                handle_user_management_http_response(call_ctx.Context, true, 200, response_contents, GetTickCount64());
            }
            emit_http_trace_response(it->second, "success", 200, response_contents);
            pending_http_trace_requests_.erase(it);
            return;
        }

        if (fn == targets_.sa_http_invoke_error) {
            int32_t response_code = 0;
            read_i32_property_named(fn, params, "httpresultcode", response_code);
            if (user_mgmt_enabled_) {
                handle_user_management_http_response(
                    call_ctx.Context,
                    false,
                    response_code,
                    response_contents,
                    GetTickCount64()
                );
            }
            emit_http_trace_response(it->second, "error", response_code, response_contents);
            pending_http_trace_requests_.erase(it);
        }
    }

    void handle_http_trace_hook(
        RC::Unreal::UFunction* fn,
        RC::Unreal::UnrealScriptFunctionCallableContext& call_ctx
    ) {
#if defined(_MSC_VER)
        __try {
            handle_http_trace_hook_impl(fn, call_ctx);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            return;
        }
#else
        handle_http_trace_hook_impl(fn, call_ctx);
#endif
    }

    void register_http_trace_hooks() {
        std::string newly_registered{};
        uint32_t new_count = 0;
        auto bind = [&](RC::Unreal::UFunction* fn, const char* name) {
            if (!fn || !is_likely_valid_object_ptr(fn)) {
                return;
            }
            for (const auto& existing : http_trace_hook_bindings_) {
                if (existing.first == fn) {
                    return;
                }
            }
            const auto callback_id = fn->RegisterPostHook(
                [](RC::Unreal::UnrealScriptFunctionCallableContext& call_ctx, void* custom_data) {
                    auto* self = reinterpret_cast<KovaaksBridgeModProduction*>(custom_data);
                    if (!self || !self->rust_ready_ || self->updates_disabled_) {
                        return;
                    }
                    auto* frame = reinterpret_cast<RC::Unreal::FFrame_50_AndBelow*>(&call_ctx.TheStack);
                    auto* current_fn = frame ? frame->Node : nullptr;
                    self->handle_http_trace_hook(current_fn, call_ctx);
                },
                this
            );
            http_trace_hook_bindings_.emplace_back(fn, callback_id);
            if (!newly_registered.empty()) {
                newly_registered += ',';
            }
            newly_registered += name;
            ++new_count;
        };

        if (http_trace_enabled_) {
            bind(targets_.send_sa_http_request, "send_sa_http_request");
        }
        bind(targets_.sa_http_invoke_success, "sa_http_invoke_success");
        bind(targets_.sa_http_invoke_error, "sa_http_invoke_error");
        http_trace_hooks_registered_ = !http_trace_hook_bindings_.empty();
        if (new_count > 0) {
            std::array<char, 160> summary{};
            std::snprintf(
                summary.data(),
                summary.size(),
                "[hooks] http_trace registered new=%u total=%u trace_enabled=%u user_mgmt=%u",
                static_cast<unsigned int>(new_count),
                static_cast<unsigned int>(http_trace_hook_bindings_.size()),
                http_trace_enabled_ ? 1u : 0u,
                user_mgmt_enabled_ ? 1u : 0u
            );
            runtime_log_line(summary.data());
            log_name_list("[hooks] http_trace new", newly_registered);
        }
    }

    void unregister_http_trace_hooks() {
        for (auto& binding : http_trace_hook_bindings_) {
            if (binding.first && is_likely_valid_object_ptr(binding.first)) {
                binding.first->UnregisterHook(binding.second);
            }
        }
        http_trace_hook_bindings_.clear();
        http_trace_hooks_registered_ = false;
        pending_http_trace_requests_.clear();
    }

    auto on_update_impl() -> void {
        const uint64_t now = GetTickCount64();
        if (now < fault_backoff_until_ms_) {
            return;
        }
        if (now < next_poll_ms_) {
            return;
        }
        // ~250Hz during a challenge (tighter shot-detect window reduces replay
        // timestamp lag); ~60Hz otherwise, which still catches start edges fast.
        const bool challenge_hot =
            lifecycle_active_
            || last_is_in_challenge_ == 1
            || pending_lifecycle_end_ms_ != 0
            || (last_true_start_transition_ms_ > 0
                && safe_elapsed_ms(now, last_true_start_transition_ms_) < 1500);
        next_poll_ms_ = now + (challenge_hot ? 4 : 16);
        perf_lap_begin();

        const bool expected_live_stream =
            lifecycle_active_
            || last_is_in_challenge_ == 1
            || last_is_in_scenario_ == 1;
        if (expected_live_stream
            && last_runtime_progress_ms_ > 0
            && safe_elapsed_ms(now, last_runtime_progress_ms_) > 350
            && now >= next_live_stream_refresh_ms_) {
            next_live_stream_refresh_ms_ = now + 250;
            live_metric_receiver_hint_ = nullptr;
            live_metric_receiver_hint_ms_ = 0;
            state_receiver_instance_ = nullptr;
            scenario_manager_instance_ = nullptr;
            next_receiver_resolve_ms_ = 0;
            next_scenario_resolve_ms_ = 0;
        }

        if (pending_bridge_recovered_snapshot_ && rust_ready_ && kovaaks::RustBridge::is_connected()) {
            refresh_live_runtime_hooks_after_reconnect(now);
            flush_pending_shot_telemetry(now, true);
            if (emit_requested_state_snapshot_safe(now, "bridge_recovered")) {
                pending_bridge_recovered_snapshot_ = false;
                runtime_log_line("[bridge_reconnect] emitted recovery snapshot");
            } else {
                runtime_log_line("[bridge_reconnect] recovery snapshot emit failed; will retry");
            }
        }

        maybe_emit_user_management_build_tag();
        maybe_emit_user_management_snapshot_safe(now, false);

        resolve_targets(false);
        perf_lap(0);
        auto* receiver = resolve_state_receiver_instance(now);
        perf_lap(1);
        int32_t iv = 0;
        float fv = 0.0f;
        int32_t current_kills_total = -1;
        int32_t current_shots_fired = -1;
        int32_t current_shots_hit = -1;
        int32_t current_is_in_challenge = -1;
        int32_t current_is_in_scenario = -1;
        int32_t current_is_in_scenario_editor = -1;
        int32_t current_is_currently_in_benchmark = -1;
        int32_t current_is_in_trainer = -1;
        int32_t current_scenario_is_paused = -1;
        float current_seconds = -1.0f;
        float current_score_total = -1.0f;
        float current_score_total_derived = -1.0f;
        float current_score_per_minute = -1.0f;
        float current_kills_per_second = -1.0f;
        float current_accuracy = -1.0f;
        float current_challenge_average_fps = -1.0f;
        float current_damage_done = -1.0f;
        float current_damage_possible = -1.0f;
        float current_damage_efficiency = -1.0f;
        float current_time_remaining = -1.0f;
        float current_queue_time_remaining = -1.0f;
        int32_t current_challenge_tick_count = -1;
        const int32_t prev_is_in_challenge = last_is_in_challenge_;
        const int32_t prev_is_in_scenario = last_is_in_scenario_;
        bool challenge_state_edge = false;
        bool scenario_state_edge = false;
        bool has_critical_score_read = false;
        bool has_critical_seconds_read = false;
        bool has_critical_tick_read = false;
        RC::Unreal::UObject* meta = nullptr;
        RC::Unreal::UObject* scenario_manager = nullptr;
        RC::Unreal::UObject* scenario_state_receiver = nullptr;
        current_scenario_is_paused = cached_scenario_is_paused_;

        {
            EmitTagScope state_scope(*this, "state_get", "non_ui_probe");
        auto pull_receiver_metrics = [&](RC::Unreal::UObject* active_receiver) {
            if (!active_receiver || !is_likely_valid_object_ptr(active_receiver)) {
                return;
            }
            if (try_read_int(active_receiver, {
                    targets_.get_kills_value_else,
                    targets_.get_kills_value_or,
                    targets_.receive_kills_value_else,
                    targets_.receive_kills_value_or,
                    targets_.receive_kills_single,
                    targets_.receive_kills
                }, iv)) {
                current_kills_total = iv;
            }
            if (try_read_int(active_receiver, {
                    targets_.get_shots_fired_value_else,
                    targets_.get_shots_fired_value_or,
                    targets_.receive_shots_fired_value_else,
                    targets_.receive_shots_fired_value_or,
                    targets_.receive_shots_fired_single,
                    targets_.receive_shots_fired
                }, iv)) {
                current_shots_fired = iv;
            }
            if (try_read_int(active_receiver, {
                    targets_.get_shots_hit_value_else,
                    targets_.get_shots_hit_value_or,
                    targets_.receive_shots_hit_value_else,
                    targets_.receive_shots_hit_value_or,
                    targets_.receive_shots_hit_single,
                    targets_.receive_shots_hit
                }, iv)) {
                current_shots_hit = iv;
            }
            if (try_read_float(active_receiver, {
                    targets_.get_score_value_else,
                    targets_.get_score_value_or,
                    targets_.receive_score_value_else,
                    targets_.receive_score_value_or,
                    targets_.receive_score_single,
                    targets_.receive_score
                }, fv)) {
                has_critical_score_read = true;
                current_score_total = fv;
            }
            if (try_read_float(active_receiver, {
                    targets_.get_accuracy_value_else,
                    targets_.get_accuracy_value_or,
                    targets_.receive_accuracy_value_else,
                    targets_.receive_accuracy_value_or,
                    targets_.receive_accuracy_single,
                    targets_.receive_accuracy
                }, fv)) {
                current_accuracy = normalize_accuracy_pct(fv, current_shots_hit, current_shots_fired);
            }
            if (try_read_float(active_receiver, {
                    targets_.get_score_per_minute_value_else,
                    targets_.get_score_per_minute_value_or,
                    targets_.receive_score_per_minute_value_else,
                    targets_.receive_score_per_minute_value_or,
                    targets_.receive_score_per_minute
                }, fv)) {
                current_score_per_minute = fv;
            }
            if (try_read_float(active_receiver, {
                    targets_.get_challenge_average_fps_value_else,
                    targets_.get_challenge_average_fps_value_or,
                    targets_.receive_challenge_average_fps_value_else,
                    targets_.receive_challenge_average_fps_value_or,
                    targets_.receive_challenge_average_fps_single,
                    targets_.receive_challenge_average_fps
                }, fv)) {
                current_challenge_average_fps = fv;
            }
            if (try_read_int(active_receiver, {
                    targets_.get_challenge_tick_count_value_else,
                    targets_.get_challenge_tick_count_value_or,
                    targets_.receive_challenge_tick_count_value_else,
                    targets_.receive_challenge_tick_count_value_or,
                    targets_.receive_challenge_tick_count_single,
                    targets_.receive_challenge_tick_count
                }, iv)) {
                has_critical_tick_read = true;
                current_challenge_tick_count = iv;
            }
            if (try_read_float(active_receiver, {
                    targets_.get_damage_done_value_else,
                    targets_.get_damage_done_value_or,
                    targets_.receive_damage_done_value_else,
                    targets_.receive_damage_done
                }, fv)) {
                current_damage_done = fv;
            }
            if (try_read_float(active_receiver, {
                    targets_.get_damage_possible_value_else,
                    targets_.get_damage_possible_value_or,
                    targets_.receive_damage_possible_value_else,
                    targets_.receive_damage_possible
                }, fv)) {
                current_damage_possible = fv;
            }
            if (try_read_float(active_receiver, {
                    targets_.get_kills_per_second_value_else,
                    targets_.get_kills_per_second_value_or,
                    targets_.receive_kills_per_second_value_else,
                    targets_.receive_kills_per_second_value_or,
                    targets_.receive_kills_per_second
                }, fv)) {
                current_kills_per_second = fv;
            }
            if (try_read_float(active_receiver, {
                    targets_.get_damage_efficiency_value_else,
                    targets_.get_damage_efficiency_value_or,
                    targets_.receive_damage_efficiency_value_else,
                    targets_.receive_damage_efficiency
                }, fv)) {
                current_damage_efficiency = fv;
            }
            if (try_read_float(active_receiver, {
                    targets_.get_seconds_value_else,
                    targets_.get_seconds_value_or,
                    targets_.receive_seconds_value_else,
                    targets_.receive_seconds_value_or,
                    targets_.receive_seconds_single,
                    targets_.receive_seconds
                }, fv)) {
                has_critical_seconds_read = true;
                current_seconds = fv;
            }
        };
        pull_receiver_metrics(receiver);
        perf_lap(2);

        auto pull_bool_state = [&](RC::Unreal::UObject* source,
                                   const char* ev,
                                   int32_t& current_value,
                                   int32_t& last_value,
                                   std::initializer_list<RC::Unreal::UFunction*> fns) {
            (void)last_value;
            bool bool_value = false;
            if (!source || !try_read_bool(source, fns, bool_value)) {
                return;
            }

            if (std::strcmp(ev, "pull_is_in_challenge") == 0) {
                if (bool_value) {
                    last_in_challenge_true_ms_ = now;
                }
            } else if (std::strcmp(ev, "pull_is_in_scenario") == 0) {
                if (bool_value) {
                    last_in_scenario_true_ms_ = now;
                }
            }

            current_value = bool_value ? 1 : 0;
        };

        meta = resolve_meta_game_instance(now);
        pull_bool_state(
            meta,
            "pull_is_in_trainer",
            current_is_in_trainer,
            last_is_in_trainer_,
            {targets_.meta_get_in_trainer}
        );

        scenario_manager = resolve_scenario_manager_instance(now);
        if (scenario_manager) {
            pull_bool_state(
                scenario_manager,
                "pull_is_in_challenge",
                current_is_in_challenge,
                last_is_in_challenge_,
                {targets_.scenario_is_in_challenge}
            );
            pull_bool_state(
                scenario_manager,
                "pull_is_in_scenario",
                current_is_in_scenario,
                last_is_in_scenario_,
                {targets_.scenario_is_in_scenario}
            );
            pull_bool_state(
                scenario_manager,
                "pull_is_in_scenario_editor",
                current_is_in_scenario_editor,
                last_is_in_scenario_editor_,
                {targets_.scenario_is_in_scenario_editor}
            );
            pull_bool_state(
                scenario_manager,
                "pull_is_currently_in_benchmark",
                current_is_currently_in_benchmark,
                last_is_currently_in_benchmark_,
                {targets_.scenario_is_currently_in_benchmark}
            );
            if (try_read_float(scenario_manager, {targets_.scenario_get_challenge_time_remaining}, fv)) {
                current_time_remaining = fv;
            }
            if (try_read_float(scenario_manager, {targets_.scenario_get_challenge_queue_time_remaining}, fv)) {
                current_queue_time_remaining = fv;
            }

            if (now >= next_scenario_identity_refresh_ms_) {
                next_scenario_identity_refresh_ms_ = now + 750;
                std::string scenario_name{};
                std::string scenario_id{};
                std::string scenario_manager_id{};
                if (try_resolve_current_scenario_identity(now, scenario_name, scenario_id, scenario_manager_id)) {
                    std::string resolved_scenario_name = scenario_name;
                    if (resolved_scenario_name.empty() && !scenario_id.empty()) {
                        resolved_scenario_name = derive_scenario_name_from_id(scenario_id);
                    }
                    if (!resolved_scenario_name.empty()) {
                        s_last_run_scenario_name = resolved_scenario_name;
                    }
                    if (!scenario_id.empty()) {
                        s_last_run_scenario_id = scenario_id;
                    }
                    if (!scenario_manager_id.empty()) {
                        s_last_run_scenario_manager_id = scenario_manager_id;
                    }
                    if (!resolved_scenario_name.empty() && resolved_scenario_name != last_scenario_name_) {
                        last_scenario_name_ = resolved_scenario_name;
                        const auto escaped = escape_json_ascii(resolved_scenario_name);
                        std::array<char, 512> json{};
                        std::snprintf(
                            json.data(),
                            json.size(),
                            "{\"ev\":\"ui_scenario_name\",\"field\":\"%s\",\"source\":\"state_manager\"}",
                            escaped.c_str()
                        );
                        kovaaks::RustBridge::emit_json(json.data());
                    }
                } else if ((current_is_in_challenge > 0 || current_is_in_scenario > 0) && last_scenario_name_.empty()) {
                    kmod_replay::ReplayContext context{};
                    kmod_replay::replay_collect_map_context(context);
                    if (!context.map_name.empty()) {
                        std::string map_label = context.map_name;
                        if (const auto dot = map_label.find_last_of('.'); dot != std::string::npos) {
                            map_label = map_label.substr(0, dot);
                        }
                        if (!map_label.empty() && map_label != last_scenario_name_) {
                            last_scenario_name_ = map_label;
                            const auto escaped = escape_json_ascii(map_label);
                            std::array<char, 512> json{};
                            std::snprintf(
                                json.data(),
                                json.size(),
                                "{\"ev\":\"ui_scenario_name\",\"field\":\"%s\",\"source\":\"map_fallback\"}",
                                escaped.c_str()
                            );
                            kovaaks::RustBridge::emit_json(json.data());
                        }
                    }
                }
            }
        }

        perf_lap(3);
        scenario_state_receiver = resolve_scenario_state_receiver_instance(now);
        const bool should_refresh_paused_state =
            scenario_state_receiver
            && (cached_scenario_is_paused_ < 0
                || now >= next_scenario_paused_read_ms_);
        if (should_refresh_paused_state) {
            bool paused_value = false;
            if (try_read_bool(
                    scenario_state_receiver,
                    {
                        targets_.scenario_state_get_is_paused_value_else,
                        targets_.scenario_state_get_is_paused_value_or,
                        targets_.scenario_state_receive_is_paused_value_else,
                        targets_.scenario_state_receive_is_paused_value_or,
                        targets_.scenario_state_receive_is_paused_single,
                        targets_.scenario_state_receive_is_paused,
                    },
                    paused_value)) {
                cached_scenario_is_paused_ = paused_value ? 1 : 0;
                current_scenario_is_paused = cached_scenario_is_paused_;
            }
            next_scenario_paused_read_ms_ = now + ((last_is_in_challenge_ == 1 || last_is_in_scenario_ == 1) ? 100 : 250);
        }

        const bool seconds_advancing =
            has_critical_seconds_read
            && std::isfinite(current_seconds)
            && std::isfinite(last_seconds_)
            && current_seconds > (last_seconds_ + 0.0001f);
        const bool ticks_advancing =
            has_critical_tick_read
            && current_challenge_tick_count >= 0
            && last_challenge_tick_count_ >= 0
            && current_challenge_tick_count > last_challenge_tick_count_;
        const bool score_advancing =
            std::isfinite(current_score_total)
            && std::isfinite(last_score_total_)
            && current_score_total > (last_score_total_ + 0.0001f);
        const bool timer_implies_challenge =
            std::isfinite(current_time_remaining) && current_time_remaining > 0.25f;
        perf_lap(4);
        const bool replay_active = kmod_replay::replay_ingame_playback_is_active();
        const bool recent_runtime_progress =
            safe_elapsed_ms(now, last_runtime_progress_ms_) < 1200;
        const bool recent_stream_activity =
            last_state_change_emit_ms_ > 0
            && safe_elapsed_ms(now, last_state_change_emit_ms_) < 1200;
        const bool progress_signal =
            current_shots_fired > 0
            || current_shots_hit > 0
            || (std::isfinite(current_seconds) && current_seconds > 0.0001f)
            || (std::isfinite(current_score_per_minute) && current_score_per_minute > 0.0001f)
            || (std::isfinite(current_damage_done) && current_damage_done > 0.0001f)
            || (std::isfinite(current_damage_possible) && current_damage_possible > 0.0001f);
        const bool lifecycle_start_alignment_hint =
            recent_stream_activity
            && (progress_signal || timer_implies_challenge)
            && (
                current_is_in_scenario > 0
                || current_is_in_challenge > 0
                || prev_is_in_scenario > 0
                || prev_is_in_challenge > 0
                || lifecycle_active_
                || (last_true_start_transition_ms_ > 0
                    && safe_elapsed_ms(now, last_true_start_transition_ms_) < 1500)
                || (std::isfinite(current_queue_time_remaining) && current_queue_time_remaining > 0.0001f)
            );
        const bool runtime_progress_active =
            seconds_advancing
            || ticks_advancing
            || score_advancing
            || timer_implies_challenge;
        const bool inferred_active =
            !replay_active
            && (lifecycle_start_alignment_hint
                || runtime_progress_active
                || (recent_runtime_progress
                    && (current_challenge_tick_count > 0
                        || timer_implies_challenge
                        || (std::isfinite(current_seconds) && current_seconds > 0.05f))));

        if (current_is_in_scenario <= 0 && current_is_in_challenge <= 0 && inferred_active) {
            current_is_in_scenario = 1;
            last_in_scenario_true_ms_ = now;
            if (timer_implies_challenge) {
                current_is_in_challenge = 1;
                last_in_challenge_true_ms_ = now;
            }
        }
        if (current_is_in_scenario < 0) {
            current_is_in_scenario = inferred_active ? 1 : 0;
        }
        if (current_is_in_challenge < 0) {
            current_is_in_challenge = (inferred_active && timer_implies_challenge) ? 1 : 0;
        }
        if (current_is_in_scenario_editor < 0) {
            current_is_in_scenario_editor = (last_is_in_scenario_editor_ > 0) ? 1 : 0;
        }
        if (current_is_currently_in_benchmark < 0) {
            current_is_currently_in_benchmark = (last_is_currently_in_benchmark_ > 0) ? 1 : 0;
        }
        if (current_is_in_trainer < 0) {
            current_is_in_trainer = (last_is_in_trainer_ > 0) ? 1 : 0;
        }
        const bool paused_active_context =
            current_scenario_is_paused == 1
            && (
                prev_is_in_challenge > 0
                || prev_is_in_scenario > 0
                || (last_in_challenge_true_ms_ > 0 && safe_elapsed_ms(now, last_in_challenge_true_ms_) < 2500)
                || (last_in_scenario_true_ms_ > 0 && safe_elapsed_ms(now, last_in_scenario_true_ms_) < 2500)
            );
        if (paused_active_context) {
            if (current_is_in_challenge <= 0 && prev_is_in_challenge > 0) {
                current_is_in_challenge = prev_is_in_challenge;
            }
            if (current_is_in_scenario <= 0 && prev_is_in_scenario > 0) {
                current_is_in_scenario = prev_is_in_scenario;
            }
        }
        emit_state_i32("pull_is_in_challenge", last_is_in_challenge_, current_is_in_challenge > 0 ? 1 : 0, now);
        emit_state_i32("pull_is_in_scenario", last_is_in_scenario_, current_is_in_scenario > 0 ? 1 : 0, now);
        emit_state_i32("pull_is_in_scenario_editor", last_is_in_scenario_editor_, current_is_in_scenario_editor > 0 ? 1 : 0, now);
        emit_state_i32("pull_is_currently_in_benchmark", last_is_currently_in_benchmark_, current_is_currently_in_benchmark > 0 ? 1 : 0, now);
        emit_state_i32("pull_is_in_trainer", last_is_in_trainer_, current_is_in_trainer > 0 ? 1 : 0, now);
        emit_state_i32("pull_scenario_is_paused", last_scenario_is_paused_, current_scenario_is_paused > 0 ? 1 : 0, now);

        const bool active_now =
            current_is_in_challenge > 0
            || current_is_in_scenario > 0;
        const bool runtime_progress_hint =
            current_challenge_tick_count > 0
            || current_shots_fired > 0
            || current_shots_hit > 0
            || current_kills_total > 0
            || (std::isfinite(current_seconds) && current_seconds > 0.05f)
            || (std::isfinite(current_time_remaining) && current_time_remaining > 0.05f)
            || (std::isfinite(current_score_total) && current_score_total > 0.05f)
            || (std::isfinite(current_score_total_derived) && current_score_total_derived > 0.05f);
        const bool effective_active_now = active_now || runtime_progress_hint;
        const bool active_prev =
            prev_is_in_challenge > 0
            || prev_is_in_scenario > 0;
        const bool entering_active_edge_fast = active_now && !active_prev;
        const float raw_queue_time_remaining = current_queue_time_remaining;

        if (active_now) {
            last_queue_countdown_edge_ms_ = 0;
        } else if (std::isfinite(raw_queue_time_remaining) && raw_queue_time_remaining > 0.0001f) {
            const bool had_raw_queue =
                std::isfinite(last_raw_queue_time_remaining_)
                && last_raw_queue_time_remaining_ > 0.0001f;
            const bool raw_queue_changed =
                !had_raw_queue
                || std::fabs(
                    static_cast<double>(last_raw_queue_time_remaining_)
                    - static_cast<double>(raw_queue_time_remaining)
                ) > 0.0001;

            if (raw_queue_changed) {
                last_queue_countdown_edge_ms_ = now;
            }

            // Real pre-run queues quickly move below 1.0. If we stay pinned at
            // exactly 1.0 for too long while inactive, treat it as non-queue menu state.
            const bool near_one_queue =
                std::fabs(static_cast<double>(raw_queue_time_remaining) - 1.0) <= 0.0001;
            const bool static_near_one_queue =
                near_one_queue
                && had_raw_queue
                && !raw_queue_changed;
            constexpr uint64_t k_static_queue_grace_ms = 1600;
            const bool stale_static_queue =
                static_near_one_queue
                && last_queue_countdown_edge_ms_ > 0
                && safe_elapsed_ms(now, last_queue_countdown_edge_ms_) > k_static_queue_grace_ms;
            constexpr uint64_t k_stale_queue_value_ms = 2200;
            const bool stale_frozen_queue =
                had_raw_queue
                && !raw_queue_changed
                && last_queue_countdown_edge_ms_ > 0
                && safe_elapsed_ms(now, last_queue_countdown_edge_ms_) > k_stale_queue_value_ms;
            if (stale_static_queue || stale_frozen_queue) {
                current_queue_time_remaining = 0.0f;
            }
        } else {
            last_queue_countdown_edge_ms_ = 0;
        }
        last_raw_queue_time_remaining_ = raw_queue_time_remaining;
        if (!active_now && (!std::isfinite(current_queue_time_remaining) || current_queue_time_remaining < 0.0f)) {
            current_queue_time_remaining = 0.0f;
        }

        if (entering_active_edge_fast) {
            state_receiver_instance_ = nullptr;
            next_receiver_resolve_ms_ = 0;
            auto* refreshed_receiver = resolve_state_receiver_instance(now);
            if (refreshed_receiver && is_likely_valid_object_ptr(refreshed_receiver)) {
                receiver = refreshed_receiver;
            }
            pull_receiver_metrics(receiver);
        }

        {
        EmitTagScope derived_scope(*this, "derived_spm_seconds", "non_ui_probe");
        const float derived_accuracy = derive_accuracy_pct(current_shots_hit, current_shots_fired);
        current_accuracy = normalize_accuracy_pct(current_accuracy, current_shots_hit, current_shots_fired);
        if (current_accuracy < 0.0f && derived_accuracy >= 0.0f) {
            current_accuracy = derived_accuracy;
        }
        const bool allow_score_derivation =
            current_is_in_challenge > 0
            || (std::isfinite(current_time_remaining) && current_time_remaining > 0.25f);
        if (allow_score_derivation
            && std::isfinite(current_score_per_minute) && current_score_per_minute > 0.0f
            && std::isfinite(current_seconds) && current_seconds > 0.0f) {
            const float derived_score_total = (current_score_per_minute * current_seconds) / 60.0f;
            if (std::isfinite(derived_score_total) && derived_score_total >= 0.0f) {
                current_score_total_derived = derived_score_total;
                if (current_score_total < 0.0f || current_score_total <= 0.0001f) {
                    current_score_total = derived_score_total;
                }
            }
        }
        }

        if (!effective_active_now) {
            current_kills_total = 0;
            current_shots_fired = 0;
            current_shots_hit = 0;
            current_score_total = 0.0f;
            current_score_total_derived = 0.0f;
            current_score_per_minute = 0.0f;
            current_kills_per_second = 0.0f;
            current_accuracy = 0.0f;
            current_challenge_average_fps = 0.0f;
            current_challenge_tick_count = 0;
            current_damage_done = 0.0f;
            current_damage_possible = 0.0f;
            current_damage_efficiency = 0.0f;
            current_seconds = 0.0f;
            current_time_remaining = 0.0f;
        }

        bool snapshot_dirty = live_pull_snapshot_dirty_;
        snapshot_dirty |= cache_pull_i32("pull_kills_total", last_kills_total_, current_kills_total, last_nonzero_kills_total_ms_, now);
        snapshot_dirty |= cache_pull_i32("pull_shots_fired_total", last_shots_fired_, current_shots_fired, last_nonzero_shots_fired_ms_, now);
        snapshot_dirty |= cache_pull_i32("pull_shots_hit_total", last_shots_hit_, current_shots_hit, last_nonzero_shots_hit_ms_, now);
        snapshot_dirty |= cache_pull_i32(
            "pull_challenge_tick_count_total",
            last_challenge_tick_count_,
            current_challenge_tick_count,
            last_nonzero_challenge_tick_count_ms_,
            now
        );
        snapshot_dirty |= cache_pull_f32("pull_seconds_total", last_seconds_, current_seconds, last_nonzero_seconds_ms_, now);
        snapshot_dirty |= cache_pull_f32(
            "pull_challenge_seconds_total",
            last_challenge_seconds_total_,
            current_seconds,
            last_nonzero_challenge_seconds_ms_,
            now
        );
        snapshot_dirty |= cache_pull_f32("pull_score_total", last_score_total_, current_score_total, last_nonzero_score_total_ms_, now);
        snapshot_dirty |= cache_pull_f32(
            "pull_score_total_derived",
            last_score_total_derived_,
            current_score_total_derived,
            last_nonzero_score_total_derived_ms_,
            now
        );
        snapshot_dirty |= cache_pull_f32("pull_score_per_minute", last_score_per_minute_, current_score_per_minute, last_nonzero_spm_ms_, now);
        snapshot_dirty |= cache_pull_f32("pull_kills_per_second", last_kills_per_second_, current_kills_per_second, last_nonzero_kills_per_second_ms_, now);
        snapshot_dirty |= cache_pull_f32("pull_accuracy", last_accuracy_, current_accuracy, last_nonzero_accuracy_ms_, now);
        snapshot_dirty |= cache_pull_f32("pull_challenge_average_fps", last_challenge_average_fps_, current_challenge_average_fps, last_nonzero_challenge_average_fps_ms_, now);
        snapshot_dirty |= cache_pull_f32("pull_damage_done", last_damage_done_, current_damage_done, last_nonzero_damage_done_ms_, now);
        snapshot_dirty |= cache_pull_f32("pull_damage_possible", last_damage_possible_, current_damage_possible, last_nonzero_damage_possible_ms_, now);
        snapshot_dirty |= cache_pull_f32("pull_damage_efficiency", last_damage_efficiency_, current_damage_efficiency, last_nonzero_damage_efficiency_ms_, now);
        snapshot_dirty |= cache_pull_f32("pull_time_remaining", last_time_remaining_, current_time_remaining, last_nonzero_time_remaining_ms_, now);
        snapshot_dirty |= cache_pull_f32(
            "pull_queue_time_remaining",
            last_queue_time_remaining_,
            current_queue_time_remaining,
            last_nonzero_queue_time_remaining_ms_,
            now
        );
        if (snapshot_dirty) {
            emit_pull_snapshot(now);
        }
        live_pull_snapshot_dirty_ = false;

        challenge_state_edge =
            current_is_in_challenge >= 0
            && prev_is_in_challenge >= 0
            && current_is_in_challenge != prev_is_in_challenge;
        scenario_state_edge =
            current_is_in_scenario >= 0
            && prev_is_in_scenario >= 0
            && current_is_in_scenario != prev_is_in_scenario;
        if (challenge_state_edge || scenario_state_edge) {
            last_runtime_progress_ms_ = now;
        }
        const bool recently_active_challenge =
            current_is_in_challenge > 0
            || prev_is_in_challenge > 0
            || current_is_in_scenario > 0
            || prev_is_in_scenario > 0
            || lifecycle_active_
            || (last_in_challenge_true_ms_ > 0 && safe_elapsed_ms(now, last_in_challenge_true_ms_) < 2500);
        const bool suspicious_false_transition =
            current_is_in_challenge == 0
            && current_is_in_scenario == 0
            && prev_is_in_challenge > 0;
        const bool severe_seconds_regression =
            std::isfinite(current_seconds)
            && std::isfinite(last_seconds_)
            && last_seconds_ > 5.0f
            && current_seconds >= 0.0f
            && (static_cast<double>(current_seconds) + 2.0) < static_cast<double>(last_seconds_)
            && static_cast<double>(current_seconds) < (static_cast<double>(last_seconds_) * 0.5);
        const bool severe_tick_regression =
            current_challenge_tick_count >= 0
            && last_challenge_tick_count_ > 300
            && current_challenge_tick_count + 200 < last_challenge_tick_count_;
        const bool severe_counter_regression =
            current_shots_fired == 0
            && current_shots_hit == 0
            && last_shots_fired_ > 50
            && last_shots_hit_ > 10;
        if (recently_active_challenge
            && suspicious_false_transition
            && (severe_seconds_regression || severe_tick_regression || severe_counter_regression)) {
            current_is_in_challenge = prev_is_in_challenge > 0 ? prev_is_in_challenge : current_is_in_challenge;
            current_is_in_scenario = prev_is_in_scenario > 0 ? prev_is_in_scenario : current_is_in_scenario;
            if (severe_seconds_regression) {
                current_seconds = last_seconds_;
            }
            if (severe_tick_regression) {
                current_challenge_tick_count = last_challenge_tick_count_;
            }
            if (severe_counter_regression) {
                current_shots_fired = last_shots_fired_;
                current_shots_hit = last_shots_hit_;
                current_kills_total = last_kills_total_;
            }
            challenge_state_edge = false;
            scenario_state_edge = false;
            if (now >= next_critical_recover_allowed_ms_) {
                next_critical_recover_allowed_ms_ = now + 1500;
                fault_state_guard_until_ms_ = now + 2500;
                reset_runtime_resolvers();
                if (rust_ready_) {
                    std::array<char, 256> json{};
                    std::snprintf(
                        json.data(),
                        json.size(),
                        "{\"ev\":\"ue4ss_prod_stale_refresh\",\"reason\":\"receiver_regression\",\"seconds\":%.6f,\"last_seconds\":%.6f}",
                        static_cast<double>(std::isfinite(current_seconds) ? current_seconds : -1.0f),
                        static_cast<double>(std::isfinite(last_seconds_) ? last_seconds_ : -1.0f)
                    );
                    kovaaks::RustBridge::emit_json(json.data());
                }
            }
        }
        if (std::isfinite(current_seconds) && current_seconds >= 0.0f) {
            if (!std::isfinite(last_observed_seconds_value_)
                || std::fabs(static_cast<double>(last_observed_seconds_value_) - static_cast<double>(current_seconds)) > 0.0001) {
                last_observed_seconds_value_ = current_seconds;
                last_runtime_progress_ms_ = now;
            }
        }
        if (current_challenge_tick_count >= 0
            && (last_observed_challenge_tick_count_ == std::numeric_limits<int32_t>::min()
                || current_challenge_tick_count != last_observed_challenge_tick_count_)) {
            last_observed_challenge_tick_count_ = current_challenge_tick_count;
            last_runtime_progress_ms_ = now;
        }
        if (std::isfinite(current_queue_time_remaining) && current_queue_time_remaining >= 0.0f) {
            if (!std::isfinite(last_observed_queue_time_remaining_)
                || std::fabs(static_cast<double>(last_observed_queue_time_remaining_) - static_cast<double>(current_queue_time_remaining)) > 0.0001) {
                last_observed_queue_time_remaining_ = current_queue_time_remaining;
                last_runtime_progress_ms_ = now;
            }
        }
        if (std::isfinite(current_time_remaining) && current_time_remaining >= 0.0f) {
            if (!std::isfinite(last_observed_time_remaining_)
                || std::fabs(static_cast<double>(last_observed_time_remaining_) - static_cast<double>(current_time_remaining)) > 0.0001) {
                last_observed_time_remaining_ = current_time_remaining;
                last_runtime_progress_ms_ = now;
            }
        }
        const bool entering_active_edge =
            (current_is_in_challenge > 0 || current_is_in_scenario > 0)
            && !(prev_is_in_challenge > 0 || prev_is_in_scenario > 0);
        const bool leaving_active_edge =
            !(current_is_in_challenge > 0 || current_is_in_scenario > 0)
            && (prev_is_in_challenge > 0 || prev_is_in_scenario > 0);
        if (entering_active_edge) {
            last_true_start_transition_ms_ = now;
        }
        if ((challenge_state_edge || scenario_state_edge)
            && (last_state_transition_refresh_ms_ == 0
                || safe_elapsed_ms(now, last_state_transition_refresh_ms_) > 250)) {
            last_state_transition_refresh_ms_ = now;
            if (leaving_active_edge || entering_active_edge) {
                state_receiver_instance_ = nullptr;
                next_receiver_resolve_ms_ = 0;
            }
        }
        }

        sync_in_game_overlay_pull_cache(
            current_is_in_challenge,
            current_is_in_scenario,
            current_is_in_scenario_editor,
            current_scenario_is_paused,
            current_queue_time_remaining,
            current_score_total,
            current_score_total_derived,
            current_kills_total,
            current_score_per_minute,
            current_seconds,
            current_challenge_average_fps,
            current_challenge_tick_count,
            current_time_remaining
        );
        perf_lap(5);
        in_game_overlay_tick(now);
        perf_lap(6);

        update_lifecycle_events(
            now,
            current_is_in_challenge,
            current_is_in_scenario,
            current_scenario_is_paused,
            current_shots_fired,
            current_shots_hit,
            current_seconds,
            current_score_per_minute,
            current_damage_done,
            current_damage_possible,
            current_time_remaining,
            current_queue_time_remaining
        );

        const bool paused_runtime_context =
            current_scenario_is_paused == 1
            || last_scenario_is_paused_ == 1
            || s_last_pull_scenario_is_paused == 1;

        const bool stale_active_flags =
            !kmod_replay::replay_ingame_playback_is_active()
            && !paused_runtime_context
            && (last_is_in_challenge_ == 1 || last_is_in_scenario_ == 1)
            && safe_elapsed_ms(now, last_runtime_progress_ms_) > 2200
            && (!std::isfinite(last_time_remaining_) || last_time_remaining_ <= 0.0001f)
            && (!std::isfinite(last_queue_time_remaining_) || last_queue_time_remaining_ <= 0.0001f)
            && (last_lifecycle_signal_ms_ == 0 || safe_elapsed_ms(now, last_lifecycle_signal_ms_) > 1200);
        const bool stale_paused_postrun_flags =
            !kmod_replay::replay_ingame_playback_is_active()
            && paused_runtime_context
            && last_is_in_challenge_ != 1
            && (last_is_in_scenario_ == 1 || current_is_in_scenario == 1)
            && safe_elapsed_ms(now, last_runtime_progress_ms_) > 2200
            && std::isfinite(last_time_remaining_)
            && last_time_remaining_ > 0.0001f
            && last_time_remaining_ <= 1.25f
            && std::isfinite(last_queue_time_remaining_)
            && last_queue_time_remaining_ >= 0.99f
            && last_queue_time_remaining_ <= 1.01f
            && (last_lifecycle_signal_ms_ == 0 || safe_elapsed_ms(now, last_lifecycle_signal_ms_) > 1200);
        if (stale_active_flags) {
            EmitTagScope stale_scope(*this, "state_guard", "state_inferred");
            emit_state_i32("pull_is_in_challenge", last_is_in_challenge_, 0, now);
            emit_state_i32("pull_is_in_scenario", last_is_in_scenario_, 0, now);
            current_is_in_challenge = 0;
            current_is_in_scenario = 0;
            last_in_challenge_true_ms_ = 0;
            last_in_scenario_true_ms_ = 0;
            pending_in_challenge_false_ms_ = 0;
            pending_in_scenario_false_ms_ = 0;
            if (verbose_logs_) {
                RC::Output::send<RC::LogLevel::Warning>(
                    STR("[kmod-prod] cleared stale active state after runtime-progress timeout\n")
                );
            }
        }
        if (stale_paused_postrun_flags) {
            EmitTagScope stale_scope(*this, "state_guard", "paused_results_timeout");
            emit_state_i32("pull_is_in_scenario", last_is_in_scenario_, 0, now);
            emit_state_i32("pull_scenario_is_paused", last_scenario_is_paused_, 0, now);
            current_is_in_scenario = 0;
            current_scenario_is_paused = 0;
            current_time_remaining = 0.0f;
            current_queue_time_remaining = 0.0f;
            last_in_scenario_true_ms_ = 0;
            pending_in_scenario_false_ms_ = 0;
            last_runtime_progress_ms_ = now;
            last_true_start_transition_ms_ = 0;
            lifecycle_active_ = false;
            lifecycle_queued_ = false;
            pending_lifecycle_end_ms_ = 0;
            if (verbose_logs_) {
                RC::Output::send<RC::LogLevel::Warning>(
                    STR("[kmod-prod] cleared stale paused post-run state before next lifecycle start\n")
                );
            }
        }

        const bool challenge_expected_active =
            (current_is_in_challenge > 0)
            || lifecycle_active_
            || (last_in_challenge_true_ms_ > 0 && (now - last_in_challenge_true_ms_) < 2000)
            || (std::isfinite(current_time_remaining) && current_time_remaining > 0.25f);
        if (challenge_expected_active && !paused_runtime_context) {
            const bool has_critical_read = has_critical_seconds_read || has_critical_tick_read || has_critical_score_read;
            if (has_critical_read) {
                critical_read_miss_streak_ = 0;
            } else {
                ++critical_read_miss_streak_;
                if (critical_read_miss_streak_ >= 75 && now >= next_critical_recover_allowed_ms_) {
                    next_critical_recover_allowed_ms_ = now + 1500;
                    critical_read_miss_streak_ = 0;
                    fault_state_guard_until_ms_ = now + 2500;
                    reset_runtime_resolvers();
                    fault_backoff_until_ms_ = now + 100;
                    if (rust_ready_) {
                        std::array<char, 192> json{};
                        std::snprintf(
                            json.data(),
                            json.size(),
                            "{\"ev\":\"ue4ss_prod_stale_refresh\",\"reason\":\"critical_reads_missing\",\"miss_ticks\":%u}",
                            static_cast<unsigned int>(75)
                        );
                        kovaaks::RustBridge::emit_json(json.data());
                    }
                    if (verbose_logs_) {
                        RC::Output::send<RC::LogLevel::Warning>(
                            STR("[kmod-prod] critical reads missing during challenge; forcing resolver refresh\n")
                        );
                    }
                }
            }
        } else {
            critical_read_miss_streak_ = 0;
            next_critical_recover_allowed_ms_ = 0;
        }

        const bool bridge_connected = kovaaks::RustBridge::is_connected();
        const bool likely_active_stream =
            lifecycle_active_
            || last_is_in_challenge_ == 1
            || last_is_in_scenario_ == 1
            || (std::isfinite(last_queue_time_remaining_) && last_queue_time_remaining_ > 0.0001f)
            || (std::isfinite(last_time_remaining_) && last_time_remaining_ > 0.0001f);
        uint64_t stream_activity_ms = last_runtime_progress_ms_;
        if (last_state_change_emit_ms_ > stream_activity_ms) {
            stream_activity_ms = last_state_change_emit_ms_;
        }
        if (likely_active_stream
            && !paused_runtime_context
            && stream_activity_ms > 0
            && !kmod_replay::replay_ingame_playback_is_active()) {
            constexpr uint64_t k_stream_stall_ms = 850;
            const uint64_t stall_ms = safe_elapsed_ms(now, stream_activity_ms);
            if (stall_ms > k_stream_stall_ms
                && now >= next_stream_stall_recover_allowed_ms_) {
                next_stream_stall_recover_allowed_ms_ = now + 900;
                fault_state_guard_until_ms_ = now + 2000;
                reset_runtime_resolvers();
                if (bridge_connected) {
                    (void)emit_requested_state_snapshot_safe(now, "stream_stall_recover");
                }
                if (rust_ready_) {
                    std::array<char, 256> json{};
                    std::snprintf(
                        json.data(),
                        json.size(),
                        "{\"ev\":\"ue4ss_prod_stale_refresh\",\"reason\":\"stream_stall\",\"stall_ms\":%llu}",
                        static_cast<unsigned long long>(stall_ms)
                    );
                    kovaaks::RustBridge::emit_json(json.data());
                }
            }
        }
        if (bridge_connected && !last_bridge_connected_) {
            refresh_live_runtime_hooks_after_reconnect(now);
            flush_pending_shot_telemetry(now, true);
            const bool snapshot_emitted = emit_requested_state_snapshot_safe(now, "bridge_connected");
            next_bridge_keepalive_snapshot_ms_ = now + 2000;
            if (snapshot_emitted) {
                std::array<char, 256> sbuf{};
                std::snprintf(
                    sbuf.data(),
                    sbuf.size(),
                    "[state_snapshot] emitted reason=%s ts_ms=%llu",
                    "bridge_connected",
                    static_cast<unsigned long long>(now)
                );
                runtime_log_line(sbuf.data());
                if (verbose_logs_) {
                    events_log_line(sbuf.data());
                }
            } else {
                runtime_log_line("[state_snapshot] emit failed reason=bridge_connected");
            }
        }
        if (bridge_connected && (challenge_state_edge || scenario_state_edge)) {
            (void)emit_requested_state_snapshot_safe(
                now,
                challenge_state_edge ? "state_edge:challenge" : "state_edge:scenario"
            );
        }
        if (!bridge_connected) {
            next_bridge_keepalive_snapshot_ms_ = 0;
        } else if (!kmod_replay::replay_ingame_playback_is_active()) {
            const bool quiet_bridge_window =
                (last_state_change_emit_ms_ == 0 || safe_elapsed_ms(now, last_state_change_emit_ms_) > 1800)
                && (last_runtime_progress_ms_ == 0 || safe_elapsed_ms(now, last_runtime_progress_ms_) > 1800);
            if (quiet_bridge_window && now >= next_bridge_keepalive_snapshot_ms_) {
                next_bridge_keepalive_snapshot_ms_ = now + 2000;
                (void)emit_requested_state_snapshot_safe(now, "bridge_keepalive");
            }
        }
        if (bridge_connected) {
            flush_pending_shot_telemetry(now, false);
            kmod_replay::BridgeCommand command{};
            while (kmod_replay::poll_bridge_command(command)) {
                if (command.kind == kmod_replay::BridgeCommandKind::StateSnapshotRequest) {
                    const auto request_reason = command.reason.empty() ? std::string{"unknown"} : command.reason;
                    flush_pending_shot_telemetry(now, true);
                    if (emit_requested_state_snapshot_safe(now, request_reason)) {
                        std::array<char, 256> sbuf{};
                        std::snprintf(
                            sbuf.data(),
                            sbuf.size(),
                            "[state_snapshot] emitted reason=%s ts_ms=%llu",
                            request_reason.c_str(),
                            static_cast<unsigned long long>(now)
                        );
                        runtime_log_line(sbuf.data());
                        if (verbose_logs_) {
                            events_log_line(sbuf.data());
                        }
                    } else {
                        std::array<char, 256> sbuf{};
                        std::snprintf(
                            sbuf.data(),
                            sbuf.size(),
                            "[state_snapshot] emit failed reason=%s",
                            request_reason.c_str()
                        );
                        runtime_log_line(sbuf.data());
                    }
                } else {
                    (void)replay_playback_command_safe(now, command);
                }
            }
        }

        perf_lap(7);
        (void)replay_playback_tick_safe(now);
        perf_lap(8);

        {
            const bool replay_effective_active_now =
                current_is_in_challenge > 0
                || current_is_in_scenario > 0
                || current_challenge_tick_count > 0
                || current_shots_fired > 0
                || current_shots_hit > 0
                || current_kills_total > 0
                || (std::isfinite(current_seconds) && current_seconds > 0.05f)
                || (std::isfinite(current_time_remaining) && current_time_remaining > 0.05f)
                || (std::isfinite(current_score_total) && current_score_total > 0.05f)
                || (std::isfinite(current_score_total_derived) && current_score_total_derived > 0.05f);
            const bool replay_paused_runtime_context =
                current_scenario_is_paused == 1
                || last_scenario_is_paused_ == 1
                || s_last_pull_scenario_is_paused == 1;
            kmod_replay::ReplayTickInput replay_input{};
            replay_input.now_ms = now;
            replay_input.bridge_connected = bridge_connected;
            replay_input.context.run_id = 0;
            replay_input.context.scenario_name = !last_scenario_name_.empty() ? last_scenario_name_ : s_last_run_scenario_name;
            replay_input.context.scenario_id = s_last_run_scenario_id;
            replay_input.context.scenario_manager_id = s_last_run_scenario_manager_id;
            replay_input.context.scenario_play_type = -1;
            replay_input.context.is_replay = 0;

            replay_input.scalars.is_in_challenge = s_last_pull_is_in_challenge;
            replay_input.scalars.is_in_scenario = s_last_pull_is_in_scenario;
            replay_input.scalars.is_in_scenario_editor = s_last_pull_is_in_scenario_editor;
            replay_input.scalars.is_in_trainer = last_is_in_trainer_;
            replay_input.scalars.scenario_is_paused = s_last_pull_scenario_is_paused;
            replay_input.scalars.scenario_is_enabled = -1;
            replay_input.scalars.challenge_seconds_total = s_last_pull_challenge_seconds;
            replay_input.scalars.session_seconds_total = last_seconds_;
            replay_input.scalars.time_remaining = s_last_pull_time_remaining;
            replay_input.scalars.queue_time_remaining = s_last_pull_queue_time_remaining;
            replay_input.scalars.score_metric_total = last_score_total_;
            replay_input.scalars.score_total_derived = last_score_total_derived_;
            replay_input.scalars.score_source = std::string{};

            const bool should_emit_live_replay_tick =
                replay_effective_active_now || (replay_paused_runtime_context && lifecycle_active_);
            if (!kmod_replay::replay_ingame_playback_is_active() && should_emit_live_replay_tick) {
                (void)replay_tick_safe(now, replay_input);
            }
        }
        perf_lap(9);
        last_bridge_connected_ = bridge_connected;

        const bool has_state_signal =
            current_is_in_challenge >= 0
            || current_is_in_scenario >= 0
            || current_is_in_scenario_editor >= 0
            || current_is_currently_in_benchmark >= 0
            || current_is_in_trainer >= 0
            || current_scenario_is_paused >= 0;
        const bool has_nonzero_signal =
            (current_is_in_challenge > 0)
            || (current_kills_total > 0)
            || (current_shots_fired > 0)
            || (current_shots_hit > 0)
            || (std::isfinite(current_seconds) && current_seconds > 0.0001f)
            || (std::isfinite(current_score_total) && current_score_total > 0.0001f)
            || (std::isfinite(current_score_per_minute) && current_score_per_minute > 0.0001f)
            || (std::isfinite(current_kills_per_second) && current_kills_per_second > 0.0001f)
            || (std::isfinite(current_accuracy) && current_accuracy > 0.0001f)
            || (std::isfinite(current_damage_done) && current_damage_done > 0.0001f)
            || (std::isfinite(current_damage_possible) && current_damage_possible > 0.0001f)
            || (std::isfinite(current_damage_efficiency) && current_damage_efficiency > 0.0001f)
            || (std::isfinite(current_time_remaining) && current_time_remaining > 0.0001f)
            || (std::isfinite(current_queue_time_remaining) && current_queue_time_remaining > 0.0001f);
        if (has_nonzero_signal || has_state_signal) {
            zero_signal_streak_ = 0;
        } else if (++zero_signal_streak_ >= 90) { // ~3s at 30Hz
            {
                // If state sources go silent for a sustained period, emit an
                // explicit idle reset so frontend state doesn't stay latched.
                EmitTagScope idle_scope(*this, "idle_fallback", "state_inferred");
                emit_state_i32("pull_is_in_challenge", last_is_in_challenge_, 0, now);
                emit_state_i32("pull_is_in_scenario", last_is_in_scenario_, 0, now);
                emit_state_i32("pull_is_in_scenario_editor", last_is_in_scenario_editor_, 0, now);
                emit_state_i32("pull_is_currently_in_benchmark", last_is_currently_in_benchmark_, 0, now);
                emit_state_i32("pull_is_in_trainer", last_is_in_trainer_, 0, now);
                emit_pull_f32("pull_time_remaining", last_time_remaining_, 0.0f, last_nonzero_time_remaining_ms_, now);
                emit_pull_f32(
                    "pull_queue_time_remaining",
                    last_queue_time_remaining_,
                    0.0f,
                    last_nonzero_queue_time_remaining_ms_,
                    now
                );
            }
            reset_runtime_resolvers();
            zero_signal_streak_ = 0;
            fault_backoff_until_ms_ = now + 100;
            if (verbose_logs_) {
                RC::Output::send<RC::LogLevel::Warning>(STR("[kmod-prod] zero-signal streak hit, forcing resolver refresh\n"));
            }
        }

        if (verbose_logs_ && now >= next_diag_log_ms_) {
            next_diag_log_ms_ = now + 2000;
            const auto receiver_name = receiver ? receiver->GetFullName() : STR("null");
            const auto scenario_name = scenario_manager ? scenario_manager->GetFullName() : STR("null");
            const auto meta_name = meta ? meta->GetFullName() : STR("null");
            const uint64_t have_state_targets = static_cast<uint64_t>(
                targets_.get_shots_fired_value_else != nullptr || targets_.get_shots_fired_value_or != nullptr
                || targets_.receive_shots_fired_value_else != nullptr || targets_.receive_shots_fired_single != nullptr || targets_.receive_shots_fired != nullptr
                || targets_.get_shots_hit_value_else != nullptr || targets_.get_shots_hit_value_or != nullptr
                || targets_.receive_shots_hit_value_else != nullptr || targets_.receive_shots_hit_single != nullptr || targets_.receive_shots_hit != nullptr
                || targets_.get_score_per_minute_value_else != nullptr || targets_.get_score_per_minute_value_or != nullptr
                || targets_.receive_score_per_minute_value_else != nullptr || targets_.receive_score_per_minute != nullptr
                || targets_.get_kills_per_second_value_else != nullptr || targets_.get_kills_per_second_value_or != nullptr
                || targets_.receive_kills_per_second_value_else != nullptr || targets_.receive_kills_per_second != nullptr
                || targets_.get_damage_done_value_else != nullptr || targets_.get_damage_done_value_or != nullptr
                || targets_.receive_damage_done_value_else != nullptr || targets_.receive_damage_done != nullptr
                || targets_.get_damage_possible_value_else != nullptr || targets_.get_damage_possible_value_or != nullptr
                || targets_.receive_damage_possible_value_else != nullptr || targets_.receive_damage_possible != nullptr
                || targets_.get_damage_efficiency_value_else != nullptr || targets_.get_damage_efficiency_value_or != nullptr
                || targets_.receive_damage_efficiency_value_else != nullptr || targets_.receive_damage_efficiency != nullptr
                || targets_.get_seconds_value_else != nullptr || targets_.get_seconds_value_or != nullptr
                || targets_.receive_seconds != nullptr
            );
            const uint64_t have_scenario_targets = static_cast<uint64_t>(
                targets_.scenario_get_challenge_time_remaining != nullptr
                || targets_.scenario_get_challenge_queue_time_remaining != nullptr
                || targets_.scenario_is_in_challenge != nullptr
                || targets_.scenario_is_in_scenario != nullptr
                || targets_.scenario_is_in_scenario_editor != nullptr
                || targets_.scenario_is_currently_in_benchmark != nullptr
            );
            const uint64_t have_meta_targets = static_cast<uint64_t>(targets_.meta_get_in_trainer != nullptr);
            RC::Output::send<RC::LogLevel::Warning>(
                STR("[kmod-prod] sf={} sh={} ic={} is={} ise={} bench={} trainer={} sec={} spm={} kps={} dd={} dp={} de={} tr={} qtr={} receiver={} scenario={} meta={} tgt_state={} tgt_scenario={} tgt_meta={}\n"),
                last_shots_fired_,
                last_shots_hit_,
                last_is_in_challenge_,
                last_is_in_scenario_,
                last_is_in_scenario_editor_,
                last_is_currently_in_benchmark_,
                last_is_in_trainer_,
                static_cast<double>(last_seconds_),
                static_cast<double>(last_score_per_minute_),
                static_cast<double>(last_kills_per_second_),
                static_cast<double>(last_damage_done_),
                static_cast<double>(last_damage_possible_),
                static_cast<double>(last_damage_efficiency_),
                static_cast<double>(last_time_remaining_),
                static_cast<double>(last_queue_time_remaining_),
                receiver_name,
                scenario_name,
                meta_name,
                have_state_targets,
                have_scenario_targets,
                have_meta_targets
            );
        }
    }

    void handle_runtime_fault(const char* where) {
        ++fault_count_;
        const uint64_t now = GetTickCount64();
        const uint64_t backoff_ms = (fault_count_ < 20) ? 250 : 1000;
        fault_backoff_until_ms_ = now + backoff_ms;
        fault_state_guard_until_ms_ = now + 2500;
        reset_runtime_resolvers();
        if (fault_count_ >= 200) {
            updates_disabled_ = true;
        }
        if (rust_ready_) {
            std::array<char, 192> json{};
            std::snprintf(
                json.data(),
                json.size(),
                "{\"ev\":\"ue4ss_prod_fault\",\"where\":\"%s\",\"count\":%u,\"disabled\":%u}",
                where ? where : "unknown",
                static_cast<unsigned int>(fault_count_),
                updates_disabled_ ? 1u : 0u
            );
            kovaaks::RustBridge::emit_json(json.data());
        }
        RC::Output::send<RC::LogLevel::Error>(
            STR("[kmod-prod] runtime fault trapped; backoff active (count={} disabled={})\n"),
            static_cast<uint64_t>(fault_count_),
            static_cast<uint64_t>(updates_disabled_ ? 1 : 0)
        );
    }

private:
    struct Targets {
        RC::Unreal::UFunction* send_sa_http_request{};
        RC::Unreal::UFunction* sa_http_invoke_success{};
        RC::Unreal::UFunction* sa_http_invoke_error{};
        RC::Unreal::UFunction* get_kills_value_else{};
        RC::Unreal::UFunction* get_kills_value_or{};
        RC::Unreal::UFunction* receive_kills_value_else{};
        RC::Unreal::UFunction* receive_kills_value_or{};
        RC::Unreal::UFunction* receive_kills_single{};
        RC::Unreal::UFunction* receive_kills{};
        RC::Unreal::UFunction* get_score_value_else{};
        RC::Unreal::UFunction* get_score_value_or{};
        RC::Unreal::UFunction* receive_score_value_else{};
        RC::Unreal::UFunction* receive_score_value_or{};
        RC::Unreal::UFunction* receive_score_single{};
        RC::Unreal::UFunction* receive_score{};
        RC::Unreal::UFunction* get_accuracy_value_else{};
        RC::Unreal::UFunction* get_accuracy_value_or{};
        RC::Unreal::UFunction* receive_accuracy_value_else{};
        RC::Unreal::UFunction* receive_accuracy_value_or{};
        RC::Unreal::UFunction* receive_accuracy_single{};
        RC::Unreal::UFunction* receive_accuracy{};
        RC::Unreal::UFunction* get_shots_fired_value_else{};
        RC::Unreal::UFunction* get_shots_fired_value_or{};
        RC::Unreal::UFunction* receive_shots_fired_value_else{};
        RC::Unreal::UFunction* receive_shots_fired_value_or{};
        RC::Unreal::UFunction* receive_shots_fired_single{};
        RC::Unreal::UFunction* receive_shots_fired{};
        RC::Unreal::UFunction* get_shots_hit_value_else{};
        RC::Unreal::UFunction* get_shots_hit_value_or{};
        RC::Unreal::UFunction* receive_shots_hit_value_else{};
        RC::Unreal::UFunction* receive_shots_hit_value_or{};
        RC::Unreal::UFunction* receive_shots_hit_single{};
        RC::Unreal::UFunction* receive_shots_hit{};
        RC::Unreal::UFunction* get_seconds_value_else{};
        RC::Unreal::UFunction* get_seconds_value_or{};
        RC::Unreal::UFunction* receive_seconds_value_else{};
        RC::Unreal::UFunction* receive_seconds_value_or{};
        RC::Unreal::UFunction* receive_seconds_single{};
        RC::Unreal::UFunction* receive_seconds{};
        RC::Unreal::UFunction* get_score_per_minute_value_else{};
        RC::Unreal::UFunction* get_score_per_minute_value_or{};
        RC::Unreal::UFunction* receive_score_per_minute_value_else{};
        RC::Unreal::UFunction* receive_score_per_minute_value_or{};
        RC::Unreal::UFunction* receive_score_per_minute{};
        RC::Unreal::UFunction* get_kills_per_second_value_else{};
        RC::Unreal::UFunction* get_kills_per_second_value_or{};
        RC::Unreal::UFunction* receive_kills_per_second_value_else{};
        RC::Unreal::UFunction* receive_kills_per_second_value_or{};
        RC::Unreal::UFunction* receive_kills_per_second{};
        RC::Unreal::UFunction* get_damage_done_value_else{};
        RC::Unreal::UFunction* get_damage_done_value_or{};
        RC::Unreal::UFunction* receive_damage_done_value_else{};
        RC::Unreal::UFunction* receive_damage_done{};
        RC::Unreal::UFunction* get_damage_possible_value_else{};
        RC::Unreal::UFunction* get_damage_possible_value_or{};
        RC::Unreal::UFunction* receive_damage_possible_value_else{};
        RC::Unreal::UFunction* receive_damage_possible{};
        RC::Unreal::UFunction* get_damage_efficiency_value_else{};
        RC::Unreal::UFunction* get_damage_efficiency_value_or{};
        RC::Unreal::UFunction* receive_damage_efficiency_value_else{};
        RC::Unreal::UFunction* receive_damage_efficiency{};
        RC::Unreal::UFunction* get_challenge_average_fps_value_else{};
        RC::Unreal::UFunction* get_challenge_average_fps_value_or{};
        RC::Unreal::UFunction* receive_challenge_average_fps_value_else{};
        RC::Unreal::UFunction* receive_challenge_average_fps_value_or{};
        RC::Unreal::UFunction* receive_challenge_average_fps_single{};
        RC::Unreal::UFunction* receive_challenge_average_fps{};
        RC::Unreal::UFunction* get_challenge_tick_count_value_else{};
        RC::Unreal::UFunction* get_challenge_tick_count_value_or{};
        RC::Unreal::UFunction* receive_challenge_tick_count_value_else{};
        RC::Unreal::UFunction* receive_challenge_tick_count_value_or{};
        RC::Unreal::UFunction* receive_challenge_tick_count_single{};
        RC::Unreal::UFunction* receive_challenge_tick_count{};
        RC::Unreal::UFunction* scenario_state_get_is_paused_value_or{};
        RC::Unreal::UFunction* scenario_state_get_is_paused_value_else{};
        RC::Unreal::UFunction* scenario_state_receive_is_paused{};
        RC::Unreal::UFunction* scenario_state_receive_is_paused_single{};
        RC::Unreal::UFunction* scenario_state_receive_is_paused_value_else{};
        RC::Unreal::UFunction* scenario_state_receive_is_paused_value_or{};
        RC::Unreal::UFunction* meta_get_in_trainer{};
        RC::Unreal::UFunction* scenario_get_challenge_time_remaining{};
        RC::Unreal::UFunction* scenario_get_challenge_queue_time_remaining{};
        RC::Unreal::UFunction* scenario_is_in_challenge{};
        RC::Unreal::UFunction* scenario_is_in_scenario{};
        RC::Unreal::UFunction* scenario_is_in_scenario_editor{};
        RC::Unreal::UFunction* scenario_is_currently_in_benchmark{};
    };

    struct UiSnapshot {
        int32_t shots_fired{-1};
        int32_t shots_hit{-1};
        int32_t kills{-1};
        std::string scenario_name{};
        float seconds{-1.0f};
        float score_per_minute{-1.0f};
        float score_total{-1.0f};
        float kills_per_second{-1.0f};
        float damage_done{-1.0f};
        float damage_possible{-1.0f};
        float damage_efficiency{-1.0f};
        float avg_ttk{-1.0f};
    };

    struct NumericInvokeResult {
        bool valid{false};
        bool is_floating{false};
        double as_float{0.0};
        int64_t as_int{0};
    };

    struct BoolInvokeResult {
        bool valid{false};
        bool value{false};
    };

    struct ReceiverActivityProbe {
        bool valid{false};
        float seconds{-1.0f};
        int32_t challenge_tick_count{-1};
        float score_total{-1.0f};
    };

    static inline std::atomic<bool> s_disable_direct_invoke_path{false};
    static inline std::atomic<uint64_t> s_direct_invoke_faults{0};
    static inline std::atomic<uint64_t> s_direct_poll_errors{0};
    static inline std::atomic<uint64_t> s_direct_invoke_last_fault_ms{0};

    static inline int32_t s_last_pull_is_in_challenge{-1};
    static inline int32_t s_last_pull_is_in_scenario{-1};
    static inline int32_t s_last_pull_is_in_scenario_editor{-1};
    static inline int32_t s_last_pull_scenario_is_in_editor{-1};
    static inline int32_t s_last_pull_scenario_is_paused{-1};
    static inline float s_last_pull_queue_time_remaining{-1.0f};
    static inline float s_last_pull_score{-1.0f};
    static inline float s_last_pull_score_derived{-1.0f};
    static inline int32_t s_last_pull_kills{-1};
    static inline float s_last_pull_spm{-1.0f};
    static inline float s_last_pull_challenge_seconds{-1.0f};
    static inline float s_last_pull_challenge_average_fps{-1.0f};
    static inline int32_t s_last_pull_challenge_tick_count{-1};
    static inline float s_last_pull_time_remaining{-1.0f};
    static inline std::string s_last_run_scenario_name{};
    static inline std::string s_last_run_scenario_id{};
    static inline std::string s_last_run_scenario_manager_id{};

    bool rust_ready_{false};
    Targets targets_{};

    RC::Unreal::UObject* meta_game_instance_{nullptr};
    RC::Unreal::UObject* state_receiver_instance_{nullptr};
    RC::Unreal::UObject* scenario_state_receiver_instance_{nullptr};
    RC::Unreal::UObject* scenario_manager_instance_{nullptr};
    RC::Unreal::UClass* meta_game_instance_class_{nullptr};
    RC::Unreal::UClass* state_receiver_class_{nullptr};
    RC::Unreal::UClass* scenario_state_receiver_class_{nullptr};
    RC::Unreal::UClass* scenario_manager_class_{nullptr};
    RC::Unreal::UFunction* text_set_fn_{nullptr};
    RC::Unreal::UFunction* text_get_fn_{nullptr};
    uint64_t text_set_hook_id_{0};
    bool text_set_hook_registered_{false};
    std::unordered_map<RC::Unreal::UClass*, RC::Unreal::FTextProperty*> text_property_cache_{};
    std::mutex ui_mutex_{};
    UiSnapshot ui_live_snapshot_{};
    bool ui_live_updated_{false};

    std::unordered_map<RC::Unreal::UFunction*, RC::Unreal::UClass*> cached_owner_class_{};

    uint64_t next_poll_ms_{0};
    uint64_t next_targets_resolve_ms_{0};
    uint64_t next_meta_resolve_ms_{0};
    uint64_t next_receiver_resolve_ms_{0};
    uint64_t next_scenario_state_resolve_ms_{0};
    uint64_t next_scenario_paused_read_ms_{0};
    uint64_t next_scenario_resolve_ms_{0};
    uint64_t next_scenario_identity_refresh_ms_{0};
    uint64_t last_in_challenge_true_ms_{0};
    uint64_t pending_in_challenge_false_ms_{0};
    uint64_t last_in_scenario_true_ms_{0};
    uint64_t pending_in_scenario_false_ms_{0};
    uint64_t fault_state_guard_until_ms_{0};
    uint64_t fault_backoff_until_ms_{0};
    uint64_t next_critical_recover_allowed_ms_{0};
    uint64_t next_rust_startup_retry_ms_{0};
    uint64_t next_rust_reconnect_log_ms_{0};
    uint64_t next_updates_disabled_log_ms_{0};
    uint64_t last_rust_reconnect_event_seq_{0};
    uint32_t critical_read_miss_streak_{0};
    uint32_t zero_signal_streak_{0};
    bool pending_bridge_recovered_snapshot_{false};

    int32_t last_kills_total_{std::numeric_limits<int32_t>::min()};
    int32_t last_shots_fired_{std::numeric_limits<int32_t>::min()};
    int32_t last_shots_hit_{std::numeric_limits<int32_t>::min()};
    int32_t last_is_in_challenge_{std::numeric_limits<int32_t>::min()};
    int32_t last_is_in_scenario_{std::numeric_limits<int32_t>::min()};
    int32_t last_is_in_scenario_editor_{std::numeric_limits<int32_t>::min()};
    int32_t last_is_currently_in_benchmark_{std::numeric_limits<int32_t>::min()};
    int32_t last_is_in_trainer_{std::numeric_limits<int32_t>::min()};
    int32_t last_scenario_is_paused_{std::numeric_limits<int32_t>::min()};
    int32_t cached_scenario_is_paused_{-1};
    float last_seconds_{std::numeric_limits<float>::quiet_NaN()};
    float last_score_total_{std::numeric_limits<float>::quiet_NaN()};
    float last_score_total_derived_{std::numeric_limits<float>::quiet_NaN()};
    float last_score_per_minute_{std::numeric_limits<float>::quiet_NaN()};
    float last_kills_per_second_{std::numeric_limits<float>::quiet_NaN()};
    float last_accuracy_{std::numeric_limits<float>::quiet_NaN()};
    float last_challenge_seconds_total_{std::numeric_limits<float>::quiet_NaN()};
    float last_challenge_average_fps_{std::numeric_limits<float>::quiet_NaN()};
    int32_t last_challenge_tick_count_{std::numeric_limits<int32_t>::min()};
    float last_damage_done_{std::numeric_limits<float>::quiet_NaN()};
    float last_damage_possible_{std::numeric_limits<float>::quiet_NaN()};
    float last_damage_efficiency_{std::numeric_limits<float>::quiet_NaN()};
    float last_time_remaining_{std::numeric_limits<float>::quiet_NaN()};
    float last_queue_time_remaining_{std::numeric_limits<float>::quiet_NaN()};
    std::string last_scenario_name_{};

    uint64_t last_nonzero_kills_total_ms_{0};
    uint64_t last_nonzero_shots_fired_ms_{0};
    uint64_t last_nonzero_shots_hit_ms_{0};
    uint64_t last_nonzero_seconds_ms_{0};
    uint64_t last_nonzero_score_total_ms_{0};
    uint64_t last_nonzero_score_total_derived_ms_{0};
    uint64_t last_nonzero_spm_ms_{0};
    uint64_t last_nonzero_kills_per_second_ms_{0};
    uint64_t last_nonzero_accuracy_ms_{0};
    uint64_t last_nonzero_challenge_seconds_ms_{0};
    uint64_t last_nonzero_challenge_average_fps_ms_{0};
    uint64_t last_nonzero_challenge_tick_count_ms_{0};
    uint64_t last_nonzero_damage_done_ms_{0};
    uint64_t last_nonzero_damage_possible_ms_{0};
    uint64_t last_nonzero_damage_efficiency_ms_{0};
    uint64_t last_nonzero_time_remaining_ms_{0};
    uint64_t last_nonzero_queue_time_remaining_ms_{0};
    bool lifecycle_active_{false};
    bool lifecycle_initialized_{false};
    bool lifecycle_queued_{false};
    bool lifecycle_seen_progress_{false};
    uint64_t last_lifecycle_signal_ms_{0};
    uint64_t last_lifecycle_start_ms_{0};
    uint64_t pending_lifecycle_end_ms_{0};
    uint64_t last_state_change_emit_ms_{0};
    uint64_t next_bridge_keepalive_snapshot_ms_{0};
    uint64_t last_state_transition_refresh_ms_{0};
    uint64_t last_runtime_progress_ms_{0};
    uint64_t next_live_stream_refresh_ms_{0};
    uint64_t next_stream_stall_recover_allowed_ms_{0};
    uint64_t last_true_start_transition_ms_{0};
    RC::Unreal::UObject* live_metric_receiver_hint_{nullptr};
    uint64_t live_metric_receiver_hint_ms_{0};
    float last_observed_seconds_value_{std::numeric_limits<float>::quiet_NaN()};
    int32_t last_observed_challenge_tick_count_{std::numeric_limits<int32_t>::min()};
    float last_observed_queue_time_remaining_{std::numeric_limits<float>::quiet_NaN()};
    float last_observed_time_remaining_{std::numeric_limits<float>::quiet_NaN()};
    float last_raw_queue_time_remaining_{std::numeric_limits<float>::quiet_NaN()};
    uint64_t last_queue_countdown_edge_ms_{0};
    uint64_t replay_fault_backoff_until_ms_{0};
    uint32_t replay_fault_count_{0};
    bool replay_fault_latched_{false};
    bool replay_capture_disabled_{false};
    bool verbose_logs_{false};
    bool http_trace_enabled_{false};
    uint64_t next_diag_log_ms_{0};
    bool updates_disabled_{false};
    uint32_t fault_count_{0};
    bool last_bridge_connected_{false};
    const char* emit_method_{"unknown"};
    const char* emit_origin_flag_{"unknown"};
    std::vector<std::string> pending_shot_telemetry_events_{};
    uint64_t pending_shot_telemetry_started_ms_{0};
    std::vector<std::pair<RC::Unreal::UFunction*, uint64_t>> live_metric_hook_bindings_{};
    bool live_metric_hooks_registered_{false};
    std::vector<std::pair<RC::Unreal::UFunction*, uint64_t>> http_trace_hook_bindings_{};
    bool http_trace_hooks_registered_{false};
    uint64_t next_http_trace_request_id_{1};
    std::unordered_map<RC::Unreal::UObject*, InGameHttpTraceRequest> pending_http_trace_requests_{};
    bool live_pull_snapshot_dirty_{false};
    bool user_management_build_tag_sent_{false};
    uint64_t user_management_fault_backoff_until_ms_{0};
    uint32_t user_management_fault_count_{0};
    bool user_mgmt_enabled_{true};
    RC::Unreal::UObject* hook_context_last_{nullptr};
    bool hook_context_is_receiver_{false};
    std::string last_resolve_log_found_{};
    std::string last_resolve_log_rejected_{};

    // Keep user identity and friend resolution isolated from the core bridge loop.
    #include "kmod/user_management.inl"

    static inline KovaaksBridgeModProduction* s_instance_{nullptr};

    void maybe_emit_user_management_build_tag() {
        if (!user_mgmt_enabled_ || user_management_build_tag_sent_ || !kovaaks::RustBridge::is_connected()) {
            return;
        }
        user_management_build_tag_sent_ = true;
        kovaaks::RustBridge::emit_json(
            "{\"ev\":\"kovaaks_user_debug\",\"message\":\"build user_mgmt_diag_v3\"}"
        );
    }

    void reset_runtime_resolvers() {
        meta_game_instance_ = nullptr;
        state_receiver_instance_ = nullptr;
        scenario_state_receiver_instance_ = nullptr;
        scenario_manager_instance_ = nullptr;
        cached_scenario_is_paused_ = -1;
        live_metric_receiver_hint_ = nullptr;
        live_metric_receiver_hint_ms_ = 0;
        next_live_stream_refresh_ms_ = 0;
        next_meta_resolve_ms_ = 0;
        next_receiver_resolve_ms_ = 0;
        next_scenario_state_resolve_ms_ = 0;
        next_scenario_paused_read_ms_ = 0;
        next_scenario_resolve_ms_ = 0;
        next_targets_resolve_ms_ = 0;
    }

    static void emit_simple_event(const char* ev) {
        std::array<char, 96> json{};
        std::snprintf(json.data(), json.size(), "{\"ev\":\"%s\"}", ev);
        kovaaks::RustBridge::emit_json(json.data());
    }

    static auto safe_elapsed_ms(uint64_t now_ms, uint64_t then_ms) -> uint64_t {
        if (then_ms == 0 || then_ms > now_ms) {
            return 0;
        }
        return now_ms - then_ms;
    }

    void refresh_live_runtime_hooks_after_reconnect(uint64_t now) {
        unregister_live_metric_hooks();
        unregister_http_trace_hooks();
        reset_runtime_resolvers();
        next_targets_resolve_ms_ = 0;
        resolve_targets(true);
        last_runtime_progress_ms_ = now;
        last_state_change_emit_ms_ = now;
        runtime_log_line("[bridge_reconnect] refreshed live metric hooks after reconnect");
    }

    void emit_lifecycle_start(uint64_t now) {
        lifecycle_active_ = true;
        lifecycle_queued_ = false;
        lifecycle_seen_progress_ = false;
        last_lifecycle_start_ms_ = now;
        replay_capture_disabled_ = false;
        last_lifecycle_signal_ms_ = now;
        pending_lifecycle_end_ms_ = 0;
        next_receiver_resolve_ms_ = 0;
        next_scenario_state_resolve_ms_ = 0;
        next_scenario_paused_read_ms_ = 0;
        next_scenario_resolve_ms_ = 0;
        cached_scenario_is_paused_ = 0;
        last_scenario_is_paused_ = 0;
        s_last_pull_scenario_is_paused = 0;
        last_observed_seconds_value_ = std::numeric_limits<float>::quiet_NaN();
        last_observed_challenge_tick_count_ = std::numeric_limits<int32_t>::min();
        last_observed_queue_time_remaining_ = std::numeric_limits<float>::quiet_NaN();
        last_observed_time_remaining_ = std::numeric_limits<float>::quiet_NaN();
        last_raw_queue_time_remaining_ = std::numeric_limits<float>::quiet_NaN();
        last_true_start_transition_ms_ = now;
        live_metric_receiver_hint_ = nullptr;
        live_metric_receiver_hint_ms_ = 0;
        emit_simple_event("session_start");
        emit_simple_event("challenge_start");
        emit_simple_event("scenario_start");
        (void)emit_requested_state_snapshot_safe(now, "lifecycle_start");
    }

    void emit_lifecycle_end(bool completed, uint64_t now) {
        lifecycle_active_ = false;
        last_lifecycle_start_ms_ = 0;
        replay_capture_disabled_ = false;
        pending_lifecycle_end_ms_ = 0;
        live_metric_receiver_hint_ = nullptr;
        live_metric_receiver_hint_ms_ = 0;
        flush_pending_shot_telemetry(now, true);
        emit_simple_event("challenge_end");
        emit_simple_event("scenario_end");
        if (completed) {
            emit_simple_event("challenge_complete");
            emit_simple_event("challenge_completed");
            emit_simple_event("post_challenge_complete");
        } else {
            emit_simple_event("challenge_canceled");
        }
        emit_simple_event("session_end");
        (void)emit_requested_state_snapshot_safe(
            now,
            completed ? "lifecycle_end_completed" : "lifecycle_end_canceled"
        );
    }

    void update_lifecycle_events(
        uint64_t now,
        int32_t is_in_challenge,
        int32_t is_in_scenario,
        int32_t scenario_is_paused,
        int32_t shots_fired,
        int32_t shots_hit,
        float seconds_total,
        float score_per_minute,
        float damage_done,
        float damage_possible,
        float time_remaining,
        float queue_time_remaining
    ) {
        const auto has_float = [](float v) {
            return std::isfinite(v) && v >= 0.0f;
        };
        const auto has_positive_float = [](float v) {
            return std::isfinite(v) && v > 0.0001f;
        };

        const bool has_signal =
            is_in_challenge >= 0 ||
            is_in_scenario >= 0 ||
            shots_fired >= 0 ||
            shots_hit >= 0 ||
            has_float(seconds_total) ||
            has_float(score_per_minute) ||
            has_float(damage_done) ||
            has_float(damage_possible) ||
            has_float(time_remaining) ||
            has_float(queue_time_remaining);
        if (!has_signal) {
            return;
        }

        const bool progress_signal =
            shots_fired > 0 ||
            shots_hit > 0 ||
            has_positive_float(seconds_total) ||
            has_positive_float(score_per_minute) ||
            has_positive_float(damage_done) ||
            has_positive_float(damage_possible);
        const bool paused_signal = scenario_is_paused > 0;
        const bool queue_signal = has_positive_float(queue_time_remaining);
        const bool recent_stream_activity =
            last_state_change_emit_ms_ > 0 && safe_elapsed_ms(now, last_state_change_emit_ms_) < 1200;
        const bool runtime_active_hint =
            !paused_signal
            && (progress_signal || has_positive_float(time_remaining))
            && recent_stream_activity;
        const bool known_in_challenge = is_in_challenge >= 0;
        const bool known_in_scenario = is_in_scenario >= 0;
        const bool challenge_active_signal = known_in_challenge && (is_in_challenge != 0);
        const bool scenario_active_signal = known_in_scenario && (is_in_scenario != 0);
        bool active_signal = challenge_active_signal;
        if (!active_signal && runtime_active_hint) {
            if (!known_in_challenge && !known_in_scenario) {
                active_signal = true;
            } else if (scenario_active_signal || lifecycle_active_ || has_positive_float(time_remaining) || progress_signal) {
                active_signal = true;
            }
        }

        if (!lifecycle_initialized_) {
            lifecycle_initialized_ = true;
            lifecycle_queued_ = queue_signal;
            lifecycle_seen_progress_ = false;
            pending_lifecycle_end_ms_ = 0;
            if (queue_signal && !active_signal) {
                emit_simple_event("challenge_queued");
            }
            if (active_signal) {
                emit_lifecycle_start(now);
                lifecycle_seen_progress_ = progress_signal;
            }
            return;
        }

        if (queue_signal && !lifecycle_queued_ && !lifecycle_active_) {
            lifecycle_queued_ = true;
            emit_simple_event("challenge_queued");
        } else if (!queue_signal && !lifecycle_active_) {
            lifecycle_queued_ = false;
        }

        if (active_signal) {
            if (!lifecycle_active_) {
                emit_lifecycle_start(now);
            }
            if (progress_signal) {
                lifecycle_seen_progress_ = true;
            }
            last_lifecycle_signal_ms_ = now;
            pending_lifecycle_end_ms_ = 0;
            return;
        }

        if (paused_signal && lifecycle_active_) {
            // Pausing mid-run is still part of the same scenario lifecycle.
            last_lifecycle_signal_ms_ = now;
            pending_lifecycle_end_ms_ = 0;
            return;
        }

        if (!lifecycle_active_) {
            pending_lifecycle_end_ms_ = 0;
            return;
        }

        if (known_in_challenge) {
            constexpr uint64_t k_state_end_confirm_ms = 120;
            if (runtime_active_hint) {
                last_lifecycle_signal_ms_ = now;
                pending_lifecycle_end_ms_ = 0;
                return;
            }
            if (pending_lifecycle_end_ms_ == 0) {
                pending_lifecycle_end_ms_ = now;
                return;
            }
            if ((now - pending_lifecycle_end_ms_) < k_state_end_confirm_ms) {
                return;
            }
            pending_lifecycle_end_ms_ = 0;
            emit_lifecycle_end(lifecycle_seen_progress_ || progress_signal, now);
            return;
        }

        constexpr uint64_t k_idle_end_ms = 700;
        if (last_lifecycle_signal_ms_ > 0 && (now - last_lifecycle_signal_ms_) > k_idle_end_ms) {
            pending_lifecycle_end_ms_ = 0;
            emit_lifecycle_end(lifecycle_seen_progress_ || progress_signal, now);
        }
    }

    void resolve_targets(bool force) {
        const uint64_t now = GetTickCount64();
        if (!force && now < next_targets_resolve_ms_) {
            return;
        }

        // Track what each lookup produced so the log shows found / missing / rejected targets.
        std::string resolved_names{};
        std::string missing_names{};
        std::string rejected_names{};
        uint32_t resolved_count = 0;
        uint32_t missing_count = 0;
        uint32_t rejected_count = 0;
        auto append_name = [](std::string& list, const char* name) {
            if (!list.empty()) {
                list += ',';
            }
            list += name;
        };
        auto resolve_tracked = [&](const char* name, const wchar_t* path) -> RC::Unreal::UFunction* {
            auto* fn = RC::Unreal::UObjectGlobals::StaticFindObject<RC::Unreal::UFunction*>(
                nullptr, nullptr, path
            );
            if (!fn) {
                append_name(missing_names, name);
                ++missing_count;
                return nullptr;
            }
            if (is_likely_valid_object_ptr(fn)
                && !is_rejected_runtime_function_name(fn->GetFullName())) {
                append_name(resolved_names, name);
                ++resolved_count;
                return fn;
            }
            append_name(rejected_names, name);
            ++rejected_count;
            return nullptr;
        };

        targets_.send_sa_http_request = resolve_tracked("send_sa_http_request", STR("/Script/GameSkillsTrainer.Send_SAHttpRequest:Send_SAHttpRequest"));
        targets_.sa_http_invoke_success = resolve_tracked("sa_http_invoke_success", STR("/Script/GameSkillsTrainer.Send_SAHttpRequest:Invoke_Success"));
        targets_.sa_http_invoke_error = resolve_tracked("sa_http_invoke_error", STR("/Script/GameSkillsTrainer.Send_SAHttpRequest:Invoke_Error"));
        targets_.get_kills_value_else = resolve_tracked("get_kills_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_Kills_ValueElse"));
        targets_.get_kills_value_or = resolve_tracked("get_kills_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_Kills_ValueOr"));
        targets_.receive_kills_value_else = resolve_tracked("receive_kills_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_Kills_ValueElse"));
        targets_.receive_kills_value_or = resolve_tracked("receive_kills_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_Kills_ValueOr"));
        targets_.receive_kills_single = resolve_tracked("receive_kills_single", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_Kills_Single"));
        targets_.receive_kills = resolve_tracked("receive_kills", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_Kills"));
        targets_.get_score_value_else = resolve_tracked("get_score_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_Score_ValueElse"));
        targets_.get_score_value_or = resolve_tracked("get_score_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_Score_ValueOr"));
        targets_.receive_score_value_else = resolve_tracked("receive_score_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_Score_ValueElse"));
        targets_.receive_score_value_or = resolve_tracked("receive_score_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_Score_ValueOr"));
        targets_.receive_score_single = resolve_tracked("receive_score_single", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_Score_Single"));
        targets_.receive_score = resolve_tracked("receive_score", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_Score"));
        targets_.get_accuracy_value_else = resolve_tracked("get_accuracy_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_Accuracy_ValueElse"));
        targets_.get_accuracy_value_or = resolve_tracked("get_accuracy_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_Accuracy_ValueOr"));
        targets_.receive_accuracy_value_else = resolve_tracked("receive_accuracy_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_Accuracy_ValueElse"));
        targets_.receive_accuracy_value_or = resolve_tracked("receive_accuracy_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_Accuracy_ValueOr"));
        targets_.receive_accuracy_single = resolve_tracked("receive_accuracy_single", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_Accuracy_Single"));
        targets_.receive_accuracy = resolve_tracked("receive_accuracy", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_Accuracy"));
        targets_.get_shots_fired_value_else = resolve_tracked("get_shots_fired_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_ShotsFired_ValueElse"));
        targets_.get_shots_fired_value_or = resolve_tracked("get_shots_fired_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_ShotsFired_ValueOr"));
        targets_.receive_shots_fired_value_else = resolve_tracked("receive_shots_fired_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_ShotsFired_ValueElse"));
        targets_.receive_shots_fired_value_or = resolve_tracked("receive_shots_fired_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_ShotsFired_ValueOr"));
        targets_.receive_shots_fired_single = resolve_tracked("receive_shots_fired_single", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_ShotsFired_Single"));
        targets_.receive_shots_fired = resolve_tracked("receive_shots_fired", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_ShotsFired"));
        targets_.get_shots_hit_value_else = resolve_tracked("get_shots_hit_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_ShotsHit_ValueElse"));
        targets_.get_shots_hit_value_or = resolve_tracked("get_shots_hit_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_ShotsHit_ValueOr"));
        targets_.receive_shots_hit_value_else = resolve_tracked("receive_shots_hit_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_ShotsHit_ValueElse"));
        targets_.receive_shots_hit_value_or = resolve_tracked("receive_shots_hit_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_ShotsHit_ValueOr"));
        targets_.receive_shots_hit_single = resolve_tracked("receive_shots_hit_single", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_ShotsHit_Single"));
        targets_.receive_shots_hit = resolve_tracked("receive_shots_hit", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_ShotsHit"));
        targets_.get_seconds_value_else = resolve_tracked("get_seconds_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_Seconds_ValueElse"));
        targets_.get_seconds_value_or = resolve_tracked("get_seconds_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_Seconds_ValueOr"));
        targets_.receive_seconds_value_else = resolve_tracked("receive_seconds_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_Seconds_ValueElse"));
        targets_.receive_seconds_value_or = resolve_tracked("receive_seconds_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_Seconds_ValueOr"));
        targets_.receive_seconds_single = resolve_tracked("receive_seconds_single", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_Seconds_Single"));
        targets_.receive_seconds = resolve_tracked("receive_seconds", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_Seconds"));
        targets_.get_score_per_minute_value_else = resolve_tracked("get_score_per_minute_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_ScorePerMinute_ValueElse"));
        targets_.get_score_per_minute_value_or = resolve_tracked("get_score_per_minute_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_ScorePerMinute_ValueOr"));
        targets_.receive_score_per_minute_value_else = resolve_tracked("receive_score_per_minute_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_ScorePerMinute_ValueElse"));
        targets_.receive_score_per_minute_value_or = resolve_tracked("receive_score_per_minute_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_ScorePerMinute_ValueOr"));
        targets_.receive_score_per_minute = resolve_tracked("receive_score_per_minute", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_ScorePerMinute"));
        targets_.get_kills_per_second_value_else = resolve_tracked("get_kills_per_second_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_KillsPerSecond_ValueElse"));
        targets_.get_kills_per_second_value_or = resolve_tracked("get_kills_per_second_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_KillsPerSecond_ValueOr"));
        targets_.receive_kills_per_second_value_else = resolve_tracked("receive_kills_per_second_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_KillsPerSecond_ValueElse"));
        targets_.receive_kills_per_second_value_or = resolve_tracked("receive_kills_per_second_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_KillsPerSecond_ValueOr"));
        targets_.receive_kills_per_second = resolve_tracked("receive_kills_per_second", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_KillsPerSecond"));
        targets_.get_damage_done_value_else = resolve_tracked("get_damage_done_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_DamageDone_ValueElse"));
        targets_.get_damage_done_value_or = resolve_tracked("get_damage_done_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_DamageDone_ValueOr"));
        targets_.receive_damage_done_value_else = resolve_tracked("receive_damage_done_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_DamageDone_ValueElse"));
        targets_.receive_damage_done = resolve_tracked("receive_damage_done", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_DamageDone"));
        targets_.get_damage_possible_value_else = resolve_tracked("get_damage_possible_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_DamagePossible_ValueElse"));
        targets_.get_damage_possible_value_or = resolve_tracked("get_damage_possible_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_DamagePossible_ValueOr"));
        targets_.receive_damage_possible_value_else = resolve_tracked("receive_damage_possible_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_DamagePossible_ValueElse"));
        targets_.receive_damage_possible = resolve_tracked("receive_damage_possible", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_DamagePossible"));
        targets_.get_damage_efficiency_value_else = resolve_tracked("get_damage_efficiency_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_DamageEfficiency_ValueElse"));
        targets_.get_damage_efficiency_value_or = resolve_tracked("get_damage_efficiency_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_DamageEfficiency_ValueOr"));
        targets_.receive_damage_efficiency_value_else = resolve_tracked("receive_damage_efficiency_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_DamageEfficiency_ValueElse"));
        targets_.receive_damage_efficiency = resolve_tracked("receive_damage_efficiency", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_DamageEfficiency"));
        targets_.get_challenge_average_fps_value_else = resolve_tracked("get_challenge_average_fps_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_ChallengeAverageFPS_ValueElse"));
        targets_.get_challenge_average_fps_value_or = resolve_tracked("get_challenge_average_fps_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_ChallengeAverageFPS_ValueOr"));
        targets_.receive_challenge_average_fps_value_else = resolve_tracked("receive_challenge_average_fps_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_ChallengeAverageFPS_ValueElse"));
        targets_.receive_challenge_average_fps_value_or = resolve_tracked("receive_challenge_average_fps_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_ChallengeAverageFPS_ValueOr"));
        targets_.receive_challenge_average_fps_single = resolve_tracked("receive_challenge_average_fps_single", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_ChallengeAverageFPS_Single"));
        targets_.receive_challenge_average_fps = resolve_tracked("receive_challenge_average_fps", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_ChallengeAverageFPS"));
        targets_.get_challenge_tick_count_value_else = resolve_tracked("get_challenge_tick_count_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_ChallengeTickCount_ValueElse"));
        targets_.get_challenge_tick_count_value_or = resolve_tracked("get_challenge_tick_count_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Get_ChallengeTickCount_ValueOr"));
        targets_.receive_challenge_tick_count_value_else = resolve_tracked("receive_challenge_tick_count_value_else", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_ChallengeTickCount_ValueElse"));
        targets_.receive_challenge_tick_count_value_or = resolve_tracked("receive_challenge_tick_count_value_or", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_ChallengeTickCount_ValueOr"));
        targets_.receive_challenge_tick_count_single = resolve_tracked("receive_challenge_tick_count_single", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_ChallengeTickCount_Single"));
        targets_.receive_challenge_tick_count = resolve_tracked("receive_challenge_tick_count", STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Receive_ChallengeTickCount"));
        targets_.scenario_state_get_is_paused_value_or = resolve_tracked("scenario_state_get_is_paused_value_or", STR("/Script/KovaaKFramework.ScenarioStateReceiver:Get_IsPaused_ValueOr"));
        targets_.scenario_state_get_is_paused_value_else = resolve_tracked("scenario_state_get_is_paused_value_else", STR("/Script/KovaaKFramework.ScenarioStateReceiver:Get_IsPaused_ValueElse"));
        targets_.scenario_state_receive_is_paused = resolve_tracked("scenario_state_receive_is_paused", STR("/Script/KovaaKFramework.ScenarioStateReceiver:Receive_IsPaused"));
        targets_.scenario_state_receive_is_paused_single = resolve_tracked("scenario_state_receive_is_paused_single", STR("/Script/KovaaKFramework.ScenarioStateReceiver:Receive_IsPaused_Single"));
        targets_.scenario_state_receive_is_paused_value_else = resolve_tracked("scenario_state_receive_is_paused_value_else", STR("/Script/KovaaKFramework.ScenarioStateReceiver:Receive_IsPaused_ValueElse"));
        targets_.scenario_state_receive_is_paused_value_or = resolve_tracked("scenario_state_receive_is_paused_value_or", STR("/Script/KovaaKFramework.ScenarioStateReceiver:Receive_IsPaused_ValueOr"));
        targets_.meta_get_in_trainer = resolve_tracked("meta_get_in_trainer", STR("/Script/GameSkillsTrainer.GTheMetaGameInstance:GetInTrainer"));
        targets_.scenario_get_challenge_time_remaining = resolve_tracked("scenario_get_challenge_time_remaining", STR("/Script/GameSkillsTrainer.ScenarioManager:GetChallengeTimeRemaining"));
        targets_.scenario_get_challenge_queue_time_remaining = resolve_tracked("scenario_get_challenge_queue_time_remaining", STR("/Script/GameSkillsTrainer.ScenarioManager:GetChallengeQueueTimeRemaining"));
        targets_.scenario_is_in_challenge = resolve_tracked("scenario_is_in_challenge", STR("/Script/GameSkillsTrainer.ScenarioManager:IsInChallenge"));
        targets_.scenario_is_in_scenario = resolve_tracked("scenario_is_in_scenario", STR("/Script/GameSkillsTrainer.ScenarioManager:IsInScenario"));
        targets_.scenario_is_in_scenario_editor = resolve_tracked("scenario_is_in_scenario_editor", STR("/Script/GameSkillsTrainer.ScenarioManager:IsInScenarioEditor"));
        targets_.scenario_is_currently_in_benchmark = resolve_tracked("scenario_is_currently_in_benchmark", STR("/Script/GameSkillsTrainer.ScenarioManager:IsCurrentlyInBenchmark"));
        // Log only when the resolved set changes (this runs every 100-2000ms).
        if (force || resolved_names != last_resolve_log_found_ || rejected_names != last_resolve_log_rejected_) {
            last_resolve_log_found_ = resolved_names;
            last_resolve_log_rejected_ = rejected_names;
            std::array<char, 160> summary{};
            std::snprintf(
                summary.data(),
                summary.size(),
                "[resolve] targets found=%u missing=%u rejected=%u force=%u",
                static_cast<unsigned int>(resolved_count),
                static_cast<unsigned int>(missing_count),
                static_cast<unsigned int>(rejected_count),
                force ? 1u : 0u
            );
            runtime_log_line(summary.data());
            log_name_list("[resolve] found", resolved_names);
            log_name_list("[resolve] missing", missing_names);
            log_name_list("[resolve] rejected", rejected_names);
        }

        register_live_metric_hooks();
        register_http_trace_hooks();

        const bool core_live_hooks_ready =
            (targets_.receive_seconds || targets_.receive_seconds_single || targets_.receive_seconds_value_else || targets_.receive_seconds_value_or) &&
            (targets_.receive_challenge_tick_count || targets_.receive_challenge_tick_count_single || targets_.receive_challenge_tick_count_value_else || targets_.receive_challenge_tick_count_value_or) &&
            (targets_.receive_score || targets_.receive_score_single || targets_.receive_score_value_else || targets_.receive_score_value_or);
        const bool active_live_window =
            lifecycle_active_
            || last_is_in_challenge_ == 1
            || last_is_in_scenario_ == 1
            || (last_true_start_transition_ms_ > 0
                && safe_elapsed_ms(now, last_true_start_transition_ms_) < 1500);
        next_targets_resolve_ms_ = now + (core_live_hooks_ready ? 2000 : (active_live_window ? 100 : 250));
    }

    RC::Unreal::UClass* resolve_function_owner_class(RC::Unreal::UFunction* fn) {
        if (!fn || !is_likely_valid_object_ptr(fn)) {
            return nullptr;
        }
        const auto it = cached_owner_class_.find(fn);
        if (it != cached_owner_class_.end() && is_likely_valid_object_ptr(it->second)) {
            return it->second;
        }

        const auto full_name = fn->GetFullName();
        const auto space_pos = full_name.find(STR(" "));
        if (space_pos == RC::StringType::npos) {
            return nullptr;
        }
        const auto colon_pos = full_name.find(STR(":"), space_pos + 1);
        if (colon_pos == RC::StringType::npos || colon_pos <= (space_pos + 1)) {
            return nullptr;
        }
        const auto class_path = full_name.substr(space_pos + 1, colon_pos - (space_pos + 1));
        auto* owner_class = RC::Unreal::UObjectGlobals::StaticFindObject<RC::Unreal::UClass*>(
            nullptr, nullptr, class_path.c_str()
        );
        if (owner_class && is_likely_valid_object_ptr(owner_class)) {
            cached_owner_class_[fn] = owner_class;
            return owner_class;
        }
        return nullptr;
    }

    static RC::Unreal::UObject* resolve_class_default_object(RC::Unreal::UClass* owner_class) {
        if (!owner_class || !is_likely_valid_object_ptr(owner_class)) {
            return nullptr;
        }
        auto* cdo = *reinterpret_cast<RC::Unreal::UObject**>(
            reinterpret_cast<uint8_t*>(owner_class) + 0x118
        );
        if (cdo && is_likely_valid_object_ptr(cdo)) {
            return cdo;
        }
        return nullptr;
    }

    RC::Unreal::UObject* resolve_receive_caller(RC::Unreal::UObject* preferred, RC::Unreal::UFunction* fn) {
        if (!fn || !is_likely_valid_object_ptr(fn)) {
            return nullptr;
        }
        auto* owner_class = resolve_function_owner_class(fn);
        if (owner_class && is_likely_valid_object_ptr(owner_class) && fn->HasAnyFunctionFlags(RC::Unreal::FUNC_Static)) {
            if (auto* cdo = resolve_class_default_object(owner_class)) {
                return cdo;
            }
        }
        if (owner_class && is_likely_valid_object_ptr(owner_class)) {
            if (preferred && is_likely_valid_object_ptr(preferred) && preferred->IsA(owner_class)) {
                return preferred;
            }
            if (meta_game_instance_ && is_likely_valid_object_ptr(meta_game_instance_) && meta_game_instance_->IsA(owner_class)) {
                return meta_game_instance_;
            }
            if (auto* cdo = resolve_class_default_object(owner_class)) {
                return cdo;
            }
        }
        if (preferred && is_likely_valid_object_ptr(preferred)) {
            return preferred;
        }
        if (meta_game_instance_ && is_likely_valid_object_ptr(meta_game_instance_)) {
            return meta_game_instance_;
        }
        return nullptr;
    }

    // Per-UFunction call metadata. Walking a function's parameters (with a
    // GetName string conversion per property) and building its full name for
    // the reject check ran on every call, and stat pulls make dozens of calls
    // per update. UFunctions never change after load, so compute it once.
    struct UFunctionCallPlan {
        bool rejected{false};
        int32_t param_size{0};
        RC::Unreal::FNumericProperty* output_numeric{nullptr};
        RC::Unreal::FBoolProperty* output_bool{nullptr};
        std::vector<RC::Unreal::FObjectPropertyBase*> world_context_props{};
        std::vector<RC::Unreal::FNumericProperty*> value_if_null_props{};
    };

    // Hook callbacks on the game thread can invoke functions too, so the cache
    // is shared under a mutex; plans are immutable once stored.
    std::mutex call_plan_mutex_{};
    std::unordered_map<RC::Unreal::UFunction*, std::shared_ptr<const UFunctionCallPlan>> call_plans_{};

    std::shared_ptr<const UFunctionCallPlan> call_plan_for(RC::Unreal::UFunction* fn) {
        {
            std::lock_guard<std::mutex> guard(call_plan_mutex_);
            const auto it = call_plans_.find(fn);
            if (it != call_plans_.end()) {
                return it->second;
            }
        }

        auto plan = std::make_shared<UFunctionCallPlan>();
        plan->rejected = is_rejected_runtime_function_name(fn->GetFullName());

        int32_t param_size = static_cast<int32_t>(fn->GetParmsSize());
        if (param_size <= 0 || param_size > 0x10000) {
            param_size = fn->GetPropertiesSize();
        }
        if (param_size <= 0 || param_size > 0x10000) {
            param_size = 0x200;
        }
        plan->param_size = param_size;

        int numeric_priority = -1;
        int bool_priority = -1;
        for (RC::Unreal::FProperty* property : enumerate_properties(fn)) {
            if (!property || !is_likely_valid_object_ptr(property)) {
                continue;
            }
            if (!property_has_any_flags(property, RC::Unreal::CPF_Parm)) {
                continue;
            }
            const auto normalized_name = normalize_ascii(property->GetName());
            const bool is_out = property_has_any_flags(property, RC::Unreal::CPF_OutParm)
                || property_has_any_flags(property, RC::Unreal::CPF_ReturnParm);
            const bool has_output_name = (normalized_name == "outvalue" || normalized_name == "returnvalue");

            if (is_out || has_output_name) {
                int priority = 1;
                if (normalized_name == "outvalue") {
                    priority = 5;
                } else if (normalized_name == "returnvalue") {
                    priority = 4;
                } else if (property_has_any_flags(property, RC::Unreal::CPF_ReturnParm)) {
                    priority = 3;
                } else if (property_has_any_flags(property, RC::Unreal::CPF_OutParm)) {
                    priority = 2;
                }
                auto* numeric = RC::Unreal::CastField<RC::Unreal::FNumericProperty>(property);
                if (numeric && is_likely_valid_object_ptr(numeric) && priority > numeric_priority) {
                    plan->output_numeric = numeric;
                    numeric_priority = priority;
                }
                auto* boolean = RC::Unreal::CastField<RC::Unreal::FBoolProperty>(property);
                if (boolean && is_likely_valid_object_ptr(boolean) && priority > bool_priority) {
                    plan->output_bool = boolean;
                    bool_priority = priority;
                }
                continue;
            }

            if (auto* object_property = RC::Unreal::CastField<RC::Unreal::FObjectPropertyBase>(property)) {
                if (normalized_name.find("worldcontextobject") != std::string::npos) {
                    plan->world_context_props.push_back(object_property);
                }
                continue;
            }

            if (auto* numeric_property = RC::Unreal::CastField<RC::Unreal::FNumericProperty>(property)) {
                if (normalized_name.find("valueifnull") != std::string::npos) {
                    plan->value_if_null_props.push_back(numeric_property);
                }
            }
        }

        std::shared_ptr<const UFunctionCallPlan> stored = plan;
        std::lock_guard<std::mutex> guard(call_plan_mutex_);
        call_plans_.emplace(fn, stored);
        return stored;
    }

    bool is_rejected_function_cached(RC::Unreal::UFunction* fn) {
        return call_plan_for(fn)->rejected;
    }

    // Fills the world-context inputs, as the old per-call property walk did.
    void apply_world_context_params(
        const UFunctionCallPlan& plan,
        std::vector<uint8_t>& params,
        RC::Unreal::UObject* receiver,
        RC::Unreal::UObject* caller
    ) {
        for (auto* object_property : plan.world_context_props) {
            void* value_ptr = safe_property_value_ptr(object_property, params.data());
            if (!value_ptr || !is_likely_readable_region(value_ptr, sizeof(void*))) {
                continue;
            }
            RC::Unreal::UObject* context_object = nullptr;
            if (meta_game_instance_ && is_likely_valid_object_ptr(meta_game_instance_)) {
                context_object = meta_game_instance_;
            } else if (receiver && is_likely_valid_object_ptr(receiver)) {
                context_object = receiver;
            } else {
                context_object = caller;
            }
            object_property->SetObjectPropertyValue(value_ptr, context_object);
        }
    }

    NumericInvokeResult invoke_numeric_ufunction(RC::Unreal::UObject* receiver, RC::Unreal::UFunction* fn) {
        NumericInvokeResult result{};
        auto* caller = resolve_receive_caller(receiver, fn);
        if (!caller || !fn || !is_likely_valid_object_ptr(fn)) {
            return result;
        }
        const auto plan = call_plan_for(fn);
        auto* output_numeric = plan->output_numeric;
        if (!output_numeric) {
            return result;
        }

        std::vector<uint8_t> params(static_cast<size_t>(plan->param_size), 0);
        apply_world_context_params(*plan, params, receiver, caller);
        for (auto* numeric_property : plan->value_if_null_props) {
            void* value_ptr = safe_property_value_ptr(numeric_property, params.data());
            if (!value_ptr || !is_likely_readable_region(value_ptr, sizeof(double))) {
                continue;
            }
            if (numeric_property->IsFloatingPoint()) {
                numeric_property->SetFloatingPointPropertyValue(value_ptr, -1.0);
            } else {
                numeric_property->SetIntPropertyValue(value_ptr, static_cast<int64_t>(-1));
            }
        }

        if (!safe_process_event_call(caller, fn, params.data())) {
            return result;
        }
        void* output_ptr = safe_property_value_ptr(output_numeric, params.data());
        if (!output_ptr || !is_likely_readable_region(output_ptr, sizeof(double))) {
            return result;
        }

        if (output_numeric->IsFloatingPoint()) {
            const double value = output_numeric->GetFloatingPointPropertyValue(output_ptr);
            if (!std::isfinite(value)) {
                return result;
            }
            result.valid = true;
            result.is_floating = true;
            result.as_float = value;
            return result;
        }
        if (output_numeric->IsInteger()) {
            result.valid = true;
            result.is_floating = false;
            result.as_int = output_numeric->GetSignedIntPropertyValue(output_ptr);
            return result;
        }
        return result;
    }

    BoolInvokeResult invoke_bool_ufunction(RC::Unreal::UObject* receiver, RC::Unreal::UFunction* fn) {
        BoolInvokeResult result{};
        auto* caller = resolve_receive_caller(receiver, fn);
        if (!caller || !fn || !is_likely_valid_object_ptr(fn)) {
            return result;
        }
        const auto plan = call_plan_for(fn);
        auto* output_bool = plan->output_bool;
        if (!output_bool) {
            return result;
        }

        std::vector<uint8_t> params(static_cast<size_t>(plan->param_size), 0);
        apply_world_context_params(*plan, params, receiver, caller);

        if (!safe_process_event_call(caller, fn, params.data())) {
            return result;
        }
        void* output_ptr = safe_property_value_ptr(output_bool, params.data());
        if (!output_ptr || !is_likely_readable_region(output_ptr, sizeof(uint8_t))) {
            return result;
        }

        result.valid = true;
        result.value = output_bool->GetPropertyValue(output_ptr);
        return result;
    }

    bool try_read_int(RC::Unreal::UObject* receiver, std::initializer_list<RC::Unreal::UFunction*> fns, int32_t& out) {
        for (auto* fn : fns) {
            if (!fn || !is_likely_valid_object_ptr(fn)) {
                continue;
            }
            if (is_rejected_function_cached(fn)) {
                continue;
            }
            const auto result = invoke_numeric_ufunction(receiver, fn);
            if (!result.valid) {
                continue;
            }
            if (result.is_floating) {
                if (!std::isfinite(result.as_float)) {
                    continue;
                }
                out = static_cast<int32_t>(std::llround(result.as_float));
            } else {
                out = static_cast<int32_t>(result.as_int);
            }
            return true;
        }
        return false;
    }

    bool try_read_float(RC::Unreal::UObject* receiver, std::initializer_list<RC::Unreal::UFunction*> fns, float& out) {
        for (auto* fn : fns) {
            if (!fn || !is_likely_valid_object_ptr(fn)) {
                continue;
            }
            if (is_rejected_function_cached(fn)) {
                continue;
            }
            const auto result = invoke_numeric_ufunction(receiver, fn);
            if (!result.valid) {
                continue;
            }
            if (result.is_floating) {
                out = static_cast<float>(result.as_float);
            } else {
                out = static_cast<float>(result.as_int);
            }
            if (!std::isfinite(out)) {
                continue;
            }
            return true;
        }
        return false;
    }

    bool try_read_bool(RC::Unreal::UObject* receiver, std::initializer_list<RC::Unreal::UFunction*> fns, bool& out) {
        for (auto* fn : fns) {
            if (!fn || !is_likely_valid_object_ptr(fn)) {
                continue;
            }
            if (is_rejected_function_cached(fn)) {
                continue;
            }
            const auto result = invoke_bool_ufunction(receiver, fn);
            if (!result.valid) {
                continue;
            }
            out = result.value;
            return true;
        }
        return false;
    }

    bool probe_state_receiver_activity_impl(RC::Unreal::UObject* receiver, ReceiverActivityProbe& out) {
        out = {};
        if (!receiver || !is_likely_valid_object_ptr(receiver)) {
            return false;
        }

        float value_f = -1.0f;
        int32_t value_i = -1;
        bool any = false;

        if (try_read_float(receiver, {
                targets_.get_seconds_value_else,
                targets_.get_seconds_value_or,
                targets_.receive_seconds
            }, value_f)) {
            out.seconds = value_f;
            any = true;
        }
        if (try_read_int(receiver, {
                targets_.get_challenge_tick_count_value_else,
                targets_.get_challenge_tick_count_value_or,
                targets_.receive_challenge_tick_count_value_else,
                targets_.receive_challenge_tick_count_value_or,
                targets_.receive_challenge_tick_count_single,
                targets_.receive_challenge_tick_count
            }, value_i)) {
            out.challenge_tick_count = value_i;
            any = true;
        }
        if (try_read_float(receiver, {
                targets_.get_score_value_else,
                targets_.get_score_value_or,
                targets_.receive_score_value_else,
                targets_.receive_score_single,
                targets_.receive_score
            }, value_f)) {
            out.score_total = value_f;
            any = true;
        }

        out.valid = any;
        return any;
    }

    bool safe_probe_state_receiver_activity(RC::Unreal::UObject* receiver, ReceiverActivityProbe& out) {
#if defined(_MSC_VER)
        __try {
            return probe_state_receiver_activity_impl(receiver, out);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            out = {};
            return false;
        }
#else
        return probe_state_receiver_activity_impl(receiver, out);
#endif
    }

    RC::Unreal::UClass* resolve_class_cached(
        RC::Unreal::UClass*& cache,
        std::initializer_list<const wchar_t*> candidate_paths
    ) {
        if (cache && is_likely_valid_object_ptr(cache)) {
            return cache;
        }
        for (const auto* path : candidate_paths) {
            if (!path) {
                continue;
            }
            auto* cls = RC::Unreal::UObjectGlobals::StaticFindObject<RC::Unreal::UClass*>(nullptr, nullptr, path);
            if (cls && is_likely_valid_object_ptr(cls)) {
                cache = cls;
                return cache;
            }
        }
        return nullptr;
    }

    RC::Unreal::UObject* resolve_meta_game_instance(uint64_t now) {
        if (meta_game_instance_ && is_likely_valid_object_ptr(meta_game_instance_) && now < next_meta_resolve_ms_) {
            return meta_game_instance_;
        }
        next_meta_resolve_ms_ = now + 2000;

        std::vector<RC::Unreal::UObject*> found{};
        RC::Unreal::UObjectGlobals::FindAllOf(STR("TheMetaGameInstance_C"), found);
        std::vector<RC::Unreal::UObject*> alt{};
        RC::Unreal::UObjectGlobals::FindAllOf(STR("GTheMetaGameInstance"), alt);
        append_unique_objects(found, alt);

        auto* cls = resolve_class_cached(
            meta_game_instance_class_,
            {STR("/Script/GameSkillsTrainer.GTheMetaGameInstance"),
             STR("/Script/GameSkillsTrainer.TheMetaGameInstance_C"),
             STR("/Script/GameSkillsTrainer.GTheMetaGameInstance_C")}
        );
        if (cls && is_likely_valid_object_ptr(cls)) {
            std::vector<RC::Unreal::UObject*> by_class{};
            collect_objects_by_class(cls, by_class);
            append_unique_objects(found, by_class);
        }

        RC::Unreal::UObject* best = nullptr;
        int best_score = -1000000;
        for (auto* obj : found) {
            if (!obj || !is_likely_valid_object_ptr(obj)) {
                continue;
            }
            const auto full_name = obj->GetFullName();
            if (is_rejected_runtime_object_name(full_name)) {
                continue;
            }
            int score = 0;
            if (full_name.find(STR("/Engine/Transient.GameEngine_")) != RC::StringType::npos) score += 200;
            if (full_name.find(STR(":TheMetaGameInstance_C_")) != RC::StringType::npos) score += 300;
            if (score > best_score) {
                best = obj;
                best_score = score;
            }
        }
        if (best && is_likely_valid_object_ptr(best)) {
            meta_game_instance_ = best;
        }
        return meta_game_instance_;
    }

    RC::Unreal::UObject* resolve_state_receiver_instance(uint64_t now) {
        if (state_receiver_instance_ && is_likely_valid_object_ptr(state_receiver_instance_) && now < next_receiver_resolve_ms_) {
            return state_receiver_instance_;
        }

        const bool prefer_live_activity =
            last_is_in_challenge_ == 1
            || last_is_in_scenario_ == 1
            || lifecycle_active_
            || (std::isfinite(last_queue_time_remaining_) && last_queue_time_remaining_ > 0.0001f)
            || (std::isfinite(last_time_remaining_) && last_time_remaining_ > 0.25f);
        const bool challenge_expected_live =
            last_is_in_challenge_ == 1
            || last_is_in_scenario_ == 1
            || lifecycle_active_;
        const bool stream_selection_stale =
            challenge_expected_live
            && last_runtime_progress_ms_ > 0
            && safe_elapsed_ms(now, last_runtime_progress_ms_) > 400;
        const bool startup_hint_window =
            last_true_start_transition_ms_ > 0
            && safe_elapsed_ms(now, last_true_start_transition_ms_) < 900;

        if (live_metric_receiver_hint_
            && (!is_likely_valid_object_ptr(live_metric_receiver_hint_)
                || safe_elapsed_ms(now, live_metric_receiver_hint_ms_) > 3000)) {
            live_metric_receiver_hint_ = nullptr;
            live_metric_receiver_hint_ms_ = 0;
        }
        const bool has_recent_live_metric_receiver_hint =
            live_metric_receiver_hint_
            && is_likely_valid_object_ptr(live_metric_receiver_hint_)
            && safe_elapsed_ms(now, live_metric_receiver_hint_ms_) <= 1500;
        if (has_recent_live_metric_receiver_hint) {
            bool use_hint_directly = true;
            if (stream_selection_stale && !startup_hint_window) {
                ReceiverActivityProbe hint_probe{};
                if (safe_probe_state_receiver_activity(live_metric_receiver_hint_, hint_probe) && hint_probe.valid) {
                    const bool hint_ahead_seconds =
                        std::isfinite(hint_probe.seconds)
                        && std::isfinite(last_seconds_)
                        && hint_probe.seconds > (last_seconds_ + 0.020f);
                    const bool hint_ahead_ticks =
                        hint_probe.challenge_tick_count >= 0
                        && last_challenge_tick_count_ >= 0
                        && hint_probe.challenge_tick_count > last_challenge_tick_count_;
                    const bool hint_ahead_score =
                        std::isfinite(hint_probe.score_total)
                        && std::isfinite(last_score_total_)
                        && hint_probe.score_total > (last_score_total_ + 0.020f);
                    if (!(hint_ahead_seconds || hint_ahead_ticks || hint_ahead_score)) {
                        use_hint_directly = false;
                    }
                } else {
                    use_hint_directly = false;
                }
            }
            if (use_hint_directly) {
                state_receiver_instance_ = live_metric_receiver_hint_;
                next_receiver_resolve_ms_ = now + 100;
                return state_receiver_instance_;
            }
        }

        const bool fast_start_window =
            last_true_start_transition_ms_ > 0
            && safe_elapsed_ms(now, last_true_start_transition_ms_) < 1500;
        const bool live_progress_stale =
            prefer_live_activity
            && (last_runtime_progress_ms_ == 0
                || safe_elapsed_ms(now, last_runtime_progress_ms_) > 250);
        // FindAllOf walks every UObject, so rescan quickly only right after a
        // start transition. A flowing receiver rarely changes; a stalled one
        // gets a faster retry, and live hooks hand us the receiver directly.
        const uint64_t receiver_resolve_interval_ms =
            prefer_live_activity
                ? (fast_start_window ? 35ULL : (live_progress_stale ? 250ULL : 500ULL))
                : 1000ULL;
        next_receiver_resolve_ms_ = now + receiver_resolve_interval_ms;

        auto* meta = resolve_meta_game_instance(now);
        RC::StringType meta_path{};
        if (meta && is_likely_valid_object_ptr(meta)) {
            meta_path = object_path_from_full_name(meta->GetFullName());
        }

        std::vector<RC::Unreal::UObject*> found{};
        RC::Unreal::UObjectGlobals::FindAllOf(STR("PerformanceIndicatorsStateReceiver"), found);
        std::vector<RC::Unreal::UObject*> alt{};
        RC::Unreal::UObjectGlobals::FindAllOf(STR("PerformanceIndicatorsStateReceiver_C"), alt);
        append_unique_objects(found, alt);

        auto* cls = resolve_class_cached(
            state_receiver_class_,
            {STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver"),
             STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver_C"),
             STR("/Game/FirstPersonBP/Blueprints/PerformanceIndicatorsStateReceiver.PerformanceIndicatorsStateReceiver_C")}
        );
        if (cls && is_likely_valid_object_ptr(cls)) {
            std::vector<RC::Unreal::UObject*> by_class{};
            collect_objects_by_class(cls, by_class);
            append_unique_objects(found, by_class);
        }

        RC::Unreal::UObject* best = nullptr;
        int best_score = -1000000;
        for (auto* obj : found) {
            if (!obj || !is_likely_valid_object_ptr(obj)) {
                continue;
            }
            const auto full_name = obj->GetFullName();
            if (is_rejected_runtime_object_name(full_name)) {
                continue;
            }
            const auto object_path = object_path_from_full_name(full_name);
            int score = 0;
            if (full_name.find(STR("/Engine/Transient.GameEngine_")) != RC::StringType::npos) score += 200;
            if (full_name.find(STR("/Engine/Transient.")) != RC::StringType::npos) score += 60;
            if (full_name.find(STR("TheMetaGameInstance")) != RC::StringType::npos) score += 200;
            if (full_name.find(STR("PerformanceIndicatorsStateReceiver_")) != RC::StringType::npos) score += 120;
            if (!meta_path.empty()) {
                RC::StringType prefix = meta_path;
                prefix += STR(".");
                if (object_path.rfind(prefix, 0) == 0) {
                    score += 100;
                }
            }
            ReceiverActivityProbe probe{};
            if (has_recent_live_metric_receiver_hint && obj == live_metric_receiver_hint_) {
                score += 500000;
            }
            if (prefer_live_activity) {
                if (safe_probe_state_receiver_activity(obj, probe) && probe.valid) {
                    score += 20000;
                    if (std::isfinite(probe.seconds) && probe.seconds >= 0.0f) {
                        score += std::min<int>(12000, static_cast<int>(std::llround(probe.seconds * 120.0)));
                    }
                    if (probe.challenge_tick_count >= 0) {
                        score += std::min<int>(16000, probe.challenge_tick_count * 8);
                    }
                    if (std::isfinite(probe.score_total) && probe.score_total > 0.0f) {
                        score += std::min<int>(6000, static_cast<int>(std::llround(probe.score_total * 40.0)));
                    }

                    const bool has_live_progress =
                        (std::isfinite(probe.seconds) && probe.seconds > 0.050f)
                        || (probe.challenge_tick_count > 0)
                        || (std::isfinite(probe.score_total) && probe.score_total > 0.010f);
                    if (challenge_expected_live) {
                        if (has_live_progress) {
                            score += 6000;
                        } else {
                            score -= 12000;
                        }

                        if (stream_selection_stale) {
                            const bool seconds_ahead =
                                std::isfinite(probe.seconds)
                                && std::isfinite(last_seconds_)
                                && probe.seconds > (last_seconds_ + 0.020f);
                            const bool ticks_ahead =
                                probe.challenge_tick_count >= 0
                                && last_challenge_tick_count_ >= 0
                                && probe.challenge_tick_count > last_challenge_tick_count_;
                            const bool score_ahead =
                                std::isfinite(probe.score_total)
                                && std::isfinite(last_score_total_)
                                && probe.score_total > (last_score_total_ + 0.020f);
                            const bool matches_last_seconds =
                                std::isfinite(probe.seconds)
                                && std::isfinite(last_seconds_)
                                && std::fabs(static_cast<double>(probe.seconds) - static_cast<double>(last_seconds_)) <= 0.0001;
                            const bool matches_last_ticks =
                                probe.challenge_tick_count >= 0
                                && last_challenge_tick_count_ >= 0
                                && probe.challenge_tick_count == last_challenge_tick_count_;
                            const bool matches_last_score =
                                std::isfinite(probe.score_total)
                                && std::isfinite(last_score_total_)
                                && std::fabs(static_cast<double>(probe.score_total) - static_cast<double>(last_score_total_)) <= 0.0001;
                            const bool matches_last_snapshot =
                                matches_last_seconds
                                && matches_last_ticks
                                && matches_last_score;

                            if (seconds_ahead || ticks_ahead || score_ahead) {
                                score += 14000;
                            } else if (matches_last_snapshot) {
                                score -= 18000;
                                if (state_receiver_instance_ && obj == state_receiver_instance_) {
                                    score -= 8000;
                                }
                            }
                        }
                    }
                } else {
                    score -= 2000;
                }
            }
            if (score > best_score) {
                best = obj;
                best_score = score;
            }
        }
        if (best && is_likely_valid_object_ptr(best)) {
            state_receiver_instance_ = best;
        }
        return state_receiver_instance_;
    }

    RC::Unreal::UObject* resolve_scenario_state_receiver_instance(uint64_t now) {
        if (scenario_state_receiver_instance_ && is_likely_valid_object_ptr(scenario_state_receiver_instance_) && now < next_scenario_state_resolve_ms_) {
            return scenario_state_receiver_instance_;
        }
        next_scenario_state_resolve_ms_ = now + 2000;

        auto* meta = resolve_meta_game_instance(now);
        RC::StringType meta_path{};
        if (meta && is_likely_valid_object_ptr(meta)) {
            meta_path = object_path_from_full_name(meta->GetFullName());
        }

        std::vector<RC::Unreal::UObject*> all{};
        RC::Unreal::UObjectGlobals::FindAllOf(STR("ScenarioStateReceiver"), all);
        std::vector<RC::Unreal::UObject*> alt{};
        RC::Unreal::UObjectGlobals::FindAllOf(STR("ScenarioStateReceiver_C"), alt);
        append_unique_objects(all, alt);

        auto* cls = resolve_class_cached(
            scenario_state_receiver_class_,
            {STR("/Script/KovaaKFramework.ScenarioStateReceiver"),
             STR("/Script/KovaaKFramework.ScenarioStateReceiver_C"),
             STR("/Game/FirstPersonBP/Blueprints/ScenarioStateReceiver.ScenarioStateReceiver_C")}
        );
        if (cls && is_likely_valid_object_ptr(cls)) {
            std::vector<RC::Unreal::UObject*> by_class{};
            collect_objects_by_class(cls, by_class);
            append_unique_objects(all, by_class);
        }

        RC::Unreal::UObject* best = nullptr;
        RC::Unreal::UObject* best_meta_scoped = nullptr;
        int best_score = -1000000;
        int best_meta_scoped_score = -1000000;
        for (auto* obj : all) {
            if (!obj || !is_likely_valid_object_ptr(obj)) {
                continue;
            }
            const auto full_name = obj->GetFullName();
            if (is_rejected_runtime_object_name(full_name)) {
                continue;
            }
            const auto object_path = object_path_from_full_name(full_name);
            int score = 0;
            if (full_name.find(STR("Default__")) != RC::StringType::npos) score -= 1000;
            if (full_name.find(STR("/Script/")) != RC::StringType::npos) score -= 100;
            if (full_name.find(STR("/Engine/Transient.")) != RC::StringType::npos) score += 40;
            if (full_name.find(STR("TheMetaGameInstance")) != RC::StringType::npos) score += 200;
            if (full_name.find(STR("ScenarioStateReceiver_")) != RC::StringType::npos) score += 120;
            if (score > best_score) {
                best = obj;
                best_score = score;
            }
            if (!meta_path.empty()) {
                RC::StringType prefix = meta_path;
                prefix += STR(".");
                if (object_path.rfind(prefix, 0) == 0 && score > best_meta_scoped_score) {
                    best_meta_scoped = obj;
                    best_meta_scoped_score = score;
                }
            }
        }

        auto* chosen = best_meta_scoped ? best_meta_scoped : best;
        if (!chosen || !is_likely_valid_object_ptr(chosen)) {
            chosen = RC::Unreal::UObjectGlobals::StaticFindObject<RC::Unreal::UObject*>(
                nullptr,
                nullptr,
                STR("/Script/KovaaKFramework.Default__ScenarioStateReceiver")
            );
        }
        if (chosen && is_likely_valid_object_ptr(chosen)) {
            scenario_state_receiver_instance_ = chosen;
        }
        return scenario_state_receiver_instance_;
    }

    RC::Unreal::UObject* resolve_scenario_manager_instance(uint64_t now) {
        if (scenario_manager_instance_ && is_likely_valid_object_ptr(scenario_manager_instance_) && now < next_scenario_resolve_ms_) {
            return scenario_manager_instance_;
        }
        next_scenario_resolve_ms_ = now + 1000;

        auto* meta = resolve_meta_game_instance(now);
        RC::StringType meta_path{};
        if (meta && is_likely_valid_object_ptr(meta)) {
            meta_path = object_path_from_full_name(meta->GetFullName());
        }

        std::vector<RC::Unreal::UObject*> all{};
        RC::Unreal::UObjectGlobals::FindAllOf(STR("ScenarioManager"), all);
        std::vector<RC::Unreal::UObject*> alt{};
        RC::Unreal::UObjectGlobals::FindAllOf(STR("ScenarioManager_C"), alt);
        append_unique_objects(all, alt);

        auto* cls = resolve_class_cached(
            scenario_manager_class_,
            {STR("/Script/GameSkillsTrainer.ScenarioManager"),
             STR("/Script/GameSkillsTrainer.ScenarioManager_C")}
        );
        if (cls && is_likely_valid_object_ptr(cls)) {
            std::vector<RC::Unreal::UObject*> by_class{};
            collect_objects_by_class(cls, by_class);
            append_unique_objects(all, by_class);
        }

        RC::Unreal::UObject* best = nullptr;
        RC::Unreal::UObject* best_meta_scoped = nullptr;
        int best_score = -1000000;
        int best_meta_scoped_score = -1000000;
        for (auto* obj : all) {
            if (!obj || !is_likely_valid_object_ptr(obj)) {
                continue;
            }
            const auto full_name = obj->GetFullName();
            if (is_rejected_runtime_object_name(full_name)) {
                continue;
            }
            const auto object_path = object_path_from_full_name(full_name);
            int score = 0;
            if (full_name.find(STR("/Engine/Transient.GameEngine_")) != RC::StringType::npos) score += 180;
            if (full_name.find(STR("/Engine/Transient.")) != RC::StringType::npos) score += 40;
            if (full_name.find(STR("TheMetaGameInstance")) != RC::StringType::npos) score += 220;
            if (full_name.find(STR("ScenarioManager_")) != RC::StringType::npos) score += 120;
            if (score > best_score) {
                best = obj;
                best_score = score;
            }
            if (!meta_path.empty()) {
                RC::StringType prefix = meta_path;
                prefix += STR(".");
                if (object_path.rfind(prefix, 0) == 0 && score > best_meta_scoped_score) {
                    best_meta_scoped = obj;
                    best_meta_scoped_score = score;
                }
            }
        }
        auto* chosen = best_meta_scoped ? best_meta_scoped : best;
        if (chosen && is_likely_valid_object_ptr(chosen)) {
            scenario_manager_instance_ = chosen;
        }
        return scenario_manager_instance_;
    }

    RC::Unreal::FTextProperty* resolve_textblock_text_property(RC::Unreal::UObject* text_block) {
        if (!text_block || !is_likely_valid_object_ptr(text_block)) {
            return nullptr;
        }
        auto* text_block_class = *reinterpret_cast<RC::Unreal::UClass**>(
            reinterpret_cast<uint8_t*>(text_block) + 0x10
        );
        if (!text_block_class || !is_likely_valid_object_ptr(text_block_class)) {
            return nullptr;
        }
        const auto cached_it = text_property_cache_.find(text_block_class);
        if (cached_it != text_property_cache_.end()) {
            auto* cached = cached_it->second;
            if (cached && is_likely_valid_object_ptr(cached)) {
                return cached;
            }
        }

        RC::Unreal::FTextProperty* found = nullptr;
        for (RC::Unreal::FProperty* property : enumerate_properties_in_chain(text_block_class)) {
            if (!property || !is_likely_valid_object_ptr(property)) {
                continue;
            }
            if (property->GetName() != STR("Text")) {
                continue;
            }
            auto* text_property = RC::Unreal::CastField<RC::Unreal::FTextProperty>(property);
            if (!text_property || !is_likely_valid_object_ptr(text_property)) {
                continue;
            }
            found = text_property;
            break;
        }
        text_property_cache_[text_block_class] = found;
        return found;
    }

    bool read_textblock_text_value(RC::Unreal::UObject* text_block, RC::StringType& out_value) {
        out_value.clear();
        if (!text_block || !is_likely_valid_object_ptr(text_block)) {
            return false;
        }
        if (!text_get_fn_ || !is_likely_valid_object_ptr(text_get_fn_)) {
            text_get_fn_ = RC::Unreal::UObjectGlobals::StaticFindObject<RC::Unreal::UFunction*>(
                nullptr,
                nullptr,
                STR("/Script/UMG.TextBlock:GetText")
            );
        }

        if (auto* text_property = resolve_textblock_text_property(text_block)) {
            void* value_ptr = safe_property_value_ptr(text_property, text_block);
            if (value_ptr && is_likely_readable_region(value_ptr, sizeof(RC::Unreal::FText))) {
                auto* text_value = reinterpret_cast<RC::Unreal::FText*>(value_ptr);
                out_value = text_value->ToString();
                if (!out_value.empty()) {
                    return true;
                }
            }
        }

        if (text_get_fn_ && is_likely_valid_object_ptr(text_get_fn_)) {
            struct TextBlockGetTextParams {
                RC::Unreal::FText ReturnValue;
            } params{};
            text_block->ProcessEvent(text_get_fn_, &params);
            out_value = params.ReturnValue.ToString();
            return !out_value.empty();
        }
        return false;
    }

    void apply_ui_text_field(const char* ui_field, const RC::StringType& text_value) {
        if (!ui_field || text_value.empty()) {
            return;
        }
        std::lock_guard<std::mutex> guard(ui_mutex_);
        float parsed_f32 = 0.0f;
        int32_t parsed_i32 = 0;

        if (std::strcmp(ui_field, "session_shots") == 0) {
            if (try_parse_int_text(text_value, parsed_i32)) {
                ui_live_snapshot_.shots_fired = parsed_i32;
                ui_live_updated_ = true;
            }
            return;
        }
        if (std::strcmp(ui_field, "session_hits") == 0) {
            if (try_parse_int_text(text_value, parsed_i32)) {
                ui_live_snapshot_.shots_hit = parsed_i32;
                ui_live_updated_ = true;
            }
            return;
        }
        if (std::strcmp(ui_field, "session_kills") == 0) {
            if (try_parse_int_text(text_value, parsed_i32)) {
                ui_live_snapshot_.kills = parsed_i32;
                ui_live_updated_ = true;
            }
            return;
        }
        if (std::strcmp(ui_field, "scenario_name") == 0) {
            const auto scenario = trim_ascii_token(text_value);
            if (looks_like_real_scenario_name(scenario)) {
                ui_live_snapshot_.scenario_name = scenario;
                ui_live_updated_ = true;
            }
            return;
        }
        if (std::strcmp(ui_field, "session_time") == 0) {
            if (try_parse_time_to_seconds(text_value, parsed_f32)) {
                ui_live_snapshot_.seconds = parsed_f32;
                ui_live_updated_ = true;
            }
            return;
        }
        if (std::strcmp(ui_field, "session_spm") == 0) {
            if (try_parse_float_text(text_value, parsed_f32)) {
                ui_live_snapshot_.score_per_minute = parsed_f32;
                ui_live_updated_ = true;
            }
            return;
        }
        if (std::strcmp(ui_field, "session_score") == 0) {
            if (try_parse_float_text(text_value, parsed_f32)) {
                ui_live_snapshot_.score_total = parsed_f32;
                ui_live_updated_ = true;
            }
            return;
        }
        if (std::strcmp(ui_field, "session_kps") == 0) {
            if (try_parse_float_text(text_value, parsed_f32)) {
                ui_live_snapshot_.kills_per_second = parsed_f32;
                ui_live_updated_ = true;
            }
            return;
        }
        if (std::strcmp(ui_field, "session_damage_done") == 0) {
            if (try_parse_float_text(text_value, parsed_f32)) {
                ui_live_snapshot_.damage_done = parsed_f32;
                ui_live_updated_ = true;
            }
            return;
        }
        if (std::strcmp(ui_field, "session_damage_possible") == 0) {
            if (try_parse_float_text(text_value, parsed_f32)) {
                ui_live_snapshot_.damage_possible = parsed_f32;
                ui_live_updated_ = true;
            }
            return;
        }
        if (std::strcmp(ui_field, "session_damage_eff") == 0) {
            if (try_parse_float_text(text_value, parsed_f32)) {
                ui_live_snapshot_.damage_efficiency = parsed_f32;
                ui_live_updated_ = true;
            }
            return;
        }
        if (std::strcmp(ui_field, "session_avg_ttk") == 0) {
            if (try_parse_float_text(text_value, parsed_f32)) {
                ui_live_snapshot_.avg_ttk = parsed_f32;
                ui_live_updated_ = true;
            }
            return;
        }
    }

    void register_text_set_hook() {
        if (text_set_hook_registered_) {
            return;
        }
        text_set_fn_ = RC::Unreal::UObjectGlobals::StaticFindObject<RC::Unreal::UFunction*>(
            nullptr,
            nullptr,
            STR("/Script/UMG.TextBlock:SetText")
        );
        if (!text_set_fn_ || !is_likely_valid_object_ptr(text_set_fn_)) {
            kovaaks::RustBridge::emit_json("{\"ev\":\"ui_textupdate_hook\",\"enabled\":0}");
            return;
        }
        text_get_fn_ = RC::Unreal::UObjectGlobals::StaticFindObject<RC::Unreal::UFunction*>(
            nullptr,
            nullptr,
            STR("/Script/UMG.TextBlock:GetText")
        );

        text_set_hook_id_ = text_set_fn_->RegisterPostHook(
            [](RC::Unreal::UnrealScriptFunctionCallableContext& call_ctx, void* custom_data) {
                auto* self = reinterpret_cast<KovaaksBridgeModProduction*>(custom_data);
                if (!self || !self->rust_ready_ || self->updates_disabled_) {
                    return;
                }
                self->handle_text_set_hook(call_ctx);
            },
            this
        );
        text_set_hook_registered_ = true;
        kovaaks::RustBridge::emit_json("{\"ev\":\"ui_textupdate_hook\",\"enabled\":1}");
    }

    void handle_text_set_hook(RC::Unreal::UnrealScriptFunctionCallableContext& call_ctx) {
#if defined(_MSC_VER)
        __try {
            handle_text_set_hook_impl(call_ctx);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            return;
        }
#else
        handle_text_set_hook_impl(call_ctx);
#endif
    }

    void handle_text_set_hook_impl(RC::Unreal::UnrealScriptFunctionCallableContext& call_ctx) {
        if (!call_ctx.Context || !is_likely_valid_object_ptr(call_ctx.Context)) {
            return;
        }

        const auto ctx_name = call_ctx.Context->GetFullName();
        const auto* ui_field = classify_session_ui_field(ctx_name);
        if (!ui_field) {
            return;
        }

        RC::StringType text_value{};
        if (!read_textblock_text_value(call_ctx.Context, text_value) || text_value.empty()) {
            return;
        }
        apply_ui_text_field(ui_field, text_value);
    }

    bool copy_live_ui_snapshot(UiSnapshot& out) {
        std::lock_guard<std::mutex> guard(ui_mutex_);
        if (!ui_live_updated_) {
            return false;
        }
        out = ui_live_snapshot_;
        return true;
    }

    static auto normalize_angle_degrees(double value) -> double {
        while (value > 180.0) {
            value -= 360.0;
        }
        while (value < -180.0) {
            value += 360.0;
        }
        return value;
    }

    static auto append_shot_target_snapshot_json(
        std::string& out,
        const kmod_replay::ReplayEntity& target_entity,
        const kmod_replay::ReplayEntity* player_entity,
        bool is_nearest
    ) -> void {
        kmod_replay::replay_append_entity_json(out, target_entity);
        if (!out.empty() && out.back() == '}') {
            // replay_append_entity_json emits a full object; keep it open so
            // target-specific distance/error fields stay inside the same JSON object.
            out.pop_back();
        }

        if (player_entity) {
            constexpr double kPi = 3.14159265358979323846;
            const double dx = static_cast<double>(target_entity.location.x)
                - static_cast<double>(player_entity->location.x);
            const double dy = static_cast<double>(target_entity.location.y)
                - static_cast<double>(player_entity->location.y);
            const double dz = static_cast<double>(target_entity.location.z)
                - static_cast<double>(player_entity->location.z);
            const double distance_2d = std::sqrt((dx * dx) + (dy * dy));
            const double distance_3d = std::sqrt((distance_2d * distance_2d) + (dz * dz));
            const double target_yaw_deg = std::atan2(dy, dx) * (180.0 / kPi);
            const double target_pitch_deg = std::atan2(dz, std::max(distance_2d, 0.0001)) * (180.0 / kPi);
            const double yaw_error_deg = normalize_angle_degrees(
                target_yaw_deg - static_cast<double>(player_entity->rotation.yaw)
            );
            const double pitch_error_deg = normalize_angle_degrees(
                target_pitch_deg - static_cast<double>(player_entity->rotation.pitch)
            );

            out += ",\"distance_2d\":";
            kmod_replay::replay_append_f32(out, distance_2d);
            out += ",\"distance_3d\":";
            kmod_replay::replay_append_f32(out, distance_3d);
            out += ",\"yaw_error_deg\":";
            kmod_replay::replay_append_f32(out, yaw_error_deg);
            out += ",\"pitch_error_deg\":";
            kmod_replay::replay_append_f32(out, pitch_error_deg);
        }

        out += ",\"is_nearest\":";
        out += is_nearest ? "true" : "false";
        out += "}";
    }

    static auto is_placeholder_shot_target(const kmod_replay::ReplayEntity& entity) -> bool {
        constexpr float k_epsilon = 0.001f;
        const bool zero_location =
            std::fabs(entity.location.x) <= k_epsilon
            && std::fabs(entity.location.y) <= k_epsilon
            && std::fabs(entity.location.z) <= k_epsilon;
        const bool zero_velocity =
            std::fabs(entity.velocity.x) <= k_epsilon
            && std::fabs(entity.velocity.y) <= k_epsilon
            && std::fabs(entity.velocity.z) <= k_epsilon;
        const bool zero_rotation =
            std::fabs(entity.rotation.pitch) <= k_epsilon
            && std::fabs(entity.rotation.yaw) <= k_epsilon
            && std::fabs(entity.rotation.roll) <= k_epsilon;
        return zero_location && zero_velocity && zero_rotation;
    }

    void emit_shot_target_telemetry(const char* shot_event, int32_t delta, int32_t total, uint64_t now) {
        if (!shot_event || delta <= 0 || total < 0) {
            return;
        }

        const auto& replay_runtime = kmod_replay::replay_runtime_state();
        const kmod_replay::ReplayEntity* player_entity = nullptr;
        std::vector<const kmod_replay::ReplayEntity*> target_entities{};
        target_entities.reserve(replay_runtime.last_entities.size());

        for (const auto& kv : replay_runtime.last_entities) {
            const auto& entity = kv.second;
            if (entity.is_player) {
                player_entity = &entity;
                break;
            }
        }
        if (!player_entity) {
            for (const auto& kv : replay_runtime.last_entities) {
                const auto& entity = kv.second;
                if (!entity.is_bot) {
                    player_entity = &entity;
                    break;
                }
            }
        }

        for (const auto& kv : replay_runtime.last_entities) {
            const auto& entity = kv.second;
            if (entity.is_bot) {
                target_entities.emplace_back(&entity);
            }
        }

        const bool has_non_placeholder_target = std::any_of(
            target_entities.begin(),
            target_entities.end(),
            [](const kmod_replay::ReplayEntity* entity) {
                return entity && !is_placeholder_shot_target(*entity);
            }
        );
        if (has_non_placeholder_target) {
            target_entities.erase(
                std::remove_if(
                    target_entities.begin(),
                    target_entities.end(),
                    [](const kmod_replay::ReplayEntity* entity) {
                        return !entity || is_placeholder_shot_target(*entity);
                    }
                ),
                target_entities.end()
            );
        }

        size_t nearest_target_index = std::numeric_limits<size_t>::max();
        if (player_entity && !target_entities.empty()) {
            double nearest_distance_sq = std::numeric_limits<double>::max();
            for (size_t index = 0; index < target_entities.size(); ++index) {
                const auto* target = target_entities[index];
                const double dx = static_cast<double>(target->location.x)
                    - static_cast<double>(player_entity->location.x);
                const double dy = static_cast<double>(target->location.y)
                    - static_cast<double>(player_entity->location.y);
                const double dz = static_cast<double>(target->location.z)
                    - static_cast<double>(player_entity->location.z);
                const double distance_sq = (dx * dx) + (dy * dy) + (dz * dz);
                if (distance_sq < nearest_distance_sq) {
                    nearest_distance_sq = distance_sq;
                    nearest_target_index = index;
                }
            }
        }

        const char* telemetry_ev = std::strcmp(shot_event, "shot_hit") == 0
            ? "shot_hit_telemetry"
            : "shot_fired_telemetry";

        std::string msg;
        msg.reserve(192 + (target_entities.size() * 224));
        msg += "{\"ev\":\"";
        msg += telemetry_ev;
        msg += "\",\"ts_ms\":";
        kmod_replay::replay_append_u64(msg, now);
        msg += ",\"count\":";
        kmod_replay::replay_append_i32(msg, delta);
        msg += ",\"total\":";
        kmod_replay::replay_append_i32(msg, total);
        msg += ",\"run_id\":";
        kmod_replay::replay_append_u64(msg, replay_runtime.current_run_id);
        msg += ",\"sample_seq\":";
        kmod_replay::replay_append_u64(msg, replay_runtime.seq);
        msg += ",\"sample_count\":";
        kmod_replay::replay_append_u64(msg, replay_runtime.samples_emitted);
        msg += ",\"source\":\"pull_telemetry\",\"method\":\"";
        msg += kmod_replay::replay_escape_json(emit_method_ ? emit_method_ : "unknown");
        msg += "\",\"origin_flag\":\"";
        msg += kmod_replay::replay_escape_json(emit_origin_flag_ ? emit_origin_flag_ : "unknown");
        msg += "\"";

        if (player_entity) {
            msg += ",\"player\":";
            kmod_replay::replay_append_entity_json(msg, *player_entity);
        }

        msg += ",\"targets\":[";
        for (size_t target_index = 0; target_index < target_entities.size(); ++target_index) {
            if (target_index > 0) {
                msg += ",";
            }
            append_shot_target_snapshot_json(
                msg,
                *target_entities[target_index],
                player_entity,
                target_index == nearest_target_index
            );
        }
        msg += "]}";

        queue_shot_telemetry_event(std::move(msg), now);
    }

    void queue_shot_telemetry_event(std::string&& json, uint64_t now) {
        if (json.empty()) {
            return;
        }

        constexpr size_t k_shot_telemetry_max_buffered = 4096;

        if (pending_shot_telemetry_events_.empty()) {
            pending_shot_telemetry_started_ms_ = now;
        }
        pending_shot_telemetry_events_.emplace_back(std::move(json));

        if (pending_shot_telemetry_events_.size() > k_shot_telemetry_max_buffered) {
            const size_t trim = pending_shot_telemetry_events_.size() - k_shot_telemetry_max_buffered;
            pending_shot_telemetry_events_.erase(
                pending_shot_telemetry_events_.begin(),
                pending_shot_telemetry_events_.begin() + trim
            );
            if (pending_shot_telemetry_events_.empty()) {
                pending_shot_telemetry_started_ms_ = 0;
            }
        }
    }

    void flush_pending_shot_telemetry(uint64_t now, bool force) {
        constexpr size_t k_shot_telemetry_flush_count = 24;
        constexpr uint64_t k_shot_telemetry_flush_interval_ms = 120;

        if (pending_shot_telemetry_events_.empty() || !kovaaks::RustBridge::is_connected()) {
            return;
        }

        const bool aged_enough =
            pending_shot_telemetry_started_ms_ > 0
            && safe_elapsed_ms(now, pending_shot_telemetry_started_ms_) >= k_shot_telemetry_flush_interval_ms;
        if (!force && pending_shot_telemetry_events_.size() < k_shot_telemetry_flush_count && !aged_enough) {
            return;
        }

        do {
            const size_t emit_count = pending_shot_telemetry_events_.size() < k_shot_telemetry_flush_count
                ? pending_shot_telemetry_events_.size()
                : k_shot_telemetry_flush_count;
            if (emit_count == 0) {
                pending_shot_telemetry_started_ms_ = 0;
                return;
            }

            std::string msg;
            msg.reserve(48 + (emit_count * 640));
            msg += "{\"ev\":\"shot_telemetry_batch\",\"events\":[";
            for (size_t index = 0; index < emit_count; ++index) {
                if (index > 0) {
                    msg += ",";
                }
                msg += pending_shot_telemetry_events_[index];
            }
            msg += "]}";

            if (!kovaaks::RustBridge::emit_json(msg.c_str())) {
                return;
            }

            pending_shot_telemetry_events_.erase(
                pending_shot_telemetry_events_.begin(),
                pending_shot_telemetry_events_.begin() + emit_count
            );
            pending_shot_telemetry_started_ms_ =
                pending_shot_telemetry_events_.empty() ? 0 : now;
        } while (force && !pending_shot_telemetry_events_.empty());
    }

    void emit_state_i32(const char* ev, int32_t& last_value, int32_t value, uint64_t now) {
        if (value != 0 && value != 1) {
            return;
        }
        if (last_value == value) {
            return;
        }
        last_value = value;
        std::array<char, 256> json{};
        std::snprintf(
            json.data(),
            json.size(),
            "{\"ev\":\"%s\",\"value\":%d,\"method\":\"%s\",\"origin_flag\":\"%s\",\"source\":\"production_stripped\"}",
            ev,
            value,
            emit_method_ ? emit_method_ : "unknown",
            emit_origin_flag_ ? emit_origin_flag_ : "unknown"
        );
        kovaaks::RustBridge::emit_json(json.data());
        last_state_change_emit_ms_ = now;
    }

    void emit_pull_i32(const char* ev, int32_t& last_value, int32_t value, uint64_t& last_nonzero_ms, uint64_t now) {
        if (value < 0) {
            return;
        }
        const bool active_stream_expected =
            lifecycle_active_
            || last_is_in_challenge_ == 1
            || last_is_in_scenario_ == 1;
        if (value == 0 && last_nonzero_ms == 0 && !active_stream_expected) {
            return;
        }
        if (value == 0 && last_value > 0) {
            constexpr uint64_t k_recent_zero_suppress_ms = 2500;
            const bool recent_nonzero =
                last_nonzero_ms > 0
                && safe_elapsed_ms(now, last_nonzero_ms) < k_recent_zero_suppress_ms;
            const bool suppress_transient_zero =
                recent_nonzero
                && (
                    std::strcmp(ev, "pull_shots_fired_total") == 0
                    || std::strcmp(ev, "pull_shots_hit_total") == 0
                    || std::strcmp(ev, "pull_kills_total") == 0
                );
            if (suppress_transient_zero) {
                return;
            }
        }
        if (value > 0) {
            last_nonzero_ms = now;
        }
        if (last_value == value) {
            return;
        }
        const int32_t prev = last_value;
        last_value = value;
        std::array<char, 320> json{};
        std::snprintf(
            json.data(),
            json.size(),
            "{\"ev\":\"%s\",\"value\":%d,\"method\":\"%s\",\"origin_flag\":\"%s\",\"source\":\"production_stripped\"}",
            ev,
            value,
            emit_method_ ? emit_method_ : "unknown",
            emit_origin_flag_ ? emit_origin_flag_ : "unknown"
        );
        kovaaks::RustBridge::emit_json(json.data());
        last_state_change_emit_ms_ = now;

        if (prev >= 0 && value > prev) {
            const int32_t delta = value - prev;
            if (std::strcmp(ev, "pull_shots_fired_total") == 0) {
                emit_shot_target_telemetry("shot_fired", delta, value, now);
            } else if (std::strcmp(ev, "pull_shots_hit_total") == 0) {
                emit_shot_target_telemetry("shot_hit", delta, value, now);
            }
        }
    }

    bool cache_pull_i32(const char* ev, int32_t& last_value, int32_t value, uint64_t& last_nonzero_ms, uint64_t now) {
        if (value < 0) {
            return false;
        }
        const bool active_stream_expected =
            lifecycle_active_
            || last_is_in_challenge_ == 1
            || last_is_in_scenario_ == 1;
        if (value == 0 && last_nonzero_ms == 0 && !active_stream_expected) {
            return false;
        }
        if (value == 0 && last_value > 0) {
            constexpr uint64_t k_recent_zero_suppress_ms = 2500;
            const bool recent_nonzero =
                last_nonzero_ms > 0
                && safe_elapsed_ms(now, last_nonzero_ms) < k_recent_zero_suppress_ms;
            const bool suppress_transient_zero =
                recent_nonzero
                && (
                    std::strcmp(ev, "pull_shots_fired_total") == 0
                    || std::strcmp(ev, "pull_shots_hit_total") == 0
                    || std::strcmp(ev, "pull_kills_total") == 0
                );
            if (suppress_transient_zero) {
                return false;
            }
        }
        if (value > 0) {
            last_nonzero_ms = now;
        }
        if (last_value == value) {
            return false;
        }
        const int32_t prev = last_value;
        last_value = value;
        last_state_change_emit_ms_ = now;

        if (prev >= 0 && value > prev) {
            const int32_t delta = value - prev;
            if (std::strcmp(ev, "pull_shots_fired_total") == 0) {
                emit_shot_target_telemetry("shot_fired", delta, value, now);
            } else if (std::strcmp(ev, "pull_shots_hit_total") == 0) {
                emit_shot_target_telemetry("shot_hit", delta, value, now);
            }
        }
        return true;
    }

    void emit_pull_f32(const char* ev, float& last_value, float value, uint64_t& last_nonzero_ms, uint64_t now) {
        if (!std::isfinite(value) || value < 0.0f) {
            return;
        }
        const bool is_qrem = (std::strcmp(ev, "pull_queue_time_remaining") == 0);
        const bool active_stream_expected =
            lifecycle_active_
            || last_is_in_challenge_ == 1
            || last_is_in_scenario_ == 1;
        if (value == 0.0f && last_nonzero_ms == 0 && !is_qrem && !active_stream_expected) {
            return;
        }
        if (value == 0.0f && std::isfinite(last_value) && last_value > 0.0f) {
            constexpr uint64_t k_recent_zero_suppress_ms = 2500;
            const bool recent_nonzero =
                last_nonzero_ms > 0
                && safe_elapsed_ms(now, last_nonzero_ms) < k_recent_zero_suppress_ms;
            const bool suppress_transient_zero =
                recent_nonzero
                && (
                    std::strcmp(ev, "pull_accuracy") == 0
                    || std::strcmp(ev, "pull_seconds_total") == 0
                    || std::strcmp(ev, "pull_score_per_minute") == 0
                );
            if (suppress_transient_zero) {
                return;
            }
        }
        if (value > 0.0f) {
            last_nonzero_ms = now;
        }
        if (std::isfinite(last_value) && std::fabs(static_cast<double>(last_value) - static_cast<double>(value)) <= 0.0001) {
            return;
        }
        last_value = value;
        std::array<char, 352> json{};
        std::snprintf(
            json.data(),
            json.size(),
            "{\"ev\":\"%s\",\"value\":%.6f,\"method\":\"%s\",\"origin_flag\":\"%s\",\"source\":\"production_stripped\"}",
            ev,
            static_cast<double>(value),
            emit_method_ ? emit_method_ : "unknown",
            emit_origin_flag_ ? emit_origin_flag_ : "unknown"
        );
        kovaaks::RustBridge::emit_json(json.data());
        last_state_change_emit_ms_ = now;
    }

    bool cache_pull_f32(const char* ev, float& last_value, float value, uint64_t& last_nonzero_ms, uint64_t now) {
        if (!std::isfinite(value) || value < 0.0f) {
            return false;
        }
        const bool is_qrem = (std::strcmp(ev, "pull_queue_time_remaining") == 0);
        const bool active_stream_expected =
            lifecycle_active_
            || last_is_in_challenge_ == 1
            || last_is_in_scenario_ == 1;
        if (value == 0.0f && last_nonzero_ms == 0 && !is_qrem && !active_stream_expected) {
            return false;
        }
        if (value == 0.0f && std::isfinite(last_value) && last_value > 0.0f) {
            constexpr uint64_t k_recent_zero_suppress_ms = 2500;
            const bool recent_nonzero =
                last_nonzero_ms > 0
                && safe_elapsed_ms(now, last_nonzero_ms) < k_recent_zero_suppress_ms;
            const bool suppress_transient_zero =
                recent_nonzero
                && (
                    std::strcmp(ev, "pull_accuracy") == 0
                    || std::strcmp(ev, "pull_seconds_total") == 0
                    || std::strcmp(ev, "pull_score_per_minute") == 0
                );
            if (suppress_transient_zero) {
                return false;
            }
        }
        if (value > 0.0f) {
            last_nonzero_ms = now;
        }
        if (std::isfinite(last_value) && std::fabs(static_cast<double>(last_value) - static_cast<double>(value)) <= 0.0001) {
            return false;
        }
        last_value = value;
        last_state_change_emit_ms_ = now;
        return true;
    }

    void emit_pull_snapshot(uint64_t now) {
        std::array<char, 2048> json{};
        std::snprintf(
            json.data(),
            json.size(),
            "{\"ev\":\"pull_snapshot\",\"ts_ms\":%llu,\"is_in_challenge\":%d,\"is_in_scenario\":%d,\"is_in_scenario_editor\":%d,\"is_in_trainer\":%d,\"scenario_is_paused\":%d,\"kills\":%d,\"shots_fired\":%d,\"shots_hit\":%d,\"challenge_tick_count_total\":%d,\"session_time_secs\":%.6f,\"challenge_seconds_total\":%.6f,\"score_total\":%.6f,\"score_total_derived\":%.6f,\"spm\":%.6f,\"kps\":%.6f,\"accuracy_pct\":%.6f,\"challenge_average_fps\":%.6f,\"damage_done\":%.6f,\"damage_possible\":%.6f,\"damage_efficiency\":%.6f,\"time_remaining\":%.6f,\"queue_time_remaining\":%.6f,\"method\":\"%s\",\"origin_flag\":\"%s\",\"source\":\"production_stripped\"}",
            static_cast<unsigned long long>(now),
            last_is_in_challenge_,
            last_is_in_scenario_,
            last_is_in_scenario_editor_,
            last_is_in_trainer_,
            last_scenario_is_paused_,
            last_kills_total_,
            last_shots_fired_,
            last_shots_hit_,
            last_challenge_tick_count_,
            static_cast<double>(last_seconds_),
            static_cast<double>(last_challenge_seconds_total_),
            static_cast<double>(last_score_total_),
            static_cast<double>(last_score_total_derived_),
            static_cast<double>(last_score_per_minute_),
            static_cast<double>(last_kills_per_second_),
            static_cast<double>(last_accuracy_),
            static_cast<double>(last_challenge_average_fps_),
            static_cast<double>(last_damage_done_),
            static_cast<double>(last_damage_possible_),
            static_cast<double>(last_damage_efficiency_),
            static_cast<double>(last_time_remaining_),
            static_cast<double>(last_queue_time_remaining_),
            emit_method_ ? emit_method_ : "unknown",
            emit_origin_flag_ ? emit_origin_flag_ : "unknown"
        );
        kovaaks::RustBridge::emit_json(json.data());
    }
};

extern "C" __declspec(dllexport) RC::CppUserModBase* start_mod() {
    return new KovaaksBridgeModProduction();
}

extern "C" __declspec(dllexport) void uninstall_mod(RC::CppUserModBase* mod) {
    delete mod;
}
