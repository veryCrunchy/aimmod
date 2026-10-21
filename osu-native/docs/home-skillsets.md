# Skillsets on Home

Home's **Your skillsets** panel shows what the player plays most and where they are strongest and weakest, from the last 30 days of analysed replays.

## Patterns

The patterns are the ones PP targets and the statistics page use, measured the same way from each replay's judgements:

- **Jumps:** wide spacing between notes.
- **Streams:** 8 or more fast notes in a row; **Bursts:** 3 to 7. Fast means at most 200 ms apart (real time) and at most two circle radii apart.
- **Sharp angles:** changes of direction of 60° or more.
- **Speed:** notes 200 ms apart or closer.

A note can belong to several patterns. **How much you play it** counts each note once, in the order streams, bursts, jumps, sharp angles, speed, so the shares do not add up to more than 100%; the remainder is other notes.

## Strongest and weakest

Each pattern's accuracy is compared with the player's accuracy on every note of the same plays, in percentage points. Comparing against the same plays matters: streams often appear on harder maps, where everything is less accurate, and that alone is not a weakness in streams. Plays are weighted by recency and by map, as in PP targets, so one map retried many times does not dominate.

A pattern needs at least 3 plays and 60 of its notes before it can be called strongest or weakest; thinner patterns show "Not enough plays yet". Misses per 100 notes and unstable rate are shown alongside.

**Train …** opens the trainer for the weakest pattern: Streams & alternating for streams and speed, Burst control for bursts, Aim control for jumps and sharp angles.

## Data

Home uses the replay analyses held in memory and the PP target cache's installed maps and earlier measurements, so plays analysed in an earlier session still count. Nothing new is stored and nothing is shared.

## Finding maps by skillset

**PP targets > Skillset** keeps only maps that are mostly one pattern: **Streams** (at least 10% of notes in streams), **Bursts** (10% in bursts, the share at which PP targets already treats a map as demanding them), **Jumps** (40% of moves are jumps) or **Sharp angles** (30% of turns are 60° or more). The shares come from each difficulty's own objects, measured as its PP is calculated, so only maps the scan has reached can match. The scan measures maps inside the star range, best fit first, which for a player weak at streams would leave stream maps until last. With **Streams** or **Bursts** chosen, half of the scan instead goes to maps already known to match and then to the maps with the most combo per beat, measured first, since streams pack more notes into each beat. A short list says how many measured maps match, and widening or lowering the star range lets the scan measure more. When new replays change the skill profile and the scan starts again, the previous targets stay on screen ("Last results while updating") until the new scan has calculated as many maps. Every row shows the chosen pattern's share, or with any skillset its most prominent pattern, next to the stars and BPM. The other filters and the sort still apply, so a weak skillset can be practised on the maps that still fit the player best.