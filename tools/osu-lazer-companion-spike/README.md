# Native lazer companion spike

This compile-only spike proves that AimMod can host the official osu!lazer game
tree in a dedicated native process. It uses `OsuGame`, the stock score importer,
and `ScorePresentType.Gameplay`, which routes an imported score into the stock
`ReplayPlayer`.

The game name is deliberately `aimmod-lazer-companion-spike`. osu!framework
therefore creates isolated storage. The spike must not point at or modify a live
osu!lazer data directory.

This is not the final IPC host. A production companion still needs a same-user
authenticated named-pipe or Unix-domain-socket command channel, lifecycle
handling, and a deliberate policy for acquiring read-only replay inputs without
opening the user's live Realm from two processes.

Do not make GitHub downloads part of application startup. Production builds
should pin an audited ppy/osu commit, build it in CI, and bundle the resulting
signed runtime with the corresponding MIT licence notices.
