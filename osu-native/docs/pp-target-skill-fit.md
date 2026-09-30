# PP target skill matching

PP targets show these values for each difficulty and the suggested mods:

- Max PP: the exact difficulty's 100% FC result from the pinned osu! ruleset.
- First try PP (next try PP after earlier attempts this session): the PP you can expect **if the attempt passes**, E[PP | pass]. It is not multiplied by the pass chance, which is shown beside it.
- Pass chance: the recency-weighted share of comparable attempts that passed (same map first, then similar stars, tempo and length).
- Target PP: the highest PP that your best score within the shown number of tries (`~N tries`, 3 to 5) reaches with about 60% probability. Failed tries earn nothing and the value is capped at Max PP. When passing within those tries is too unlikely, no target is shown.
- Expected earned PP: pass chance × first try PP. It orders targets and account-gain estimates; it is never displayed as a score.
- Skill fit: a ranking score from recent comparable replay outcomes. It is not a calibrated probability.

## Outcome distribution

The former estimate plugged one average score into the calculator: average head accuracy and average miss rate over every attempt, failed ones included. PP falls steeply with misses, so PP at the average miss count is well below the average PP of passes, and far below a best-of-several result. Failures were then counted a second time as zero PP.

Now each candidate uses a per-attempt distribution fitted to passed plays only:

- Evidence is score-level: each play's own accuracy, miss count, recorded combo and object count, including stable/Classic slider accuracy. Replay head judgements only provide a prior when score history is thin; a paired head-versus-score accuracy offset corrects that prior.
- Neighbours are weighted by recency (14-day half-life), star difference, effective OD and AR (after HR/EZ and the clock rate), tempo, length and, when both maps have replay geometry, pattern similarity. One map contributes at most three plays' weight.
- Neighbour outcomes are moved to the candidate's star rating with a monotone weighted regression of log miss rate and hit accuracy on the star difference, shrunk towards a prior slope when sparse. A shrunk, bounded monthly trend of the same quantities moves the evidence to today, because players improve between the plays it comes from.
- Misses follow a negative binomial (Poisson without overdispersion) scaled by object count; hit accuracy is normal; combo is the even-spread segment scaled by the observed combo efficiency, with a separate no-miss (slider-break) efficiency.
- The official calculator evaluates up to eight miss counts where the probability mass is, plus one accuracy step for a local slope. PP between them is interpolated log-linearly; the result is a discrete pass-conditional PP distribution. Calls are cached per beatmap content, mods and statistics.

Scoring mode: the automatic setup is the recency-weighted majority of configuration and scoring system (stable or lazer), represented by its most recent play. When evidence in that mode is thin, plays from the other mode join at 40% weight with a paired accuracy shift, and the pass-chance model falls back to both modes; the profile summary names how many recent other-mode plays are used. Submitted best scores are selected successes; they only fill a thin history, at 30% weight.

## Calibration

Up to 40 recent passes with recorded PP (hydrated local PP or submitted PP) are recalculated with the pinned calculator at the same accuracy, misses and combo. The ratio recorded / calculated exposes what the scenarios cannot see, such as lazer slider-tail misses or PP from an older algorithm. Each candidate uses a recency- and star-weighted geometric mean of nearby ratios with the same setup, shrunk towards 1 (two pseudo-samples) and bounded to 0.75-1.3. Ratios outside 0.6-1.6 are discarded as revision mismatches. Row tooltips state whether calibration used recorded PP.

## Retry trend

Recorded practice sessions on similar maps give the passed score's PP at each attempt number relative to that map's mean passed PP. Numerator and denominator are both passes-only; failures only affect the pass chance. Sessions that may still be running count at 60% weight; a pass without PP skips only that attempt. The first try PP is scaled by the next attempt's ratio and the target by the mean ratio over its tries, each shrunk towards 1 and bounded to 0.8-1.2.

## Ranking and scan

The preferred star range uses recency-weighted percentiles and extends to the stars of recent high-PP passes. Of the exact-calculation budget, 20% explores tempo/length/star bands and 25% goes to candidates harder than the typical selection with the highest expected earned PP; the rest follows the ranking.

## Evidence

The pattern profile uses the last 30 days of cached replay judgements, with daily recency weighting. Pattern fit and projected head accuracy use passed plays only. Circle and slider-head judgements are deduplicated by object; slider tails and ticks are not treated as separate circle attempts.

Candidate geometry is read from the exact `.osu` file using osu!'s playable beatmap processing. Features describe head spacing, jump distances, movement speed, tapping rate, burst/stream sequences, direction changes and map context (stars, effective OD and AR). Distances are normalized by circle radius and intervals by gameplay rate. Unknown radius, variable rate, unsupported settings or insufficient comparable maps remain unmeasured.

## Backtest

`PpTargetBacktest` is a chronological held-out evaluation. Each practice session (consecutive attempts of one map, setup and scoring mode, gaps of at most two hours) is predicted from plays strictly before it. It reports, overall and by star band:

- PP if pass against every recorded pass in the session: mean error, mean absolute error and bias (mean error / mean actual);
- the same for the former plug-in baseline;
- expected earned PP against every attempt, failures at zero;
- target coverage: the share of sessions whose best score within the shown tries is at most the target, and the reach rate (best at least the target; nominally 60%);
- a calculator check: the PP curve at each pass's actual statistics against its recorded PP, which isolates the calculator stand-in's own error.

Unit tests run it on synthetic players with a known generating distribution.

Developer probe, from a Release build:

```sh
cd osu-native
dotnet build src/AimMod.Desktop/AimMod.Desktop.csproj -c Release
dotnet src/AimMod.Desktop/bin/Release/net8.0/AimMod.dll --pp-backtest
```

It discovers the local osu!lazer and osu!stable libraries, keeps the most frequent local player's plays and prints aggregate metrics only: no map titles, paths or player names. Do not commit its output. It does not sign in, so only locally stored PP is used (lazer scores; stable scores need AimMod's hydrated PP). AimMod's hydrated local PP is read from a private copy of its cache, so the app cache is never modified; No Fail plays are skipped. Without beatmap files the probe replaces the calculator with a fixed osu!-like shape (miss penalty `0.96 / (m / (4 ln(n)^0.94) + 1)`, `accuracy^4`, `combo^0.1`) anchored on each map's own near-full-combo recorded PP; errors therefore measure the predicted score statistics in PP units, not calculator differences.

## Caching and validation

Source files and modded geometry are cached independently of the player profile. PP estimate identities include the exact content hash, mods, model versions, pattern and score-history identities and the candidate's catalog context. Calculator results and calibration ratios are persisted separately, so a changed profile mostly reuses calculations. Daily-stable evidence weights allow same-day reopen reuse.

Remaining limits: predictions can stay low for a player who improves faster than the shrunk trend or keeps replaying maps they have learned (map familiarity across sessions is not modelled); pass chance does not use the fitted outcome distribution; the retry trend needs recorded sessions with replay geometry; slider tracking, reading and stamina beyond observed lengths are unmeasured; aim/speed difficulty components are not compared because neighbour maps have no difficulty attributes without extra calculations.
