# osu coaching and PP roadmap

AimMod should treat osu support as a local-first data product. The client can answer useful coaching questions from local lazer scores, replays, beatmaps, and cached online metadata, but exact hypothetical PP should come from a lazer-compatible calculator.

## Data sources

- Local lazer Realm database: primary source for installed beatmaps, local scores, replay availability, skin state, and user activity.
- Local file storage: beatmap files, backgrounds, replays, skins, and exported assets. Exporters such as BeatmapExporter show that the lazer storage layout can be bridged without relying on stable user-facing folders.
- osu API v2: online enrichment for user profile, best scores, recent scores, beatmap metadata, beatmapset status, scores by beatmap, and ranked availability. API use must be cached and rate-limited; the official docs explicitly warn against treating the API as an uncached database.
- data.ppy.sh: seed/sample datasets for bulk model building where API polling would be abusive.
- Calculator layer: use osu-tools directly for .NET-native exact difficulty/performance, or rosu-pp through a native/WASM bridge for fast multi-mode batch calculation.

## Coaching model layers

1. Exact replay analysis: object-level hit timing, misses, slider breaks, cursor distance, and weak map segments from available replay files.
2. Personal empirical model: weighted history predictions by star rating, mods, same beatmap, and recency. This works without external services and is already used for accuracy and miss estimates.
3. PP opportunity model: rank realistic improvements from repeated setups and nearby-star personal ceilings, then simulate best-score replacement and osu!'s `0.95^rank` weighting across the local top 100. Targets that improve a retry but do not beat the existing best score on that map are excluded from the farming list.
4. Exact PP what-if model: calculate FC, target accuracy, miss cleanup, mod variants, and retry scenarios using osu/lazer-compatible difficulty and performance calculators.
5. Recommendation model: combine player fit, exact what-if PP, map length, AR/OD/CS, BPM, object density, recent consistency, retry burden, and online ranked status.

The worker sidecar exposes exact calculations through `pp.whatif.calculate`. It validates a staged `.osu` file, normalizes mod acronyms, builds osu!standard difficulty attributes through the bundled `osu.Game` ruleset, and returns PP plus aim, speed, accuracy, flashlight, reading, and effective miss count values. Coaching batches its leading empirical opportunities through this boundary, stages beatmaps from the local lazer library by content hash, estimates a conservative target combo, ranks the calculated gains, and persists deterministic results in a versioned cache.

## Cache design

- Store immutable beatmap calculation entries by ruleset, beatmap hash, mods, lazer ruleset version, calculator version, and clock-rate affecting mods.
- Store exact PP what-if entries by the beatmap calculation key plus target accuracy, miss count, max combo, generated hitresult counts, and score-version assumptions.
- Store replay analysis by score id plus replay hash and analysis engine version.
- Store API metadata by endpoint identity, response version, OAuth user when required, and expiry policy. Beatmap metadata can be long-lived; recent/user score windows need short TTLs and backoff.
- Store derived coaching snapshots by input hashes rather than wall-clock only. A new replay, score, beatmap import, or calculator version should invalidate affected slices.
- Keep cache records explainable: each recommendation should be able to say which data source and model produced its estimate.

## Better-than-basic support

- Never guess exact PP from a handmade formula. Use empirical PP only for local observed opportunity and exact calculator PP for hypothetical scenarios.
- Separate raw-score PP gain from account weighted PP gain. A map can be a strong target even if it does not move account total much.
- Prefer local data when fresh and exact, online data when enriching, and bulk datasets only for offline model calibration.
- Make recommendations actionable: target accuracy, target miss count, expected PP window, confidence, why the map fits, and why it may be risky.
- Treat caching as product behavior, not only performance. Cached calculations keep recommendations stable and make repeated coaching views instant.
