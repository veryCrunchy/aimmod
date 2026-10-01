#include "Bridge.hpp"

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <random>

namespace bridge
{
    using namespace std::chrono_literals;
    using steamabi::HSteamNetConnection;

    namespace
    {
        constexpr int MaxMembersLimit = 16;
        constexpr std::size_t MaxFriends = 500;
        constexpr auto HandshakeTimeout = 10s;
        constexpr auto PingInterval = 2s;
        constexpr auto LobbyPollInterval = 250ms;
        constexpr auto ReconnectInterval = 3s;
        constexpr auto CallTimeout = 20s;
        // Keys the bridge owns; the service can read but not set them.
        constexpr const char* KeyVersion = "aimmod.v";
        constexpr const char* KeyToken = "aimmod.token";
        constexpr const char* KeyBridge = "aimmod.bridge";

        std::string Id(std::uint64_t id) { return std::to_string(id); }

        std::string Hex(std::uint64_t value)
        {
            char buf[24];
            std::snprintf(buf, sizeof(buf), "%016llx", static_cast<unsigned long long>(value));
            return buf;
        }
        std::optional<std::uint64_t> FromHex(const std::string& text)
        {
            if (text.size() != 16) return std::nullopt;
            std::uint64_t v = 0;
            for (const char c : text)
            {
                v <<= 4;
                if (c >= '0' && c <= '9') v |= static_cast<std::uint64_t>(c - '0');
                else if (c >= 'a' && c <= 'f') v |= static_cast<std::uint64_t>(c - 'a' + 10);
                else return std::nullopt;
            }
            return v;
        }
        std::uint64_t Random64()
        {
            std::random_device device;
            return (static_cast<std::uint64_t>(device()) << 32) ^ device() ^ (static_cast<std::uint64_t>(device()) << 16);
        }

        const char* PersonaState(int state)
        {
            switch (state)
            {
            case 0: return "offline";
            case 1: return "online";
            case 2: return "busy";
            case 3: return "away";
            case 4: return "snooze";
            case 5:
            case 6: return "online";
            default: return "offline";
            }
        }

        steamabi::SteamNetworkingConfigValue_t RelayOnly()
        {
            steamabi::SteamNetworkingConfigValue_t v{};
            v.m_eValue = steamabi::k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable;
            v.m_eDataType = steamabi::k_ESteamNetworkingConfig_Int32;
            v.m_val.m_int32 = steamabi::k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_Disable;
            return v;
        }

        // Allowed top-level fields per command (besides v, cmd, id).
        const std::map<std::string, std::set<std::string>, std::less<>> CommandFields = {
            {"hello", {}},
            {"lobby.create", {"privacy", "maxMembers", "data"}},
            {"lobby.join", {"lobby"}},
            {"lobby.leave", {}},
            {"lobby.setData", {"data"}},
            {"lobby.setJoinable", {"joinable"}},
            {"lobby.invite", {"friend"}},
            {"lobby.kick", {"peer"}},
            {"lobby.transfer", {"peer"}},
            {"join.dismiss", {}},
            {"friends.list", {}},
            {"avatar.get", {"peer"}},
            {"presence.set", {"status"}},
            {"p2p.send", {"peer", "reliable", "data"}},
            {"p2p.close", {"peer"}},
            {"ugc.state", {"item"}},
            {"ugc.subscribe", {"item"}},
            {"ugc.download", {"item", "highPriority"}},
            {"xfer.chunk", {"peer", "transfer", "index", "data"}},
            {"xfer.cancel", {"peer", "transfer", "reason"}},
        };

        const char* XferReason(std::uint16_t code)
        {
            switch (code)
            {
            case 0: return "complete";
            case 1: return "cancel";
            default: return "error";
            }
        }
    } // namespace

    // Passive listener: copies the callback and wakes the worker.
    class Bridge::Listener final : public steamabi::CCallbackBase
    {
    public:
        Listener(Bridge& owner, int id, int size) : m_owner(owner), m_size(size) { m_iCallback = id; }
        void Run(void* param) override
        {
            RawCallback cb{m_iCallback, {}};
            cb.bytes.assign(static_cast<const std::uint8_t*>(param), static_cast<const std::uint8_t*>(param) + m_size);
            {
                std::lock_guard lock(m_owner.m_mutex);
                if (m_owner.m_callbacks.size() < 256) m_owner.m_callbacks.push_back(std::move(cb));
            }
            m_owner.m_wake.notify_one();
        }
        void Run(void* param, bool, steamabi::SteamAPICall_t) override { Run(param); }
        int GetCallbackSizeBytes() override { return m_size; }
        int Id() const { return m_iCallback; }

    private:
        Bridge& m_owner;
        int m_size;
    };

    Bridge::Bridge(LogFn log, GameThreadFn onGameThread) : m_log(std::move(log)), m_onGameThread(std::move(onGameThread)) {}

    Bridge::~Bridge() { Stop(); }

    bool Bridge::Start(HMODULE steamApi, std::wstring commandLine)
    {
        std::string missing;
        if (!m_steam.Resolve(steamApi, missing))
        {
            m_log("Steam unavailable (" + missing + "); AimModSteam is disabled");
            return false;
        }
        m_self = m_steam.User_GetSteamID(m_steam.user);
        if (!IsIndividualId(m_self) || m_steam.Utils_GetAppID(m_steam.utils) != KovaaksAppId)
        {
            m_log("unexpected Steam identity or AppID; AimModSteam is disabled");
            return false;
        }
        m_log("Steam ready as " + Redact(m_self));

        // Warm up relay access and the P2P cert (contacts Valve only).
        m_steam.netUtils->CheckPingDataUpToDate(1e10f);
        m_steam.sockets->InitAuthentication();

        if (auto launch = ParseLaunchCommandLine(commandLine))
        {
            m_pendingJoin = PendingJoin{launch->source, launch->target.lobby, 0, launch->target.version};
            m_log("launched with a join request for lobby " + Redact(launch->target.lobby));
        }

        m_listeners = {new Listener(*this, CbGameLobbyJoinRequested, sizeof(GameLobbyJoinRequested_t)),
                       new Listener(*this, CbGameRichPresenceJoinRequested, sizeof(GameRichPresenceJoinRequested_t)),
                       new Listener(*this, CbConnectionStatusChanged, sizeof(steamabi::SteamNetConnectionStatusChangedCallback_t)),
                       new Listener(*this, CbLobbyInvite, sizeof(LobbyInvite_t)),
                       new Listener(*this, CbItemInstalled, sizeof(ItemInstalled_t)),
                       new Listener(*this, CbDownloadItemResult, sizeof(DownloadItemResult_t))};
        m_onGameThread([this] {
            for (auto* l : m_listeners) m_steam.RegisterCallback(l, l->Id());
        });
        m_listenersRegistered = true;

        m_steam.F_SetRichPresence(m_steam.friends, "aimmod", std::to_string(ContractVersion).c_str());

        m_pipe = std::make_unique<PipeServer>(
            PipeName,
            [this](std::string&& message) {
                {
                    std::lock_guard lock(m_mutex);
                    if (m_commands.size() < 512) m_commands.push_back(std::move(message));
                }
                m_wake.notify_one();
            },
            [this](bool connected) {
                {
                    std::lock_guard lock(m_mutex);
                    m_pipeStates.push_back(connected);
                }
                m_wake.notify_one();
            },
            m_log);
        m_pipe->Start();
        m_worker = std::thread([this] { Run(); });
        return true;
    }

    void Bridge::Stop()
    {
        if (m_stop.exchange(true)) return;
        m_wake.notify_all();
        if (m_worker.joinable()) m_worker.join(); // the worker leaves the lobby on exit
        if (m_pipe) m_pipe->Stop();
        if (m_listenersRegistered && m_steam.Initialised())
        {
            m_onGameThread([this] {
                for (auto* l : m_listeners) m_steam.UnregisterCallback(l);
            });
            m_listenersRegistered = false;
            // Listener objects are leaked on purpose: steam_api must never be
            // left pointing at freed memory.
        }
        else if (!m_listenersRegistered)
            for (auto* l : m_listeners) delete l;
        m_listeners.clear();
    }

    // --- worker -----------------------------------------------------------

    void Bridge::Run()
    {
        while (!m_stop.load())
        {
            {
                std::unique_lock lock(m_mutex);
                m_wake.wait_for(lock, 5ms, [this] { return m_stop.load() || !m_commands.empty() || !m_callbacks.empty() || !m_pipeStates.empty(); });
            }
            Tick();
        }
        // Shutdown: leave cleanly, clear what we set (skip if the game already shut Steam down).
        if (!m_steam.Initialised())
        {
            m_log("stopped after Steam shut down; nothing to clean up");
            return;
        }
        if (m_lobby) LeaveLobby("shutdown");
        CloseListen();
        m_steam.F_ClearRichPresence(m_steam.friends);
        m_log("stopped; lobby left and rich presence cleared");
    }

    void Bridge::Tick()
    {
        if (!m_steam.Initialised()) return; // the game shut Steam down
        std::deque<std::string> commands;
        std::deque<RawCallback> callbacks;
        std::deque<bool> states;
        {
            std::lock_guard lock(m_mutex);
            commands.swap(m_commands);
            callbacks.swap(m_callbacks);
            states.swap(m_pipeStates);
        }
        for (const bool connected : states)
            if (!connected) m_avatars.clear();
        for (const auto& cb : callbacks) HandleCallback(cb);
        for (const auto& text : commands) HandleCommand(text);
        PollCalls();
        PollUgc();
        if (m_options.ghostDemo && m_pendingJoin && !m_lobby && m_calls.empty() && !(m_pipe && m_pipe->Connected()))
            AutoJoin(m_pendingJoin->lobby, m_pendingJoin->source.c_str());
        if (m_lobby)
        {
            PollLobby(false);
            PollConnections();
            ReceiveAll();
        }
        if (m_options.ghostDemo) GhostTick();
        // Avatars that were still loading.
        const auto now = Clock::now();
        auto avatars = std::move(m_avatars);
        m_avatars.clear();
        for (const auto& a : avatars)
        {
            if (now >= a.deadline) EmitAvatar(a.peer, true);
            else
            {
                const int handle = m_steam.F_GetSmallFriendAvatar(m_steam.friends, a.peer);
                if (handle > 0) EmitAvatar(a.peer, true);
                else m_avatars.push_back(a);
            }
        }
    }

    void Bridge::HandleCallback(const RawCallback& cb)
    {
        if (cb.id == CbGameLobbyJoinRequested && cb.bytes.size() == sizeof(GameLobbyJoinRequested_t))
        {
            GameLobbyJoinRequested_t e{};
            std::memcpy(&e, cb.bytes.data(), sizeof(e));
            if (!IsLobbyId(e.m_steamIDLobby)) return;
            m_pendingJoin = PendingJoin{"steam-invite", e.m_steamIDLobby, e.m_steamIDFriend, JoinStringVersion};
            m_log("join request (Steam invite) for lobby " + Redact(e.m_steamIDLobby));
            EmitJoinRequest(*m_pendingJoin);
            if (m_options.ghostDemo && !(m_pipe && m_pipe->Connected())) AutoJoin(e.m_steamIDLobby, "Steam invite");
        }
        else if (cb.id == CbGameRichPresenceJoinRequested && cb.bytes.size() == sizeof(GameRichPresenceJoinRequested_t))
        {
            GameRichPresenceJoinRequested_t e{};
            std::memcpy(&e, cb.bytes.data(), sizeof(e));
            e.m_rgchConnect[sizeof(e.m_rgchConnect) - 1] = '\0';
            const auto target = ParseJoinString(e.m_rgchConnect);
            if (!target) return; // not ours
            m_pendingJoin = PendingJoin{"rich-presence", target->lobby, e.m_steamIDFriend, target->version};
            m_log("join request (Join Game) for lobby " + Redact(target->lobby));
            EmitJoinRequest(*m_pendingJoin);
            if (m_options.ghostDemo && !(m_pipe && m_pipe->Connected())) AutoJoin(target->lobby, "Join Game");
        }
        else if (cb.id == CbLobbyInvite && cb.bytes.size() == sizeof(LobbyInvite_t))
        {
            // An invite arrived in Steam chat; the user has NOT accepted it yet.
            LobbyInvite_t e{};
            std::memcpy(&e, cb.bytes.data(), sizeof(e));
            const bool kovaaks = (e.m_ulGameID & 0xFFFFFFull) == KovaaksAppId && ((e.m_ulGameID >> 24) & 0xFF) == 0;
            if (!kovaaks || !IsLobbyId(e.m_ulSteamIDLobby) || !IsIndividualId(e.m_ulSteamIDUser) || e.m_ulSteamIDLobby == m_lobby) return;
            m_log("invite received for lobby " + Redact(e.m_ulSteamIDLobby) + " from " + Redact(e.m_ulSteamIDUser));
            Emit(json::Object()
                     .Int("v", ContractVersion)
                     .Str("ev", "invite.received")
                     .Str("from", Id(e.m_ulSteamIDUser))
                     .Str("fromName", Name(e.m_ulSteamIDUser))
                     .Str("lobby", Id(e.m_ulSteamIDLobby))
                     .Done());
        }
        else if (cb.id == CbItemInstalled && cb.bytes.size() == sizeof(ItemInstalled_t))
        {
            ItemInstalled_t e{};
            std::memcpy(&e, cb.bytes.data(), sizeof(e));
            if (e.m_unAppID != KovaaksAppId || !m_ugcWatch.count(e.m_nPublishedFileId)) return; // only items we asked for
            m_ugcWatch.erase(e.m_nPublishedFileId);
            std::string folder;
            InstallFolder(e.m_nPublishedFileId, folder);
            m_log("workshop item " + Id(e.m_nPublishedFileId) + " installed");
            Emit(json::Object().Int("v", ContractVersion).Str("ev", "ugc.installed").Str("item", Id(e.m_nPublishedFileId)).Str("folder", folder).Done());
        }
        else if (cb.id == CbDownloadItemResult && cb.bytes.size() == sizeof(DownloadItemResult_t))
        {
            DownloadItemResult_t e{};
            std::memcpy(&e, cb.bytes.data(), sizeof(e));
            if (e.m_unAppID != KovaaksAppId || !m_ugcWatch.count(e.m_nPublishedFileId)) return;
            if (e.m_eResult != 1)
            {
                m_ugcWatch.erase(e.m_nPublishedFileId);
                m_log("workshop item " + Id(e.m_nPublishedFileId) + " download failed (result " + std::to_string(e.m_eResult) + ")");
                Emit(json::Object().Int("v", ContractVersion).Str("ev", "ugc.error").Str("item", Id(e.m_nPublishedFileId)).Int("result", e.m_eResult).Str("message", "Steam could not download the item.").Done());
            }
            else m_ugcWatch[e.m_nPublishedFileId].next = Clock::now(); // installed state follows
        }
        else if (cb.id == CbConnectionStatusChanged && cb.bytes.size() == sizeof(steamabi::SteamNetConnectionStatusChangedCallback_t))
        {
            steamabi::SteamNetConnectionStatusChangedCallback_t e{};
            std::memcpy(&e, cb.bytes.data(), sizeof(e));
            const std::uint64_t remote = e.m_info.m_identityRemote.m_eType == 16 ? e.m_info.m_identityRemote.m_steamID64 : 0;
            // Incoming connection on our listen socket.
            if (m_listen && e.m_info.m_hListenSocket == m_listen && e.m_info.m_eState == steamabi::k_EConnState_Connecting && !FindConnByHandle(e.m_hConn))
            {
                if (!IsHost() || !IsIndividualId(remote) || m_banned.count(remote) || FindConn(remote))
                {
                    m_steam.sockets->CloseConnection(e.m_hConn, 5001, "aimmod: refused", false);
                    return;
                }
                if (m_steam.sockets->AcceptConnection(e.m_hConn) != 1) return;
                Conn conn;
                conn.peer = remote;
                conn.handle = e.m_hConn;
                conn.state = ConnState::Handshaking;
                conn.deadline = Clock::now() + HandshakeTimeout;
                conn.nextPing = Clock::now() + PingInterval;
                m_conns[remote] = conn;
                return;
            }
            // Our own connections closing.
            if (e.m_info.m_eState == steamabi::k_EConnState_ClosedByPeer || e.m_info.m_eState == steamabi::k_EConnState_ProblemDetectedLocally)
                if (Conn* conn = FindConnByHandle(e.m_hConn)) CloseConn(conn->peer, false, "closed");
        }
    }

    // --- commands ---------------------------------------------------------

    void Bridge::HandleCommand(const std::string& text)
    {
        const auto parsed = json::Parse(text, json::Limits{MaxPipeFrame, 6, 32 * 1024, 64});
        if (!parsed || !parsed->IsObject())
        {
            Error("invalid", "Command is not a JSON object.");
            return;
        }
        const json::Value& c = *parsed;
        const auto id = c.Get("id") ? c.Int("id") : std::optional<std::int64_t>(-1);
        const auto v = c.Int("v");
        const auto cmd = c.Str("cmd", 32);
        if (!id || *id < -1 || *id > 0x7fffffff)
        {
            Error("invalid", "id must be a non-negative integer.");
            return;
        }
        if (!v || *v != ContractVersion)
        {
            Result(*id, false, "version", "This bridge speaks contract version " + std::to_string(ContractVersion) + ".");
            return;
        }
        const auto spec = cmd ? CommandFields.find(*cmd) : CommandFields.end();
        if (spec == CommandFields.end())
        {
            Result(*id, false, "unknown", "Unknown command.");
            return;
        }
        for (const auto& [key, _] : c.object)
            if (key != "v" && key != "cmd" && key != "id" && !spec->second.count(key))
            {
                Result(*id, false, "invalid", "Unknown field: " + key.substr(0, 32));
                return;
            }
        const std::string& name = *cmd;
        auto peerArg = [&](const char* key) -> std::optional<std::uint64_t> {
            const auto s = c.Str(key, 20);
            if (!s) return std::nullopt;
            const auto p = ParseId(*s);
            return p && IsIndividualId(*p) ? p : std::nullopt;
        };
        auto requireLobby = [&]() {
            if (m_lobby) return true;
            Result(*id, false, "no-lobby", "Not in a lobby.");
            return false;
        };
        auto requireHost = [&]() {
            if (!requireLobby()) return false;
            if (IsHost()) return true;
            Result(*id, false, "not-host", "Only the lobby host can do that.");
            return false;
        };

        if (name == "hello")
        {
            EmitReady();
            if (m_lobby) EmitLobby();
            Result(*id, true);
        }
        else if (name == "lobby.create")
        {
            if (m_lobby || !m_calls.empty())
            {
                Result(*id, false, "busy", "Leave the current lobby first.");
                return;
            }
            const auto privacy = c.Str("privacy", 16).value_or("friends");
            if (privacy != "friends" && privacy != "invite")
            {
                Result(*id, false, "invalid", "privacy must be friends or invite (public lobbies go through the Hub).");
                return;
            }
            const auto max = c.Int("maxMembers").value_or(4);
            if (max < 2 || max > MaxMembersLimit)
            {
                Result(*id, false, "invalid", "maxMembers must be 2 to 16.");
                return;
            }
            PendingCall call{PendingCall::Kind::Create};
            call.commandId = *id;
            call.privacy = privacy;
            call.maxMembers = static_cast<int>(max);
            if (const json::Value* data = c.Get("data"))
            {
                if (!data->IsObject() || data->object.size() > MaxServiceLobbyKeys)
                {
                    Result(*id, false, "invalid", "data must be an object of aimmod.* strings.");
                    return;
                }
                for (const auto& [k, value] : data->object)
                {
                    if (!ValidLobbyKey(k) || k == KeyVersion || k == KeyToken || k == KeyBridge || value.type != json::Value::Type::String ||
                        value.string.size() > MaxLobbyValue)
                    {
                        Result(*id, false, "invalid", "Bad lobby data key or value: " + k.substr(0, 48));
                        return;
                    }
                    call.data[k] = value.string;
                }
            }
            call.call = m_steam.MM_CreateLobby(m_steam.mm, privacy == "friends" ? LobbyFriendsOnly : LobbyPrivate, call.maxMembers);
            call.deadline = Clock::now() + CallTimeout;
            m_calls.push_back(std::move(call));
        }
        else if (name == "lobby.join")
        {
            const auto s = c.Str("lobby", 20);
            const auto lobby = s ? ParseId(*s) : std::nullopt;
            if (!lobby || !IsLobbyId(*lobby))
            {
                Result(*id, false, "invalid", "lobby must be a Steam lobby id.");
                return;
            }
            if (m_lobby == *lobby)
            {
                Result(*id, true);
                return;
            }
            if (m_lobby || !m_calls.empty())
            {
                Result(*id, false, "busy", "Leave the current lobby first.");
                return;
            }
            PendingCall call{PendingCall::Kind::Join};
            call.commandId = *id;
            call.lobby = *lobby;
            call.call = m_steam.MM_JoinLobby(m_steam.mm, *lobby);
            call.deadline = Clock::now() + CallTimeout;
            m_calls.push_back(std::move(call));
            if (m_pendingJoin && m_pendingJoin->lobby == *lobby) m_pendingJoin.reset();
        }
        else if (name == "lobby.leave")
        {
            if (m_lobby) LeaveLobby("left");
            Result(*id, true);
        }
        else if (name == "lobby.setData")
        {
            if (!requireHost()) return;
            const json::Value* data = c.Get("data");
            if (!data || !data->IsObject() || data->object.empty() || data->object.size() > MaxServiceLobbyKeys)
            {
                Result(*id, false, "invalid", "data must be a non-empty object.");
                return;
            }
            for (const auto& [k, value] : data->object)
                if (!ValidLobbyKey(k) || k == KeyVersion || k == KeyToken || k == KeyBridge ||
                    !(value.type == json::Value::Type::Null || (value.type == json::Value::Type::String && value.string.size() <= MaxLobbyValue)))
                {
                    Result(*id, false, "invalid", "Bad lobby data key or value: " + k.substr(0, 48));
                    return;
                }
            std::size_t serviceKeys = 0;
            for (const auto& [k, _] : m_data)
                if (k != KeyVersion && k != KeyToken && k != KeyBridge) ++serviceKeys;
            for (const auto& [k, value] : data->object)
                if (value.type == json::Value::Type::String && !m_data.count(k)) ++serviceKeys;
            if (serviceKeys > MaxServiceLobbyKeys)
            {
                Result(*id, false, "invalid", "Too many lobby data keys.");
                return;
            }
            for (const auto& [k, value] : data->object)
            {
                if (value.type == json::Value::Type::Null) m_steam.MM_DeleteLobbyData(m_steam.mm, m_lobby, k.c_str());
                else m_steam.MM_SetLobbyData(m_steam.mm, m_lobby, k.c_str(), value.string.c_str());
            }
            PollLobby(true);
            Result(*id, true);
        }
        else if (name == "lobby.setJoinable")
        {
            if (!requireHost()) return;
            const auto joinable = c.Bool("joinable");
            if (!joinable)
            {
                Result(*id, false, "invalid", "joinable must be true or false.");
                return;
            }
            m_joinable = *joinable;
            m_steam.MM_SetLobbyJoinable(m_steam.mm, m_lobby, m_joinable);
            UpdatePresence();
            EmitLobby();
            Result(*id, true);
        }
        else if (name == "lobby.invite")
        {
            if (!requireLobby()) return;
            if (c.Get("friend"))
            {
                const auto peer = peerArg("friend");
                if (!peer || *peer == m_self)
                {
                    Result(*id, false, "invalid", "friend must be a SteamID64.");
                    return;
                }
                const bool ok = m_steam.MM_InviteUserToLobby(m_steam.mm, m_lobby, *peer);
                Result(*id, ok, ok ? nullptr : "steam", ok ? std::string() : "Steam refused the invite.");
            }
            else
            {
                m_steam.F_ActivateGameOverlayInviteDialog(m_steam.friends, m_lobby);
                Result(*id, true);
            }
        }
        else if (name == "lobby.kick")
        {
            if (!requireHost()) return;
            const auto peer = peerArg("peer");
            if (!peer || *peer == m_self)
            {
                Result(*id, false, "invalid", "peer must be another member.");
                return;
            }
            m_banned.insert(*peer);
            if (Conn* conn = FindConn(*peer))
            {
                WireMessage kick{WireType::Kick};
                kick.lobby = m_lobby;
                SendWire(*conn, kick, true);
                CloseConn(*peer, false, "kicked", true); // linger so the kick is delivered
            }
            m_log("kicked " + Redact(*peer));
            Result(*id, true);
        }
        else if (name == "lobby.transfer")
        {
            if (!requireHost()) return;
            const auto peer = peerArg("peer");
            if (!peer || *peer == m_self || !IsMember(*peer))
            {
                Result(*id, false, "invalid", "peer must be another member.");
                return;
            }
            const bool ok = m_steam.MM_SetLobbyOwner(m_steam.mm, m_lobby, *peer);
            Result(*id, ok, ok ? nullptr : "steam", ok ? std::string() : "Steam refused the host transfer.");
            PollLobby(true);
        }
        else if (name == "join.dismiss")
        {
            m_pendingJoin.reset();
            Result(*id, true);
        }
        else if (name == "friends.list")
        {
            EmitFriends();
            Result(*id, true);
        }
        else if (name == "avatar.get")
        {
            const auto peer = peerArg("peer");
            if (!peer)
            {
                Result(*id, false, "invalid", "peer must be a SteamID64.");
                return;
            }
            Result(*id, true);
            const int handle = m_steam.F_GetSmallFriendAvatar(m_steam.friends, *peer);
            if (handle > 0) EmitAvatar(*peer, true);
            else
            {
                m_steam.F_RequestUserInformation(m_steam.friends, *peer, false);
                if (m_avatars.size() < 64) m_avatars.push_back({*peer, Clock::now() + 5s});
            }
        }
        else if (name == "presence.set")
        {
            const auto status = c.Str("status", 64);
            if (!status)
            {
                Result(*id, false, "invalid", "status must be a string of at most 64 bytes.");
                return;
            }
            m_status = *status;
            m_steam.F_SetRichPresence(m_steam.friends, "status", m_status.c_str());
            Result(*id, true);
        }
        else if (name == "p2p.send")
        {
            const auto peer = peerArg("peer");
            const auto reliable = c.Bool("reliable").value_or(true);
            const auto data = c.Str("data", (MaxPayload + 2) / 3 * 4);
            Conn* conn = peer ? FindConn(*peer) : nullptr;
            if (!conn || conn->state != ConnState::Ready)
            {
                Result(*id, false, "not-connected", "No connection to that peer.");
                return;
            }
            const auto bytes = data ? Base64Decode(*data, MaxPayload) : std::nullopt;
            if (!bytes || bytes->empty())
            {
                Result(*id, false, "invalid", "data must be base64 of 1 to 16384 bytes.");
                return;
            }
            WireMessage m{WireType::Data};
            m.payload = std::move(*bytes);
            const bool ok = SendWire(*conn, m, reliable);
            if (*id >= 0) Result(*id, ok, ok ? nullptr : "send-failed", ok ? std::string() : "Steam did not accept the message.");
        }
        else if (name == "ugc.state" || name == "ugc.subscribe" || name == "ugc.download")
        {
            const auto s = c.Str("item", 20);
            const auto item = s ? ParseId(*s) : std::nullopt;
            if (!item)
            {
                Result(*id, false, "invalid", "item must be a Workshop item id.");
                return;
            }
            if (!m_steam.ugc)
            {
                Result(*id, false, "unavailable", "Steam Workshop is unavailable.");
                return;
            }
            if (name == "ugc.state")
            {
                EmitUgcState(*item);
                Result(*id, true);
            }
            else if (name == "ugc.subscribe")
            {
                if (m_ugcCalls.size() >= 16)
                {
                    Result(*id, false, "busy", "Too many Workshop requests at once.");
                    return;
                }
                UgcCall call;
                call.item = *item;
                call.commandId = *id;
                call.call = m_steam.UGC_SubscribeItem(m_steam.ugc, *item);
                call.deadline = Clock::now() + 30s;
                m_ugcCalls.push_back(call);
            }
            else
            {
                const bool high = c.Bool("highPriority").value_or(true);
                if (m_ugcWatch.size() >= 16 && !m_ugcWatch.count(*item))
                {
                    Result(*id, false, "busy", "Too many Workshop downloads at once.");
                    return;
                }
                if (!m_steam.UGC_DownloadItem(m_steam.ugc, *item, high))
                {
                    Result(*id, false, "steam", "Steam refused the download (bad item id, or Steam is offline).");
                    return;
                }
                auto& watch = m_ugcWatch[*item];
                watch.next = Clock::now();
                watch.deadline = Clock::now() + 15min;
                m_log("workshop item " + Id(*item) + " download requested");
                Result(*id, true);
            }
        }
        else if (name == "xfer.chunk")
        {
            const auto peer = peerArg("peer");
            const auto transfer = c.Int("transfer");
            const auto index = c.Int("index");
            Conn* conn = peer ? FindConn(*peer) : nullptr;
            if (!conn || conn->state != ConnState::Ready)
            {
                Result(*id, false, "not-connected", "No connection to that peer.");
                return;
            }
            if (!transfer || *transfer < 1 || *transfer > 0x7fffffff || !index || *index < 0 || *index > 0x7fffffff)
            {
                Result(*id, false, "invalid", "transfer must be 1..2^31-1 and index 0..2^31-1.");
                return;
            }
            const auto data = c.Str("data", (MaxChunk + 2) / 3 * 4);
            auto bytes = data ? Base64Decode(*data, MaxChunk) : std::nullopt;
            if (!bytes || bytes->empty())
            {
                Result(*id, false, "invalid", "data must be base64 of 1 to 32768 bytes.");
                return;
            }
            const auto key = std::make_pair(*peer, static_cast<std::uint32_t>(*transfer));
            if (!m_outgoing.count(key))
            {
                std::size_t open = 0;
                for (const auto& [k, _] : m_outgoing)
                    if (k.first == *peer) ++open;
                if (open >= MaxTransfersPerPeer)
                {
                    Result(*id, false, "busy", "Too many transfers to that peer.");
                    return;
                }
            }
            Xfer& x = m_outgoing[key];
            if (x.inflight.size() >= XferWindow && !x.inflight.count(static_cast<std::uint32_t>(*index)))
            {
                Result(*id, false, "window", "Window full; wait for xfer.ack.");
                return;
            }
            WireMessage m{WireType::Chunk};
            m.transfer = key.second;
            m.index = static_cast<std::uint32_t>(*index);
            m.payload = std::move(*bytes);
            if (!SendChunk(*conn, m))
            {
                Result(*id, false, "send-failed", "Steam did not accept the chunk.");
                return;
            }
            x.inflight.insert(m.index);
            Result(*id, true);
        }
        else if (name == "xfer.cancel")
        {
            const auto peer = peerArg("peer");
            const auto transfer = c.Int("transfer");
            const auto reason = c.Str("reason", 16).value_or("cancel");
            if (!peer || !transfer || *transfer < 1 || *transfer > 0x7fffffff || (reason != "cancel" && reason != "complete" && reason != "error"))
            {
                Result(*id, false, "invalid", "peer, transfer and reason (cancel, complete or error) are required.");
                return;
            }
            const auto key = std::make_pair(*peer, static_cast<std::uint32_t>(*transfer));
            if (Conn* conn = FindConn(*peer); conn && conn->state == ConnState::Ready)
            {
                WireMessage m{WireType::Cancel};
                m.transfer = key.second;
                m.code = static_cast<std::uint16_t>(reason == "complete" ? 0 : reason == "cancel" ? 1 : 2);
                SendWire(*conn, m, true);
            }
            m_outgoing.erase(key);
            m_incoming.erase(key);
            Result(*id, true);
        }
        else if (name == "p2p.close")
        {
            const auto peer = peerArg("peer");
            if (peer && FindConn(*peer)) CloseConn(*peer, true, "closed");
            Result(*id, true);
        }
    }

    // --- call results -----------------------------------------------------

    void Bridge::PollCalls()
    {
        auto calls = std::move(m_calls);
        m_calls.clear();
        for (auto& call : calls)
        {
            bool failed = false;
            if (call.kind == PendingCall::Kind::Create)
            {
                steamabi::LobbyCreated_t r{};
                if (!m_steam.PollCall(call.call, CbLobbyCreated, &r, sizeof(r), failed))
                {
                    if (Clock::now() < call.deadline) m_calls.push_back(std::move(call));
                    else Result(call.commandId, false, "timeout", "Steam did not create the lobby in time.");
                    continue;
                }
                if (failed || r.m_eResult != 1 || !IsLobbyId(r.m_ulSteamIDLobby))
                {
                    Result(call.commandId, false, "steam", "Steam could not create the lobby (result " + std::to_string(r.m_eResult) + ").");
                    continue;
                }
                EnterLobby(r.m_ulSteamIDLobby, true, &call);
                Result(call.commandId, true);
            }
            else
            {
                LobbyEnter_t r{};
                if (!m_steam.PollCall(call.call, CbLobbyEnter, &r, sizeof(r), failed))
                {
                    if (Clock::now() < call.deadline) m_calls.push_back(std::move(call));
                    else Result(call.commandId, false, "timeout", "Steam did not join the lobby in time.");
                    continue;
                }
                if (failed || r.m_EChatRoomEnterResponse != 1 || r.m_ulSteamIDLobby != call.lobby)
                {
                    Result(call.commandId, false, "steam", "Could not join (response " + std::to_string(r.m_EChatRoomEnterResponse) + ").");
                    continue;
                }
                // Only AimMod lobbies of our wire version.
                const char* version = m_steam.MM_GetLobbyData(m_steam.mm, call.lobby, KeyVersion);
                if (!version || std::strcmp(version, "1") != 0)
                {
                    m_steam.MM_LeaveLobby(m_steam.mm, call.lobby);
                    Result(call.commandId, false, "not-aimmod", "That lobby isn't an AimMod lobby of this version.");
                    continue;
                }
                EnterLobby(call.lobby, false, &call);
                Result(call.commandId, true);
            }
        }
    }

    // --- lobby ------------------------------------------------------------

    void Bridge::EnterLobby(std::uint64_t lobby, bool created, const PendingCall* call)
    {
        m_lobby = lobby;
        m_banned.clear();
        m_joinable = true;
        if (created)
        {
            m_privacy = call->privacy;
            m_token = Random64();
            m_steam.MM_SetLobbyData(m_steam.mm, lobby, KeyVersion, "1");
            m_steam.MM_SetLobbyData(m_steam.mm, lobby, KeyBridge, BridgeVersion);
            m_steam.MM_SetLobbyData(m_steam.mm, lobby, KeyToken, Hex(m_token).c_str());
            m_steam.MM_SetLobbyData(m_steam.mm, lobby, "aimmod.privacy", m_privacy.c_str());
            for (const auto& [k, value] : call->data) m_steam.MM_SetLobbyData(m_steam.mm, lobby, k.c_str(), value.c_str());
            m_log("created lobby " + Redact(lobby));
        }
        else
        {
            const char* token = m_steam.MM_GetLobbyData(m_steam.mm, lobby, KeyToken);
            m_token = FromHex(token ? token : "").value_or(0);
            const char* privacy = m_steam.MM_GetLobbyData(m_steam.mm, lobby, "aimmod.privacy");
            m_privacy = privacy && std::strcmp(privacy, "invite") == 0 ? "invite" : "friends";
            m_log("joined lobby " + Redact(lobby));
        }
        m_owner = 0;
        m_members.clear();
        m_data.clear();
        PollLobby(true);
    }

    void Bridge::LeaveLobby(const char* reason)
    {
        if (!m_lobby) return;
        CloseAllConns("left");
        CloseListen();
        m_steam.MM_LeaveLobby(m_steam.mm, m_lobby);
        const std::uint64_t left = m_lobby;
        m_lobby = m_owner = m_token = 0;
        m_members.clear();
        m_data.clear();
        m_banned.clear();
        {
            std::lock_guard lock(m_ghostMutex);
            m_ghosts.clear();
            m_ghostSeen.clear();
            m_dataSnapshot.clear();
        }
        UpdatePresence();
        m_log(std::string("left lobby ") + Redact(left) + " (" + reason + ")");
        Emit(json::Object().Int("v", ContractVersion).Str("ev", "lobby.left").Str("lobby", Id(left)).Str("reason", reason).Done());
    }

    bool Bridge::IsMember(std::uint64_t peer) const { return std::find(m_members.begin(), m_members.end(), peer) != m_members.end(); }

    void Bridge::PollLobby(bool force)
    {
        const auto now = Clock::now();
        if (!force && now < m_nextLobbyPoll) return;
        m_nextLobbyPoll = now + LobbyPollInterval;

        const int count = m_steam.MM_GetNumLobbyMembers(m_steam.mm, m_lobby);
        if (count <= 0)
        {
            // We are no longer in it (lobby gone or we were dropped).
            LeaveLobby("closed");
            return;
        }
        std::vector<std::uint64_t> members;
        for (int i = 0; i < count && i < 64; ++i)
        {
            const std::uint64_t m = m_steam.MM_GetLobbyMemberByIndex(m_steam.mm, m_lobby, i);
            if (IsIndividualId(m)) members.push_back(m);
        }
        std::map<std::string, std::string> data;
        const int keys = m_steam.MM_GetLobbyDataCount(m_steam.mm, m_lobby);
        for (int i = 0; i < keys && i < 64; ++i)
        {
            char key[64]{}, value[MaxLobbyValue + 1]{};
            if (!m_steam.MM_GetLobbyDataByIndex(m_steam.mm, m_lobby, i, key, sizeof(key), value, sizeof(value))) continue;
            if (ValidLobbyKey(key)) data[key] = value;
        }
        const std::uint64_t owner = m_steam.MM_GetLobbyOwner(m_steam.mm, m_lobby);

        bool changed = force;
        for (const auto m : members)
            if (!IsMember(m))
            {
                changed = true;
                m_steam.F_RequestUserInformation(m_steam.friends, m, true);
                m_log("member joined: " + Redact(m));
                if (!m_members.empty()) // the first poll after entering is covered by lobby.updated
                    Emit(json::Object().Int("v", ContractVersion).Str("ev", "member.joined").Raw("member", MemberJson(m)).Done());
            }
        for (const auto m : m_members)
            if (std::find(members.begin(), members.end(), m) == members.end())
            {
                changed = true;
                Emit(json::Object().Int("v", ContractVersion).Str("ev", "member.left").Str("peer", Id(m)).Done());
                m_log("member left: " + Redact(m));
                ForgetGhost(m);
                if (FindConn(m)) CloseConn(m, false, "left the lobby");
            }
        const bool ownerChanged = owner != m_owner;
        if (ownerChanged || data != m_data) changed = true;
        m_members = std::move(members);
        if (data != m_data)
        {
            std::lock_guard lock(m_ghostMutex);
            m_dataSnapshot = data;
        }
        m_data = std::move(data);
        m_owner = owner;
        if (ownerChanged) UpdateRole();
        if (changed)
        {
            UpdatePresence();
            EmitLobby();
        }
    }

    void Bridge::UpdateRole()
    {
        if (!m_lobby) return;
        if (IsHost())
        {
            // Drop an outgoing connection to a previous host.
            std::vector<std::uint64_t> outgoing;
            for (const auto& [peer, conn] : m_conns)
                if (conn.outgoing) outgoing.push_back(peer);
            for (const auto peer : outgoing) CloseConn(peer, true, "host changed");
            if (!m_token)
            {
                // A new host keeps the token; mint one only if none is readable.
                m_token = Random64();
                m_steam.MM_SetLobbyData(m_steam.mm, m_lobby, KeyToken, Hex(m_token).c_str());
            }
            OpenListen();
            m_log("this machine is the lobby host");
        }
        else
        {
            CloseListen();
            CloseAllConns("host changed");
            m_nextConnect = Clock::now();
        }
    }

    void Bridge::OpenListen()
    {
        if (m_listen) return;
        const auto relayOnly = RelayOnly();
        m_listen = m_steam.sockets->CreateListenSocketP2P(AimModVirtualPort, 1, &relayOnly);
        if (!m_listen) Error("p2p", "Could not open the P2P listen socket.");
    }

    void Bridge::CloseListen()
    {
        if (!m_listen) return;
        m_steam.sockets->CloseListenSocket(m_listen);
        m_listen = 0;
    }

    void Bridge::ConnectToOwner()
    {
        if (!m_lobby || IsHost() || !IsIndividualId(m_owner) || FindConn(m_owner) || !m_token) return;
        steamabi::SteamNetworkingIdentity identity{};
        identity.m_eType = 16;
        identity.m_cbSize = sizeof(std::uint64_t);
        identity.m_steamID64 = m_owner;
        const auto relayOnly = RelayOnly();
        const HSteamNetConnection handle = m_steam.sockets->ConnectP2P(identity, AimModVirtualPort, 1, &relayOnly);
        if (!handle) return;
        Conn conn;
        conn.peer = m_owner;
        conn.handle = handle;
        conn.outgoing = true;
        conn.state = ConnState::Connecting;
        conn.deadline = Clock::now() + 20s;
        conn.nextPing = Clock::now() + PingInterval;
        m_conns[m_owner] = conn;
    }

    Bridge::Conn* Bridge::FindConn(std::uint64_t peer)
    {
        const auto it = m_conns.find(peer);
        return it == m_conns.end() ? nullptr : &it->second;
    }

    Bridge::Conn* Bridge::FindConnByHandle(HSteamNetConnection handle)
    {
        for (auto& [_, conn] : m_conns)
            if (conn.handle == handle) return &conn;
        return nullptr;
    }

    void Bridge::CloseConn(std::uint64_t peer, bool bye, const char* reason, bool linger)
    {
        const auto it = m_conns.find(peer);
        if (it == m_conns.end()) return;
        Conn conn = it->second;
        m_conns.erase(it);
        CancelTransfersWith(peer, "disconnected");
        if (bye && conn.state == ConnState::Ready) SendWire(conn, WireMessage{WireType::Bye}, true);
        m_steam.sockets->CloseConnection(conn.handle, 0, "aimmod", bye || linger);
        if (conn.state == ConnState::Ready) m_log("p2p disconnected from " + Redact(peer) + " (" + reason + ")");
        if (conn.outgoing)
        {
            std::lock_guard lock(m_ghostMutex);
            m_ghosts.clear(); // every ghost came through our host link
            m_ghostSeen.clear();
        }
        else ForgetGhost(peer);
        if (conn.state == ConnState::Ready)
            Emit(json::Object().Int("v", ContractVersion).Str("ev", "p2p.disconnected").Str("peer", Id(peer)).Str("reason", reason).Done());
        if (conn.outgoing) m_nextConnect = Clock::now() + ReconnectInterval;
    }

    void Bridge::CloseAllConns(const char* reason)
    {
        std::vector<std::uint64_t> peers;
        for (const auto& [peer, _] : m_conns) peers.push_back(peer);
        for (const auto peer : peers) CloseConn(peer, true, reason);
    }

    void Bridge::PollConnections()
    {
        const auto now = Clock::now();
        if (!IsHost() && now >= m_nextConnect && !FindConn(m_owner))
        {
            m_nextConnect = now + ReconnectInterval;
            ConnectToOwner();
        }
        std::vector<std::uint64_t> drop;
        for (auto& [peer, conn] : m_conns)
        {
            if (conn.state != ConnState::Ready && now >= conn.deadline)
            {
                drop.push_back(peer);
                continue;
            }
            if (conn.state == ConnState::Connecting)
            {
                steamabi::SteamNetConnectionInfo_t info{};
                if (!m_steam.sockets->GetConnectionInfo(conn.handle, &info))
                {
                    drop.push_back(peer);
                    continue;
                }
                if (info.m_eState == steamabi::k_EConnState_Connected)
                {
                    const bool authenticated = (info.m_nFlags & (steamabi::k_nConnFlags_Unauthenticated | steamabi::k_nConnFlags_Unencrypted)) == 0;
                    if (!authenticated || info.m_identityRemote.m_steamID64 != peer)
                    {
                        drop.push_back(peer);
                        continue;
                    }
                    WireMessage hello{WireType::Hello};
                    hello.lobby = m_lobby;
                    hello.token = m_token;
                    SendWire(conn, hello, true);
                    conn.state = ConnState::Handshaking;
                    conn.deadline = now + HandshakeTimeout;
                }
                else if (info.m_eState == steamabi::k_EConnState_ClosedByPeer || info.m_eState == steamabi::k_EConnState_ProblemDetectedLocally)
                    drop.push_back(peer);
            }
            else if (conn.state == ConnState::Ready && now >= conn.nextPing)
            {
                conn.nextPing = now + PingInterval;
                WireMessage ping{WireType::Ping};
                ping.seq = ++conn.pingSeq;
                ping.time = m_steam.netUtils->GetLocalTimestamp();
                SendWire(conn, ping, false);
            }
        }
        for (const auto peer : drop) CloseConn(peer, false, "timeout");
    }

    void Bridge::ReceiveAll()
    {
        std::vector<std::uint64_t> peers;
        for (const auto& [peer, _] : m_conns) peers.push_back(peer);
        for (const auto peer : peers)
        {
            for (int batch = 0; batch < 4; ++batch)
            {
                Conn* conn = FindConn(peer);
                if (!conn) break;
                steamabi::SteamNetworkingMessage_t* messages[16]{};
                const int n = m_steam.sockets->ReceiveMessagesOnConnection(conn->handle, messages, 16);
                if (n <= 0) break;
                for (int i = 0; i < n; ++i)
                {
                    auto* msg = messages[i];
                    Conn* live = FindConn(peer);
                    if (live && msg->m_cbSize > 0)
                    {
                        const auto decoded = Decode(static_cast<const std::uint8_t*>(msg->m_pData), static_cast<std::size_t>(msg->m_cbSize));
                        if (decoded) OnWire(*live, *decoded, (msg->m_nFlags & steamabi::k_nSteamNetworkingSend_Reliable) != 0);
                        else CloseConn(peer, false, "invalid frame");
                    }
                    msg->m_pfnRelease(msg);
                }
                if (n < 16) break;
            }
        }
    }

    void Bridge::OnWire(Conn& conn, const WireMessage& m, bool reliable)
    {
        const std::uint64_t peer = conn.peer;
        if (conn.state == ConnState::Handshaking)
        {
            if (!conn.outgoing)
            {
                // Host side: Hello bound to this lobby, its token and membership.
                if (m.type != WireType::Hello) return CloseConn(peer, false, "no hello");
                WireMessage reply{WireType::Reject};
                if (m.lobby != m_lobby || m.token != m_token) reply.code = static_cast<std::uint16_t>(RejectCode::BadToken);
                else if (m_banned.count(peer)) reply.code = static_cast<std::uint16_t>(RejectCode::Banned);
                else if (!IsMember(peer))
                {
                    PollLobby(true); // membership can lag the connection by a moment
                    if (!IsMember(peer)) reply.code = static_cast<std::uint16_t>(RejectCode::NotMember);
                }
                Conn* again = FindConn(peer); // PollLobby may have closed connections
                if (!again) return;
                if (reply.code)
                {
                    SendWire(*again, reply, true);
                    return CloseConn(peer, false, "rejected", true);
                }
                WireMessage welcome{WireType::Welcome};
                welcome.lobby = m_lobby;
                SendWire(*again, welcome, true);
                m_log("p2p connected: " + Redact(peer) + " (client of this host)");
                again->state = ConnState::Ready;
                {
                    const int priorities[2] = {0, 1};
                    const std::uint16_t weights[2] = {1, 1};
                    again->lanes = m_steam.sockets->ConfigureConnectionLanes(again->handle, 2, priorities, weights) == 1;
                }
                again->nextPing = Clock::now();
                Emit(json::Object().Int("v", ContractVersion).Str("ev", "p2p.connected").Str("peer", Id(peer)).Bool("host", false).Done());
                EmitLobby();
                return;
            }
            else
            {
                // Client side: Welcome from the host for our lobby.
                if (m.type == WireType::Reject)
                {
                    Error("rejected", "The host refused the connection (code " + std::to_string(m.code) + ").");
                    if (m.code == static_cast<std::uint16_t>(RejectCode::Banned)) return LeaveLobby("kicked");
                    return CloseConn(peer, false, "rejected");
                }
                if (m.type != WireType::Welcome || m.lobby != m_lobby) return CloseConn(peer, false, "bad welcome");
            }
            conn.state = ConnState::Ready;
            conn.nextPing = Clock::now();
            {
                const int priorities[2] = {0, 1};
                const std::uint16_t weights[2] = {1, 1};
                conn.lanes = m_steam.sockets->ConfigureConnectionLanes(conn.handle, 2, priorities, weights) == 1;
            }
            m_log("p2p connected: " + Redact(peer) + " (lobby host)");
            Emit(json::Object().Int("v", ContractVersion).Str("ev", "p2p.connected").Str("peer", Id(peer)).Bool("host", conn.outgoing).Done());
            EmitLobby();
            return;
        }
        if (conn.state != ConnState::Ready) return;
        switch (m.type)
        {
        case WireType::Data:
            Emit(json::Object()
                     .Int("v", ContractVersion)
                     .Str("ev", "p2p.message")
                     .Str("peer", Id(peer))
                     .Bool("reliable", reliable)
                     .Str("data", Base64Encode(m.payload.data(), m.payload.size()))
                     .Done());
            break;
        case WireType::Ping:
        {
            WireMessage pong{WireType::Pong};
            pong.seq = m.seq;
            pong.time = m.time;
            SendWire(conn, pong, false);
            break;
        }
        case WireType::Pong:
            if (m.seq == conn.pingSeq)
            {
                const auto rttUs = m_steam.netUtils->GetLocalTimestamp() - m.time;
                if (rttUs >= 0 && rttUs < 10'000'000)
                {
                    conn.rtt = static_cast<int>(rttUs / 1000);
                    auto& nextLog = m_nextPingLog[peer];
                    if (Clock::now() >= nextLog)
                    {
                        nextLog = Clock::now() + 10s;
                        m_log("ping " + Redact(peer) + ": " + std::to_string(*conn.rtt) + " ms");
                    }
                    Emit(json::Object().Int("v", ContractVersion).Str("ev", "p2p.ping").Str("peer", Id(peer)).Int("rtt", *conn.rtt).Done());
                }
            }
            break;
        case WireType::Kick:
            // Only the host may kick, and only from our own lobby.
            if (conn.outgoing && peer == m_owner && m.lobby == m_lobby) LeaveLobby("kicked");
            break;
        case WireType::Bye: CloseConn(peer, false, "bye"); break;
        case WireType::Pose:
            if (m_options.ghostDemo) OnPose(conn, m.pose);
            break;
        case WireType::Chunk:
        {
            const auto key = std::make_pair(peer, m.transfer);
            if (!m_incoming.count(key))
            {
                std::size_t open = 0;
                for (const auto& k : m_incoming)
                    if (k.first == peer) ++open;
                if (open >= MaxTransfersPerPeer) return CloseConn(peer, false, "too many transfers");
                m_incoming.insert(key);
            }
            Emit(json::Object()
                     .Int("v", ContractVersion)
                     .Str("ev", "xfer.chunk")
                     .Str("peer", Id(peer))
                     .Int("transfer", m.transfer)
                     .Int("index", m.index)
                     .Str("data", Base64Encode(m.payload.data(), m.payload.size()))
                     .Done());
            // Acknowledge once handed to the service: the sender's window follows the receiver's pace.
            WireMessage ackMsg{WireType::ChunkAck};
            ackMsg.transfer = m.transfer;
            ackMsg.index = m.index;
            SendWire(conn, ackMsg, true);
            break;
        }
        case WireType::ChunkAck:
        {
            const auto it = m_outgoing.find(std::make_pair(peer, m.transfer));
            if (it == m_outgoing.end() || !it->second.inflight.erase(m.index)) break;
            Emit(json::Object()
                     .Int("v", ContractVersion)
                     .Str("ev", "xfer.ack")
                     .Str("peer", Id(peer))
                     .Int("transfer", m.transfer)
                     .Int("index", m.index)
                     .Int("credit", static_cast<std::int64_t>(XferWindow - it->second.inflight.size()))
                     .Done());
            break;
        }
        case WireType::Cancel:
        {
            const auto key = std::make_pair(peer, m.transfer);
            const bool known = m_outgoing.erase(key) + m_incoming.erase(key) > 0;
            if (known)
                Emit(json::Object()
                         .Int("v", ContractVersion)
                         .Str("ev", "xfer.end")
                         .Str("peer", Id(peer))
                         .Int("transfer", m.transfer)
                         .Str("reason", XferReason(m.code))
                         .Str("by", "peer")
                         .Done());
            break;
        }
        default: break; // handshake frames after the handshake are ignored
        }
    }

    bool Bridge::SendWire(Conn& conn, const WireMessage& m, bool reliable)
    {
        const auto bytes = Encode(m);
        std::int64_t number = 0;
        const int flags = reliable ? steamabi::k_nSteamNetworkingSend_Reliable : steamabi::k_nSteamNetworkingSend_UnreliableNoDelay;
        return m_steam.sockets->SendMessageToConnection(conn.handle, bytes.data(), static_cast<std::uint32_t>(bytes.size()), flags, &number) == 1;
    }

    void Bridge::UpdatePresence()
    {
        // "Join Game" from the Steam friends list only for joinable friends lobbies.
        if (m_lobby && m_joinable && m_privacy == "friends")
        {
            m_steam.F_SetRichPresence(m_steam.friends, "connect", ConnectString(m_lobby).c_str());
            m_steam.F_SetRichPresence(m_steam.friends, "steam_player_group", Id(m_lobby).c_str());
            m_steam.F_SetRichPresence(m_steam.friends, "steam_player_group_size", std::to_string(m_members.size()).c_str());
        }
        else
        {
            m_steam.F_SetRichPresence(m_steam.friends, "connect", "");
            m_steam.F_SetRichPresence(m_steam.friends, "steam_player_group", "");
            m_steam.F_SetRichPresence(m_steam.friends, "steam_player_group_size", "");
        }
    }

    // --- events -----------------------------------------------------------

    void Bridge::Emit(const std::string& text)
    {
        if (m_pipe) m_pipe->Send(text);
    }

    void Bridge::Result(std::int64_t id, bool ok, const char* code, const std::string& message)
    {
        if (!ok) m_log(std::string("command failed: ") + (code ? code : "error") + " " + message);
        if (id < 0 && ok) return;
        json::Object o;
        o.Int("v", ContractVersion).Str("ev", "result");
        if (id >= 0) o.Int("id", id);
        else o.Null("id");
        o.Bool("ok", ok);
        if (code) o.Str("code", code);
        if (!message.empty()) o.Str("message", message);
        Emit(o.Done());
    }

    void Bridge::Error(const char* code, const std::string& message)
    {
        Emit(json::Object().Int("v", ContractVersion).Str("ev", "error").Str("code", code).Str("message", message).Done());
    }

    std::string Bridge::Name(std::uint64_t peer) const
    {
        const char* name = peer == m_self ? m_steam.F_GetPersonaName(m_steam.friends) : m_steam.F_GetFriendPersonaName(m_steam.friends, peer);
        std::string out = name ? name : "";
        if (out.size() > 64) out.resize(64);
        return out;
    }

    std::string Bridge::Initials(const std::string& name)
    {
        std::string out;
        bool start = true;
        for (const char c : name)
        {
            if (c == ' ' || c == '_' || c == '-' || c == '.')
            {
                start = true;
                continue;
            }
            if (start && ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')))
            {
                out.push_back(c >= 'a' && c <= 'z' ? static_cast<char>(c - 32) : c);
                if (out.size() == 2) break;
            }
            start = false;
        }
        return out.empty() ? "?" : out;
    }

    std::string Bridge::MemberJson(std::uint64_t peer) const
    {
        const std::string name = Name(peer);
        bool connected = peer == m_self;
        if (!connected)
        {
            const auto it = m_conns.find(peer);
            connected = it != m_conns.end() && it->second.state == ConnState::Ready;
            // A client sees the host as connected through its own link; other
            // clients are reachable only through the host (star topology).
            if (!connected && !IsHost() && peer != m_owner)
            {
                const auto host = m_conns.find(m_owner);
                connected = host != m_conns.end() && host->second.state == ConnState::Ready;
            }
        }
        json::Object o;
        o.Str("peer", Id(peer)).Str("name", name).Str("initials", Initials(name)).Bool("host", peer == m_owner).Bool("self", peer == m_self).Bool("connected", connected);
        const auto it = m_conns.find(peer);
        if (it != m_conns.end() && it->second.rtt) o.Int("rtt", *it->second.rtt);
        return o.Done();
    }

    void Bridge::EmitReady()
    {
        steamabi::SteamRelayNetworkStatus_t relay{};
        const auto avail = m_steam.netUtils->GetRelayNetworkStatus(&relay);
        const std::string name = Name(m_self);
        json::Object o;
        o.Int("v", ContractVersion)
            .Str("ev", "ready")
            .Int("contract", ContractVersion)
            .Int("wire", WireVersion)
            .Str("bridge", BridgeVersion)
            .Bool("steam", true)
            .Int("appId", KovaaksAppId)
            .Raw("self", json::Object().Str("peer", Id(m_self)).Str("name", name).Str("initials", Initials(name)).Done())
            .Str("relay", steamabi::AvailabilityName(avail))
            .Raw("features", R"(["lobby","p2p","ugc","xfer"])")
            .Int("maxChunk", static_cast<std::int64_t>(MaxChunk))
            .Int("xferWindow", static_cast<std::int64_t>(XferWindow));
        Emit(o.Done());
        if (m_pendingJoin) EmitJoinRequest(*m_pendingJoin);
    }

    void Bridge::EmitJoinRequest(const PendingJoin& join)
    {
        json::Object o;
        o.Int("v", ContractVersion).Str("ev", "join.requested").Str("source", join.source).Str("lobby", Id(join.lobby)).Bool("compatible", join.version == JoinStringVersion);
        if (join.from)
        {
            const std::string name = Name(join.from);
            o.Str("from", Id(join.from)).Str("fromName", name);
        }
        else o.Null("from");
        Emit(o.Done());
    }

    void Bridge::EmitLobby()
    {
        if (!m_lobby) return;
        std::vector<std::string> members;
        for (const auto m : m_members) members.push_back(MemberJson(m));
        json::Object data;
        for (const auto& [k, value] : m_data)
            if (k != KeyToken) data.Str(k, value);
        json::Object o;
        o.Int("v", ContractVersion)
            .Str("ev", "lobby.updated")
            .Str("lobby", Id(m_lobby))
            .Str("owner", Id(m_owner))
            .Bool("isHost", IsHost())
            .Str("privacy", m_privacy)
            .Bool("joinable", m_joinable)
            .Int("maxMembers", m_steam.MM_GetLobbyMemberLimit(m_steam.mm, m_lobby))
            .Raw("members", json::Array(members))
            .Raw("data", data.Done());
        Emit(o.Done());
    }

    void Bridge::EmitFriends()
    {
        constexpr int FlagImmediate = 4;
        const int count = std::min(m_steam.F_GetFriendCount(m_steam.friends, FlagImmediate), static_cast<int>(MaxFriends));
        const auto now = Clock::now();
        std::vector<std::string> list;
        for (int i = 0; i < count; ++i)
        {
            const std::uint64_t f = m_steam.F_GetFriendByIndex(m_steam.friends, i, FlagImmediate);
            if (!IsIndividualId(f)) continue;
            FriendGameInfo_t game{};
            const bool inGame = m_steam.F_GetFriendGamePlayed(m_steam.friends, f, &game);
            const bool playing = inGame && (game.m_gameID & 0xFFFFFFull) == KovaaksAppId && ((game.m_gameID >> 24) & 0xFF) == 0;
            bool aimmod = false;
            std::uint64_t joinLobby = 0;
            if (playing)
            {
                auto& requested = m_presenceRequested[f];
                if (now >= requested)
                {
                    m_steam.F_RequestFriendRichPresence(m_steam.friends, f);
                    requested = now + 30s;
                }
                const char* flag = m_steam.F_GetFriendRichPresence(m_steam.friends, f, "aimmod");
                aimmod = flag && *flag;
                const char* connect = m_steam.F_GetFriendRichPresence(m_steam.friends, f, "connect");
                if (const auto target = ParseJoinString(connect ? connect : "")) joinLobby = target->lobby;
            }
            const std::string name = Name(f);
            json::Object o;
            o.Str("peer", Id(f))
                .Str("name", name)
                .Str("initials", Initials(name))
                .Str("state", PersonaState(m_steam.F_GetFriendPersonaState(m_steam.friends, f)))
                .Bool("playing", playing)
                .Bool("aimmod", aimmod);
            if (joinLobby) o.Str("lobby", Id(joinLobby));
            list.push_back(o.Done());
        }
        Emit(json::Object().Int("v", ContractVersion).Str("ev", "friends").Raw("friends", json::Array(list)).Done());
    }

    void Bridge::EmitAvatar(std::uint64_t peer, bool)
    {
        const int handle = m_steam.F_GetSmallFriendAvatar(m_steam.friends, peer);
        std::uint32_t w = 0, h = 0;
        if (handle <= 0 || !m_steam.Utils_GetImageSize(m_steam.utils, handle, &w, &h) || w == 0 || h == 0 || w > 64 || h > 64)
        {
            Emit(json::Object().Int("v", ContractVersion).Str("ev", "avatar").Str("peer", Id(peer)).Bool("missing", true).Done());
            return;
        }
        std::vector<std::uint8_t> rgba(static_cast<std::size_t>(w) * h * 4);
        if (!m_steam.Utils_GetImageRGBA(m_steam.utils, handle, rgba.data(), static_cast<int>(rgba.size())))
        {
            Emit(json::Object().Int("v", ContractVersion).Str("ev", "avatar").Str("peer", Id(peer)).Bool("missing", true).Done());
            return;
        }
        Emit(json::Object()
                 .Int("v", ContractVersion)
                 .Str("ev", "avatar")
                 .Str("peer", Id(peer))
                 .Int("w", w)
                 .Int("h", h)
                 .Str("rgba", Base64Encode(rgba.data(), rgba.size()))
                 .Done());
    }
    // --- Workshop ---------------------------------------------------------

    bool Bridge::InstallFolder(std::uint64_t item, std::string& folder)
    {
        std::uint64_t size = 0;
        std::uint32_t stamp = 0;
        char path[1024]{};
        if (!m_steam.UGC_GetItemInstallInfo(m_steam.ugc, item, &size, path, sizeof(path), &stamp)) return false;
        path[sizeof(path) - 1] = '\0';
        folder = path;
        return true;
    }

    void Bridge::EmitUgcState(std::uint64_t item)
    {
        const std::uint32_t state = m_steam.UGC_GetItemState(m_steam.ugc, item);
        std::uint64_t downloaded = 0, total = 0;
        m_steam.UGC_GetItemDownloadInfo(m_steam.ugc, item, &downloaded, &total);
        json::Object o;
        o.Int("v", ContractVersion)
            .Str("ev", "ugc.state")
            .Str("item", Id(item))
            .Int("state", state)
            .Bool("subscribed", (state & ItemSubscribed) != 0)
            .Bool("installed", (state & ItemInstalled) != 0)
            .Bool("downloading", (state & (ItemDownloading | ItemDownloadPending)) != 0)
            .Bool("needsUpdate", (state & ItemNeedsUpdate) != 0)
            .Int("downloaded", static_cast<std::int64_t>(downloaded))
            .Int("total", static_cast<std::int64_t>(total));
        std::string folder;
        if ((state & ItemInstalled) && InstallFolder(item, folder)) o.Str("folder", folder);
        Emit(o.Done());
    }

    void Bridge::PollUgc()
    {
        if (!m_steam.ugc) return;
        const auto now = Clock::now();
        // SubscribeItem call results.
        auto calls = std::move(m_ugcCalls);
        m_ugcCalls.clear();
        for (auto& call : calls)
        {
            RemoteStorageSubscribePublishedFileResult_t r{};
            bool failed = false;
            if (!m_steam.PollCall(call.call, CbRemoteStorageSubscribe, &r, sizeof(r), failed))
            {
                if (now < call.deadline) m_ugcCalls.push_back(call);
                else Result(call.commandId, false, "timeout", "Steam did not answer the subscription in time.");
                continue;
            }
            if (failed || r.m_eResult != 1) Result(call.commandId, false, "steam", "Steam could not subscribe (result " + std::to_string(r.m_eResult) + ").");
            else
            {
                m_log("workshop item " + Id(call.item) + " subscribed");
                Result(call.commandId, true);
                EmitUgcState(call.item);
            }
        }
        // Downloads in progress.
        for (auto it = m_ugcWatch.begin(); it != m_ugcWatch.end();)
        {
            const std::uint64_t item = it->first;
            UgcWatch& w = it->second;
            if (now < w.next)
            {
                ++it;
                continue;
            }
            w.next = now + 500ms;
            const std::uint32_t state = m_steam.UGC_GetItemState(m_steam.ugc, item);
            std::uint64_t downloaded = 0, total = 0;
            m_steam.UGC_GetItemDownloadInfo(m_steam.ugc, item, &downloaded, &total);
            if (downloaded != w.downloaded || total != w.total)
            {
                w.downloaded = downloaded;
                w.total = total;
                Emit(json::Object()
                         .Int("v", ContractVersion)
                         .Str("ev", "ugc.progress")
                         .Str("item", Id(item))
                         .Int("downloaded", static_cast<std::int64_t>(downloaded))
                         .Int("total", static_cast<std::int64_t>(total))
                         .Done());
            }
            const bool busy = (state & (ItemDownloading | ItemDownloadPending | ItemNeedsUpdate)) != 0;
            if ((state & ItemInstalled) && !busy)
            {
                std::string folder;
                InstallFolder(item, folder);
                m_log("workshop item " + Id(item) + " installed");
                Emit(json::Object().Int("v", ContractVersion).Str("ev", "ugc.installed").Str("item", Id(item)).Str("folder", folder).Done());
                it = m_ugcWatch.erase(it);
                continue;
            }
            if (now >= w.deadline)
            {
                Emit(json::Object().Int("v", ContractVersion).Str("ev", "ugc.error").Str("item", Id(item)).Int("result", 0).Str("message", "The download did not finish in time.").Done());
                it = m_ugcWatch.erase(it);
                continue;
            }
            ++it;
        }
    }

    // --- bulk transfers ---------------------------------------------------

    bool Bridge::SendChunk(Conn& conn, const WireMessage& m)
    {
        const auto bytes = Encode(m);
        if (!conn.lanes) return SendWire(conn, m, true);
        // Lane 1 has lower priority than lane 0, so match traffic overtakes bulk data.
        steamabi::SteamNetworkingMessage_t* msg = m_steam.netUtils->AllocateMessage(static_cast<int>(bytes.size()));
        if (!msg) return false;
        std::memcpy(msg->m_pData, bytes.data(), bytes.size());
        msg->m_conn = conn.handle;
        msg->m_nFlags = steamabi::k_nSteamNetworkingSend_Reliable;
        msg->m_idxLane = 1;
        std::int64_t result = 0;
        m_steam.sockets->SendMessages(1, &msg, &result); // takes ownership
        return result > 0;
    }

    void Bridge::CancelTransfersWith(std::uint64_t peer, const char* reason)
    {
        std::vector<std::uint32_t> ended;
        for (auto it = m_outgoing.begin(); it != m_outgoing.end();)
        {
            if (it->first.first == peer)
            {
                ended.push_back(it->first.second);
                it = m_outgoing.erase(it);
            }
            else ++it;
        }
        for (auto it = m_incoming.begin(); it != m_incoming.end();)
        {
            if (it->first == peer)
            {
                ended.push_back(it->second);
                it = m_incoming.erase(it);
            }
            else ++it;
        }
        for (const auto transfer : ended)
            Emit(json::Object()
                     .Int("v", ContractVersion)
                     .Str("ev", "xfer.end")
                     .Str("peer", Id(peer))
                     .Int("transfer", transfer)
                     .Str("reason", reason)
                     .Str("by", "local")
                     .Done());
    }
    // --- ghost demo -------------------------------------------------------

    double Bridge::Now() { return std::chrono::duration<double>(Clock::now().time_since_epoch()).count(); }

    void Bridge::SubmitLocalPose(const Pose& pose)
    {
        std::lock_guard lock(m_ghostMutex);
        m_localPose = pose;
    }

    std::vector<Bridge::GhostPeer> Bridge::Ghosts()
    {
        std::lock_guard lock(m_ghostMutex);
        std::vector<GhostPeer> out;
        out.reserve(m_ghosts.size());
        for (const auto& [_, g] : m_ghosts) out.push_back(g);
        return out;
    }

    std::string Bridge::LobbyValue(const std::string& key)
    {
        std::lock_guard lock(m_ghostMutex);
        const auto it = m_dataSnapshot.find(key);
        return it == m_dataSnapshot.end() ? std::string() : it->second;
    }

    std::string Bridge::LocalScene()
    {
        std::lock_guard lock(m_ghostMutex);
        return m_scene;
    }

    void Bridge::ForgetGhost(std::uint64_t peer)
    {
        std::lock_guard lock(m_ghostMutex);
        m_ghosts.erase(peer);
        m_ghostSeen.erase(peer);
    }

    void Bridge::AutoJoin(std::uint64_t lobby, const char* why)
    {
        if (m_lobby == lobby || !m_calls.empty()) return;
        if (m_lobby) LeaveLobby("switching lobby");
        m_log(std::string("ghost demo: auto-joining lobby ") + Redact(lobby) + " (" + why + ")");
        PendingCall call{PendingCall::Kind::Join};
        call.commandId = -1;
        call.lobby = lobby;
        call.call = m_steam.MM_JoinLobby(m_steam.mm, lobby);
        call.deadline = Clock::now() + CallTimeout;
        m_calls.push_back(std::move(call));
        if (m_pendingJoin && m_pendingJoin->lobby == lobby) m_pendingJoin.reset();
    }

    void Bridge::GhostTick()
    {
        const auto now = Clock::now();
        if (now >= m_nextScene)
        {
            m_nextScene = now + 1s;
            std::string scene;
            if (!m_options.scenePath.empty())
            {
                HANDLE file = CreateFileW(m_options.scenePath.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
                if (file != INVALID_HANDLE_VALUE)
                {
                    char buffer[4096];
                    DWORD got = 0;
                    if (ReadFile(file, buffer, sizeof(buffer), &got, nullptr) && got > 0 && got < sizeof(buffer))
                        if (const auto doc = json::Parse(std::string_view(buffer, got), json::Limits{4096, 4, 1024, 64}))
                            scene = doc->Str("scenario", MaxPoseScene).value_or("");
                    CloseHandle(file);
                }
            }
            std::lock_guard lock(m_ghostMutex);
            if (scene != m_scene) m_log("ghost demo: local scenario is \"" + scene + "\"");
            m_scene = scene;
        }
        if (!m_lobby || now < m_nextPose) return;
        m_nextPose = now + 33ms; // 30 Hz
        std::optional<Pose> local;
        std::string scene;
        {
            std::lock_guard lock(m_ghostMutex);
            local = m_localPose;
            scene = m_scene;
            // Drop samples nobody refreshed for 3 s.
            const double cutoff = Now() - 3.0;
            for (auto it = m_ghosts.begin(); it != m_ghosts.end();)
            {
                if (it->second.samples.empty() || it->second.samples.back().time < cutoff) it = m_ghosts.erase(it);
                else ++it;
            }
        }
        if (!local) return;
        WireMessage m{WireType::Pose};
        m.pose = *local;
        m.pose.origin = m_self;
        m.pose.seq = ++m_poseSeq;
        m.pose.scene = scene;
        for (auto& [peer, conn] : m_conns)
            if (conn.state == ConnState::Ready) SendWire(conn, m, false);
    }

    void Bridge::OnPose(Conn& conn, const Pose& pose)
    {
        if (pose.origin == m_self || !IsIndividualId(pose.origin) || !IsMember(pose.origin)) return;
        if (!conn.outgoing && pose.origin != conn.peer) return; // a client only speaks for itself
        if (conn.outgoing && conn.peer != m_owner) return;      // clients only take relays from the host
        if (IsHost())
        {
            // Star topology: forward a client's pose to the other clients.
            WireMessage relay{WireType::Pose};
            relay.pose = pose;
            for (auto& [peer, other] : m_conns)
                if (peer != conn.peer && other.state == ConnState::Ready) SendWire(other, relay, false);
        }
        std::lock_guard lock(m_ghostMutex);
        GhostPeer& g = m_ghosts[pose.origin];
        g.peer = pose.origin;
        if (!g.samples.empty())
        {
            const auto last = g.samples.back().pose.seq;
            if (pose.seq == last || static_cast<std::int32_t>(pose.seq - last) < 0) return; // duplicate or out of order
        }
        g.samples.push_back({Now(), pose});
        if (g.samples.size() > 16) g.samples.erase(g.samples.begin());
        if (m_ghostSeen.insert(pose.origin).second) m_log("ghost demo: receiving poses from " + Redact(pose.origin) + " on \"" + pose.scene + "\"");
    }} // namespace bridge
