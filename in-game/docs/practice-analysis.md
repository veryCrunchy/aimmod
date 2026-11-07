# Practice analysis: beyond scores

Research and design pass for taking KovaaK's practice in AimMod past
per-scenario scores: what the replays already contain, what the motor-control
and esports-aim literature says is worth measuring, which features that
enables, and the analysis core that now exists in
`in-game/native-service/Analysis/`.

Everything here is analysis of recorded runs or freeplay-only generated
scenarios. Nothing touches a running challenge or KovaaK's ranked
leaderboards (see "Fairness and privacy" at the end).

## 1. What AimMod captures today

### Per-run data (native capture, replay format 2)

| Stream | Rate / precision | Where |
|---|---|---|
| Engine frame times | every engine frame while recording (several hundred Hz at typical frame rates), 50 us units | `native-mod/mod/src/ReplaySampler.cpp:106-155` (Tick), `ReplayFormat2.cs:147-149` |
| Look input (`AxisTurn`, `AxisLookUp`) | per engine frame, as consumed by the game; stored as integer mouse counts when the 0.07 quantum is detected, else raw floats | `native-mod/mod/src/Observer.cpp:301-307` (MetaInputRecordingComponent hooks), `ReplayFormat2.cs:152-158` |
| Yaw/pitch per unit (sensitivity) | least-squares fit over the run, stored in the file | `native-mod/DESIGN.md:173-177`, `ReplayFormat2.cs:159` |
| View rotation | keyframes at start, every button edge, kill or hit, at most every 0.5 s, or when drift exceeds 0.02 deg; reconstructed at every engine frame on decode (max 0.002 deg error measured) | `ReplayFormat2.cs:160-167`, `:211-237`, `DESIGN.md:179-183`, `:241-249` |
| Camera position and FOV | 60 Hz samples, piecewise-linear keyframes within 0.05 cm | `ReplaySampler.cpp:158-169`, `ReplayFormat2.cs:182` |
| Targets: position, capsule radius and half height, health, profile name, rotation | 60 Hz, keyframes within 0.5 cm / 0.5 deg, segment starts on (re)appearance | `ReplaySampler.cpp:174-215`, `ReplayFormat2.cs:184-195` |
| Buttons (fire, alt fire, jump, crouch, reload, ADS, ability, weapon) | per engine frame, press and release edges | `ReplayFormat2.cs:87-90` |
| Score, shots, hits, kills, damage, clock | change records at 60 Hz | `ReplayFormat2.cs:197-204` |
| Registered hits with target id | per engine frame via `WeaponParentActor:Send_ShotHit`; coverage depends on the game build (several local runs carry none) | `Observer.cpp:253-294`, `ReplaySampler.cpp:251-257`, `ReplayFormat2.cs:206-208` |
| Clip marks | up to 32 per run | `ReplaySampler.cpp:244-249` |

Format 1 (older Lua capture) has the same fields at 60 Hz only, without the
input stream.

### Per-run and per-session data outside the replay

- `completed.tsv` journal: score, accuracy, duration, kills, damage, time
  (`native-mod/DESIGN.md:127-129`, `NativeRuns.cs`).
- Desktop database (read-only from the service): `sessions` with a
  `smoothness_json` (`History.cs:7-9`, `:38`), 1 Hz `session_run_timelines`
  (`RunMetrics.cs:55`), target-response episodes, replay context windows,
  shot events with per-target yaw/pitch error (`RunInspection.cs:37-62`),
  and the `session_smoothness` / `session_shot_timings` scalars
  (`HistoricalMeasurements.cs:226-231`).

### Analysis that already exists

In-game service (`in-game/native-service`):

- `StatsAnalysis.cs:102-245`: per-scenario score statistics, trend, rolling
  mean, histogram, practice calendar and blocks, warm-up split.
- `WarmupAnalysis.cs:355-366`: scenario-relative early-session recovery
  classification (median/MAD z-scores).
- `Coaching.cs:259-335`: threshold rules on accuracy, shots per hit, damage
  efficiency, pace variation and early/late thirds (ported from the desktop
  engine).
- `RunInspection.cs`, `RunMetrics.cs`: read the desktop's precomputed tables;
  deliberately never read replay blobs.
- `ReplayCompare.cs:124-142`: target spawn detection (new id, reappearance,
  jump over 150 cm), used for determinism checks.

Desktop app (Rust, `src-tauri/src/`): raw-input mouse metrics at the polling
rate, aggregated per second over 5 s windows (`mouse_hook.rs:1385-1690`):
jitter (sideways velocity RMS over a PCA axis), velocity CV, overshoot rate
(velocity reversals over 120 degrees), path efficiency over 15-event windows,
click-interval CV, correction ratio (share of slow samples), directional
bias, and a weighted composite "smoothness". Target responses
(`target_response.rs:700-887`): reaction, pre-slowdown and recovery per
target switch or path change from 60 Hz tick frames and 125 Hz cursor
positions. Shot timings (`bridge.rs:1406-1454`): wall-clock fire-to-hit FIFO
pairing, corrective shot ratio.

Hub (`aimmod-hub`): stores the desktop feature set verbatim
(`api/internal/service/ingest.go:244`), stores raw mouse paths as JSON
(`store/replay_media.go:20-32`), computes the six-axis Aim Fingerprint from
distribution statistics of those features (`store/read.go:3009-3193`) and
draws an overshoot marker when the path reverses by more than 120 degrees
(`web/src/components/ReplayMouseOverlay.tsx:103-139`). No server-side path
analysis, no ghost, no top-player path comparison.

### Gaps the inventory shows

1. Nothing analyses the native replay's view path. The richest signal AimMod
   records (every look input the game consumed, at engine rate, with the
   sensitivity constant) is only used for playback.
2. All movement metrics are in mouse counts, windowed by sample count, and
   mix idle seconds into session means (`mouse_hook.rs:595-615`). None is in
   degrees or relative to a target, so none can say "you overshoot 20 degree
   flicks to the right".
3. "Overshoot" means four different things across desktop live feedback,
   fingerprint, viewer and hub, on scales that differ by orders of magnitude.
4. Hits are not reliably attributable to a target on current game builds; the
   hit counter is, and the vanishing target after a click is a strong proxy.
5. Session-level structure (warm-up, fatigue) exists only as score drift.

## 2. Research

### Motor control of aimed movement

- **Fitts' law** (Fitts 1954; ISO 9241-9 as formalised by MacKenzie):
  movement time grows with the index of difficulty `ID = log2(D / W + 1)`;
  throughput `ID / MT` in bits per second is the standard single-number
  summary of a pointing device plus user. For a flick, `D` is the angular
  distance from the view at onset to the target and `W` its angular diameter.
  Effective width (`We = 4.133 x SD of the endpoint error`) corrects for
  players who trade accuracy for speed. Donovan et al. 2022 found that
  professional players' Aim Lab data were poorly described by Fitts' law
  across target sizes, so throughput should be reported per amplitude and
  size bin rather than as one slope.
- **Two-component model and submovements** (Woodworth 1899; Meyer et al.
  1988 "optimized submovement"; Elliott et al. 2010): a rapid aim consists
  of a large pre-programmed ballistic movement whose endpoint variance grows
  with speed, followed by zero or more visually guided corrective
  submovements. Primary submovements tend to undershoot because an overshoot
  costs more time and energy to reverse (Roberts, Burkitt, Elliott 2020,
  2024). Submovement boundaries are read from zero crossings of velocity,
  acceleration or jerk ("type 1 to 3"), or by fitting a sum of bells; there
  is no single canonical parser, and the 2026 wavelet method notes a fixed
  primary-plus-corrective model suffices for simple tasks.
- **Minimum jerk** (Flash and Hogan 1985): unconstrained point-to-point
  movements follow a straight path with a symmetric bell-shaped speed
  profile `v(u) = 30 (D/T) u^2 (1-u)^2`, peak `1.875 D/T` at mid-movement.
  Deviation from it (shape error, peak ratio, time to peak) is a compact
  description of how a flick was executed: front-loaded, late braking,
  double peaked.
- **Smoothness**: spectral arc length (SPARC, Balasubramanian et al. 2015)
  is independent of amplitude and duration and more noise-robust than jerk
  measures; log dimensionless jerk (LDLJ) is duration-sensitive and is only
  comparable across segments of similar length. Comparison studies disagree
  on which is "better"; both are reported here and they are used for
  different things (SPARC for tracking windows, LDLJ for single flicks).
- **Pursuit tracking**: error decomposes into bias, horizontal and vertical
  RMS, and a time shift (lag when following, lead when predicting). Smooth
  pursuit of predictable motion is anticipatory, so reversal "reaction" can
  be negative. The 60 to 100 ms range is typical for visually driven
  pursuit corrections; longer values indicate reactive rather than
  predictive tracking.
- **Reaction, movement, verification**: in click-timing scenarios the time
  between target appearance and click splits into reaction (appearance to
  movement onset), movement (ballistic plus corrections) and verification
  (hold before the click). Elite players shorten verification most; the
  ballistic phase scales with amplitude and is hard to shorten without
  raising endpoint variance.
- **Speed-accuracy trade-off and sensitivity**: a lower cm/360 shortens
  ballistic time but raises endpoint variance (more overshoot and
  corrections). The replay's fitted deg-per-count makes every metric
  comparable across sensitivities, and the per-amplitude overshoot share is
  the direct evidence a sensitivity change needs.
- **Movement amplitude and effector**: with a known cm/360, angular
  amplitude converts to centimetres of mouse travel. Movements under about
  3 cm are mostly finger and wrist; over about 8 cm they need the arm. The
  distribution of per-flick travel in centimetres therefore says which
  effector a scenario trains and where a player's wrist-to-arm transition
  sits.
- **Fatigue and warm-up**: session-level drift of the same metrics (longer
  verification, more corrections, lower efficiency) is a more specific
  fatigue signal than score alone, which also moves with target luck.

### What tools show today

- **KovaaK's**: score, accuracy, damage, kills, SPM, per-run graphs,
  benchmark ranks. Nothing per flick.
- **Aim Lab**: task-level accuracy, reaction time and "movement efficiency",
  automated insights; definitions are not published.
- **Aiming.pro**: publishes metric definitions: path efficiency (path over
  direct distance per shot), initial move angle, reaction as acquire-to-move.
- **FPS Performance Lab** (Steam, listed): "Aim Technique Coach" detecting
  overshooting, undershooting, late braking, excessive corrections, and
  recommending exercises.
- **Oblivity** and **3D Aim Trainer** sensitivity finders report deviation
  from the ideal path, overshoot and undershoot as sensitivity evidence.
- **Community statistics sites** list scores and ranks; none analyses mouse
  paths because they only receive KovaaK's CSV stats.

What nobody does: analysis on the game's own consumed input stream at engine
rate (not a separate mouse hook), in degrees and relative to the actual
target geometry, per flick and per direction; a replay overlay against the
minimum-jerk ideal; drills generated from the measured weakness; and a
ghost of your own best run in the actual scenario.

## 3. Features, ranked

Ranking is value to players over effort. All are analysis of recorded runs
unless marked freeplay.

| # | Feature | Value | Effort | Data present |
|---|---|---|---|---|
| 1 | Flick breakdown per run (phases, overshoot/undershoot, corrections, by direction and amplitude) | high | low (core done) | yes |
| 2 | Optimal-path overlay in the replay viewer with per-flick scores | high | medium | yes |
| 3 | Tracking lag/lead graph, error decomposition, smoothness | high | low (core done) | yes |
| 4 | Overshoot/undershoot map by direction and amplitude, across runs | high | low | yes |
| 5 | Generated drills (freeplay `.sce`) targeting the measured weakness | high | medium | yes (needs scenario templates) |
| 6 | Sensitivity guidance from overshoot-by-amplitude trends | medium-high | low after 4 | yes |
| 7 | Warm-up readiness and fatigue alerts from mechanics drift | medium | low | yes (needs per-run storage) |
| 8 | Ghost of your best run while practising (freeplay) | medium | high (needs live world visuals, done partly for run-vs-run) | yes |
| 9 | Path comparison against a top player's run (Hub) | medium | medium (needs Hub replay uploads) | partly |
| 10 | Live in-game cues after each flick (HUD or audio) | medium | medium | yes (format 2 live stream exists for the overlay) |
| 11 | Smoothness score (SPARC-based, replaces the mouse-count composite) | medium | low | yes |
| 12 | Wrist/arm range report in centimetres | low-medium | low | yes (cm/360 from file constant and DPI setting) |

### 3.1 Flick breakdown

- Story: after a clicking run, "Your flicks took 480 ms on average. 55% of
  that was the hold before the click. Flicks to the right ended 8% past the
  target; flicks up landed short."
- Data: view path (engine rate), target tracks, fire presses, hit counter,
  track ends (all in the replay).
- Algorithm: section 4 (FlickAnalysis).
- UI: a "Flicks" tab in Run analysis (`ui/run-details.js:76` next to
  Summary, Target responses, Shots) with phase bars, a direction rose
  (eight sectors) coloured by primary-end ratio, an amplitude table, and a
  per-flick list that seeks the replay viewer to that flick.
- Cost: about 0.4 s per 60 s replay on one core (measured), run once per
  completed run in the worker, cached as JSON next to the replay.
- Fairness: analysis only.

### 3.2 Optimal-path overlay

- Story: in the replay viewer, each flick shows the recorded path (coloured
  by speed) and the minimum-jerk straight path from the onset view to the
  target, with the efficiency, overshoot and phase times beside it.
- Data: the flick records above plus `MinimumJerk.Path` for the ideal.
- Algorithm: ideal path in (yaw, pitch) from onset to goal over the recorded
  ballistic duration; both drawn in the scene as short polylines projected
  800 cm along the view, the same way the run-vs-run ghost sphere is placed
  (`native-mod/DESIGN.md:261-268`).
- UI: Lua scene overlay in the paused replay world, toggled from the
  replay HUD; the per-flick list in 3.1 seeks to the flick.
- Cost: negligible at render.
- Fairness: replay viewer only (paused world).

### 3.3 Tracking lag/lead and error graph

- Story: "You trail the target by 20 ms and sit 0.4 degrees behind it on
  average; vertical error is half the horizontal; on reversals you react
  40 ms early but overshoot by 2 degrees."
- Data: view path, target tracks, fire hold.
- Algorithm: section 4 (TrackingAnalysis).
- UI: Run analysis tab for tracking runs: error timeline (horizontal and
  vertical), lag estimate, reversal markers with their peak error, SPARC per
  2 s window.
- Cost: as 3.1.

### 3.4 Overshoot/undershoot map

- Story: "Across your last 30 clicking runs you overshoot 5 to 15 degree
  flicks by 6% and undershoot 30+ degree flicks by 10%."
- Data: flick records of many runs (needs the per-run JSON cache).
- UI: Mechanics page (`ui/mechanics.js`) heat grid: direction x amplitude,
  cell colour = mean primary-end ratio, cell label = count.
- Cost: aggregation of cached records.

### 3.5 Generated drills

- Story: "Drill for today: 20 to 40 degree flicks to the upper left, small
  targets, 60 s." One click installs and loads it in freeplay.
- Data: the map in 3.4 picks direction and amplitude bins; tracking runs
  pick speed and reversal frequency.
- Algorithm: a scenario template per drill family (static clicking on a
  wall, strafing tracking) with spawn regions set by yaw/pitch bins; the
  generator already rewrites `.sce` files for matches
  (`Multiplayer/MatchScenario.cs:131-200` sets player and bot profile
  values; `Spawns` at `:310-360` reads spawn points). Drill scenarios use a
  distinct prefix (`AimMod Drill - `), the `Description` marker, and the
  freeplay-only load path (`load-scenario`, never `start-scenario` with
  `mode=challenge`).
- UI: Coaching page card "Next block" gains a "Generate drill" action;
  Mechanics map cells offer "Drill this".
- Fairness: generated scenarios are temporary, freeplay-only, never
  published and never a challenge, exactly like match scenarios.

### 3.6 Sensitivity guidance

- Story: "At your current cm/360, mid-range flicks overshoot and need a
  correction 30% of the time while long flicks land short: a slightly lower
  sensitivity would likely help." Suggestions are directional (lower,
  higher, keep) with the evidence shown, never a number to copy.
- Data: 3.4 plus the file's yaw-per-unit constant and the DPI from settings.
- Fairness: advice only.

### 3.7 Readiness and fatigue

- Story: a warm-up gauge on Home fills as this session's verification time,
  corrections and efficiency reach the player's settled band; after a long
  block, "Corrections are up 40% over your last six runs: break time?"
- Data: per-run flick/tracking summaries stored per run (small JSON).
- Algorithm: the warm-up classifier (`WarmupAnalysis.cs`) applied to
  mechanics instead of score; fatigue as a sustained drift beyond the
  session's own early band.

### 3.8 Ghost of your best run

- Story: while practising a scenario in freeplay, a faint marker shows where
  your best run was aiming at the same scenario time.
- Blocker: freeplay exposes no attempt clock (`DESIGN.md:325-329`); the
  presenter can only place proxies in a paused world today. Needs a live
  world proxy actor and a clock source; run-vs-run in the replay viewer
  already covers the comparison after the fact.

### 3.9 Top-player path comparison (Hub)

- The Hub stores desktop mouse paths as pixel JSON; a native replay upload
  (format 2 file, already shareable via import) would let the Hub run the
  same analysis core and show two paths per flick. Needs replay uploads with
  consent and the Hub's own compute; the per-flick records are small enough
  to upload instead of whole replays.

### 3.10 Live cues

- Story: a soft tick after a clean flick, a different one after an overshoot
  corrected; optional, freeplay only.
- Data: the live overlay stream already publishes 60 Hz state; the flick
  segmentation needs the look input, which the mod has per frame. A live
  variant of `FlickAnalysis.Segment` running in the mod on the last two
  clicks is feasible but a separate implementation.
- Fairness: cues must be disabled during challenges (like every other
  override), since audio feedback could otherwise aid a ranked run.

## 4. Prototype: the analysis core

Location: `in-game/native-service/Analysis/`. Pure .NET, no I/O, no game or
Hub references; the only replay dependency is the adapter.

| File | Role |
|---|---|
| `Kinematics.cs` | `AnalysisInput` (view samples, targets per grid sample, presses, releases, hits, spawns, despawns), `ViewPath` (250 Hz resampling, 20 ms triangular velocity smoothing), angle and statistics helpers |
| `MinimumJerk.cs` | position and speed profile, ideal path, comparison with truncation-aware fit |
| `Smoothness.cs` | SPARC, LDLJ, high-frequency velocity share, radix-2 FFT |
| `FlickAnalysis.cs` | flick segmentation, phases, geometry, per-direction and per-amplitude summaries, Fitts throughput |
| `TrackingAnalysis.cs` | error decomposition, lag by cross-correlation and by error minimisation, on-target share, reversals, switches, windowed smoothness |
| `ReplayAnalysis.cs` | adapter from `NativeReplay` (format 2 engine-rate rotation and keyframe tracks, format 1 60 Hz frames), ray-capsule test, scenario kind by fire-hold share |
| `AnalysisChecks.cs` | 63 synthetic checks, run by `--self-test` and `--self-test-analysis` |
| `ReplayAnalysisCli.cs` | `--analyze-replays [--output <folder>] [--id <id>] [--flicks]`: console only |

`ReplayFormat2.cs` now exposes the decoded target tracks and registered hits
on `Motion` for the adapter; playback is unchanged.

### Flick segmentation

For every fire press (the click) in a run:

1. Window from the previous click. Rest threshold = max(20 deg/s, 8% of the
   window's peak speed). Speed peaks above max(1.5 x rest, 10% of max) are
   candidate submovements; neighbours whose valley stays above 80% of the
   lower peak merge.
2. The primary submovement is the first peak at or above half the maximum.
   Onset: walk back from it until speed drops below rest (or the previous
   click: a continuous flow). Primary end: the valley before the next peak,
   or the first sample below rest. Correction end: first sample below rest
   after the last peak. Verification: correction end to click.
3. Target: the recorded hit target; else the target whose track ended within
   -30 to +150 ms of the click (a kill); else the nearest to the view ray.
   The goal direction is taken from the last grid sample where that target
   was visible (a killed target's track ends up to 16 ms before the shot).
4. Geometry on the (yaw, pitch) plane from the onset view to the goal:
   distance, path length, efficiency = distance / path length, projection of
   the primary end on the axis (primary-end ratio: below 0.9 undershoot,
   above 1.05 overshoot), maximum excursion past the goal (overshoot ratio),
   maximum sideways deviation (lateral ratio), sector (eight compass
   directions), amplitude bin, click error against the target's angular
   radius, index of difficulty.
5. Shape of the primary against the minimum-jerk bell with the same
   displacement and duration, fitted to the full bell the rest-threshold
   truncation implies (otherwise every clean flick reads as 14% slow):
   shape error (RMS / ideal peak), peak ratio, time to peak. LDLJ over the
   whole flick.
6. Reaction = onset minus the target's spawn, only when the movement
   started from rest and the spawn fell after the previous click; in a
   continuous clicking flow reaction is undefined and reported as such.
7. Flicks under 1 degree (adjustments between shots on one target), over
   2 s, or without any movement above twice the rest floor are skipped and
   counted.

### Tracking

Per grid sample: nearest target to the view ray, signed yaw and pitch error
(view minus target), target angular velocity (same smoothing as the view).
Reported: RMS and bias per axis, mean absolute error, error projected on the
target's direction of motion (negative = behind) and the share of time
behind, lag by velocity cross-correlation and by error minimisation over
-300 to +300 ms (undefined for static targets or boundary solutions),
on-target share overall and while firing, target reversals (the target
reached 15 deg/s on both sides of a yaw sign change within 300 ms) with the
view's reversal delay (negative = anticipated) and the error peak after
each, nearest-target switches with acquisition time, SPARC and high-frequency
share over 2 s windows.

### Validation

Synthetic: minimum-jerk flicks with known overshoot, undershoot, multiple
targets, hit-counter-only hits, vanishing targets, continuous flow; a
sinusoidal target with a delayed follower (lag, behind share, reversal
delay, pitch bias recovered), an exact follower (zero lag and error) and a
9 Hz tremor (lower SPARC, higher high-frequency share); ray-capsule
geometry; yaw unwrap; resampling.

Local replays (aggregate sanity only, nothing stored): on a 60 s multi-target
clicking run the segmentation recovers roughly one flick per click with a
hit rate matching the run's accuracy, sub-degree click error, verification
as the costliest phase, mostly clean primaries with small overshoot on short
and medium flicks and a mild undershoot on long ones; the primary
submovement is slightly sharper than minimum jerk. On tracking runs lag is
a few tens of milliseconds, horizontal error exceeds vertical, the view is
behind the target slightly more often than ahead, reversal delay is
negative on predictable bots and positive on an erratic one, and SPARC over
2 s windows sits in the expected -2.5 to -3 band. Analysis of a 60 s
replay takes about 0.2 to 0.6 s.

### Known limitations

- Reaction is only defined for movements from rest; multi-target scenarios
  rarely provide it. A dedicated single-target drill does.
- Submovement parsing uses speed peaks and valleys on a 20 ms smoothed
  profile; overlapping corrections merge into the primary. Good enough for
  phase times and overshoot; a bell-fitting parser would separate more.
- Yaw and pitch are treated as a plane; fine for flicks under about 60
  degrees, which is almost all of them.
- Format 1 replays carry no input stream (60 Hz only) and no registered
  hits; the hit counter stands in.
- The scenario kind is inferred from the fire-hold share (30%); mixed
  scenarios get both analyses with the flick part thinned.

## 5. Roadmap: parallel workstreams

A. **Per-run pipeline** (service): run the analysis when a replay lands,
store a compact JSON per run (phases, per-flick records, tracking summary),
expose `GET run-details/{id}/practice` next to `WorkspaceHost.cs:180`.
Independent of UI.

B. **Run analysis UI**: Flicks and Tracking tabs in `ui/run-details.js`,
reusing its canvas and table helpers; Mechanics page direction x amplitude
map fed by the per-run cache. Depends on A's JSON shape only.

C. **Replay viewer overlay**: minimum-jerk path and recorded path polylines
in the paused scene, flick seek from the UI. Lua scene plus protocol rows
like `ghostmotion`; depends on A for flick times.

D. **Drill generator**: scenario templates and `AimMod Drill - ` generation,
install and freeplay load through the existing command path; a drill
recommendation from the aggregate map. Depends on B's aggregates.

E. **Readiness, fatigue and sensitivity guidance**: session-level analysis
over A's cache, surfaced on Home and Coaching. Independent of B to D.

F. **Hub**: optional upload of per-flick records (not replays) with the
existing sharing consent; fingerprint axes recomputed from degrees-based
metrics; later, path-vs-path comparison when both players uploaded.

## 6. Fairness and privacy

- Analysis reads completed replays. It never changes weapons, targets,
  timing or input in a challenge, and it never runs during a challenge.
- Generated drills are temporary freeplay scenarios with a marker in their
  description, never started as a challenge and never published.
- Live cues, if built, are disabled during challenges like every override.
- Per-run analysis JSON stays in the AimMod data folder; nothing is uploaded
  without the Hub sharing consent. Documentation and tests use synthetic
  data only.
