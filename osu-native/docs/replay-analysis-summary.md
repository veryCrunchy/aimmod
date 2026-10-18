# Replay analysis on the statistics page

The statistics page sums up every analysed replay in the current filter, failed plays included, so nothing has to be watched again.

- **Timing:** unstable rate and mean hit offset, each a mean over plays with its 95% confidence interval, and the share of early hits. A mean offset whose interval spans zero is shown as no clear early or late bias.
- **Keys:** for each osu! key, its share of hits, its own mean offset and unstable rate. The key that pressed each hit circle or slider head is recorded by replay analysis from this version on; older analyses have none, and the page says how many plays have key data. Key data stays on the computer: it is left out of analysis shared to the Hub.
- **Patterns:** the same pattern classes as PP targets (jumps, bursts, streams, speed, direction changes), comparing the earlier half of the plays that contain each pattern with the recent half: accuracy, misses per 100 objects and unstable rate. The recent half shows its interval; halves that overlap may not differ.
- **Latest misses:** misses from the most recent analysed plays, with a button to watch each one in the replay.

Every number names how many plays it rests on. Autoplay, relax, autopilot and other modes are left out.
