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
