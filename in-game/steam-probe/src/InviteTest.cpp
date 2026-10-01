// Steam invite test for the stage 3 kit (harness only). Two variants:
//
//  - Rich presence: the sender sets rich presence connect=<AimMod connect
//    string> plus status, then calls InviteUserToGame(peer, same string). The
//    receiver gets GameRichPresenceJoinRequested_t (337) when it accepts the
//    chat invite or clicks "Join Game" in the friends list.
//  - Lobby: the sender creates a friends-only, two-slot lobby (aimmod.proto
//    only) and calls InviteUserToLobby(peer). The receiver gets
//    GameLobbyJoinRequested_t (333), joins, checks aimmod.proto and leaves.
//
// The connect string starts with "-aimmodjoin=" so that when Steam launches
// a game that isn't running with the string on its command line, UE treats it
// as an ignored switch rather than a startup map URL, and OnlineSubsystemSteam
// (which only looks for "+connect", "+connect_lobby" and "SteamConnectIP=")
// ignores it.
//
// Compiled only with -DAIMMOD_PROBE_STAGE3=ON.
#include "Probe.hpp"

#include <chrono>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <thread>

namespace probe
{
#if !defined(AIMMOD_PROBE_STAGE3)
    bool RunInviteTest(const SteamApi&, const InviteOptions&, const LogFn& log, const GameThreadFn&, InviteResult* result)
    {
        if (result) result->failure = "the invite test is not compiled into this build";
        log("invite test: not compiled into this build");
        return false;
    }
#else
    namespace
    {
        using Clock = std::chrono::steady_clock;
        using namespace steamabi;

        template <typename T> T Export(HMODULE module, const char* name)
        {
            return reinterpret_cast<T>(GetProcAddress(module, name));
        }

        using PFN_SetRichPresence = bool (*)(std::intptr_t, const char*, const char*);
        using PFN_ClearRichPresence = void (*)(std::intptr_t);
        using PFN_InviteUserToGame = bool (*)(std::intptr_t, std::uint64_t, const char*);
        using PFN_GetFriendRelationship = int (*)(std::intptr_t, std::uint64_t);
        using PFN_RequestFriendRichPresence = void (*)(std::intptr_t, std::uint64_t);
        using PFN_GetFriendRichPresence = const char* (*)(std::intptr_t, std::uint64_t, const char*);
        using PFN_InviteUserToLobby = bool (*)(std::intptr_t, std::uint64_t lobby, std::uint64_t invitee);
        using PFN_JoinLobby = SteamAPICall_t (*)(std::intptr_t, std::uint64_t lobby);
        using PFN_GetNumLobbyMembers = int (*)(std::intptr_t, std::uint64_t lobby);
        using PFN_CreateLobby = PFN_ISteamMatchmaking_CreateLobby;

        constexpr int k_ELobbyTypeFriendsOnly = 1;
        constexpr int k_EFriendRelationshipFriend = 3;
        constexpr int k_iLobbyInvite = 503;
        constexpr int k_iLobbyEnter = 504;

#pragma pack(push, 8)
        struct GameLobbyJoinRequested_t
        {
            std::uint64_t m_steamIDLobby;
            std::uint64_t m_steamIDFriend;
        };
        struct GameRichPresenceJoinRequested_t
        {
            std::uint64_t m_steamIDFriend;
            char m_rgchConnect[256];
        };
        struct LobbyInvite_t
        {
            std::uint64_t m_ulSteamIDUser;
            std::uint64_t m_ulSteamIDLobby;
            std::uint64_t m_ulGameID;
        };
        struct LobbyEnter_t
        {
            std::uint64_t m_ulSteamIDLobby;
            std::uint32_t m_rgfChatPermissions;
            bool m_bLocked;
            std::uint32_t m_EChatRoomEnterResponse;
        };
#pragma pack(pop)
        static_assert(sizeof(GameLobbyJoinRequested_t) == 16);
        static_assert(sizeof(GameRichPresenceJoinRequested_t) == 264);
        static_assert(sizeof(LobbyInvite_t) == 24);
        static_assert(sizeof(LobbyEnter_t) == 24);

        struct Event
        {
            int id;
            std::uint64_t a; // friend (337) / lobby (333, 503)
            std::uint64_t b; // - / friend (333) / inviter (503)
            std::string connect;
        };

        class Listener final : public CCallbackBase
        {
        public:
            Listener(int id, int size, std::mutex& mutex, std::deque<Event>& events) : m_size(size), m_mutex(mutex), m_events(events)
            {
                m_iCallback = id;
            }
            void Run(void* param) override
            {
                Event e{m_iCallback, 0, 0, {}};
                if (m_iCallback == k_iGameRichPresenceJoinRequested)
                {
                    const auto* cb = static_cast<const GameRichPresenceJoinRequested_t*>(param);
                    e.a = cb->m_steamIDFriend;
                    e.connect.assign(cb->m_rgchConnect, strnlen(cb->m_rgchConnect, sizeof(cb->m_rgchConnect)));
                }
                else if (m_iCallback == k_iGameLobbyJoinRequested)
                {
                    const auto* cb = static_cast<const GameLobbyJoinRequested_t*>(param);
                    e.a = cb->m_steamIDLobby;
                    e.b = cb->m_steamIDFriend;
                }
                else if (m_iCallback == k_iLobbyInvite)
                {
                    const auto* cb = static_cast<const LobbyInvite_t*>(param);
                    e.a = cb->m_ulSteamIDLobby;
                    e.b = cb->m_ulSteamIDUser;
                }
                std::lock_guard lock(m_mutex);
                if (m_events.size() < 64) m_events.push_back(std::move(e));
            }
            void Run(void* param, bool, SteamAPICall_t) override { Run(param); }
            int GetCallbackSizeBytes() override { return m_size; }
            int Id() const { return m_iCallback; }

        private:
            int m_size;
            std::mutex& m_mutex;
            std::deque<Event>& m_events;
        };

        bool PollCall(const SteamApi& api, SteamAPICall_t call, int expected, void* out, int size, int timeoutMs)
        {
            auto done = Export<PFN_ISteamUtils_IsAPICallCompleted>(api.module, "SteamAPI_ISteamUtils_IsAPICallCompleted");
            auto result = Export<PFN_ISteamUtils_GetAPICallResult>(api.module, "SteamAPI_ISteamUtils_GetAPICallResult");
            const auto utils = reinterpret_cast<std::intptr_t>(api.Interface("SteamUtils009"));
            if (!done || !result || !utils || call == k_uAPICallInvalid) return false;
            const auto deadline = Clock::now() + std::chrono::milliseconds(timeoutMs);
            bool failed = false;
            while (Clock::now() < deadline)
            {
                if (done(utils, call, &failed)) return result(utils, call, out, size, expected, &failed) && !failed;
                std::this_thread::sleep_for(std::chrono::milliseconds(20));
            }
            return false;
        }

        std::string ConnectString(const std::string& code) { return "-aimmodjoin=aimmod:test:" + code; }

        bool Send(const SteamApi& api, const InviteOptions& options, const LogFn& log, InviteResult& r)
        {
            const auto friends = reinterpret_cast<std::intptr_t>(api.Interface("SteamFriends017"));
            const auto mm = reinterpret_cast<std::intptr_t>(api.Interface("SteamMatchMaking009"));
            auto relationship = Export<PFN_GetFriendRelationship>(api.module, "SteamAPI_ISteamFriends_GetFriendRelationship");
            if (!friends || !mm || !relationship)
            {
                r.failure = "friends or matchmaking interface unavailable";
                return false;
            }
            const bool isFriend = relationship(friends, options.peer) == k_EFriendRelationshipFriend;
            log(std::string("invite test: peer ") + RedactSteamId(options.peer) + (isFriend ? " is a Steam friend" : " is NOT a Steam friend"));
            if (!isFriend)
            {
                r.failure = "the other person must be on your Steam friends list";
                return false;
            }
            const auto start = Clock::now();
            const auto deadline = start + std::chrono::seconds(options.seconds);
            char line[256];

            if (options.variant == "rp")
            {
                auto setRp = Export<PFN_SetRichPresence>(api.module, "SteamAPI_ISteamFriends_SetRichPresence");
                auto clearRp = Export<PFN_ClearRichPresence>(api.module, "SteamAPI_ISteamFriends_ClearRichPresence");
                auto invite = Export<PFN_InviteUserToGame>(api.module, "SteamAPI_ISteamFriends_InviteUserToGame");
                if (!setRp || !clearRp || !invite)
                {
                    r.failure = "rich presence exports missing";
                    return false;
                }
                const std::string connect = ConnectString(options.code);
                const bool setConnect = setRp(friends, "connect", connect.c_str());
                const bool setStatus = setRp(friends, "status", "AimMod invite test (needs AimMod to join)");
                r.sent = invite(friends, options.peer, connect.c_str());
                std::snprintf(line, sizeof(line), "invite test: rich presence connect=%d status=%d; InviteUserToGame=%d", setConnect, setStatus, r.sent);
                log(line);
                log("invite test: keep this window open; the other person accepts in Steam chat or with \"Join Game\"");
                while (Clock::now() < deadline) std::this_thread::sleep_for(std::chrono::milliseconds(250));
                clearRp(friends);
                log("invite test: rich presence cleared");
                r.pass = setConnect && r.sent;
                if (!r.pass) r.failure = "SetRichPresence or InviteUserToGame returned false";
                return r.pass;
            }

            // Lobby variant.
            auto create = Export<PFN_CreateLobby>(api.module, "SteamAPI_ISteamMatchmaking_CreateLobby");
            auto setData = Export<PFN_ISteamMatchmaking_SetLobbyData>(api.module, "SteamAPI_ISteamMatchmaking_SetLobbyData");
            auto inviteLobby = Export<PFN_InviteUserToLobby>(api.module, "SteamAPI_ISteamMatchmaking_InviteUserToLobby");
            auto members = Export<PFN_GetNumLobbyMembers>(api.module, "SteamAPI_ISteamMatchmaking_GetNumLobbyMembers");
            auto leave = Export<PFN_ISteamMatchmaking_LeaveLobby>(api.module, "SteamAPI_ISteamMatchmaking_LeaveLobby");
            if (!create || !setData || !inviteLobby || !members || !leave)
            {
                r.failure = "matchmaking exports missing";
                return false;
            }
            LobbyCreated_t created{};
            const SteamAPICall_t call = create(mm, k_ELobbyTypeFriendsOnly, 2);
            if (!PollCall(api, call, k_iLobbyCreated, &created, sizeof(created), 15000) || created.m_eResult != 1 || !created.m_ulSteamIDLobby)
            {
                r.failure = "CreateLobby failed";
                return false;
            }
            const std::uint64_t lobby = created.m_ulSteamIDLobby;
            std::snprintf(line, sizeof(line), "invite test: friends-only lobby %s created in %.0f ms", RedactSteamId(lobby).c_str(), ElapsedMs(start));
            log(line);
            setData(mm, lobby, "aimmod.proto", "invite-test-1");
            r.sent = inviteLobby(mm, lobby, options.peer);
            std::snprintf(line, sizeof(line), "invite test: InviteUserToLobby=%d; waiting up to %d s for the peer to join", r.sent, options.seconds);
            log(line);
            while (r.sent && Clock::now() < deadline && !r.joined)
            {
                r.joined = members(mm, lobby) >= 2;
                if (!r.joined) std::this_thread::sleep_for(std::chrono::milliseconds(250));
            }
            if (r.joined)
            {
                std::snprintf(line, sizeof(line), "invite test: peer joined after %.0f ms", ElapsedMs(start));
                log(line);
                std::this_thread::sleep_for(std::chrono::seconds(3)); // let the peer read the lobby data
            }
            leave(mm, lobby);
            log("invite test: lobby left");
            r.pass = r.sent && r.joined;
            if (!r.pass) r.failure = r.sent ? "the peer did not join before the deadline" : "InviteUserToLobby returned false";
            return r.pass;
        }

        bool Receive(const SteamApi& api, const InviteOptions& options, const LogFn& log, const GameThreadFn& onGameThread, InviteResult& r)
        {
            if (!api.RegisterCallback || !api.UnregisterCallback)
            {
                r.failure = "callback exports missing";
                return false;
            }
            std::mutex mutex;
            std::deque<Event> events;
            // Leaked on purpose after registration (see CallbackObserver).
            auto* rp = new Listener(k_iGameRichPresenceJoinRequested, sizeof(GameRichPresenceJoinRequested_t), mutex, events);
            auto* lj = new Listener(k_iGameLobbyJoinRequested, sizeof(GameLobbyJoinRequested_t), mutex, events);
            auto* li = new Listener(k_iLobbyInvite, sizeof(LobbyInvite_t), mutex, events);
            onGameThread([&] {
                api.RegisterCallback(rp, rp->Id());
                api.RegisterCallback(lj, lj->Id());
                api.RegisterCallback(li, li->Id());
            });

            const auto friends = reinterpret_cast<std::intptr_t>(api.Interface("SteamFriends017"));
            const auto mm = reinterpret_cast<std::intptr_t>(api.Interface("SteamMatchMaking009"));
            if (options.peer && friends)
            {
                auto request = Export<PFN_RequestFriendRichPresence>(api.module, "SteamAPI_ISteamFriends_RequestFriendRichPresence");
                if (request) request(friends, options.peer);
            }
            log("invite test: waiting for an invite. Accept it in Steam chat, or use \"Join Game\" on the other person in your friends list.");

            const auto start = Clock::now();
            const auto deadline = start + std::chrono::seconds(options.seconds);
            bool checkedRp = false;
            char line[320];
            while (Clock::now() < deadline && !r.pass)
            {
                if (!checkedRp && options.peer && friends && ElapsedMs(start) > 3000)
                {
                    checkedRp = true;
                    auto get = Export<PFN_GetFriendRichPresence>(api.module, "SteamAPI_ISteamFriends_GetFriendRichPresence");
                    const char* connect = get ? get(friends, options.peer, "connect") : nullptr;
                    r.sawRichPresence = connect && std::strncmp(connect, "-aimmodjoin=aimmod:", 19) == 0;
                    log(std::string("invite test: the other person's rich presence connect key is ") +
                        (r.sawRichPresence ? "visible and is an AimMod string" : (connect && *connect ? "set but not AimMod" : "not visible (yet)")));
                }
                std::deque<Event> batch;
                {
                    std::lock_guard lock(mutex);
                    batch.swap(events);
                }
                for (const auto& e : batch)
                {
                    if (e.id == k_iLobbyInvite)
                    {
                        log("invite test: LobbyInvite_t received (a lobby invite arrived in Steam chat)");
                        continue;
                    }
                    if (e.id == k_iGameRichPresenceJoinRequested)
                    {
                        r.variant = "rp";
                        const bool aimmod = e.connect.rfind("-aimmodjoin=aimmod:", 0) == 0;
                        std::snprintf(line, sizeof(line), "invite test: GameRichPresenceJoinRequested from %s, connect string %s (%zu chars)",
                                      RedactSteamId(e.a).c_str(), aimmod ? "is AimMod" : "is NOT AimMod", e.connect.size());
                        log(line);
                        r.received = true;
                        r.pass = aimmod && (options.code.empty() || e.connect == ConnectString(options.code));
                        if (aimmod && !r.pass) r.failure = "connect string code does not match --stage3-match";
                        continue;
                    }
                    if (e.id == k_iGameLobbyJoinRequested && mm)
                    {
                        r.variant = "lobby";
                        r.received = true;
                        std::snprintf(line, sizeof(line), "invite test: GameLobbyJoinRequested for lobby %s from %s; joining",
                                      RedactSteamId(e.a).c_str(), RedactSteamId(e.b).c_str());
                        log(line);
                        auto join = Export<PFN_JoinLobby>(api.module, "SteamAPI_ISteamMatchmaking_JoinLobby");
                        auto getData = Export<PFN_ISteamMatchmaking_GetLobbyData>(api.module, "SteamAPI_ISteamMatchmaking_GetLobbyData");
                        auto leave = Export<PFN_ISteamMatchmaking_LeaveLobby>(api.module, "SteamAPI_ISteamMatchmaking_LeaveLobby");
                        if (!join || !getData || !leave) continue;
                        const auto joinStart = Clock::now();
                        LobbyEnter_t entered{};
                        const bool ok = PollCall(api, join(mm, e.a), k_iLobbyEnter, &entered, sizeof(entered), 15000) &&
                                        entered.m_EChatRoomEnterResponse == 1;
                        const char* proto = ok ? getData(mm, e.a, "aimmod.proto") : nullptr;
                        r.joined = ok;
                        const bool isAimMod = proto && std::strcmp(proto, "invite-test-1") == 0;
                        std::snprintf(line, sizeof(line), "invite test: JoinLobby %s after %.0f ms (response=%u), aimmod.proto %s", ok ? "ok" : "FAILED",
                                      ElapsedMs(joinStart), entered.m_EChatRoomEnterResponse, isAimMod ? "matches" : "missing");
                        log(line);
                        std::this_thread::sleep_for(std::chrono::seconds(2));
                        leave(mm, e.a); // always leave, joined or not
                        log("invite test: lobby left");
                        r.pass = ok && isAimMod;
                        if (!r.pass) r.failure = ok ? "joined, but the lobby is not an AimMod test lobby" : "JoinLobby failed";
                    }
                }
                if (!r.pass) std::this_thread::sleep_for(std::chrono::milliseconds(50));
            }
            onGameThread([&] {
                api.UnregisterCallback(rp);
                api.UnregisterCallback(lj);
                api.UnregisterCallback(li);
            });
            if (!r.received && r.failure.empty()) r.failure = "no invite or join request arrived before the deadline";
            return r.pass;
        }
    } // namespace

    bool RunInviteTest(const SteamApi& api, const InviteOptions& options, const LogFn& log, const GameThreadFn& onGameThread, InviteResult* out)
    {
        InviteResult local;
        InviteResult& r = out ? *out : local;
        r = InviteResult{};
        r.role = options.role;
        r.variant = options.variant;
        if (!api.Initialised())
        {
            r.failure = "SteamAPI not initialised";
            return false;
        }
        if (options.role == "send")
        {
            if (options.peer == 0 || options.code.empty() || (options.variant != "rp" && options.variant != "lobby"))
            {
                r.failure = "send needs --invite-variant rp|lobby, --stage3-peer and --stage3-match";
                log("invite test: " + r.failure);
                return false;
            }
            const bool ok = Send(api, options, log, r);
            if (!ok) log("invite test: " + r.failure);
            return ok;
        }
        const bool ok = Receive(api, options, log, onGameThread, r);
        if (!ok) log("invite test: " + r.failure);
        return ok;
    }
#endif
} // namespace probe
