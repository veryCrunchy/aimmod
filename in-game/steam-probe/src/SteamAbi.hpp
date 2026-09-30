#pragma once
// Minimal Steamworks ABI used by the probe. KovaaK's 3.9.11 ships the SDK 1.47
// steam_api64.dll: it has the old "instance pointer" flat API
// (SteamAPI_ISteamUser_GetSteamID(intptr_t) ...) and no SteamAPI_SteamXxx_vNNN
// accessors or SteamNetworking* flat exports. Interfaces are therefore resolved
// by version string through SteamInternal_FindOrCreateUserInterface, which
// forwards to the installed Steam client; newer interfaces (networking
// sockets, messages, utils) are then called through their vtables, declared
// below in the exact order of the public Steamworks headers.
//
// No Steamworks SDK source is included. Every export is resolved at run time
// with GetProcAddress so the probe never loads or initialises Steam itself
// when running inside the game.

#include <cstddef>
#include <cstdint>

namespace steamabi
{
    using HSteamUser = std::int32_t;
    using HSteamPipe = std::int32_t;
    using SteamAPICall_t = std::uint64_t;
    using HSteamNetConnection = std::uint32_t;
    using SteamNetworkingPOPID = std::uint32_t;
    using SteamNetworkingMicroseconds = std::int64_t;
    using EResult = int;
    using ESteamNetworkingAvailability = int;

    constexpr SteamAPICall_t k_uAPICallInvalid = 0;
    constexpr HSteamNetConnection k_HSteamNetConnection_Invalid = 0;

    // ESteamNetworkingAvailability
    inline const char* AvailabilityName(ESteamNetworkingAvailability value)
    {
        switch (value)
        {
        case -102: return "CannotTry";
        case -101: return "Failed";
        case -100: return "Previously";
        case -10: return "Retrying";
        case 1: return "NeverTried";
        case 2: return "Waiting";
        case 3: return "Attempting";
        case 100: return "Current";
        case 0: return "Unknown";
        default: return "?";
        }
    }

    // Callback ids (k_iCallback) used by the probe.
    constexpr int k_iPersonaStateChange = 304;         // k_iSteamFriendsCallbacks + 4
    constexpr int k_iGameLobbyJoinRequested = 333;     // k_iSteamFriendsCallbacks + 33
    constexpr int k_iGameRichPresenceJoinRequested = 337;
    constexpr int k_iLobbyCreated = 513;               // k_iSteamMatchmakingCallbacks + 13
    constexpr int k_iSteamRelayNetworkStatus = 1281;   // k_iSteamNetworkingUtilsCallbacks + 1

    // Flat exports present in the SDK 1.47 steam_api64.dll.
    using PFN_SteamAPI_IsSteamRunning = bool (*)();
    using PFN_SteamAPI_GetHSteamUser = HSteamUser (*)();
    using PFN_SteamAPI_GetHSteamPipe = HSteamPipe (*)();
    using PFN_SteamInternal_FindOrCreateUserInterface = void* (*)(HSteamUser, const char*);
    using PFN_SteamAPI_Init = bool (*)();          // harness only, never in-process
    using PFN_SteamAPI_Shutdown = void (*)();      // harness only
    using PFN_SteamAPI_RunCallbacks = void (*)();  // harness only
    using PFN_SteamAPI_RegisterCallback = void (*)(void* callback, int iCallback);
    using PFN_SteamAPI_UnregisterCallback = void (*)(void* callback);

    // Old flat API: first argument is the interface pointer; CSteamID travels
    // as a uint64 (an 8-byte trivially copyable class is passed in a register).
    using PFN_ISteamUser_GetSteamID = std::uint64_t (*)(std::intptr_t self);
    using PFN_ISteamUser_BLoggedOn = bool (*)(std::intptr_t self);
    using PFN_ISteamUtils_GetAppID = std::uint32_t (*)(std::intptr_t self);
    using PFN_ISteamUtils_IsAPICallCompleted = bool (*)(std::intptr_t self, SteamAPICall_t call, bool* failed);
    using PFN_ISteamUtils_GetAPICallResult = bool (*)(std::intptr_t self, SteamAPICall_t call, void* result, int size, int expected,
                                                       bool* failed);
    using PFN_ISteamMatchmaking_CreateLobby = SteamAPICall_t (*)(std::intptr_t self, int lobbyType, int maxMembers);
    using PFN_ISteamMatchmaking_SetLobbyData = bool (*)(std::intptr_t self, std::uint64_t lobby, const char* key, const char* value);
    using PFN_ISteamMatchmaking_SetLobbyJoinable = bool (*)(std::intptr_t self, std::uint64_t lobby, bool joinable);
    using PFN_ISteamMatchmaking_SendLobbyChatMsg = bool (*)(std::intptr_t self, std::uint64_t lobby, const void* data, int size);
    using PFN_ISteamMatchmaking_GetLobbyChatEntry = int (*)(std::intptr_t self, std::uint64_t lobby, int chatId, std::uint64_t* user,
                                                            void* data, int size, int* entryType);
    using PFN_ISteamMatchmaking_GetLobbyData = const char* (*)(std::intptr_t self, std::uint64_t lobby, const char* key);
    using PFN_ISteamMatchmaking_LeaveLobby = void (*)(std::intptr_t self, std::uint64_t lobby);

    // ELobbyType. Private lobbies are joinable by invite only and never
    // returned by lobby searches. (k_ELobbyTypeInvisible = 3 is the wrong
    // choice for a hidden lobby: it hides the lobby from friends but IS
    // returned by searches.)
    constexpr int k_ELobbyTypePrivate = 0;

#pragma pack(push, 8)
    struct LobbyCreated_t
    {
        EResult m_eResult;
        std::uint64_t m_ulSteamIDLobby;
    };
    static_assert(sizeof(LobbyCreated_t) == 16);

    struct PersonaStateChange_t
    {
        std::uint64_t m_ulSteamID;
        int m_nChangeFlags;
    };
    static_assert(sizeof(PersonaStateChange_t) == 16);

    struct SteamRelayNetworkStatus_t
    {
        ESteamNetworkingAvailability m_eAvail;
        int m_bPingMeasurementInProgress;
        ESteamNetworkingAvailability m_eAvailNetworkConfig;
        ESteamNetworkingAvailability m_eAvailAnyRelay;
        char m_debugMsg[256];
    };
    static_assert(sizeof(SteamRelayNetworkStatus_t) == 272);

    struct SteamNetAuthenticationStatus_t
    {
        ESteamNetworkingAvailability m_eAvail;
        char m_debugMsg[256];
    };
    static_assert(sizeof(SteamNetAuthenticationStatus_t) == 260);
#pragma pack(pop)

#pragma pack(push, 1)
    struct SteamNetworkingIdentity
    {
        int m_eType;
        int m_cbSize;
        union
        {
            std::uint64_t m_steamID64;
            char m_raw[128];
        };
    };
    static_assert(sizeof(SteamNetworkingIdentity) == 136);
#pragma pack(pop)

#pragma pack(push, 8)
    struct SteamNetworkingMessage_t
    {
        void* m_pData;
        int m_cbSize;
        HSteamNetConnection m_conn;
        SteamNetworkingIdentity m_identityPeer;
        std::int64_t m_nConnUserData;
        SteamNetworkingMicroseconds m_usecTimeReceived;
        std::int64_t m_nMessageNumber;
        void (*m_pfnFreeData)(SteamNetworkingMessage_t*);
        void (*m_pfnRelease)(SteamNetworkingMessage_t*);
        int m_nChannel;
        int m_nFlags;
        std::int64_t m_nUserData;
        std::uint16_t m_idxLane;
        std::uint16_t _pad1__;
    };
    static_assert(offsetof(SteamNetworkingMessage_t, m_identityPeer) == 16);
    static_assert(offsetof(SteamNetworkingMessage_t, m_pfnRelease) == 184);
#pragma pack(pop)

    using HSteamListenSocket = std::uint32_t;
    constexpr HSteamListenSocket k_HSteamListenSocket_Invalid = 0;

    // ESteamNetworkingConnectionState
    constexpr int k_EConnState_None = 0;
    constexpr int k_EConnState_Connecting = 1;
    constexpr int k_EConnState_FindingRoute = 2;
    constexpr int k_EConnState_Connected = 3;
    constexpr int k_EConnState_ClosedByPeer = 4;
    constexpr int k_EConnState_ProblemDetectedLocally = 5;

    // SteamNetConnectionInfo_t::m_nFlags
    constexpr int k_nConnFlags_Unauthenticated = 1;
    constexpr int k_nConnFlags_Unencrypted = 2;
    constexpr int k_nConnFlags_Relayed = 16;

    // ESteamNetworkingConfigValue / ESteamNetworkingConfigDataType
    constexpr int k_ESteamNetworkingConfig_TimeoutInitial = 24;
    constexpr int k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable = 104;
    constexpr int k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_Disable = 0;
    constexpr int k_ESteamNetworkingConfig_Int32 = 1;

    constexpr int k_iSteamNetConnectionStatusChanged = 1221; // k_iSteamNetworkingSocketsCallbacks + 1

#pragma pack(push, 1)
    struct SteamNetworkingIPAddr
    {
        std::uint8_t m_ipv6[16];
        std::uint16_t m_port;
    };
    static_assert(sizeof(SteamNetworkingIPAddr) == 18);
#pragma pack(pop)

#pragma pack(push, 8)
    struct SteamNetworkingConfigValue_t
    {
        int m_eValue;
        int m_eDataType;
        union
        {
            std::int32_t m_int32;
            std::int64_t m_int64;
            float m_float;
            const char* m_string;
            void* m_ptr;
        } m_val;
    };
    static_assert(sizeof(SteamNetworkingConfigValue_t) == 16);

    struct SteamNetConnectionInfo_t
    {
        SteamNetworkingIdentity m_identityRemote;
        std::int64_t m_nUserData;
        HSteamListenSocket m_hListenSocket;
        SteamNetworkingIPAddr m_addrRemote; // never logged
        std::uint16_t m__pad1;
        SteamNetworkingPOPID m_idPOPRemote;
        SteamNetworkingPOPID m_idPOPRelay;
        int m_eState;
        int m_eEndReason;
        char m_szEndDebug[128];
        char m_szConnectionDescription[128];
        int m_nFlags;
        std::uint32_t reserved[63];
    };
    static_assert(offsetof(SteamNetConnectionInfo_t, m_nUserData) == 136);
    static_assert(offsetof(SteamNetConnectionInfo_t, m_idPOPRemote) == 168);
    static_assert(offsetof(SteamNetConnectionInfo_t, m_eState) == 176);
    static_assert(sizeof(SteamNetConnectionInfo_t) == 696);

    struct SteamNetConnectionStatusChangedCallback_t
    {
        HSteamNetConnection m_hConn;
        SteamNetConnectionInfo_t m_info;
        int m_eOldState;
    };
    static_assert(offsetof(SteamNetConnectionStatusChangedCallback_t, m_info) == 8);
    static_assert(sizeof(SteamNetConnectionStatusChangedCallback_t) == 712);
#pragma pack(pop)

    // "SteamNetworkingUtils004". InitRelayNetworkAccess() is an inline helper
    // in the public header (CheckPingDataUpToDate(1e10f)), not a vtable slot.
    class ISteamNetworkingUtils
    {
    public:
        virtual SteamNetworkingMessage_t* AllocateMessage(int cbAllocateBuffer) = 0;                 // 0
        virtual ESteamNetworkingAvailability GetRelayNetworkStatus(SteamRelayNetworkStatus_t* details) = 0; // 1
        virtual void Slot2_GetLocalPingLocation() = 0;                                              // 2 (never called)
        virtual void Slot3_EstimatePingTimeBetweenTwoLocations() = 0;                               // 3 (never called)
        virtual void Slot4_EstimatePingTimeFromLocalHost() = 0;                                     // 4 (never called)
        virtual void Slot5_ConvertPingLocationToString() = 0;                                       // 5 (never called)
        virtual void Slot6_ParsePingLocationString() = 0;                                           // 6 (never called)
        virtual bool CheckPingDataUpToDate(float maxAgeSeconds) = 0;                                // 7
        virtual void Slot8_GetPingToDataCenter() = 0;                                               // 8 (never called)
        virtual void Slot9_GetDirectPingToPOP() = 0;                                                // 9 (never called)
        virtual int GetPOPCount() = 0;                                                              // 10
        virtual void Slot11_GetPOPList() = 0;                                                       // 11 (never called)
        virtual SteamNetworkingMicroseconds GetLocalTimestamp() = 0;                                // 12
    protected:
        ~ISteamNetworkingUtils() = default;
    };

    // "SteamNetworkingSockets012" (prefix up to the slots the probe uses).
    class ISteamNetworkingSockets
    {
    public:
        virtual void Slot0_CreateListenSocketIP() = 0;
        virtual void Slot1_ConnectByIPAddress() = 0;
        virtual HSteamListenSocket CreateListenSocketP2P(int localVirtualPort, int nOptions,
                                                         const SteamNetworkingConfigValue_t* options) = 0; // 2
        virtual HSteamNetConnection ConnectP2P(const SteamNetworkingIdentity& remote, int remoteVirtualPort, int nOptions,
                                               const SteamNetworkingConfigValue_t* options) = 0; // 3
        virtual EResult AcceptConnection(HSteamNetConnection conn) = 0;                            // 4
        virtual bool CloseConnection(HSteamNetConnection peer, int reason, const char* debug, bool linger) = 0; // 5
        virtual bool CloseListenSocket(HSteamListenSocket socket) = 0;                             // 6
        virtual void Slot7_SetConnectionUserData() = 0;
        virtual void Slot8_GetConnectionUserData() = 0;
        virtual void Slot9_SetConnectionName() = 0;
        virtual void Slot10_GetConnectionName() = 0;
        virtual EResult SendMessageToConnection(HSteamNetConnection conn, const void* data, std::uint32_t size, int flags,
                                                std::int64_t* outMessageNumber) = 0; // 11
        virtual void Slot12_SendMessages() = 0;
        virtual void Slot13_FlushMessagesOnConnection() = 0;
        virtual int ReceiveMessagesOnConnection(HSteamNetConnection conn, SteamNetworkingMessage_t** out, int max) = 0; // 14
        virtual bool GetConnectionInfo(HSteamNetConnection conn, SteamNetConnectionInfo_t* info) = 0; // 15
        virtual void Slot16_GetConnectionRealTimeStatus() = 0;
        virtual void Slot17_GetDetailedConnectionStatus() = 0;
        virtual void Slot18_GetListenSocketAddress() = 0;
        virtual bool CreateSocketPair(HSteamNetConnection* out1, HSteamNetConnection* out2, bool useNetworkLoopback,
                                      const SteamNetworkingIdentity* identity1, const SteamNetworkingIdentity* identity2) = 0; // 19
        virtual void Slot20_ConfigureConnectionLanes() = 0;
        virtual void Slot21_GetIdentity() = 0;
        virtual ESteamNetworkingAvailability InitAuthentication() = 0;                                     // 22
        virtual ESteamNetworkingAvailability GetAuthenticationStatus(SteamNetAuthenticationStatus_t* details) = 0; // 23
    protected:
        ~ISteamNetworkingSockets() = default;
    };

    constexpr int k_nSteamNetworkingSend_Reliable = 8;
    constexpr int k_nSteamNetworkingSend_UnreliableNoDelay = 0 | 4 | 1;

    // Layout of steam_api's CCallbackBase (steam_api_common.h). The dll
    // dispatches through this vtable; MSVC lays the overloads out exactly as
    // it did when Valve compiled the same declaration.
    class CCallbackBase
    {
    public:
        CCallbackBase() = default;
        virtual void Run(void* param) = 0;
        virtual void Run(void* param, bool ioFailure, SteamAPICall_t call) = 0;
        virtual int GetCallbackSizeBytes() = 0;

    protected:
        ~CCallbackBase() = default;
        std::uint8_t m_nCallbackFlags = 0;
        int m_iCallback = 0;
    };
} // namespace steamabi
