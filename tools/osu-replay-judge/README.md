# Official osu! replay judge

This sidecar runs a local osu!standard replay through the official lazer replay player and ruleset score processor. Its JSON output uses one clock: beatmap gameplay milliseconds. Replay frames, hit-object times, judgement times, and pause times are not rebased to the first recorded cursor frame.

The project pins `ppy.osu.Game` and `ppy.osu.Game.Rulesets.Osu` to `2026.730.0`. That is the newest official ppy NuGet release available as of 2026-09-02, and the nearest published build to the locally installed lazer `2026.804.2`. Both packages stay on the same version so replay decoding and judgement rules cannot drift apart.

Usage:

```text
osu-replay-judge <local-beatmap-file> <local-replay-file>
```

The headless judge mutes master, track, and sample audio before it creates the
official replay player. Its two-minute safety timeout uses elapsed wall time,
not the accelerated headless game clock.

The output includes top-level object index, nested slider object path, object and judgement times, result, maximum result, hit offset, object position, hit cursor position when the osu! ruleset supplies one, combo before and after, and persisted replay pauses.

This is an exact playback result for the pinned ruleset build. Old replays can still differ from the score header when lazer gameplay mechanics have changed since the replay was recorded. AimMod must retain the original header totals beside the reconstructed event stream and label the engine version used.
