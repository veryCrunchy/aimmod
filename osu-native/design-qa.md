# Native AimMod osu workspace design QA

Reference targets:

- Replay: `exec-5014bc07-6c1d-49ac-931a-ddeb73075375.png`
- Coaching: `exec-9edae3e5-be1d-499c-a7a5-56a05d5d074e.png`

Captured at 1440 x 1024 from the self-contained Linux Release build with the real local lazer library (889 local runs). The replay capture opened an actual local score in the official `ReplayPlayer`; the coaching capture used the same local history and artwork. Side-by-side comparison inputs were saved as `/tmp/replay-design-compare.png` and `/tmp/coaching-design-compare.png`.

## Resolved findings

- P0: none observed. The Release app launched, both routes loaded, and opening a real replay did not crash.
- P1: the official player rendered outside the centre workspace bounds and covered part of the replay browser/inspector. Resolved by masking the player viewport.
- P1: dense Great judgements collapsed into an unreadable solid block. Resolved by reducing bounded timeline sampling to 240 marks, preserving every retained exact miss/slider break, and shortening ordinary marks.
- P2: long score titles and replay overlays collided with score metadata. Resolved with a bounded truncating title and the masked player viewport.
- P2: recommendation and analysis prose ran beyond the narrow inspector. Resolved with deterministic word wrapping capped to four lines.
- P2: the replay browser panel initially rendered at one logical pixel. Resolved with the intended fixed browser width.

## Verified qualities

- Real local beatmap artwork is used for replay groups, score rows, session header, selected run, and practice recommendation.
- Replay browser is bounded to the newest 80 runs and coaching history to 200 runs / 24 visible selectors.
- Star values use osu!'s official star-difficulty colours.
- Replay judgements, object numbers, timestamps, hit distribution, session trend, difficulty fit, session drift, predictions, and recommendations are backed by actual local score/analysis data.
- Replay seeking, pause/resume, +/- 5 seconds, and playback speed use the official player's single gameplay clock; there is no parallel audio player.
- Coaching and replay workspaces are scrollable where content exceeds the viewport.

## Honest limitations

- The coaching screen opens the selected run into the dedicated replay workspace rather than running a second embedded `ReplayPlayer` concurrently inside coaching. This avoids duplicate audio/gameplay clocks.
- The final masking/timeline/wrapping corrections compile and pass focused Release tests, but the post-correction Release screenshot was not recaptured in this bounded pass.
- Skin, beatmap-colour, hitsound, device, and master-volume behaviour currently remain inherited inside the official osu! player. Separate duplicate controls are intentionally not presented until they can bind directly to the inherited configuration without creating a second audio path.

## Packaged-screen polish audit

Evidence inspected:

1. `/tmp/aimmod-final-beatmaps.png` - healthy real-art list, but the search field had no visible affordance and star filtering was visually detached from the primary search task.
2. `/tmp/aimmod-final-replay-analysis.png` - healthy official replay playback and exact timeline; the next-play sentence clipped at the inspector edge.
3. `/tmp/aimmod-final-coaching.png` - healthy hierarchy, real session art, useful trend and measured coaching panels; lower content correctly continues in the scroll view.
4. `/tmp/aimmod-final-statistics.png` - healthy high-level hierarchy; graph detail is owned by the separate statistics workstream.

Changes from this audit:

- Beatmaps now has a persistent, readable search affordance even when osu!'s default placeholder fades.
- Search, min/max star filtering, and real ordering controls are presented as one compact filter row.
- Recent, title A-Z, and highest-star ordering are interactive and issue actual bounded local-library queries.
- Replay inspector analysis and next-play copy use a width-aware native text flow instead of clipped single-line text.
- Existing 60-row paging and 24-drawable virtualisation remain unchanged.

Final result: blocked pending root's requested Release rebuild and visual recapture. Code-level Release verification is recorded separately; this pass intentionally did not launch the app.
