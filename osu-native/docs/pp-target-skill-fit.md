# PP target skill matching

PP targets distinguish three values:

- Official maximum PP: the exact difficulty's 100% FC result, calculated by the pinned osu! ruleset with the suggested mods.
- Expected PP: the official calculator evaluated with projected accuracy, misses and combo. This is a scenario, not a guaranteed result.
- Skill fit: a ranking score from recent comparable replay outcomes. It is not a calibrated probability of passing or full-comboing the map.

## Evidence

The profile uses the last 30 days of cached replay judgements, with daily recency weighting. Successes and misses both contribute. Circle and slider-head judgements are deduplicated by object; slider tails and ticks are not treated as separate circle attempts. Multiple retries share a map/setup weight rather than becoming independent evidence of broad skill.

Candidate geometry is read from the exact `.osu` file using osu!'s playable beatmap processing. Features describe head spacing, jump distances, movement speed, tapping rate, burst/stream sequences and direction changes. Distances are normalized by circle radius and intervals by gameplay rate. Unknown radius, variable rate, unsupported settings or insufficient comparable maps remain unmeasured.

For context, osu!'s [difficulty preprocessing](https://github.com/ppy/osu/blob/master/osu.Game.Rulesets.Osu/Difficulty/Preprocessing/OsuDifficultyHitObject.cs) distinguishes normalized distances, clock-rate-adjusted timing, angles and slider movement. This matcher does not replace those official difficulty or PP calculations.

## Ranking

Measured demanded-pattern bottlenecks take precedence over easy sections. Default ranking weights skill fit at 75%, preferences at 10%, skill-adjusted attainable gain at 8%, mod preference at 4% and confidence at 3%. Unknown coverage gets a neutral ordering value, not a claim of proficiency; known weaknesses still count in partially measured maps. Explicit PP sorting remains available.

Whole-map accuracy and miss projections use disjoint object exposure counts. A stream object is not also counted as a speed and direction-change object. A short weak jump section can make a map a poor skill match without projecting its miss rate onto the successful sections. Ranking still uses the weakest demanded pattern; score projection uses the amount of each pattern present.

Suggested mods come from the most common actually played setup, counted across distinct map setups. Separate HD and HR preferences do not become a synthetic HDHR recommendation. Pattern evidence must match the candidate setup and measured clock rate.

Submitted scores remain in the broader preference/performance history. A score without replay judgements does not establish which pattern caused its misses. The pattern model currently measures object-head performance, not slider tracking, reading visibility, hand fatigue, full-map completion probability or an independently validated player skill rating. Combo projection remains an approximation.

## Caching and validation

Source files and modded geometry are cached independently of the player profile. PP scenario identities include the exact content hash, mods, model version and evidence identity. New replay evidence refreshes the skill profile without rehydrating all local PP scores. Daily-stable evidence weights allow same-day reopen reuse.

Tests cover same-star maps with different patterns, success/failure differences, weak sections within otherwise comfortable maps, recency, retry balancing, mod compatibility, missing measurements, cache reuse and official PP preservation. These are regression checks, not a claim of population-level predictive calibration. Calibration should use chronological held-out plays, report coverage alongside accuracy/miss error, and compare against the previous score-history baseline before changing fit into a probability.
