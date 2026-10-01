#pragma once
// Steam access for AimModSteam: the game's already-initialised SDK 1.47
// steam_api64.dll, flat exports for matchmaking/friends/utils, vtables for
// SteamNetworkingSockets012 (see steam-probe/src/SteamAbi.hpp and
// in-game/docs/multiplayer.md). Never initialises, pumps or shuts down Steam.

#include "SteamAbi.hpp"

#include <Windows.h>

#include <cstddef>
#include <cstdint>
#include <string>

namespace bridge
{
    constexpr std::uint32_t KovaaksAppId = 824270;

    // ELobbyType
    constexpr int LobbyPrivate = 0;
    constexpr int LobbyFriendsOnly = 1;
    constexpr int LobbyInvisible = 3; // joinable by id, hidden from friends (tournament lobbies)

    // Callback ids handled by the bridge.
    constexpr int CbGameLobbyJoinRequested = 333;
    constexpr int CbGameRichPresenceJoinRequested = 337;
    constexpr int CbLobbyInvite = 503;
    constexpr int CbLobbyCreated = 513;
    constexpr int CbLobbyEnter = 504;
    constexpr int CbConnectionStatusChanged = 1221;
    constexpr int CbItemInstalled = 3405;              // k_iClientUGCCallbacks + 5
    constexpr int CbDownloadItemResult = 3406;         // k_iClientUGCCallbacks + 6
    constexpr int CbRemoteStorageSubscribe = 1313;     // RemoteStorageSubscribePublishedFileResult_t (SubscribeItem call result)
    constexpr int CbPersonaStateChange = 304;          // k_iSteamFriendsCallbacks + 4
    constexpr int CbAvatarImageLoaded = 334;           // k_iSteamFriendsCallbacks + 34

    // EPersonaChange bits the bridge reacts to.
    constexpr int PersonaChangeName = 0x0001, PersonaChangeAvatar = 0x0040, PersonaChangeNameFirstSet = 0x0400, PersonaChangeNickname = 0x1000;

    // EItemState
    constexpr std::uint32_t ItemSubscribed = 1, ItemInstalled = 4, ItemNeedsUpdate = 8, ItemDownloading = 16, ItemDownloadPending = 32;

#pragma pack(push, 8)
    struct LobbyEnter_t
    {
        std::uint64_t m_ulSteamIDLobby;
        std::uint32_t m_rgfChatPermissions;
        bool m_bLocked;
        std::uint32_t m_EChatRoomEnterResponse;
    };
    struct FriendGameInfo_t
    {
        std::uint64_t m_gameID;
        std::uint32_t m_unGameIP; // never read
        std::uint16_t m_usGamePort;
        std::uint16_t m_usQueryPort;
        std::uint64_t m_steamIDLobby;
    };
    struct GameLobbyJoinRequested_t
    {
        std::uint64_t m_steamIDLobby;
        std::uint64_t m_steamIDFriend;
    };
    struct LobbyInvite_t
    {
        std::uint64_t m_ulSteamIDUser;  // inviter
        std::uint64_t m_ulSteamIDLobby;
        std::uint64_t m_ulGameID;
    };
    struct ItemInstalled_t
    {
        std::uint32_t m_unAppID;
        std::uint64_t m_nPublishedFileId;
    };
    struct DownloadItemResult_t
    {
        std::uint32_t m_unAppID;
        std::uint64_t m_nPublishedFileId;
        int m_eResult;
    };
    struct RemoteStorageSubscribePublishedFileResult_t
    {
        int m_eResult;
        std::uint64_t m_nPublishedFileId;
    };
    struct PersonaStateChange_t
    {
        std::uint64_t m_ulSteamID;
        int m_nChangeFlags;
    };
    struct AvatarImageLoaded_t
    {
        std::uint64_t m_steamID;
        int m_iImage;
        int m_iWide;
        int m_iTall;
    };
    struct GameRichPresenceJoinRequested_t
    {
        std::uint64_t m_steamIDFriend;
        char m_rgchConnect[256];
    };
#pragma pack(pop)
    static_assert(sizeof(LobbyEnter_t) == 24);
    static_assert(sizeof(FriendGameInfo_t) == 24);
    static_assert(sizeof(GameLobbyJoinRequested_t) == 16);
    static_assert(sizeof(GameRichPresenceJoinRequested_t) == 264);
    static_assert(sizeof(ItemInstalled_t) == 16);
    static_assert(sizeof(LobbyInvite_t) == 24);
    static_assert(sizeof(PersonaStateChange_t) == 16);
    static_assert(sizeof(AvatarImageLoaded_t) == 24);

    // SteamUGCDetails_t as STEAMUGC_INTERFACE_VERSION014 writes it (SDK 1.47).
#pragma pack(push, 8)
    struct SteamUGCDetails_t
    {
        std::uint64_t m_nPublishedFileId;
        int m_eResult;
        int m_eFileType;
        std::uint32_t m_nCreatorAppID;
        std::uint32_t m_nConsumerAppID;
        char m_rgchTitle[129];
        char m_rgchDescription[8000];
        std::uint64_t m_ulSteamIDOwner;
        std::uint32_t m_rtimeCreated;
        std::uint32_t m_rtimeUpdated;
        std::uint32_t m_rtimeAddedToUserList;
        int m_eVisibility;
        bool m_bBanned;
        bool m_bAcceptedForUse;
        bool m_bTagsTruncated;
        char m_rgchTags[1025];
        std::uint64_t m_hFile;
        std::uint64_t m_hPreviewFile;
        char m_pchFileName[260];
        std::int32_t m_nFileSize;
        std::int32_t m_nPreviewFileSize;
        char m_rgchURL[256];
        std::uint32_t m_unVotesUp;
        std::uint32_t m_unVotesDown;
        float m_flScore;
        std::uint32_t m_unNumChildren;
    };
#pragma pack(pop)
    static_assert(offsetof(SteamUGCDetails_t, m_ulSteamIDOwner) == 8160);
    static_assert(offsetof(SteamUGCDetails_t, m_nFileSize) == 9492);
    static_assert(sizeof(SteamUGCDetails_t) == 9776);
    constexpr int UGCQueryRankedByPublicationDate = 1;
    constexpr int UGCQueryRankedByTextSearch = 11;
    constexpr int UGCMatchingItems = 0;
    static_assert(sizeof(DownloadItemResult_t) == 24);
    static_assert(sizeof(RemoteStorageSubscribePublishedFileResult_t) == 16);

    struct Steam
    {
        HMODULE module = nullptr;
        steamabi::PFN_SteamAPI_GetHSteamUser GetHSteamUser = nullptr;
        steamabi::PFN_SteamAPI_GetHSteamPipe GetHSteamPipe = nullptr;
        steamabi::PFN_SteamInternal_FindOrCreateUserInterface FindOrCreate = nullptr;
        steamabi::PFN_SteamAPI_RegisterCallback RegisterCallback = nullptr;
        steamabi::PFN_SteamAPI_UnregisterCallback UnregisterCallback = nullptr;

        std::intptr_t user = 0, friends = 0, mm = 0, utils = 0, ugc = 0;
        steamabi::ISteamNetworkingSockets* sockets = nullptr;
        steamabi::ISteamNetworkingUtils* netUtils = nullptr;

        // ISteamUser / ISteamUtils
        std::uint64_t (*User_GetSteamID)(std::intptr_t) = nullptr;
        bool (*User_BLoggedOn)(std::intptr_t) = nullptr;
        std::uint32_t (*Utils_GetAppID)(std::intptr_t) = nullptr;
        bool (*Utils_IsAPICallCompleted)(std::intptr_t, steamabi::SteamAPICall_t, bool*) = nullptr;
        bool (*Utils_GetAPICallResult)(std::intptr_t, steamabi::SteamAPICall_t, void*, int, int, bool*) = nullptr;
        bool (*Utils_GetImageSize)(std::intptr_t, int, std::uint32_t*, std::uint32_t*) = nullptr;
        bool (*Utils_GetImageRGBA)(std::intptr_t, int, std::uint8_t*, int) = nullptr;

        // ISteamMatchmaking (CSteamID as uint64)
        steamabi::SteamAPICall_t (*MM_CreateLobby)(std::intptr_t, int, int) = nullptr;
        steamabi::SteamAPICall_t (*MM_JoinLobby)(std::intptr_t, std::uint64_t) = nullptr;
        void (*MM_LeaveLobby)(std::intptr_t, std::uint64_t) = nullptr;
        bool (*MM_InviteUserToLobby)(std::intptr_t, std::uint64_t, std::uint64_t) = nullptr;
        int (*MM_GetNumLobbyMembers)(std::intptr_t, std::uint64_t) = nullptr;
        std::uint64_t (*MM_GetLobbyMemberByIndex)(std::intptr_t, std::uint64_t, int) = nullptr;
        const char* (*MM_GetLobbyData)(std::intptr_t, std::uint64_t, const char*) = nullptr;
        bool (*MM_SetLobbyData)(std::intptr_t, std::uint64_t, const char*, const char*) = nullptr;
        bool (*MM_DeleteLobbyData)(std::intptr_t, std::uint64_t, const char*) = nullptr;
        int (*MM_GetLobbyDataCount)(std::intptr_t, std::uint64_t) = nullptr;
        bool (*MM_GetLobbyDataByIndex)(std::intptr_t, std::uint64_t, int, char*, int, char*, int) = nullptr;
        bool (*MM_SetLobbyMemberLimit)(std::intptr_t, std::uint64_t, int) = nullptr;
        int (*MM_GetLobbyMemberLimit)(std::intptr_t, std::uint64_t) = nullptr;
        bool (*MM_SetLobbyJoinable)(std::intptr_t, std::uint64_t, bool) = nullptr;
        std::uint64_t (*MM_GetLobbyOwner)(std::intptr_t, std::uint64_t) = nullptr;
        bool (*MM_SetLobbyOwner)(std::intptr_t, std::uint64_t, std::uint64_t) = nullptr;
        bool (*MM_RequestLobbyData)(std::intptr_t, std::uint64_t) = nullptr;

        // ISteamFriends
        const char* (*F_GetPersonaName)(std::intptr_t) = nullptr;
        int (*F_GetFriendCount)(std::intptr_t, int) = nullptr;
        std::uint64_t (*F_GetFriendByIndex)(std::intptr_t, int, int) = nullptr;
        const char* (*F_GetFriendPersonaName)(std::intptr_t, std::uint64_t) = nullptr;
        int (*F_GetFriendPersonaState)(std::intptr_t, std::uint64_t) = nullptr;
        bool (*F_GetFriendGamePlayed)(std::intptr_t, std::uint64_t, FriendGameInfo_t*) = nullptr;
        int (*F_GetSmallFriendAvatar)(std::intptr_t, std::uint64_t) = nullptr;
        int (*F_GetMediumFriendAvatar)(std::intptr_t, std::uint64_t) = nullptr; // 64x64; 0 none, -1 loading
        bool (*F_RequestUserInformation)(std::intptr_t, std::uint64_t, bool) = nullptr;
        bool (*F_SetRichPresence)(std::intptr_t, const char*, const char*) = nullptr;
        void (*F_ClearRichPresence)(std::intptr_t) = nullptr;
        const char* (*F_GetFriendRichPresence)(std::intptr_t, std::uint64_t, const char*) = nullptr;
        void (*F_RequestFriendRichPresence)(std::intptr_t, std::uint64_t) = nullptr;
        void (*F_ActivateGameOverlayInviteDialog)(std::intptr_t, std::uint64_t) = nullptr;
        int (*F_GetFriendRelationship)(std::intptr_t, std::uint64_t) = nullptr;

        // ISteamUGC (STEAMUGC_INTERFACE_VERSION014, the version the 1.47 flat exports wrap). Read and download only.
        steamabi::SteamAPICall_t (*UGC_SubscribeItem)(std::intptr_t, std::uint64_t) = nullptr;
        std::uint32_t (*UGC_GetItemState)(std::intptr_t, std::uint64_t) = nullptr;
        bool (*UGC_GetItemInstallInfo)(std::intptr_t, std::uint64_t, std::uint64_t*, char*, std::uint32_t, std::uint32_t*) = nullptr;
        bool (*UGC_GetItemDownloadInfo)(std::intptr_t, std::uint64_t, std::uint64_t*, std::uint64_t*) = nullptr;
        bool (*UGC_DownloadItem)(std::intptr_t, std::uint64_t, bool) = nullptr;
        // Queries (read-only)
        std::uint64_t (*UGC_CreateQueryAll)(std::intptr_t, int queryType, int matchingType, std::uint32_t creatorApp, std::uint32_t consumerApp, std::uint32_t page) = nullptr;
        std::uint64_t (*UGC_CreateQueryDetails)(std::intptr_t, std::uint64_t* ids, std::uint32_t count) = nullptr;
        bool (*UGC_AddRequiredTag)(std::intptr_t, std::uint64_t handle, const char* tag) = nullptr;
        bool (*UGC_SetSearchText)(std::intptr_t, std::uint64_t handle, const char* text) = nullptr;
        steamabi::SteamAPICall_t (*UGC_SendQuery)(std::intptr_t, std::uint64_t handle) = nullptr;
        bool (*UGC_GetQueryResult)(std::intptr_t, std::uint64_t handle, std::uint32_t index, void* details) = nullptr;
        bool (*UGC_ReleaseQuery)(std::intptr_t, std::uint64_t handle) = nullptr;
        std::uint32_t (*UGC_GetNumSubscribedItems)(std::intptr_t) = nullptr;
        std::uint32_t (*UGC_GetSubscribedItems)(std::intptr_t, std::uint64_t* ids, std::uint32_t max) = nullptr;

        // Resolves everything from an already-loaded module. Returns false and
        // names the first missing item in `missing` if anything is absent.
        bool Resolve(HMODULE steamApi, std::string& missing);
        bool Initialised() const { return GetHSteamUser && GetHSteamPipe && GetHSteamUser() != 0 && GetHSteamPipe() != 0; }

        // Polls one of our own call results. Returns true when complete.
        bool PollCall(steamabi::SteamAPICall_t call, int expected, void* out, int size, bool& failed) const;
    };
} // namespace bridge
