// Stage 2: NOT read-only. Creates one private (invite-only, never listed), non-joinable Steam lobby
// with a single slot, loops one chat message back to ourselves, loops one
// message through a local (in-memory) SteamNetworkingSockets socket pair, and
// leaves the lobby. Nothing is sent to any other player.
//
// Double gated: compiled only with -DAIMMOD_PROBE_STAGE2=ON, and run only
// when config.txt sets stage2_loopback=1. Both are off by default.
#include "Probe.hpp"

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
        template <typename T> T Export(HMODULE module, const char* name)
        {
            return reinterpret_cast<T>(GetProcAddress(module, name));
        }

        constexpr const char* Nonce = "aimmod-probe-loopback-v1";

        bool LobbyLoopback(const SteamApi& api, const LogFn& log)
        {
            using namespace steamabi;
            auto createLobby = Export<PFN_ISteamMatchmaking_CreateLobby>(api.module, "SteamAPI_ISteamMatchmaking_CreateLobby");
            auto setData = Export<PFN_ISteamMatchmaking_SetLobbyData>(api.module, "SteamAPI_ISteamMatchmaking_SetLobbyData");
            auto setJoinable = Export<PFN_ISteamMatchmaking_SetLobbyJoinable>(api.module, "SteamAPI_ISteamMatchmaking_SetLobbyJoinable");
            auto sendChat = Export<PFN_ISteamMatchmaking_SendLobbyChatMsg>(api.module, "SteamAPI_ISteamMatchmaking_SendLobbyChatMsg");
            auto getChat = Export<PFN_ISteamMatchmaking_GetLobbyChatEntry>(api.module, "SteamAPI_ISteamMatchmaking_GetLobbyChatEntry");
            auto leave = Export<PFN_ISteamMatchmaking_LeaveLobby>(api.module, "SteamAPI_ISteamMatchmaking_LeaveLobby");
            auto callDone = Export<PFN_ISteamUtils_IsAPICallCompleted>(api.module, "SteamAPI_ISteamUtils_IsAPICallCompleted");
            auto callResult = Export<PFN_ISteamUtils_GetAPICallResult>(api.module, "SteamAPI_ISteamUtils_GetAPICallResult");
            auto* mm = api.Interface("SteamMatchMaking009");
            auto* utils = api.Interface("SteamUtils009");
            if (!createLobby || !setData || !setJoinable || !sendChat || !getChat || !leave || !callDone || !callResult || !mm || !utils)
            {
                log("stage 2 lobby: matchmaking exports or interfaces missing");
                return false;
            }
            const auto self = reinterpret_cast<std::intptr_t>(mm);
            const auto utilsSelf = reinterpret_cast<std::intptr_t>(utils);

            const SteamAPICall_t call = createLobby(self, k_ELobbyTypePrivate, 1);
            if (call == k_uAPICallInvalid)
            {
                log("stage 2 lobby: CreateLobby returned an invalid call");
                return false;
            }
            // Poll the call result ourselves. No CCallResult is registered, so
            // the host's SteamAPI_RunCallbacks has nothing to hand to anyone.
            LobbyCreated_t created{};
            bool failed = false;
            bool done = false;
            for (int i = 0; i < 150 && !done; ++i)
            {
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
                done = callDone(utilsSelf, call, &failed);
            }
            if (!done || failed || !callResult(utilsSelf, call, &created, sizeof(created), k_iLobbyCreated, &failed) || failed ||
                created.m_eResult != 1 || created.m_ulSteamIDLobby == 0)
            {
                char line[128];
                std::snprintf(line, sizeof(line), "stage 2 lobby: create failed (done=%d failed=%d result=%d)", done, failed, created.m_eResult);
                log(line);
                return false;
            }
            const std::uint64_t lobby = created.m_ulSteamIDLobby;
            log("stage 2 lobby: private lobby created " + RedactSteamId(lobby));
            setJoinable(self, lobby, false);
            setData(self, lobby, "aimmod.proto", "probe-1");

            bool ok = sendChat(self, lobby, Nonce, static_cast<int>(std::strlen(Nonce) + 1));
            bool echoed = false;
            for (int attempt = 0; ok && attempt < 100 && !echoed; ++attempt)
            {
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
                for (int chatId = 0; chatId < 8 && !echoed; ++chatId)
                {
                    char buffer[128]{};
                    std::uint64_t sender = 0;
                    int type = 0;
                    const int size = getChat(self, lobby, chatId, &sender, buffer, sizeof(buffer) - 1, &type);
                    echoed = size > 0 && std::strcmp(buffer, Nonce) == 0;
                }
            }
            leave(self, lobby);
            log(std::string("stage 2 lobby: chat loopback ") + (echoed ? "received" : "NOT received") + "; lobby left");
            return echoed;
        }

        bool SocketPairLoopback(const SteamApi& api, const LogFn& log)
        {
            using namespace steamabi;
            auto* sockets = static_cast<ISteamNetworkingSockets*>(api.Interface("SteamNetworkingSockets012"));
            if (!sockets)
            {
                log("stage 2 sockets: SteamNetworkingSockets012 unavailable");
                return false;
            }
            HSteamNetConnection a = k_HSteamNetConnection_Invalid;
            HSteamNetConnection b = k_HSteamNetConnection_Invalid;
            if (!sockets->CreateSocketPair(&a, &b, false, nullptr, nullptr))
            {
                log("stage 2 sockets: CreateSocketPair failed");
                return false;
            }
            std::int64_t number = 0;
            const EResult sent = sockets->SendMessageToConnection(a, Nonce, static_cast<std::uint32_t>(std::strlen(Nonce) + 1),
                                                                  k_nSteamNetworkingSend_Reliable, &number);
            bool received = false;
            for (int i = 0; i < 50 && !received && sent == 1; ++i)
            {
                SteamNetworkingMessage_t* message = nullptr;
                if (sockets->ReceiveMessagesOnConnection(b, &message, 1) == 1 && message)
                {
                    received = message->m_cbSize > 0 && std::strcmp(static_cast<const char*>(message->m_pData), Nonce) == 0;
                    message->m_pfnRelease(message);
                }
                else std::this_thread::sleep_for(std::chrono::milliseconds(20));
            }
            sockets->CloseConnection(a, 0, "probe done", false);
            sockets->CloseConnection(b, 0, "probe done", false);
            char line[128];
            std::snprintf(line, sizeof(line), "stage 2 sockets: send result=%d, loopback %s", sent, received ? "received" : "NOT received");
            log(line);
            return received;
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
        const bool lobby = LobbyLoopback(api, log);
        const bool sockets = SocketPairLoopback(api, log);
        log(std::string("stage 2: done (lobby=") + (lobby ? "ok" : "fail") + " sockets=" + (sockets ? "ok" : "fail") + ")");
        return lobby && sockets;
    }
#endif
} // namespace probe
