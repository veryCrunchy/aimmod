#pragma once
// Writer thread: owns every file the mod writes. The game thread only hands
// over small immutable jobs (never blocks on disk).
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <filesystem>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>

namespace aimmod
{
    class Output
    {
    public:
        Output() = default;
        ~Output() { Stop(); }
        Output(const Output&) = delete;
        Output& operator=(const Output&) = delete;

        // %LOCALAPPDATA%\AimMod\KovaaksNative
        static std::filesystem::path DefaultRoot();

        bool Start(const std::filesystem::path& root, std::string version);
        void Stop();

        void AppendJournal(std::string line);
        void PublishLive(std::string body);
        void PublishReplayStatus(std::string body);
        void ReplayOpen(const std::string& id);
        void ReplayAppend(const std::string& id, std::string chunk);
        void ReplayClose(const std::string& id, bool publish);
        void SetCapabilities(std::string capabilities);

        bool recordingEnabled() const { return m_recording.load(std::memory_order_relaxed); }
        bool playbackActive() const { return m_playback.load(std::memory_order_relaxed); }
        const std::filesystem::path& root() const { return m_root; }

    private:
        struct Job
        {
            enum class Kind { Journal, ReplayOpen, ReplayAppend, ReplayClose };
            Kind kind{};
            std::string id;
            std::string data;
            bool publish{};
            int attempts{};
        };

        void Run();
        bool Execute(Job& job);
        void Periodic(bool force);
        bool WriteAtomic(const std::filesystem::path& path, const std::string& body);
        void WriteShared(const std::string& body);

        std::filesystem::path m_root;
        std::string m_version;
        std::thread m_thread;
        std::mutex m_mutex;
        std::condition_variable m_wake;
        std::deque<Job> m_jobs;
        std::string m_live, m_status, m_capabilities;
        bool m_liveDirty{}, m_statusDirty{}, m_capsDirty{};
        bool m_stop{};
        std::atomic<bool> m_recording{true};
        std::atomic<bool> m_playback{false};

        // Writer-thread state.
        std::unordered_map<std::string, void*> m_replays;
        std::string m_lastLive, m_writtenCaps;
        std::uint64_t m_lastLiveWrite{}, m_lastHeartbeat{}, m_lastSettings{}, m_lastPlaybackCheck{};
        void* m_mapping{};
        void* m_view{};
    };
} // namespace aimmod
