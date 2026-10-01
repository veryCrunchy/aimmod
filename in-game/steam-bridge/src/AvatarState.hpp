#pragma once
// avatar-state.tsv, written by the native service during combat matches
// (in-game/docs/game-modes.md on feat/kovaaks-game-modes):
//
//   AIMMOD_AVATARS_1\t<sequence>
//   match\t<%-escaped match id>
//   peer\t<SteamID64, or a stand-in avatar 1..16>\t<alive 0/1>\t<friend|enemy>\t<health>\t<died at unix ms, 0>\t<respawn at unix ms, 0>[\t<weapon>]
//
// The optional weapon (CS, game-modes.md 6.6.2) is the third-person model of what that player
// holds, by KovaaK's WeaponMeshViewModels name ("AK47", "Pistol", ...), or "-" for none.

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <string_view>

namespace bridge::avatarstate
{
    struct PeerState
    {
        bool alive = true;
        bool friendly = false; // "friend": on the local player's team
        double health = -1;    // -1 = not given
        std::int64_t diedAt = 0, respawnAt = 0;
        std::string weapon; // third-person weapon model name; empty: none (or not a CS match)
    };
    struct File
    {
        std::int64_t sequence = 0;
        std::string match;
        std::map<std::uint64_t, PeerState> peers;
    };

    // The bridge's stand-in avatars (bots, simulated players) are synthetic peers 1..16
    // (Ghosts.cpp, MultiplayerService.StandIn.cs); the service names them so in this file too.
    constexpr std::uint64_t MaxStandInPeer = 16;
    inline bool IsStandInPeer(std::uint64_t peer) { return peer >= 1 && peer <= MaxStandInPeer; }

    // Strict: header first, at most 64 peers, valid ids, flags and numbers.
    std::optional<File> Parse(std::string_view text);

    // The mesh of a third-person weapon model name (KovaaK's WeaponMeshViewModels, 3.9.11), or empty.
    std::wstring ThirdPersonMesh(std::string_view model);
} // namespace bridge::avatarstate
