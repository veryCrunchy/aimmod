# Trainers

Open **Trainers** in the Improve section of the desktop sidebar. Choose a focus from the exercise buttons, set the session length and tempo, then select **Start practice**. **Controls & audio** expands the key, offset and volume controls. Sessions last 15 or 30 seconds, or 1, 2 or 3 minutes. Completed sessions are saved locally per account; stopping, leaving the page or switching away from AimMod ends the attempt without recording it as a completed session.

## Exercises

| Exercise | What to do | What to compare |
| --- | --- | --- |
| Tapping accuracy | Follow an even two-tap-per-beat rhythm. | On-time percentage, early/late offset and spread. |
| Streams & alternating | Alternate your two keys at four taps per beat: full streams, 8- or 24-note streams, or building into streams. | Accuracy, early/late offset and timing spread. |
| Burst control | Play three-note bursts with recovery gaps. | Even spacing through each burst. |
| Rhythm changes | Switch between two and four taps per beat every bar. | Timing through the changes and missed notes. |
| Aim control | Move to each circle and tap after landing. | Accuracy, missed notes and timing spread. |
| Reading order | Read numbered circles and their approach circles through changing positions. | Accuracy, misses and timing spread. |
| Reaction | Wait for mint before responding; the delay varies. | Median response time and false starts. |

Start at a comfortable tempo, repeat the same exercise and compare completed sessions with the same settings. The timing drills include a four-beat count-in. A short cue track contains all reference clicks; osu!'s gameplay clock and standard ruleset drive the playfield and judgement.

All standard-mode exercises run in the embedded osu! Player with the selected AimMod/osu! skin: approach circles, cursor, hitsounds, combo and accuracy HUD. They occupy the full client viewport to avoid changing aiming distance by shrinking the playfield into a card. Timing drills move through a smooth figure-eight path: steady tapping uses spaced notes, alternating follows tighter streams, bursts move in groups of three with repositioning gaps, and rhythm changes switch between spaced notes and dense streams. Aim and reading drills use wider patterns. Reaction remains a separate cue exercise. Practice scores are saved in trainer history, never submitted or imported as ranked plays.

## Controls and timing

Trainers read the selected osu! client's gameplay keys, global offset and mouse-button preference. Lazer input.json supplies raw mouse mode, sensitivity, built-in tablet area, rotation, pressure threshold and output mapping. Stable supplies raw mouse mode and sensitivity; an external tablet driver retains its own mapping without enabling a second tablet driver. Cursor size and gameplay scaling are inherited from lazer as well. Input handler and appearance changes are scoped to gameplay and restored on exit. Auto selection prefers the detected lazer client, then stable. Lazer bindings are read from a private, transactionally consistent database snapshot in the worker process. The source installation is never changed. Stable uses the current Windows user's configuration, or a single unambiguous configuration.

Custom keyboard pairs appear in the controls, including offsets between preset steps. Changing trainer keys or offset overrides the imported values for this workspace session; **Use osu! settings** restores the imported values. Importing preferences never changes an active exercise. Mouse tapping respects the imported mouse-button preference.

The offset sign matches osu!: a positive global offset advances the judgement timeline. The upstream [beatmap clock](https://github.com/ppy/osu/blob/1032a7c31581513c8be751e46f0940e1c95ed252/osu.Game/Beatmaps/FramedBeatmapClock.cs) and [binding model](https://github.com/ppy/osu/blob/1032a7c31581513c8be751e46f0940e1c95ed252/osu.Game/Input/Bindings/RealmKeyBinding.cs) define these conventions. Keep the same output device and display setup when comparing runs. The embedded player retains lazer's standard bindings, including additional bindings and chords. The primary-key selector shows available single-key bindings; changing it replaces the primary controls for that session. The separate reaction drill uses the displayed single keys.

## Results

- Standard exercises show osu! accuracy alongside the percentage of all notes hit within 25 ms. Misses remain in that percentage's denominator. The 25 ms metric is separate from osu! accuracy.
- Average offset measures early/late timing. Spread is the population standard deviation of successful hit offsets; a small spread does not imply good accuracy when notes were missed.
- End-versus-start compares the last and first thirds of the exercise. It requires at least three successful hits in both regions.
- Standard exercises use native hit judgements; repeated-key and extra-tap counts are not inferred from those judgements. Reaction excludes premature presses from response-time measurements and reports them separately.
- History retains the latest 500 completed sessions and exposes recent results for review. Comparisons use matching engine, exercise, tempo, duration, keys and offset. Older cue-drill results are kept separately from embedded-game results. Moving tapping patterns are compared separately from earlier stationary-circle sessions.

These drills isolate specific actions. Use **Map practice** to return to coaching and check whether the improvement carries over to your osu! maps; trainer results alone do not establish that transfer.

Use the same window size, display and external driver profile as osu! when comparing physical aim. Importing mapping values does not calibrate hardware latency or remap third-party driver application profiles.

## Workspace and progress

The workspace shows progress for the selected exercise. The chart shows accuracy and timing spread for up to 30 recent sessions made with your current keys and offset, with each session's tempo in a strip below; missing values are omitted. Select a recent session to inspect its results. **Repeat exercise** reuses its exercise, tempo and duration with your current controls; **Try 10 BPM slower** lowers the tempo before starting.

## Stream ladder

Open **Trainers > Stream ladder** to measure the fastest stream you can play cleanly. Each step is 15 seconds of full streams with click cues, starting 20 BPM below the chosen **edge tempo**. A clean step, with no misses and timing spread at or below the **spread limit**, adds 10 BPM. The ladder ends at the first step that is not clean, or after a clean step at 240 BPM. Its result is the **highest clean BPM**.

Every step uses the same drill: adaptive difficulty and randomised patterns are off, the layout is fixed and only the tempo changes. A new ladder repeats the previous ladder's start and limit so results stay comparable. Without an earlier ladder the limit is 1.25 × the median timing spread of recent clean stream runs (20 ms without them) and the edge tempo is their median tempo. Both can be changed before starting.

Steps are saved in the normal per-account trainer history with a ladder marker, and are left out of adaptive difficulty, warmup and skill-limit evidence, because a ladder fails on purpose. Escape stops a step without counting it. **Finish here** keeps the steps played, but the ladder is listed as finished early and stays out of the trend, since its highest clean tempo is only a lower bound.

The ladder page and the Streams & alternating progress panel chart the highest clean BPM of every complete ladder, together with the timing spread on each ladder's first step, which is played at the same tempo every time.