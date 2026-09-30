// Stage 2: NOT read-only. Waits for the relay network and P2P cert, creates
// one private (invite-only, never listed), non-joinable, single-slot Steam
// lobby, sets and reads back one aimmod.* key, loops one chat message back to
// ourselves, leaves the lobby, and times round trips through an in-memory
// SteamNetworkingSockets socket pair. Nothing is sent to any other player: no
// invites, no rich presence, none of the game's UE session keys, no Duels.
// Every resource is released by a scope guard, including on failure.
//
// Double gated: compiled only with -DAIMMOD_PROBE_STAGE2=ON, and run only
// when config.txt sets stage2_loopback=1.
#include "Probe.hpp"

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <thread>

namespace probe
{
#if !defined(AIMMOD_PROBE_STAGE2)
    bool RunStage2(const SteamApi&, const Options& options, const LogFn& log)
    {
        if (options.stage2Loopback) log("stage 2: requested in config but not compiled into this build; skipped");
        else log("stage 2: disabled");
        return false;
    }
#else
    namespace
    {
        using Clock = std::chrono::steady_clock;

        template <typename T> T Export(HMODULE module, const char* name)
        {
            return reinterpret_cast<T>(GetProcAddress(module, name));
        }

        constexpr const char* Nonce = "aimmod-probe-loopback-v1";

        struct Matchmaking
        {
            steamabi::PFN_ISteamMatchmaking_CreateLobby createLobby = nullptr;
            steamabi::PFN_ISteamMatchmaking_SetLobbyData setData = nullptr;
            steamabi::PFN_ISteamMatchmaking_GetLobbyData getData = nullptr;
            steamabi::PFN_ISteamMatchmaking_SetLobbyJoinable setJoinable = nullptr;
            steamabi::PFN_ISteamMatchmaking_SendLobbyChatMsg sendChat = nullptr;
            steamabi::PFN_ISteamMatchmaking_GetLobbyChatEntry getChat = nullptr;
            steamabi::PFN_ISteamMatchmaking_LeaveLobby leave = nullptr;
            steamabi::PFN_ISteamUtils_IsAPICallCompleted callDone = nullptr;
            steamabi::PFN_ISteamUtils_GetAPICallResult callResult = nullptr;
            std::intptr_t mm = 0;
            std::intptr_t utils = 0;

            bool Resolve(const SteamApi& api)
            {
                using namespace steamabi;
                createLobby = Export<PFN_ISteamMatchmaking_CreateLobby>(api.module, "SteamAPI_ISteamMatchmaking_CreateLobby");
                setData = Export<PFN_ISteamMatchmaking_SetLobbyData>(api.module, "SteamAPI_ISteamMatchmaking_SetLobbyData");
                getData = Export<PFN_ISteamMatchmaking_GetLobbyData>(api.module, "SteamAPI_ISteamMatchmaking_GetLobbyData");
                setJoinable = Export<PFN_ISteamMatchmaking_SetLobbyJoinable>(api.module, "SteamAPI_ISteamMatchmaking_SetLobbyJoinable");
                sendChat = Export<PFN_ISteamMatchmaking_SendLobbyChatMsg>(api.module, "SteamAPI_ISteamMatchmaking_SendLobbyChatMsg");
                getChat = Export<PFN_ISteamMatchmaking_GetLobbyChatEntry>(api.module, "SteamAPI_ISteamMatchmaking_GetLobbyChatEntry");
                leave = Export<PFN_ISteamMatchmaking_LeaveLobby>(api.module, "SteamAPI_ISteamMatchmaking_LeaveLobby");
                callDone = Export<PFN_ISteamUtils_IsAPICallCompleted>(api.module, "SteamAPI_ISteamUtils_IsAPICallCompleted");
                callResult = Export<PFN_ISteamUtils_GetAPICallResult>(api.module, "SteamAPI_ISteamUtils_GetAPICallResult");
                mm = reinterpret_cast<std::intptr_t>(api.Interface("SteamMatchMaking009"));
                utils = reinterpret_cast<std::intptr_t>(api.Interface("SteamUtils009"));
                return createLobby && setData && getData && setJoinable && sendChat && getChat && leave && callDone && callResult && mm && utils;
            }

            // Polls our own call result; nothing is registered, so the
            // host's RunCallbacks has nothing to hand to anyone.
            bool PollCreated(steamabi::SteamAPICall_t call, int timeoutMs, steamabi::LobbyCreated_t& out, bool& failed) const
            {
                const auto deadline = Clock::now() + std::chrono::milliseconds(timeoutMs);
                while (Clock::now() < deadline)
                {
                    if (callDone(utils, call, &failed))
                        return callResult(utils, call, &out, sizeof(out), steamabi::k_iLobbyCreated, &failed);
                    std::this_thread::sleep_for(std::chrono::milliseconds(20));
                }
                return false;
            }
        };

        // Leaves the lobby on every exit path. If CreateLobby had not
        // completed by the time the probe gave up, keeps polling for up to
        // 30 s so a late lobby is still left.
        class LobbyGuard
        {
        public:
            LobbyGuard(const Matchmaking& mm, const LogFn& log) : m_mm(mm), m_log(log) {}
            ~LobbyGuard() { Release(); }
            LobbyGuard(const LobbyGuard&) = delete;
            LobbyGuard& operator=(const LobbyGuard&) = delete;

            void SetPending(steamabi::SteamAPICall_t call) { m_pending = call; }
            void SetLobby(std::uint64_t lobby)
            {
                m_pending = steamabi::k_uAPICallInvalid;
                m_lobby = lobby;
            }

            void Release()
            {
                if (m_pending != steamabi::k_uAPICallInvalid && m_lobby == 0)
                {
                    steamabi::LobbyCreated_t late{};
                    bool failed = false;
                    if (m_mm.PollCreated(m_pending, 30000, late, failed) && !failed && late.m_eResult == 1 && late.m_ulSteamIDLobby != 0)
                    {
                        m_lobby = late.m_ulSteamIDLobby;
                        m_log("stage 2 lobby: late create completed during cleanup");
                    }
                    m_pending = steamabi::k_uAPICallInvalid;
                }
                if (m_lobby == 0) return;
                const auto start = Clock::now();
                m_mm.leave(m_mm.mm, m_lobby);
                char line[128];
                std::snprintf(line, sizeof(line), "stage 2 lobby: LeaveLobby issued in %.1f ms", ElapsedMs(start));
                m_log(line);
                m_lobby = 0;
            }

        private:
            const Matchmaking& m_mm;
            const LogFn& m_log;
            steamabi::SteamAPICall_t m_pending = steamabi::k_uAPICallInvalid;
            std::uint64_t m_lobby = 0;
        };

        bool LobbyLoopback(const SteamApi& api, const LogFn& log)
        {
            using namespace steamabi;
            Matchmaking mm;
            if (!mm.Resolve(api))
            {
                log("stage 2 lobby: matchmaking exports or interfaces missing");
                return false;
            }
            char line[256];
            LobbyGuard guard(mm, log);

            auto start = Clock::now();
            const SteamAPICall_t call = mm.createLobby(mm.mm, k_ELobbyTypePrivate, 1);
            if (call == k_uAPICallInvalid)
            {
                log("stage 2 lobby: CreateLobby returned an invalid call");
                return false;
            }
            guard.SetPending(call);
            LobbyCreated_t created{};
            bool failed = false;
            const bool done = mm.PollCreated(call, 15000, created, failed);
            if (!done || failed || created.m_eResult != 1 || created.m_ulSteamIDLobby == 0)
            {
                std::snprintf(line, sizeof(line), "stage 2 lobby: create failed after %.0f ms (done=%d failed=%d result=%d)", ElapsedMs(start),
                              done, failed, created.m_eResult);
                log(line);
                return false; // guard handles a late lobby
            }
            const std::uint64_t lobby = created.m_ulSteamIDLobby;
            guard.SetLobby(lobby);
            std::snprintf(line, sizeof(line), "stage 2 lobby: private lobby %s created in %.0f ms", RedactSteamId(lobby).c_str(), ElapsedMs(start));
            log(line);

            start = Clock::now();
            const bool notJoinable = mm.setJoinable(mm.mm, lobby, false);
            std::snprintf(line, sizeof(line), "stage 2 lobby: SetLobbyJoinable(false)=%d in %.1f ms", notJoinable, ElapsedMs(start));
            log(line);

            // Only our own namespaced key. Never OWNINGID/SESSIONFLAGS/
            // P2PADDR/BUILDID or any other UE session setting.
            start = Clock::now();
            const bool set = mm.setData(mm.mm, lobby, "aimmod.proto", "probe-1");
            const char* back = mm.getData(mm.mm, lobby, "aimmod.proto");
            const bool readBack = back && std::strcmp(back, "probe-1") == 0;
            std::snprintf(line, sizeof(line), "stage 2 lobby: SetLobbyData(aimmod.proto)=%d, read back %s, in %.1f ms", set,
                          readBack ? "ok" : "MISMATCH", ElapsedMs(start));
            log(line);

            start = Clock::now();
            const bool sent = mm.sendChat(mm.mm, lobby, Nonce, static_cast<int>(std::strlen(Nonce) + 1));
            bool echoed = false;
            while (sent && !echoed && ElapsedMs(start) < 10000)
            {
                for (int chatId = 0; chatId < 8 && !echoed; ++chatId)
                {
                    char buffer[128]{};
                    std::uint64_t sender = 0;
                    int type = 0;
                    const int size = mm.getChat(mm.mm, lobby, chatId, &sender, buffer, sizeof(buffer) - 1, &type);
                    echoed = size > 0 && std::strcmp(buffer, Nonce) == 0;
                }
                if (!echoed) std::this_thread::sleep_for(std::chrono::milliseconds(10));
            }
            std::snprintf(line, sizeof(line), "stage 2 lobby: chat loopback send=%d %s after %.0f ms", sent, echoed ? "received" : "NOT received",
                          ElapsedMs(start));
            log(line);

            guard.Release();
            return notJoinable && set && readBack && echoed;
        }

        class SocketPairGuard
        {
        public:
            explicit SocketPairGuard(steamabi::ISteamNetworkingSockets* sockets) : m_sockets(sockets) {}
            ~SocketPairGuard()
            {
                if (a) m_sockets->CloseConnection(a, 0, "aimmod probe done", false);
                if (b) m_sockets->CloseConnection(b, 0, "aimmod probe done", false);
            }
            steamabi::HSteamNetConnection a = steamabi::k_HSteamNetConnection_Invalid;
            steamabi::HSteamNetConnection b = steamabi::k_HSteamNetConnection_Invalid;

        private:
            steamabi::ISteamNetworkingSockets* m_sockets;
        };

        // Receives one message on `conn` within timeoutMs; copies it into
        // `out` and releases it.
        bool ReceiveOne(steamabi::ISteamNetworkingSockets* sockets, steamabi::HSteamNetConnection conn, char* out, int size, int timeoutMs)
        {
            const auto deadline = Clock::now() + std::chrono::milliseconds(timeoutMs);
            while (Clock::now() < deadline)
            {
                steamabi::SteamNetworkingMessage_t* message = nullptr;
                if (sockets->ReceiveMessagesOnConnection(conn, &message, 1) == 1 && message)
                {
                    const int n = std::min(message->m_cbSize, size - 1);
                    std::memcpy(out, message->m_pData, static_cast<std::size_t>(n > 0 ? n : 0));
                    out[n > 0 ? n : 0] = '\0';
                    message->m_pfnRelease(message);
                    return true;
                }
                std::this_thread::yield();
            }
            return false;
        }

        bool SocketPairLoopback(const SteamApi& api, const LogFn& log)
        {
            using namespace steamabi;
            auto* sockets = static_cast<ISteamNetworkingSockets*>(api.Interface("SteamNetworkingSockets012"));
            auto* utils = static_cast<ISteamNetworkingUtils*>(api.Interface("SteamNetworkingUtils004"));
            if (!sockets || !utils)
            {
                log("stage 2 sockets: networking interfaces unavailable");
                return false;
            }
            SocketPairGuard pair(sockets);
            if (!sockets->CreateSocketPair(&pair.a, &pair.b, false, nullptr, nullptr))
            {
                log("stage 2 sockets: CreateSocketPair failed");
                return false;
            }
            constexpr int Rounds = 5;
            long long minUs = -1, totalUs = 0;
            int ok = 0;
            for (int round = 0; round < Rounds; ++round)
            {
                char ping[64];
                std::snprintf(ping, sizeof(ping), "%s#%d", Nonce, round);
                const auto t0 = utils->GetLocalTimestamp();
                std::int64_t number = 0;
                if (sockets->SendMessageToConnection(pair.a, ping, static_cast<std::uint32_t>(std::strlen(ping) + 1), k_nSteamNetworkingSend_Reliable,
                                                     &number) != 1)
                    break;
                char got[64]{};
                if (!ReceiveOne(sockets, pair.b, got, sizeof(got), 1000) || std::strcmp(got, ping) != 0) break;
                if (sockets->SendMessageToConnection(pair.b, got, static_cast<std::uint32_t>(std::strlen(got) + 1), k_nSteamNetworkingSend_Reliable,
                                                     &number) != 1)
                    break;
                char echo[64]{};
                if (!ReceiveOne(sockets, pair.a, echo, sizeof(echo), 1000) || std::strcmp(echo, ping) != 0) break;
                const long long us = utils->GetLocalTimestamp() - t0;
                minUs = minUs < 0 ? us : std::min(minUs, us);
                totalUs += us;
                ++ok;
            }
            char line[160];
            std::snprintf(line, sizeof(line), "stage 2 sockets: in-memory pair round trips %d/%d, min %lld us, avg %lld us", ok, Rounds, minUs,
                          ok ? totalUs / ok : -1);
            log(line);
            return ok == Rounds;
        }
    } // namespace

    bool RunStage2(const SteamApi& api, const Options& options, const LogFn& log)
    {
        if (!options.stage2Loopback)
        {
            log("stage 2: compiled in but disabled (stage2_loopback=0)");
            return false;
        }
        if (!api.Initialised())
        {
            log("stage 2: SteamAPI not initialised; skipped");
            return false;
        }
        log("stage 2 (writes): begin; private single-slot lobby + local loopback, nothing sent to other players");
        const bool ready = WaitForRelayAndCert(api, 30, log);
        const bool lobby = LobbyLoopback(api, log);
        const bool sockets = SocketPairLoopback(api, log);
        log(std::string("stage 2: done (relay+cert=") + (ready ? "ready" : "not ready") + " lobby=" + (lobby ? "ok" : "fail") +
            " sockets=" + (sockets ? "ok" : "fail") + ")");
        return ready && lobby && sockets;
    }
#endif
} // namespace probe
