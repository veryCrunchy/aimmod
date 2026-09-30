#include "Output.hpp"

#include "Log.hpp"

#include <aimmod/Formats.hpp>
#include <aimmod/Settings.hpp>

#include <Windows.h>
#include <ShlObj.h>

#include <chrono>
#include <cmath>
#include <cstdio>
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
        {
            // <game>\FPSAimTrainer\Binaries\Win64\<exe> -> <game>\FPSAimTrainer\stats
            wchar_t exe[MAX_PATH * 4]{};
            const DWORD length = GetModuleFileNameW(nullptr, exe, static_cast<DWORD>(std::size(exe)));
            if (length > 0 && length < std::size(exe)) m_stats = std::filesystem::path(exe).parent_path().parent_path().parent_path() / L"stats";
        }
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
        // Retract the handshake so the Lua mod resumes immediately.
        DeleteFileW((m_root / L"core-active.tsv").c_str());
        DeleteFileW((m_root / L"core-scene.json").c_str());
        if (m_view) UnmapViewOfFile(m_view);
        if (m_mapping) CloseHandle(m_mapping);
        m_view = m_mapping = nullptr;
    }

    void Output::AppendJournal(std::string line)
    {
        {
            std::lock_guard lock(m_mutex);
            m_jobs.push_back({Job::Kind::Journal, std::move(line)});
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

    void Output::PublishScene(std::string body)
    {
        std::lock_guard lock(m_mutex);
        if (body == m_sceneBody) return;
        m_sceneBody = std::move(body);
        m_sceneDirty = true;
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

    void Output::WatchGameStats(std::string scenario, std::int64_t sinceUnixMs, std::optional<double> localStartSeconds)
    {
        std::lock_guard lock(m_mutex);
        m_found.reset();
        m_watch = StatsWatch{std::move(scenario), sinceUnixMs, localStartSeconds, NowMs() + 15000};
    }

    void Output::StopGameStats()
    {
        std::lock_guard lock(m_mutex);
        m_watch.reset();
        m_found.reset();
    }

    std::optional<GameStats> Output::TakeGameStats()
    {
        std::lock_guard lock(m_mutex);
        std::optional<GameStats> out;
        out.swap(m_found);
        return out;
    }

    void Output::ScanGameStats(std::uint64_t now)
    {
        std::optional<StatsWatch> watch;
        {
            std::lock_guard lock(m_mutex);
            if (!m_watch || m_found) return;
            if (now >= m_watch->expires)
            {
                m_watch.reset();
                return;
            }
            watch = m_watch;
        }
        if (m_stats.empty() || now - m_lastStatsScan < 150) return;
        m_lastStatsScan = now;
        std::error_code error;
        const auto since = std::chrono::system_clock::time_point(std::chrono::milliseconds(watch->since - 2000));
        for (const auto& entry : std::filesystem::directory_iterator(m_stats, error))
        {
            std::error_code itemError;
            const std::wstring name = entry.path().filename().wstring();
            if (m_consumed.contains(name) || !entry.is_regular_file(itemError)) continue;
            const auto written = std::chrono::clock_cast<std::chrono::system_clock>(entry.last_write_time(itemError));
            if (itemError || written < since) continue;
            const std::string narrow = entry.path().filename().string();
            if (!IsChallengeStatsFile(narrow)) continue;
            std::string text;
            if (!ReadSmall(entry.path(), text, 256 * 1024)) continue;
            auto stats = ParseGameStats(text);
            if (!stats) continue; // possibly still being written: retried next scan
            m_consumed.insert(name);
            if (stats->scenario != watch->scenario) continue;
            if (watch->localStart && stats->challengeStartSeconds)
            {
                double delta = std::fabs(*stats->challengeStartSeconds - *watch->localStart);
                delta = std::min(delta, 86400.0 - delta);
                if (delta > 5.0) continue;
            }
            std::lock_guard lock(m_mutex);
            if (m_watch && m_watch->scenario == watch->scenario && m_watch->since == watch->since)
            {
                m_found = std::move(stats);
                m_watch.reset();
            }
            return;
        }
    }

    void Output::ReplayWrite(std::unique_ptr<replay2::Capture> capture)
    {
        if (!capture) return;
        {
            std::lock_guard lock(m_mutex);
            Job job{Job::Kind::Replay};
            job.capture = std::move(capture);
            m_jobs.push_back(std::move(job));
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
        case Job::Kind::Replay:
        {
            const replay2::Capture& c = *job.capture;
            replay2::EncodeReport report;
            const std::vector<std::uint8_t> bytes = replay2::Encode(c, {}, &report);
            if (bytes.empty())
            {
                Warn("replay not saved: capture unusable (id=" + c.header.id + ")");
                return true;
            }
            const auto partial = replays / std::filesystem::path(c.header.id + ".partial");
            const auto final = replays / std::filesystem::path(c.header.id + ".amreplay");
            HANDLE file = CreateFileW(partial.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file == INVALID_HANDLE_VALUE) return false; // retried
            const bool ok = WriteAll(file, std::string(bytes.begin(), bytes.end()));
            CloseHandle(file);
            if (!ok || !MoveFileExW(partial.c_str(), final.c_str(), 0))
            {
                DeleteFileW(partial.c_str());
                Warn("replay not saved: write failed (id=" + c.header.id + ")");
                return true;
            }
            char line[512];
            std::snprintf(line, sizeof(line),
                          "replay saved id=%s bytes=%zu (%.1f KB/min, body %zu) frames=%u inputs=%u samples=%u keyframes=%u targets %u->%u points "
                          "yaw/unit=%.9g pitch/unit=%.9g quantum=%.9g drift max=%.4f rms=%.5f deg",
                          c.header.id.c_str(), report.fileBytes, report.duration > 0 ? report.fileBytes / 1024.0 / (report.duration / 60.0) : 0.0,
                          report.bodyBytes, report.engineFrames, report.inputEvents, report.cameraSamples, report.keyframes, report.actorSamples,
                          report.actorPoints, report.yawPerUnit, report.pitchPerUnit, report.quantum, report.keyframeErrorMax, report.keyframeErrorRms);
            Log(line);
            return true;
        }
        }
        return true;
    }

    void Output::Periodic(bool force)
    {
        const std::uint64_t now = NowMs();
        std::string live, status, caps, scene;
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
            if (m_sceneDirty || (!m_sceneBody.empty() && now - m_lastSceneWrite >= 1000))
            {
                scene = m_sceneBody;
                m_sceneDirty = false;
            }
        }
        // Rewritten at least once a second: readers treat it as stale after 3 s.
        if (!scene.empty() && WriteAtomic(m_root / L"core-scene.json", scene)) m_lastSceneWrite = now;
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
        ScanGameStats(now);
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
