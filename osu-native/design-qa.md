# Native AimMod design QA

## Beatmaps reference

- Selected mock: `C:/Users/yarod/.codex/generated_images/01a06832-3cef-76f1-9958-c5bccdbac1e4/exec-9f65af10-0d6f-43e1-b7bf-43614384a7f2.png`
- Populated implementation capture: `tests/AimMod.Desktop.Tests/bin/Release/net8.0/visual-captures/aimmod-beatmaps-populated-1600x900.png`
- Compared state: installed osu!standard library, first set expanded, first difficulty selected, local score history present.

## Resolved findings

- P1: the old installed library did not expose per-difficulty data or selected-set context. Replaced with a set-first master/detail workspace and an expandable difficulty table.
- P1: PP and recent-performance surfaces could imply fabricated values. Exact PP now comes only from the existing per-difficulty osu performance calculator; recent performance comes only from matching local scores.
- P1: search and scrolling content previously shared no hard clipping boundary. Toolbar, set list, expanded table, and inspector now use separate masked containers and independent osu scroll containers.
- P2: the initial implementation left an extra discovery band above the toolbar. Search, filters, sort, and Installed/Online tabs now share the top workspace region.
- P2: difficulty pills moved when a set expanded. Pills now remain in the fixed set header and the expanded table has stable labelled columns.
- P2: filter controls were incomplete. Search, star range, BPM, length, played state, sort, and Installed/Online selection are interactive and backed by real indexed fields.
- P2: narrow layouts compressed the inspector into the list. Below 1160 logical pixels the inspector is removed and the list receives the full bounded width.
- P1: personal fit and recent performance previously ignored submitted scores. The inspector now merges exact per-difficulty online submissions with local replay-rich attempts through the shared account history service.
- P1: replay completion could release the active track while queued scrubber seeks were still pending. Completion now stops transport, invalidates queued actions, and prevents menu music from continuing into an unrelated song.
- P1: replay analysis could spin until the sidecar timeout without emitting coaching data. Official ruleset playback now has deterministic terminal conditions based on score completion, transformed last-object time, final replay-frame time, and failed-playback state.
- P2: Statistics was a local-only summary. It now has source, period, mods, difficulty, result, and sort filters; hoverable osu-style graph surfaces; four primary metrics; and a bounded selected-difficulty sidebar.

## Verified qualities

- Existing osu framework controls are used for text input, tabs, dropdowns, range selection, scrolling, artwork, icons, difficulty colours, and graph rendering.
- Real local artwork paths are used when available; no replacement artwork or fake score data is generated.
- Skill demand is a deterministic projection of the selected difficulty's stars, BPM, length, AR, OD, and CS.
- PP requests use the exact selected difficulty ID/hash and cache through `PpTargetExactCalculationService`.
- Local score lookup is constrained to the selected beatmap ID before accuracy, score, PP, and trend values render.
- Online best/recent account feeds and exact per-beatmap submissions are cached and merged by online score ID. Online values remain authoritative while local replay availability is retained.
- The selected online difficulty can be opened through osu!'s registered `osu://b/{id}` protocol. Unimplemented actions are visibly disabled.
- Populated, empty, loading, compact, expanded, selected, and calculation-unavailable states have explicit UI treatment.

## Verification

- Release build: passed, 0 warnings.
- Desktop tests: 177 passed.
- Runtime tests: 98 passed, 4 platform-specific integration tests skipped.
- Worker tests: every fixture passes independently (40 passed, 4 platform skips across fixtures). Running all Realm-backed fixtures in one testhost can trigger a native Realm scheduler access violation.
- Private-desktop populated Beatmaps captures: passed at 1100x760 and 1600x900 and were visually compared with the selected mock.
- Private-desktop populated Statistics captures: passed at 1100x760 and 1600x900 and were visually inspected for clipping, responsive bounds, sidebar behavior, and graph visibility.

Final result: passed.
