#include "Steam.hpp"

namespace bridge
{
    namespace
    {
        template <typename T> bool Bind(HMODULE module, T& target, const char* name, std::string& missing)
        {
            target = reinterpret_cast<T>(GetProcAddress(module, name));
            if (!target && missing.empty()) missing = name;
            return target != nullptr;
        }
    } // namespace

    bool Steam::Resolve(HMODULE steamApi, std::string& missing)
    {
        missing.clear();
        module = steamApi;
        if (!module)
        {
            missing = "steam_api64.dll";
            return false;
        }
        Bind(module, GetHSteamUser, "SteamAPI_GetHSteamUser", missing);
        Bind(module, GetHSteamPipe, "SteamAPI_GetHSteamPipe", missing);
        Bind(module, FindOrCreate, "SteamInternal_FindOrCreateUserInterface", missing);
        Bind(module, RegisterCallback, "SteamAPI_RegisterCallback", missing);
        Bind(module, UnregisterCallback, "SteamAPI_UnregisterCallback", missing);

        Bind(module, User_GetSteamID, "SteamAPI_ISteamUser_GetSteamID", missing);
        Bind(module, User_BLoggedOn, "SteamAPI_ISteamUser_BLoggedOn", missing);
        Bind(module, Utils_GetAppID, "SteamAPI_ISteamUtils_GetAppID", missing);
        Bind(module, Utils_IsAPICallCompleted, "SteamAPI_ISteamUtils_IsAPICallCompleted", missing);
        Bind(module, Utils_GetAPICallResult, "SteamAPI_ISteamUtils_GetAPICallResult", missing);
        Bind(module, Utils_GetImageSize, "SteamAPI_ISteamUtils_GetImageSize", missing);
        Bind(module, Utils_GetImageRGBA, "SteamAPI_ISteamUtils_GetImageRGBA", missing);

        Bind(module, MM_CreateLobby, "SteamAPI_ISteamMatchmaking_CreateLobby", missing);
        Bind(module, MM_JoinLobby, "SteamAPI_ISteamMatchmaking_JoinLobby", missing);
        Bind(module, MM_LeaveLobby, "SteamAPI_ISteamMatchmaking_LeaveLobby", missing);
        Bind(module, MM_InviteUserToLobby, "SteamAPI_ISteamMatchmaking_InviteUserToLobby", missing);
        Bind(module, MM_GetNumLobbyMembers, "SteamAPI_ISteamMatchmaking_GetNumLobbyMembers", missing);
        Bind(module, MM_GetLobbyMemberByIndex, "SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex", missing);
        Bind(module, MM_GetLobbyData, "SteamAPI_ISteamMatchmaking_GetLobbyData", missing);
        Bind(module, MM_SetLobbyData, "SteamAPI_ISteamMatchmaking_SetLobbyData", missing);
        Bind(module, MM_DeleteLobbyData, "SteamAPI_ISteamMatchmaking_DeleteLobbyData", missing);
        Bind(module, MM_GetLobbyDataCount, "SteamAPI_ISteamMatchmaking_GetLobbyDataCount", missing);
        Bind(module, MM_GetLobbyDataByIndex, "SteamAPI_ISteamMatchmaking_GetLobbyDataByIndex", missing);
        Bind(module, MM_SetLobbyMemberLimit, "SteamAPI_ISteamMatchmaking_SetLobbyMemberLimit", missing);
        Bind(module, MM_GetLobbyMemberLimit, "SteamAPI_ISteamMatchmaking_GetLobbyMemberLimit", missing);
        Bind(module, MM_SetLobbyJoinable, "SteamAPI_ISteamMatchmaking_SetLobbyJoinable", missing);
        Bind(module, MM_GetLobbyOwner, "SteamAPI_ISteamMatchmaking_GetLobbyOwner", missing);
        Bind(module, MM_SetLobbyOwner, "SteamAPI_ISteamMatchmaking_SetLobbyOwner", missing);
        Bind(module, MM_RequestLobbyData, "SteamAPI_ISteamMatchmaking_RequestLobbyData", missing);

        Bind(module, F_GetPersonaName, "SteamAPI_ISteamFriends_GetPersonaName", missing);
        Bind(module, F_GetFriendCount, "SteamAPI_ISteamFriends_GetFriendCount", missing);
        Bind(module, F_GetFriendByIndex, "SteamAPI_ISteamFriends_GetFriendByIndex", missing);
        Bind(module, F_GetFriendPersonaName, "SteamAPI_ISteamFriends_GetFriendPersonaName", missing);
        Bind(module, F_GetFriendPersonaState, "SteamAPI_ISteamFriends_GetFriendPersonaState", missing);
        Bind(module, F_GetFriendGamePlayed, "SteamAPI_ISteamFriends_GetFriendGamePlayed", missing);
        Bind(module, F_GetSmallFriendAvatar, "SteamAPI_ISteamFriends_GetSmallFriendAvatar", missing);
        Bind(module, F_RequestUserInformation, "SteamAPI_ISteamFriends_RequestUserInformation", missing);
        Bind(module, F_SetRichPresence, "SteamAPI_ISteamFriends_SetRichPresence", missing);
        Bind(module, F_ClearRichPresence, "SteamAPI_ISteamFriends_ClearRichPresence", missing);
        Bind(module, F_GetFriendRichPresence, "SteamAPI_ISteamFriends_GetFriendRichPresence", missing);
        Bind(module, F_RequestFriendRichPresence, "SteamAPI_ISteamFriends_RequestFriendRichPresence", missing);
        Bind(module, F_ActivateGameOverlayInviteDialog, "SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog", missing);
        Bind(module, F_GetFriendRelationship, "SteamAPI_ISteamFriends_GetFriendRelationship", missing);
        Bind(module, UGC_SubscribeItem, "SteamAPI_ISteamUGC_SubscribeItem", missing);
        Bind(module, UGC_GetItemState, "SteamAPI_ISteamUGC_GetItemState", missing);
        Bind(module, UGC_GetItemInstallInfo, "SteamAPI_ISteamUGC_GetItemInstallInfo", missing);
        Bind(module, UGC_GetItemDownloadInfo, "SteamAPI_ISteamUGC_GetItemDownloadInfo", missing);
        Bind(module, UGC_DownloadItem, "SteamAPI_ISteamUGC_DownloadItem", missing);
        if (!missing.empty() || !Initialised()) return false;

        const auto hUser = GetHSteamUser();
        auto iface = [&](const char* version) {
            void* p = FindOrCreate(hUser, version);
            if (!p && missing.empty()) missing = version;
            return p;
        };
        user = reinterpret_cast<std::intptr_t>(iface("SteamUser020"));
        friends = reinterpret_cast<std::intptr_t>(iface("SteamFriends017"));
        mm = reinterpret_cast<std::intptr_t>(iface("SteamMatchMaking009"));
        utils = reinterpret_cast<std::intptr_t>(iface("SteamUtils009"));
        ugc = reinterpret_cast<std::intptr_t>(iface("STEAMUGC_INTERFACE_VERSION014"));
        sockets = static_cast<steamabi::ISteamNetworkingSockets*>(iface("SteamNetworkingSockets012"));
        netUtils = static_cast<steamabi::ISteamNetworkingUtils*>(iface("SteamNetworkingUtils004"));
        if (!missing.empty()) return false;
        // Layout check before trusting the networking vtables.
        if (netUtils->GetLocalTimestamp() <= 0)
        {
            missing = "SteamNetworkingUtils004 layout check";
            return false;
        }
        return true;
    }

    bool Steam::PollCall(steamabi::SteamAPICall_t call, int expected, void* out, int size, bool& failed) const
    {
        failed = false;
        if (call == steamabi::k_uAPICallInvalid) return (failed = true);
        if (!Utils_IsAPICallCompleted(utils, call, &failed)) return false;
        if (!Utils_GetAPICallResult(utils, call, out, size, expected, &failed)) failed = true;
        return true;
    }
} // namespace bridge
