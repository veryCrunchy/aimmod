#pragma once
// AimModSteam bridge: Steam lobbies, invites, friends, rich presence and
// relay-only P2P for the AimMod native service. The service speaks the
// versioned JSON contract in in-game/docs/multiplayer.md over PipeServer.
//
// Threads: one worker thread owns all Steam calls and all bridge state; the
// pipe thread only enqueues commands; Steam callbacks (dispatched on the game
// thread by the game's SteamAPI_RunCallbacks) only enqueue raw copies.

#include "Codec.hpp"
#include "Json.hpp"
#include "Pipe.hpp"
#include "PoseFile.hpp"
#include "Steam.hpp"

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <thread>
#include <vector>

namespace bridge
{
    using LogFn = std::function<void(const std::string&)>;
    using GameThreadFn = std::function<void(const std::function<void()>&)>;

    constexpr const char* BridgeVersion = "0.1.0";
    constexpr const wchar_t* PipeName = L"\\\\.\\pipe\\aimmod-steam-v1";

    class Bridge
    {
    public:
        Bridge(LogFn log, GameThreadFn onGameThread);
        ~Bridge();
        Bridge(const Bridge&) = delete;
        Bridge& operator=(const Bridge&) = delete;

        // steamApi: the game's loaded steam_api64.dll. commandLine: this
        // process's command line (read once, for invite launches).
        bool Start(HMODULE steamApi, std::wstring commandLine);
        // Leaves the lobby, clears rich presence, closes every connection,
        // unregisters callbacks and stops the threads.
        void Stop();

        // Ghost demo (config ghost_demo=1): auto-join Steam invites when no
        // service is connected, and stream poses at 30 Hz to lobby peers.
        struct Options
        {
            bool ghostDemo = false;
            std::wstring scenePath; // AimModCore's core-scene.json
            std::wstring stateDir;  // where steam-last-lobby.json lives (KovaaksNative)
            bool hideScenario = false; // keep the scenario out of rich presence
            SpectatePrivacy spectatePrivacy = SpectatePrivacy::Friends; // lobby-less spectating
            bool showSpectating = false; // publish whom we spectate in rich presence
        };
        void SetOptions(Options options) { m_options = std::move(options); } // before Start
        struct GhostSample
        {
            double time; // local receive time, seconds (steady clock)
            Pose pose;
        };
        struct GhostPeer
        {
            std::uint64_t peer = 0;
            std::vector<GhostSample> samples; // oldest first
        };
        // Thread-safe; called from the game thread.
        void SubmitLocalPose(const Pose& pose);
        void SubmitLocalCamera(const CameraFrame& camera);
        bool CameraWanted() const { return m_cameraRate.load() > 0 || m_directWatchers.load() > 0; }
        std::vector<GhostPeer> Ghosts();
        std::string LocalScene();
        std::string LobbyValue(const std::string& key); // current lobby data, empty if unset
        static double Now();

    private:
        using Clock = std::chrono::steady_clock;

        struct RawCallback
        {
            int id;
            std::vector<std::uint8_t> bytes;
        };
        class Listener;

        enum class ConnState { Connecting, Handshaking, Ready };
        struct Conn
        {
            std::uint64_t peer = 0;
            steamabi::HSteamNetConnection handle = 0;
            ConnState state = ConnState::Connecting;
            bool outgoing = false;
            Clock::time_point deadline;
            Clock::time_point nextPing;
            std::uint32_t pingSeq = 0;
            std::optional<int> rtt;
            bool lanes = false; // bulk chunks on a lower-priority lane
            // Lobby-less spectating (kept in m_direct): Watcher = they watch us, Watched = we watch them.
            enum class Role { Lobby, Watcher, Watched } role = Role::Lobby;
            bool asked = false; // waiting for spectate.answer
            int rate = 0;
        };

        struct Xfer
        {
            std::set<std::uint32_t> inflight; // sent, not yet acknowledged
        };

        struct UgcWatch
        {
            std::uint64_t downloaded = 0, total = 0;
            Clock::time_point next{};
            Clock::time_point deadline{};
        };

        struct UgcCall
        {
            std::uint64_t item = 0;
            std::int64_t commandId = -1;
            steamabi::SteamAPICall_t call = 0;
            Clock::time_point deadline{};
        };

        struct PendingCall
        {
            enum class Kind { Create, Join } kind;
            steamabi::SteamAPICall_t call = 0;
            std::int64_t commandId = -1;
            std::uint64_t lobby = 0; // join target
            std::string privacy;
            int maxMembers = 0;
            std::map<std::string, std::string> data;
            Clock::time_point deadline;
        };

        struct Avatar
        {
            std::uint64_t peer;
            Clock::time_point deadline;
        };

        struct PendingJoin
        {
            std::string source;
            std::uint64_t lobby = 0;
            std::uint64_t from = 0;
            int version = 0;
        };

        // Worker
        void Run();
        void Tick();
        void HandleCommand(const std::string& text);
        void HandleCallback(const RawCallback& cb);
        void PollCalls();
        void PollLobby(bool force);
        void PollConnections();
        void ReceiveAll();
        void OnWire(Conn& conn, const WireMessage& m, bool reliable);

        // Lobby
        void EnterLobby(std::uint64_t lobby, bool created, const PendingCall* call);
        void LeaveLobby(const char* reason);
        void UpdateRole();
        void OpenListen();
        void CloseListen();
        void ConnectToOwner();
        void CloseConn(std::uint64_t peer, bool bye, const char* reason, bool linger = false);
        void CloseAllConns(const char* reason);
        bool IsMember(std::uint64_t peer) const;
        bool IsHost() const { return m_lobby != 0 && m_owner == m_self; }
        void UpdatePresence();

        // Events
        void Emit(const std::string& json);
        void Result(std::int64_t id, bool ok, const char* code = nullptr, const std::string& message = {});
        void Error(const char* code, const std::string& message);
        void EmitReady();
        void EmitLobby();
        void EmitFriends();
        void EmitAvatar(std::uint64_t peer, bool final);
        void EmitJoinRequest(const PendingJoin& join);
        std::string Name(std::uint64_t peer) const;
        static std::string Initials(const std::string& name);
        std::string MemberJson(std::uint64_t peer) const;

        // Workshop (read item state, subscribe, download)
        void PollUgc();
        void EmitUgcState(std::uint64_t item);
        bool InstallFolder(std::uint64_t item, std::string& folder);

        // Bulk transfers
        bool SendChunk(Conn& conn, const WireMessage& m);
        void CancelTransfersWith(std::uint64_t peer, const char* reason);

        // Reconnect
        void LoadLast();
        void SaveLast();
        void ClearLast();

        // Presence and scene
        void ReadScene();
        void UpdateStatusPresence();
        std::string WorkshopIdFor(const std::string& scenario);
        std::string m_workshopId, m_rpWorkshop;

        // Spectate
        void UpdateSpectateRoute(std::uint64_t target);
        void ForgetSpectate(std::uint64_t peer);
        void SendCamera();
        void EmitCamera(const CameraFrame& c);
        void OnSpectateFrame(const CameraFrame& c);
        void WriteSpectatePose(bool force);
        void ResetSpectator();

        // Lobby-less spectating
        void HandleBulk(Conn& conn, const WireMessage& m, bool reliable);
        Conn* FindLink(std::uint64_t peer); // lobby link, else spectate link
        void EnsureListen();
        void PollDirect();
        void ReceiveDirect();
        void OnDirectWire(std::uint64_t peer, const WireMessage& m, bool reliable);
        void CloseDirect(std::uint64_t peer, const char* reason);
        void AcceptWatcher(std::uint64_t peer);
        void RefuseWatcher(std::uint64_t peer, RejectCode code);
        void EmitSpectators();
        void SendScore(const std::vector<Conn*>& targets);
        void EmitScore(const ScoreFrame& s);
        Clock::time_point m_nextListenTry{};
        std::vector<Conn*> CameraTargets();

        // Ghost demo
        void GhostTick();
        void OnPose(Conn& conn, const Pose& pose);
        void ForgetGhost(std::uint64_t peer);
        void AutoJoin(std::uint64_t lobby, const char* why);

        // Steam P2P helpers
        bool SendWire(Conn& conn, const WireMessage& m, bool reliable);
        Conn* FindConn(std::uint64_t peer);
        Conn* FindConnByHandle(steamabi::HSteamNetConnection handle);

        LogFn m_log;
        GameThreadFn m_onGameThread;
        Steam m_steam;
        std::unique_ptr<PipeServer> m_pipe;
        std::vector<Listener*> m_listeners;
        bool m_listenersRegistered = false;

        std::thread m_worker;
        std::atomic<bool> m_stop{false};
        std::mutex m_mutex; // guards the three queues below
        std::condition_variable m_wake;
        std::deque<std::string> m_commands;
        std::deque<RawCallback> m_callbacks;
        std::deque<bool> m_pipeStates;

        // Worker-owned state.
        std::uint64_t m_self = 0;
        std::uint64_t m_lobby = 0;
        std::uint64_t m_owner = 0;
        std::uint64_t m_token = 0;
        std::string m_privacy;
        bool m_joinable = true;
        std::vector<std::uint64_t> m_members;
        std::map<std::string, std::string> m_data;
        std::set<std::uint64_t> m_banned;
        std::vector<PendingCall> m_calls;
        std::vector<Avatar> m_avatars;
        std::optional<PendingJoin> m_pendingJoin;
        std::map<std::uint64_t, Conn> m_conns;
        steamabi::HSteamListenSocket m_listen = 0;
        Clock::time_point m_nextLobbyPoll{};
        Clock::time_point m_nextConnect{};
        std::map<std::uint64_t, Clock::time_point> m_presenceRequested;
        std::string m_status;

        std::map<std::pair<std::uint64_t, std::uint32_t>, Xfer> m_outgoing; // (peer, transfer)
        std::set<std::pair<std::uint64_t, std::uint32_t>> m_incoming;
        std::map<std::uint64_t, UgcWatch> m_ugcWatch;
        std::vector<UgcCall> m_ugcCalls;

        struct LastLobby
        {
            std::uint64_t lobby = 0, host = 0;
            std::int64_t at = 0; // unix seconds
        };
        std::optional<LastLobby> m_last;
        bool m_sceneRunning = false;
        bool m_hideScenario = false;
        std::string m_rpState, m_rpScenario, m_rpLobby;
        Clock::time_point m_nextPresence{};
        std::map<std::uint64_t, std::map<std::uint64_t, int>> m_spectators; // host: target -> spectator -> Hz
        std::atomic<int> m_cameraRate{0};                                    // as a target: Hz requested of us
        std::uint64_t m_watching = 0;                                        // as a spectator
        int m_watchRate = 0;
        std::uint32_t m_cameraSeq = 0;
        Clock::time_point m_nextCamera{};
        std::optional<CameraFrame> m_localCamera; // guarded by m_ghostMutex
        // AimModCore pose files (pose format 1)
        Clock::time_point m_nextRequestTouch{};
        Clock::time_point m_nextMetaSend{};
        std::int64_t m_lastSelfMs = 0;
        std::string m_selfScenario, m_selfMap;
        float m_selfScale = 0;
        posefile::File m_spectate;            // spectator: rows for spectate-pose.tsv
        std::optional<std::int64_t> m_spectateOffset; // local ms - sender ms
        bool m_spectateDirty = false;
        Clock::time_point m_nextSpectateWrite{};

        std::map<std::uint64_t, Conn> m_direct; // lobby-less spectate links
        SpectatePrivacy m_spectatePrivacy = SpectatePrivacy::Friends;
        std::atomic<int> m_directWatchers{0};
        bool m_watchingDirect = false;
        Clock::time_point m_nextScore{};
        std::string m_rpSpectatable, m_rpSpectators, m_rpSpectating;

        Options m_options;
        std::mutex m_ghostMutex; // guards the four members below
        std::optional<Pose> m_localPose;
        std::map<std::uint64_t, GhostPeer> m_ghosts;
        std::string m_scene;
        std::map<std::string, std::string> m_dataSnapshot;
        std::set<std::uint64_t> m_ghostSeen;
        std::uint32_t m_poseSeq = 0;
        Clock::time_point m_nextPose{};
        Clock::time_point m_nextScene{};
        std::map<std::uint64_t, Clock::time_point> m_nextPingLog;
    };
} // namespace bridge
