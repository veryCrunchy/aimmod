#pragma once
// Writer thread: owns every file the mod writes. The game thread only hands
// over small immutable jobs (never blocks on disk).
#include <aimmod/Cosmetics.hpp>
#include <aimmod/GameCommand.hpp>
#include <aimmod/GameStats.hpp>
#include <aimmod/MatchPlay.hpp>
#include <aimmod/ReplayV2.hpp>
#include <aimmod/Settings.hpp>

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <memory>
#include <filesystem>
#include <mutex>
#include <optional>
#include <set>
#include <variant>
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
        // core-scene.json: what the game currently shows (replay start gate).
        void PublishScene(std::string body);
        // core-command.tsv requests (validated) and their answers.
        std::optional<std::variant<GameCommand, CommandError>> TakeCommand();
        void PublishCommandResult(std::string body);
        // self-pose.tsv: the local view for spectators, only while requested
        // (self-pose.request touched within the last 5 s).
        bool poseRequested() const { return m_poseRequested.load(std::memory_order_relaxed); }
        // avatars.tsv (multiplayer bridge): actor name -> stream id of the
        // player that actor represents, for `tag` rows. Returns an empty map
        // when absent.
        std::shared_ptr<const std::unordered_map<std::string, std::string>> avatars() const;
        void PublishSelfPose(std::string body);
        // self-shots.tsv: the local player's shots for match modes, only while
        // requested (self-shots.request touched within the last 5 s).
        bool shotsRequested() const { return m_shotsRequested.load(std::memory_order_relaxed); }
        void PublishSelfShots(std::string body);
        // play-state.tsv (host verdict on this player). `state` is null while
        // the file is absent, malformed or not rewritten for 5 s; `version`
        // changes whenever a new state (or its loss) is read.
        struct PlayStateSnapshot
        {
            std::shared_ptr<const PlayState> state;
            std::uint64_t version{};
        };
        PlayStateSnapshot playState() const;
        // round-state.tsv (service, same rules as play-state.tsv).
        struct RoundStateSnapshot
        {
            std::shared_ptr<const RoundState> state;
            std::uint64_t version{};
        };
        RoundStateSnapshot roundState() const;

        // Cosmetics inputs (DESIGN.md "Cosmetics"): the installed catalog,
        // verified once against its manifest by the writer thread, and the
        // session marker and looks the service writes, re-read every second.
        struct CosmeticsLibrary
        {
            cosmetics::Index index;
            std::set<std::string> verifiedPaks;
            std::string status;
        };
        struct CosmeticsInputs
        {
            std::shared_ptr<const CosmeticsLibrary> library; // null until loaded
            std::optional<cosmetics::Marker> marker;
            std::shared_ptr<const cosmetics::Looks> looks;   // null: absent or malformed
            bool allowDrafts{};                              // cosmetics-dev.txt (local team tests)
        };
        // Before Start: Mods\AimModCore\service\cosmetics and <game>\Content\Paks\~AimMod.
        void SetCosmeticsSources(std::filesystem::path catalogDir, std::filesystem::path paksDir);
        CosmeticsInputs cosmetics() const;
        // Clip hotkey and window (clip-settings.tsv, defaults F8 / 8 s / 2 s).
        ClipSettings clipSettings() const;
        void PublishReplayStatus(std::string body);
        // Encodes (format 2) and publishes a completed recording.
        void ReplayWrite(std::unique_ptr<replay2::Capture> capture);
        void SetCapabilities(std::string capabilities);

        // Looks for the stats CSV the game writes when this attempt completes
        // (scenario match, written after `sinceUnixMs`, challenge start within
        // 5 s of `localStartSeconds` when known). Polled by the writer thread.
        void WatchGameStats(std::string scenario, std::int64_t sinceUnixMs, std::optional<double> localStartSeconds);
        void StopGameStats();
        // Logs whether the game wrote a challenge stats CSV for `scenario`
        // within 15 s (quit-run audit; a quit must not write one).
        void AuditQuitStats(std::string scenario);
        std::optional<GameStats> TakeGameStats();
        const std::filesystem::path& statsFolder() const { return m_stats; }

        bool recordingEnabled() const { return m_recording.load(std::memory_order_relaxed); }
        bool playbackActive() const { return m_playback.load(std::memory_order_relaxed); }
        const std::filesystem::path& root() const { return m_root; }

    private:
        struct Job
        {
            enum class Kind { Journal, Replay };
            Kind kind{};
            std::string data;
            std::shared_ptr<replay2::Capture> capture;
            int attempts{};
        };

        void Run();
        bool Execute(Job& job);
        void Periodic(bool force);
        bool WriteAtomic(const std::filesystem::path& path, const std::string& body);
        void WriteShared(const std::string& body);
        void ScanGameStats(std::uint64_t now);

        std::filesystem::path m_root, m_stats;
        struct StatsWatch
        {
            std::string scenario;
            std::int64_t since{};
            std::optional<double> localStart;
            std::uint64_t expires{};
        };
        std::optional<StatsWatch> m_watch;
        std::optional<GameStats> m_found;
        std::set<std::wstring> m_consumed;
        std::uint64_t m_lastStatsScan{};
        std::deque<std::variant<GameCommand, CommandError>> m_commands;
        std::string m_commandText;
        std::uint64_t m_commandSequence{}, m_lastCommandCheck{};
        bool m_commandPrimed{};
        std::deque<std::string> m_results;
        void ReadCommand(std::uint64_t now);
        std::string m_version;
        std::thread m_thread;
        std::mutex m_mutex;
        std::condition_variable m_wake;
        std::deque<Job> m_jobs;
        std::string m_live, m_status, m_capabilities, m_sceneBody;
        bool m_liveDirty{}, m_statusDirty{}, m_capsDirty{}, m_sceneDirty{};
        bool m_stop{};
        std::atomic<bool> m_recording{true};
        std::atomic<bool> m_playback{false};
        std::atomic<bool> m_poseRequested{false};
        std::atomic<bool> m_shotsRequested{false};
        std::string m_selfShots;
        bool m_selfShotsDirty{};
        std::shared_ptr<const PlayState> m_playState;
        std::uint64_t m_playStateVersion{}, m_playStateStamp{}, m_lastPlayStateCheck{}, m_playStateSeenAt{};
        void ReadPlayState(std::uint64_t now);
        std::shared_ptr<const RoundState> m_roundState;
        std::uint64_t m_roundStateVersion{}, m_roundStateStamp{};
        void ReadRoundState(std::uint64_t now);
        struct QuitStats
        {
            std::string scenario;
            std::filesystem::file_time_type since;
            std::uint64_t until{};
            bool found{};
        };
        std::optional<QuitStats> m_quitStats;
        void CheckQuitStats(std::uint64_t now);
        std::filesystem::path m_catalogDir, m_paksDir;
        bool m_libraryLoaded{};
        CosmeticsInputs m_cosmetics;
        std::string m_markerText, m_looksText;
        std::uint64_t m_lastCosmeticsCheck{};
        void LoadCosmeticsLibrary();
        void ReadCosmeticsInputs();
        ClipSettings m_clips;
        std::string m_selfPose;
        bool m_selfPoseDirty{};
        std::uint64_t m_lastPoseCheck{}, m_lastClipCheck{};
        std::shared_ptr<const std::unordered_map<std::string, std::string>> m_avatars = std::make_shared<std::unordered_map<std::string, std::string>>();
        std::string m_avatarText;

        // Writer-thread state.
        std::string m_lastLive, m_writtenCaps;
        std::uint64_t m_lastLiveWrite{}, m_lastSceneWrite{}, m_lastHeartbeat{}, m_lastSettings{}, m_lastPlaybackCheck{};
        void* m_mapping{};
        void* m_view{};
    };
} // namespace aimmod
