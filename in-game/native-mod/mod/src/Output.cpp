#include "Output.hpp"

#include <aimmod/Formats.hpp>
#include <aimmod/Settings.hpp>

#include <Windows.h>
#include <ShlObj.h>

#include <chrono>
#include <cstring>

namespace aimmod
{
    namespace
    {
        constexpr wchar_t SharedLiveName[] = L"Local\\AimMod.KovaaksNative.Live";
        constexpr std::size_t SharedLiveSize = 8192;
        constexpr std::size_t SharedHeader = 32;

        std::uint64_t NowMs()
        {
            return static_cast<std::uint64_t>(
                std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
        }

        std::int64_t UnixMs()
        {
            return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
        }

        bool ReadSmall(const std::filesystem::path& path, std::string& out, std::size_t limit)
        {
            HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                                      FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file == INVALID_HANDLE_VALUE) return false;
            out.assign(limit, '\0');
            DWORD read = 0;
            BOOL ok = ReadFile(file, out.data(), static_cast<DWORD>(limit), &read, nullptr);
            CloseHandle(file);
            if (!ok) return false;
            out.resize(read);
            return true;
        }

        bool WriteAll(HANDLE file, const std::string& data)
        {
            std::size_t done = 0;
            while (done < data.size())
            {
                DWORD written = 0;
                DWORD chunk = static_cast<DWORD>(std::min<std::size_t>(data.size() - done, 1u << 20));
                if (!WriteFile(file, data.data() + done, chunk, &written, nullptr) || written == 0) return false;
                done += written;
            }
            return true;
        }
    } // namespace

    std::filesystem::path Output::DefaultRoot()
    {
        PWSTR local = nullptr;
        std::filesystem::path root;
        if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local)) && local) root = local;
        if (local) CoTaskMemFree(local);
        if (root.empty()) return {};
        return root / L"AimMod" / L"KovaaksNative";
    }

    bool Output::Start(const std::filesystem::path& root, std::string version)
    {
        if (m_thread.joinable() || root.empty()) return false;
        m_root = root;
        m_version = std::move(version);
        std::error_code error;
        std::filesystem::create_directories(m_root / L"replays", error);
        if (!std::filesystem::is_directory(m_root, error)) return false;
        // Unfinished recordings from earlier sessions (either recorder) are
        // never published; remove ones not touched for ten minutes.
        const auto cutoff = std::filesystem::file_time_type::clock::now() - std::chrono::minutes(10);
        for (const auto& entry : std::filesystem::directory_iterator(m_root / L"replays", error))
        {
            std::error_code itemError;
            if (entry.path().extension() != L".partial" || !entry.is_regular_file(itemError)) continue;
            if (entry.last_write_time(itemError) < cutoff && !itemError) std::filesystem::remove(entry.path(), itemError);
        }
        m_mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, static_cast<DWORD>(SharedLiveSize), SharedLiveName);
        if (m_mapping) m_view = MapViewOfFile(m_mapping, FILE_MAP_WRITE, 0, 0, SharedLiveSize);
        if (m_view)
        {
            auto* bytes = static_cast<std::uint8_t*>(m_view);
            const std::uint32_t magic = 0x564C4D41u, layout = 1u; // "AMLV"
            std::memcpy(bytes, &magic, 4);
            std::memcpy(bytes + 4, &layout, 4);
        }
        m_stop = false;
        Periodic(true);
        m_thread = std::thread([this] { Run(); });
        return true;
    }

    void Output::Stop()
    {
        if (!m_thread.joinable()) return;
        {
            std::lock_guard lock(m_mutex);
            m_stop = true;
        }
        m_wake.notify_all();
        m_thread.join();
        for (auto& [id, handle] : m_replays)
        {
            CloseHandle(handle);
            DeleteFileW((m_root / L"replays" / std::filesystem::path(id + ".partial")).c_str());
        }
        m_replays.clear();
        // Retract the handshake so the Lua mod resumes immediately.
        DeleteFileW((m_root / L"core-active.tsv").c_str());
        if (m_view) UnmapViewOfFile(m_view);
        if (m_mapping) CloseHandle(m_mapping);
        m_view = m_mapping = nullptr;
    }

    void Output::AppendJournal(std::string line)
    {
        {
            std::lock_guard lock(m_mutex);
            m_jobs.push_back({Job::Kind::Journal, {}, std::move(line)});
        }
        m_wake.notify_one();
    }

    void Output::PublishLive(std::string body)
    {
        std::lock_guard lock(m_mutex);
        if (body == m_live) return;
        m_live = std::move(body);
        m_liveDirty = true;
    }

    void Output::PublishReplayStatus(std::string body)
    {
        {
            std::lock_guard lock(m_mutex);
            if (body == m_status) return;
            m_status = std::move(body);
            m_statusDirty = true;
        }
        m_wake.notify_one();
    }

    void Output::SetCapabilities(std::string capabilities)
    {
        std::lock_guard lock(m_mutex);
        if (capabilities == m_capabilities) return;
        m_capabilities = std::move(capabilities);
        m_capsDirty = true;
    }

    void Output::ReplayOpen(const std::string& id)
    {
        {
            std::lock_guard lock(m_mutex);
            m_jobs.push_back({Job::Kind::ReplayOpen, id, {}});
        }
        m_wake.notify_one();
    }

    void Output::ReplayAppend(const std::string& id, std::string chunk)
    {
        if (chunk.empty()) return;
        {
            std::lock_guard lock(m_mutex);
            m_jobs.push_back({Job::Kind::ReplayAppend, id, std::move(chunk)});
        }
        m_wake.notify_one();
    }

    void Output::ReplayClose(const std::string& id, bool publish)
    {
        {
            std::lock_guard lock(m_mutex);
            m_jobs.push_back({Job::Kind::ReplayClose, id, {}, publish});
        }
        m_wake.notify_one();
    }

    bool Output::WriteAtomic(const std::filesystem::path& path, const std::string& body)
    {
        std::filesystem::path next = path;
        next += L".next";
        HANDLE file = CreateFileW(next.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return false;
        bool ok = WriteAll(file, body);
        CloseHandle(file);
        if (ok && MoveFileExW(next.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING)) return true;
        DeleteFileW(next.c_str());
        return false;
    }

    void Output::WriteShared(const std::string& body)
    {
        if (!m_view || body.size() > SharedLiveSize - SharedHeader) return;
        auto* bytes = static_cast<std::uint8_t*>(m_view);
        auto* sequence = reinterpret_cast<volatile LONG64*>(bytes + 8);
        InterlockedIncrement64(sequence); // odd: write in progress
        const std::int64_t stamp = UnixMs();
        const std::int32_t length = static_cast<std::int32_t>(body.size());
        std::memcpy(bytes + 16, &stamp, 8);
        std::memcpy(bytes + 24, &length, 4);
        std::memcpy(bytes + SharedHeader, body.data(), body.size());
        InterlockedIncrement64(sequence); // even: stable
    }

    bool Output::Execute(Job& job)
    {
        const std::filesystem::path replays = m_root / L"replays";
        switch (job.kind)
        {
        case Job::Kind::Journal:
        {
            HANDLE file = CreateFileW((m_root / L"completed.tsv").c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                      nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file == INVALID_HANDLE_VALUE) return false;
            bool ok = WriteAll(file, job.data);
            CloseHandle(file);
            return ok;
        }
        case Job::Kind::ReplayOpen:
        {
            if (!IsValidAttemptId(job.id) || m_replays.contains(job.id)) return true;
            HANDLE file = CreateFileW((replays / std::filesystem::path(job.id + ".partial")).c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr,
                                      CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file != INVALID_HANDLE_VALUE) m_replays.emplace(job.id, file);
            return true; // a failed open drops this recording only
        }
        case Job::Kind::ReplayAppend:
        {
            auto it = m_replays.find(job.id);
            if (it == m_replays.end()) return true;
            if (!WriteAll(it->second, job.data))
            {
                CloseHandle(it->second);
                DeleteFileW((replays / std::filesystem::path(job.id + ".partial")).c_str());
                m_replays.erase(it);
            }
            return true;
        }
        case Job::Kind::ReplayClose:
        {
            auto it = m_replays.find(job.id);
            if (it == m_replays.end()) return true;
            CloseHandle(it->second);
            m_replays.erase(it);
            const auto partial = replays / std::filesystem::path(job.id + ".partial");
            const auto final = replays / std::filesystem::path(job.id + ".amreplay");
            if (!job.publish || !MoveFileExW(partial.c_str(), final.c_str(), 0)) DeleteFileW(partial.c_str());
            return true;
        }
        }
        return true;
    }

    void Output::Periodic(bool force)
    {
        const std::uint64_t now = NowMs();
        std::string live, status, caps;
        bool liveDirty = false, statusDirty = false;
        {
            std::lock_guard lock(m_mutex);
            if (m_liveDirty)
            {
                live = m_live;
                liveDirty = true;
                m_liveDirty = false;
            }
            else if (!m_live.empty() && now - m_lastLiveWrite >= 1000) live = m_live;
            if (m_statusDirty)
            {
                status = m_status;
                statusDirty = true;
                m_statusDirty = false;
            }
            caps = m_capabilities;
            m_capsDirty = false;
        }
        if (!live.empty())
        {
            // Readers treat live-overlay.json older than 2 s as stale: rewrite
            // unchanged content once per second, changed content immediately.
            if (WriteAtomic(m_root / L"live-overlay.json", live)) m_lastLiveWrite = now;
            else if (liveDirty)
            {
                std::lock_guard lock(m_mutex);
                if (!m_liveDirty) m_liveDirty = true;
            }
            if (liveDirty) WriteShared(live);
        }
        if (statusDirty && !WriteAtomic(m_root / L"replay-status.json", status))
        {
            std::lock_guard lock(m_mutex);
            if (!m_statusDirty) m_statusDirty = true;
        }
        if (force || now - m_lastHeartbeat >= 1000)
        {
            if (WriteAtomic(m_root / L"core-active.tsv", FormatCoreActive(m_version, std::time(nullptr), caps))) m_lastHeartbeat = now;
        }
        if (force || now - m_lastSettings >= 1000)
        {
            m_lastSettings = now;
            std::string text;
            // A missing or locked file keeps the previous state; a readable
            // malformed file fails closed (matches the service store).
            if (ReadSmall(m_root / L"native-settings.tsv", text, 1025))
            {
                auto parsed = ParseNativeSettings(text);
                m_recording.store(parsed && parsed->replayRecordingEnabled, std::memory_order_relaxed);
            }
        }
        if (force || now - m_lastPlaybackCheck >= 500)
        {
            m_lastPlaybackCheck = now;
            std::string head;
            bool active = false;
            if (ReadSmall(m_root / L"replay-frame.tsv", head, 64))
            {
                auto newline = head.find('\n');
                active = IsReplayPlaybackHeader(std::string_view(head).substr(0, newline));
            }
            m_playback.store(active, std::memory_order_relaxed);
        }
    }

    void Output::Run()
    {
        for (;;)
        {
            std::deque<Job> jobs;
            bool stop;
            {
                std::unique_lock lock(m_mutex);
                m_wake.wait_for(lock, std::chrono::milliseconds(100), [this] { return m_stop || !m_jobs.empty() || m_statusDirty; });
                jobs.swap(m_jobs);
                stop = m_stop;
            }
            std::deque<Job> retry;
            for (Job& job : jobs)
            {
                if (!Execute(job) && ++job.attempts < 50) retry.push_back(std::move(job));
            }
            if (!retry.empty())
            {
                std::lock_guard lock(m_mutex);
                for (auto it = retry.rbegin(); it != retry.rend(); ++it) m_jobs.push_front(std::move(*it));
            }
            Periodic(false);
            if (stop)
            {
                std::lock_guard lock(m_mutex);
                if (m_jobs.empty() || retry.size() == m_jobs.size()) break;
            }
            if (!retry.empty()) std::this_thread::sleep_for(std::chrono::milliseconds(200));
        }
    }
} // namespace aimmod
