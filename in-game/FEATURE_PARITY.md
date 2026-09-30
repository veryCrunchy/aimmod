# In-game feature parity

This is a migration checklist, not a claim of completed parity. Desktop behavior
is referenced from the KovaaK AimMod implementation; osu implementations are not
dependencies. Runtime rendering and recorder verification remain separate gates.

| Desktop area | In-game implementation | Still to port or verify |
| --- | --- | --- |
| Overview and scenario history | Local + cached Hub previews, scenario selection, search/pagination, PB and recent chart | Complete local history and supplementary measurements loaded read-only; paginated Hub client and server changes implemented locally; server deployment pending |
| Score trends | 7/30/90-day and all available history; five-run mean, regression, distribution, mean/median/spread, previous-period comparison | Live Gameface visual verification |
| Practice analysis | Active days, practice hours, per-scenario comparison, practice blocks | Persisted goals and richer practice-plan controls |
| Movement history | Control/path/jitter/correction plus historical overshoot, speed variation, average speed, click timing and directional bias; exact-run joins, scenario/period charts and measured sample counts | Live Gameface verification; native capture of equivalent desktop mouse metrics |
| Performance history | Accuracy/pace timeline plus historical KPS, average/best/spread TTK, recorded shot-to-hit mean/P90 intervals, shots per hit and corrective-shot ratio in trends and mechanics | Live Gameface verification; native capture coverage of optional timing metrics |
| Run analysis | History drilldown to exact selected run; summary/timeline, paginated shots and nearest-target telemetry, target-response summaries/episodes, key-moment windows | Live Gameface verification; per-run coaching feedback and expanded target/shot detail |
| Coaching | Run tips plus shared desktop coaching engine global/scenario profiles, compact scoped profiles, measured signal bars and expandable recommendation cards; up to 1,000 history records with actual supplementary metrics | Practice-block/warm-up personalization, feedback persistence, recommendation history, local coach model install/chat/streaming; live Gameface verification |
| Replays | Native recorder, bounded archive, main-view playback under active development; searchable favorites, explicit local deletion and exact-file export | Timer jitter and transient inactive capture boundaries hardened; whole-map visual parity, complete input/weapon/target fidelity and end-to-end verification; foreground-scoped worker keyboard shortcuts implemented; in-game validation pending |
| Hub | Device linking with prefilled browser approval, encrypted credentials, offline score cache, searchable benchmark ranks and per-scenario threshold detail | Full leaderboard; uploader/resync controls; complete account history endpoint |
| History management | Complete read-only desktop history plus explicit CSV folder import into owned storage, with duplicate detection | User-requested history deletion |
| Settings and HUD | Current branding, main menu entry, native replay controls; persisted recording, Hub and independent game/OBS layout controls with named saved layouts | Live Gameface verification; complete desktop Overlay Studio editing parity and independent overlay window management |
| Live statistics HUD | Read-only native live score/counters, accuracy, SPM, KPS, native remaining time and last-target TTK; freshness/countdown/replay suppression | User confirmed live score increases; compact bottom-edge HUD and bounded transient grace deployed, visual verification pending; average/best/spread live TTK requires authoritative per-kill samples, plus desktop live smoothness/target-response capture |
| VS mode | Actual same-scenario PB comparison and observed native friends/leaderboard opponent selection with stable identity and bounded persistent score cache; explicitly estimated finish score, native duration when available | Live validation; automatic friend score retrieval, avatar/profile management and full friends UI |
| OBS/browser overlay | Shared transparent source, live-state backend, persisted layout and capability URL route | End-to-end OBS capture verification; desktop scene/window capture and per-display configuration parity |

Relevant desktop sources: `src/analytics/StatsWindow.tsx`, `src/coaching/engine.ts`,
`src/settings/Settings.tsx`, `src/settings/OverlayStudio.tsx`,
`src/settings/FriendManager.tsx`, `src-tauri/src/stats_db.rs`, and the corresponding
Tauri commands. Historical shot-to-hit intervals use queued shot pairing and can include older missed shots; they are not input latency. Last-target live TTK is distinct from an average TTK. Raw scores
must never be averaged across different scenarios to claim global improvement.

