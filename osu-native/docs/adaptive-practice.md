# Adaptive practice

Adaptive practice is coaching's default way to practise a map. The player plays the original beatmap in the shared embedded osu! player, but never replays parts they can already play just to reach the part they fail. The coach drills the part that needs work at a speed the player can clear, speeds it back up as they clear it cleanly, and moves on once it holds at full speed. The map is complete after one full run from the start at full speed without a fail.

Generated practice sets (section breakdowns with reduced-movement, aim-focus and combined difficulties) remain available beside it on every coaching page.

## Sections

The playable beatmap is split at musical boundaries. Breaks and gaps of three seconds or more always start a section. Otherwise a section ends at the first new combo, or the first gap of 400 ms or more, after ten seconds of play, and never runs past 18 seconds. A final section shorter than five seconds joins the previous one. Times are map times, so the split does not change with speed.

## Drills

Each attempt is a fresh osu! player on the original map, trimmed to the drill: objects before and after it are removed, and play starts three seconds of music before its first object. Combo numbers, hit windows and approach rate are the map's own. Every attempt plays at **one constant speed**; speed never changes under the player mid-run.

- **Section:** the first section not yet solid, on its own.
- **Spot:** a few seconds inside a section, with a short run-up. A section gets a spot when it is cleared with one to three misses within four seconds of each other, or failed twice within two seconds of the same place. Two clean spot runs put the whole section back.
- **Connect:** a section that has just become solid, played together with the section before it, so the transition is practised too.
- **Full run:** once every section is solid, the whole map from the start at full speed.

A fail is osu!'s own, health reaching zero, or a section or spot drill that fell apart: more than two combo breaks (or more than one in ten of its objects, for long sections), or a run of nearly all 50s. A drill starts with full health and lasts seconds, so osu! alone would rarely fail it even when the part was not playable. Connect and full runs only count osu!'s own fail, because getting through them is the point. Instead of the fail animation the attempt stops and a short pause shows what comes next. The next attempt starts automatically, after three seconds following a fail and a second and a half after a clean clear; Space starts it immediately and Escape ends the session. S skips the drill: a section or spot marks that section as solid at full speed, and a connect run is passed over. The full run cannot be skipped, so a section skipped too early fails there and returns to drills.

## Progression

Speeds are per section and are a fraction of the play's full speed:

- Each consecutive fail lowers the failed section's speed by 5%, 10%, then 15% per fail, down to a 50% floor.
- Attempts are graded by hit counts, not accuracy, because a couple of 100s swing accuracy wildly on a few notes. A **clean** run has no combo breaks and at most the player's usual share of 100s and 50s plus a quarter (at least a tenth of the notes, and never fewer than one). The usual share comes from the hit counts of the play being practised, or is estimated from its accuracy. 100s and 50s alone never make a fail.
- Below full speed, a clean run raises the speed by 5%, and so do two runs in a row without a fail.
- At full speed, a section becomes solid after a streak of clean runs. The streak grows with the section's fails: one run, plus one for every two fails, up to four. A run that is not clean restarts the streak.
- After each section or spot drill the pause says why it counted, for example "No misses, 2 × 100/50 (3 allowed for a clean run).".
- A fail anywhere, including in a connect or full run, sends that section back to drills.

Speed is applied through `AdaptiveSpeedMod`, a Half Time subclass that the ruleset accepts and that keeps pitch.

## Setup and progress

Full speed is the selected play's setup. HR, HD, EZ and FL are kept; DT, NC, HT and DC (including custom rates) become the full-speed target. Assist and automation mods are never carried over. Progress is saved per account, map and setup after every attempt, so a session can stop at any time and resume later. The saved split is reused only when the map still splits into the same number of sections at the same target speed; progress saved by the first release keeps its section speeds.
