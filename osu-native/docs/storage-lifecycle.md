# Temporary storage and cache limits

Replay analysis uses a private `AimMod/replay-worker` directory under the operating
system's temporary directory. It does not use a live osu! library for host storage.

The replay game supplies an offline `IBeatmapUpdater`. Do not replace it with the
default updater: its metadata lookup starts a separate `online.db` download for
each fresh host, which may finish after host cleanup and recreate abandoned files.
Replay input already includes the complete beatmap; online metadata is unnecessary.

## Lifecycle requirements

- Hold the exclusive cross-process scratch lease until host disposal and cleanup finish.
- Remove recognised abandoned run directories before starting another host. If a
  locked artifact cannot be removed, fail the new analysis instead of accumulating runs.
- Remove the current scratch directory after success, failure or cancellation.
- Retry crash leftovers on the next analysis. Legacy replay-analysis directories
  are eligible after 24 hours without writes; unrelated directories and links are excluded.
- Refuse analysis below 2 GiB free space. Check scratch usage before analysis and
  every second during playback, stopping the host at the 256 MiB threshold.
  This is a monitored limit, not a filesystem quota; a write can temporarily overshoot it.
- Never sweep installed maps, settings, replay originals, practice history or captures
  as cache data. Test and media-renderer workspaces have separate owners and lifetimes.

## Reproducible cache budgets

| Cache | Budget | Retention |
| --- | --- | --- |
| Replay analysis results | 256 MiB, 500 results | Newest complete results that fit |
| Local library pages | 256 MiB, 128 pages, 8 MiB per page | Seven days |
| Skin screenshots | 128 MiB, 192 images | Thirty days |

Library-page and screenshot budgets are enforced when publishing cache entries.
Locked older entries must not permit new writes to grow the cache indefinitely.
Eviction accepts only recognised hash-named files and never traverses links.

## Regression checks

Test storage cleanup after failures, crash recovery, cancellation while waiting for
the lease, byte limits, low space, locked files and link rejection. Verify cache
budgets using actual encoded bytes, including a result too large to retain.

Also exercise the release worker outside NUnit: upstream deliberately suppresses
metadata downloads under NUnit, so tests alone cannot detect their return. Run
repeated synthetic replay analyses with an isolated temporary root, measure peak
storage, and check for `online.db` files and leftover run directories.
