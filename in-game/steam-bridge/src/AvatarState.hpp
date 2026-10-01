#pragma once
// avatar-state.tsv, written by the native service during combat matches
// (in-game/docs/game-modes.md on feat/kovaaks-game-modes):
//
//   AIMMOD_AVATARS_1\t<sequence>
//   match\t<%-escaped match id>
//   peer\t<SteamID64>\t<alive 0/1>\t<friend|enemy>\t<health>\t<died at unix ms, 0>\t<respawn at unix ms, 0>

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
    };
    struct File
    {
        std::int64_t sequence = 0;
        std::string match;
        std::map<std::uint64_t, PeerState> peers;
    };

    // Strict: header first, at most 64 peers, valid ids, flags and numbers.
    std::optional<File> Parse(std::string_view text);
} // namespace bridge::avatarstate
