// Stage 3: relay-only SteamNetworkingSockets P2P test between two Steam
// accounts on AimMod's own virtual port (AimModVirtualPort), with the AimMod
// handshake. One side runs stage3_role=listen, the other stage3_role=connect;
// each names the other in stage3_peer and both use the same stage3_match.
//
// - ICE (direct UDP) is disabled on the listen socket and the connection, so
//   traffic only goes over Valve's relays and neither side learns the other's
//   IP. The remote address is never logged.
// - The listener accepts only the configured peer arriving on our own listen
//   socket; anything else is closed. No lobby, invite or rich presence is used.
// - Handshake: every frame starts with "AMP1", protocol version 1 and the
//   64-bit FNV-1a hash of stage3_match; a mismatch closes the connection.
// - Everything is closed and the listener unregistered on every exit path.
//
// Compiled only with -DAIMMOD_PROBE_STAGE3=ON; runs only with stage3_role set.
#include "Probe.hpp"

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <thread>
#include <vector>

namespace probe
{
#if !defined(AIMMOD_PROBE_STAGE3)
    bool RunStage3(const SteamApi&, const Options& options, const LogFn& log, const GameThreadFn&)
    {
        if (!options.stage3Role.empty()) log("stage 3: requested in config but not compiled into this build; skipped");
        return false;
    }
#else
    namespace
    {
        using Clock = std::chrono::steady_clock;
        using namespace steamabi;

        constexpr std::uint16_t ProtocolVersion = 1;
        enum FrameType : std::uint8_t
        {
            Hello = 1,
            HelloAck = 2,
            Ping = 3,
            Pong = 4,
            Bye = 5,
        };

#pragma pack(push, 1)
        struct Frame
        {
            char magic[4];
            std::uint16_t version;
            std::uint8_t type;
            std::uint8_t reserved;
            std::uint64_t match;
            std::uint32_t seq;
            std::int64_t timeUs;
        };
        static_assert(sizeof(Frame) == 28);
#pragma pack(pop)

        std::uint64_t Fnv1a(const std::string& text)
        {
            std::uint64_t hash = 1469598103934665603ull;
            for (unsigned char c : text)
            {
                hash ^= c;
                hash *= 1099511628211ull;
            }
            return hash;
        }

        Frame MakeFrame(FrameType type, std::uint64_t match, std::uint32_t seq = 0, std::int64_t timeUs = 0)
        {
            Frame f{};
            std::memcpy(f.magic, "AMP1", 4);
            f.version = ProtocolVersion;
            f.type = type;
            f.match = match;
            f.seq = seq;
            f.timeUs = timeUs;
            return f;
        }

        bool Valid(const SteamNetworkingMessage_t* message, std::uint64_t match, Frame& out)
        {
            if (!message || message->m_cbSize != static_cast<int>(sizeof(Frame))) return false;
            std::memcpy(&out, message->m_pData, sizeof(Frame));
            return std::memcmp(out.magic, "AMP1", 4) == 0 && out.version == ProtocolVersion && out.match == match;
        }

        std::string PopName(SteamNetworkingPOPID pop)
        {
            if (pop == 0) return "none";
            std::string name;
            for (int shift = 24; shift >= 0; shift -= 8)
            {
                const char c = static_cast<char>((pop >> shift) & 0xFF);
                if (c >= 0x20 && c < 0x7f) name.push_back(c);
            }
            return name;
        }

        struct ConnEvent
        {
            HSteamNetConnection conn;
            HSteamListenSocket listen;
            int state;
            std::uint64_t remote;
        };

        // Passive SteamNetConnectionStatusChangedCallback_t listener. Runs
        // on whatever thread the host pumps callbacks on; only enqueues.
        class ConnListener final : public CCallbackBase
        {
        public:
            ConnListener() { m_iCallback = k_iSteamNetConnectionStatusChanged; }
            void Run(void* param) override
            {
                const auto* cb = static_cast<const SteamNetConnectionStatusChangedCallback_t*>(param);
                std::lock_guard lock(m_mutex);
                if (m_events.size() < 64)
                    m_events.push_back({cb->m_hConn, cb->m_info.m_hListenSocket, cb->m_info.m_eState, cb->m_info.m_identityRemote.m_steamID64});
            }
            void Run(void* param, bool, SteamAPICall_t) override { Run(param); }
            int GetCallbackSizeBytes() override { return static_cast<int>(sizeof(SteamNetConnectionStatusChangedCallback_t)); }

            std::vector<ConnEvent> Drain()
            {
                std::lock_guard lock(m_mutex);
                std::vector<ConnEvent> out(m_events.begin(), m_events.end());
                m_events.clear();
                return out;
            }

        private:
            std::mutex m_mutex;
            std::deque<ConnEvent> m_events;
        };

        // Owns everything stage 3 creates and releases it in the destructor.
        class Session
        {
        public:
            Session(const SteamApi& api, ISteamNetworkingSockets* sockets, const GameThreadFn& onGameThread, const LogFn& log)
                : m_api(api), m_sockets(sockets), m_onGameThread(onGameThread), m_log(log)
            {
            }
            ~Session()
            {
                if (m_conn) m_sockets->CloseConnection(m_conn, 0, "aimmod stage3 done", m_linger);
                if (m_listen) m_sockets->CloseListenSocket(m_listen);
                if (m_registered)
                {
                    m_onGameThread([this] { m_api.UnregisterCallback(m_listener); });
                    // The listener object is leaked on purpose: steam_api must
                    // never be left pointing at freed memory.
                }
                else delete m_listener;
                m_log("stage 3: cleaned up (connection closed, listen socket closed, listener unregistered)");
            }
            Session(const Session&) = delete;
            Session& operator=(const Session&) = delete;

            void RegisterListener()
            {
                if (!m_api.RegisterCallback || !m_api.UnregisterCallback) return;
                m_onGameThread([this] { m_api.RegisterCallback(m_listener, k_iSteamNetConnectionStatusChanged); });
                m_registered = true;
            }

            const SteamApi& m_api;
            ISteamNetworkingSockets* m_sockets;
            const GameThreadFn& m_onGameThread;
            const LogFn& m_log;
            ConnListener* m_listener = new ConnListener();
            bool m_registered = false;
            HSteamListenSocket m_listen = k_HSteamListenSocket_Invalid;
            HSteamNetConnection m_conn = k_HSteamNetConnection_Invalid;
            bool m_linger = false;
        };

        SteamNetworkingConfigValue_t RelayOnly()
        {
            SteamNetworkingConfigValue_t value{};
            value.m_eValue = k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable;
            value.m_eDataType = k_ESteamNetworkingConfig_Int32;
            value.m_val.m_int32 = k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_Disable;
            return value;
        }

        SteamNetworkingMessage_t* ReceiveOne(ISteamNetworkingSockets* sockets, HSteamNetConnection conn, int timeoutMs)
        {
            const auto deadline = Clock::now() + std::chrono::milliseconds(timeoutMs);
            while (Clock::now() < deadline)
            {
                SteamNetworkingMessage_t* message = nullptr;
                if (sockets->ReceiveMessagesOnConnection(conn, &message, 1) == 1 && message) return message;
                std::this_thread::sleep_for(std::chrono::milliseconds(2));
            }
            return nullptr;
        }

        bool Send(ISteamNetworkingSockets* sockets, HSteamNetConnection conn, const Frame& frame, int flags)
        {
            std::int64_t number = 0;
            return sockets->SendMessageToConnection(conn, &frame, sizeof(frame), flags, &number) == 1;
        }

        void Reject(ISteamNetworkingSockets* sockets, HSteamNetConnection conn, const char* why, const LogFn& log)
        {
            sockets->CloseConnection(conn, 5002, why, false);
            log(std::string("stage 3: rejected connection: ") + why);
        }
    } // namespace

    bool RunStage3(const SteamApi& api, const Options& options, const LogFn& log, const GameThreadFn& onGameThread)
    {
        if (options.stage3Role.empty()) return false;
        if (!api.Initialised())
        {
            log("stage 3: SteamAPI not initialised; skipped");
            return false;
        }
        if (options.stage3Peer == 0 || options.stage3Match.empty())
        {
            log("stage 3: stage3_peer and stage3_match are required; skipped");
            return false;
        }
        auto* sockets = static_cast<ISteamNetworkingSockets*>(api.Interface("SteamNetworkingSockets012"));
        if (!sockets)
        {
            log("stage 3: SteamNetworkingSockets012 unavailable");
            return false;
        }
        const bool listen = options.stage3Role == "listen";
        const std::uint64_t match = Fnv1a(options.stage3Match);
        char line[256];
        std::snprintf(line, sizeof(line), "stage 3 (P2P): role=%s peer=%s vport=%d relay-only", options.stage3Role.c_str(),
                      RedactSteamId(options.stage3Peer).c_str(), AimModVirtualPort);
        log(line);
        if (!WaitForRelayAndCert(api, 30, log))
        {
            log("stage 3: relay or cert not ready; skipped");
            return false;
        }

        Session session(api, sockets, onGameThread, log);
        const SteamNetworkingConfigValue_t relayOnly = RelayOnly();
        const auto start = Clock::now();
        const auto deadline = start + std::chrono::seconds(options.stage3Seconds);

        if (listen)
        {
            session.RegisterListener();
            session.m_listen = sockets->CreateListenSocketP2P(AimModVirtualPort, 1, &relayOnly);
            if (!session.m_listen)
            {
                log("stage 3: CreateListenSocketP2P failed");
                return false;
            }
            log("stage 3: listening; waiting for the peer");
            while (!session.m_conn && Clock::now() < deadline)
            {
                for (const auto& event : session.m_listener->Drain())
                {
                    if (event.listen != session.m_listen || event.state != k_EConnState_Connecting) continue;
                    if (session.m_conn || event.remote != options.stage3Peer)
                    {
                        Reject(sockets, event.conn, "aimmod: unexpected peer", log);
                        continue;
                    }
                    if (sockets->AcceptConnection(event.conn) == 1) session.m_conn = event.conn;
                    else Reject(sockets, event.conn, "aimmod: accept failed", log);
                }
                if (!session.m_conn) std::this_thread::sleep_for(std::chrono::milliseconds(20));
            }
        }
        else
        {
            SteamNetworkingIdentity peer{};
            peer.m_eType = 16; // k_ESteamNetworkingIdentityType_SteamID
            peer.m_cbSize = sizeof(std::uint64_t);
            peer.m_steamID64 = options.stage3Peer;
            session.m_conn = sockets->ConnectP2P(peer, AimModVirtualPort, 1, &relayOnly);
        }
        if (!session.m_conn)
        {
            log("stage 3: no connection (timed out or ConnectP2P failed)");
            return false;
        }

        // Wait for Connected by polling (no callback needed on this side).
        SteamNetConnectionInfo_t info{};
        while (Clock::now() < deadline)
        {
            if (!sockets->GetConnectionInfo(session.m_conn, &info)) break;
            if (info.m_eState == k_EConnState_Connected || info.m_eState == k_EConnState_ClosedByPeer ||
                info.m_eState == k_EConnState_ProblemDetectedLocally)
                break;
            std::this_thread::sleep_for(std::chrono::milliseconds(20));
        }
        if (info.m_eState != k_EConnState_Connected)
        {
            std::snprintf(line, sizeof(line), "stage 3: not connected (state=%d end=%d)", info.m_eState, info.m_eEndReason);
            log(line);
            return false;
        }
        const bool relayed = (info.m_nFlags & k_nConnFlags_Relayed) != 0;
        const bool authenticated = (info.m_nFlags & (k_nConnFlags_Unauthenticated | k_nConnFlags_Unencrypted)) == 0;
        const bool rightPeer = info.m_identityRemote.m_steamID64 == options.stage3Peer;
        std::snprintf(line, sizeof(line), "stage 3: connected in %.0f ms; relayed=%d authenticated+encrypted=%d peer-match=%d relay POP=%s",
                      ElapsedMs(start), relayed, authenticated, rightPeer, PopName(info.m_idPOPRelay).c_str());
        log(line);
        if (!rightPeer || !authenticated)
        {
            Reject(sockets, session.m_conn, "aimmod: identity check failed", log);
            session.m_conn = k_HSteamNetConnection_Invalid;
            return false;
        }

        auto* utils = static_cast<ISteamNetworkingUtils*>(api.Interface("SteamNetworkingUtils004"));
        Frame frame{};
        if (!listen)
        {
            // Connector: Hello -> HelloAck, then pings.
            Send(sockets, session.m_conn, MakeFrame(Hello, match), k_nSteamNetworkingSend_Reliable);
            auto* ack = ReceiveOne(sockets, session.m_conn, 10000);
            const bool ok = Valid(ack, match, frame) && frame.type == HelloAck;
            if (ack) ack->m_pfnRelease(ack);
            if (!ok)
            {
                Reject(sockets, session.m_conn, "aimmod: bad handshake", log);
                session.m_conn = k_HSteamNetConnection_Invalid;
                return false;
            }
            log("stage 3: handshake ok");
            std::vector<long long> rtts;
            for (std::uint32_t seq = 0; seq < 20 && Clock::now() < deadline; ++seq)
            {
                Send(sockets, session.m_conn, MakeFrame(Ping, match, seq, utils ? utils->GetLocalTimestamp() : 0),
                     k_nSteamNetworkingSend_UnreliableNoDelay);
                if (auto* pong = ReceiveOne(sockets, session.m_conn, 1000))
                {
                    if (Valid(pong, match, frame) && frame.type == Pong && frame.seq == seq && utils)
                        rtts.push_back(utils->GetLocalTimestamp() - frame.timeUs);
                    pong->m_pfnRelease(pong);
                }
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
            }
            Send(sockets, session.m_conn, MakeFrame(Bye, match), k_nSteamNetworkingSend_Reliable);
            session.m_linger = true;
            std::sort(rtts.begin(), rtts.end());
            std::snprintf(line, sizeof(line), "stage 3: %zu/20 pongs; RTT min %.1f ms, median %.1f ms, max %.1f ms", rtts.size(),
                          rtts.empty() ? -1.0 : rtts.front() / 1000.0, rtts.empty() ? -1.0 : rtts[rtts.size() / 2] / 1000.0,
                          rtts.empty() ? -1.0 : rtts.back() / 1000.0);
            log(line);
            return rtts.size() >= 15 && relayed;
        }

        // Listener: expect Hello, answer HelloAck, echo pings until Bye.
        auto* hello = ReceiveOne(sockets, session.m_conn, 10000);
        const bool ok = Valid(hello, match, frame) && frame.type == Hello;
        if (hello) hello->m_pfnRelease(hello);
        if (!ok)
        {
            Reject(sockets, session.m_conn, "aimmod: bad handshake", log);
            session.m_conn = k_HSteamNetConnection_Invalid;
            return false;
        }
        Send(sockets, session.m_conn, MakeFrame(HelloAck, match), k_nSteamNetworkingSend_Reliable);
        log("stage 3: handshake ok; echoing pings");
        int echoed = 0;
        bool bye = false;
        while (!bye && Clock::now() < deadline)
        {
            auto* message = ReceiveOne(sockets, session.m_conn, 500);
            if (!message)
            {
                if (sockets->GetConnectionInfo(session.m_conn, &info) && info.m_eState != k_EConnState_Connected) break;
                continue;
            }
            if (Valid(message, match, frame))
            {
                if (frame.type == Ping)
                {
                    Send(sockets, session.m_conn, MakeFrame(Pong, match, frame.seq, frame.timeUs), k_nSteamNetworkingSend_UnreliableNoDelay);
                    ++echoed;
                }
                else if (frame.type == Bye) bye = true;
            }
            message->m_pfnRelease(message);
        }
        std::snprintf(line, sizeof(line), "stage 3: echoed %d pings, bye=%d", echoed, bye);
        log(line);
        return bye && relayed;
    }
#endif
} // namespace probe
