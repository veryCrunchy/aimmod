# Native local library

After lazer discovery, AimMod reads catalog metadata through its isolated worker and a private read-only Realm snapshot. The native UI never opens `client.realm` or receives an account token. If discovery is unavailable, it falls back to the Realm components inherited by its `OsuGameBase` instance.

## Integration hook

`AimModGame` accepts an `ILocalLibrarySource` in its composition constructor. Both native routes use that source. `ExternalLazerLocalLibrarySource` translates `library.catalog.search` results into the existing `LocalBeatmapSet` and `LocalReplay` records without exposing worker protocol types to the screens:

```csharp
host.Run(new AimModGame(launchOptions, externalCatalogSource));
```

When no source is supplied, AimMod starts with its in-process fallback, then atomically switches both screens to `ExternalLazerLocalLibrarySource` when a complete lazer data root is detected. Active screens automatically refresh. Each external query uses a dedicated worker and finishes private snapshot cleanup before a cancelled result is discarded. The fallback adds one `RealmDetachedBeatmapStore` to the AimMod drawable tree, then constructs the library source with that store and the inherited `ScoreManager`:

```csharp
var beatmapStore = new RealmDetachedBeatmapStore();
AddInternal(beatmapStore);

var replayMetadata = new RealmLocalReplayMetadataSource();
AddInternal(replayMetadata);

var library = new OsuManagerLocalLibrarySource(
    beatmapStore,
    ScoreManager,
    replayMetadata);

content.Child = new NativeLocalLibraryScreen(library, NativeLocalLibraryMode.Beatmaps);
```

Adding the store to the tree lets osu!framework inject the same `RealmAccess` owned by `OsuGameBase`. `RealmDetachedBeatmapStore` freezes and detaches its initial result off the update thread and publishes later changes through its bindable list. AimMod makes one pass over that detached list and pages the resulting native models.

`NativeLocalLibraryScreen` requests 60 models at a time and uses `VirtualisedListContainer` with a 24-row drawable pool. A library with thousands of models therefore does not create thousands of drawable rows.

`LocalLibraryController` owns the active request. A new search cancels the previous source call. It publishes loading, ready, empty, and error states, while the screen ignores older revisions that were already queued for the update thread. A failed initial page shows a retry action. A failed later page keeps the rows already loaded and retries from the same offset.

## Replay metadata

`RealmLocalReplayMetadataSource` is loaded through the same ppy dependency container and receives `RealmAccess` and `IAPIProvider`. It performs one official `GetAllLocalScoresForUser()` query, filters to osu!standard, orders newest first, caps the detached snapshot at 100,000 scores, and releases managed Realm objects before indexing. Search, star filtering, sorting, and paging then run against plain records without per-row database work.

The source follows the API's local user bindable and invalidates on both account and score-table changes. Selecting a listed replay still resolves the full score through `ScoreManager.Query()` and `ScoreManager.GetScore()` via `ILocalReplayResolver`.

## External asset resolution

The native worker advertises the `library.resolve-assets` capability. Its request contains an osu!lazer data root, an empty AimMod-owned staging directory outside that root, up to 512 known beatmap hashes, and up to 512 known score IDs. Beatmap selectors may be MD5 or SHA-256 hashes. Score selectors are Realm score GUIDs.

For matching beatmaps, the response can contain the `.osu` file, audio, and background. For matching scores, it can contain the `.osr` replay. Each result includes its kind, owner ID, logical name, SHA-256 hash, absolute staged path, and length. `missingFiles` reports database references absent from the hashed store. Separate `missingBeatmaps` and `missingScores` lists report selectors whose required `.osu` or `.osr` could not be resolved.

Each request creates a private Realm snapshot outside the lazer data root. The worker opens the snapshot read-only, performs one bounded beatmap-table pass and one bounded score-table pass, and resolves at most 8,192 file references by their hashed-store paths. It copies at most 2 GiB into staging and verifies every copied file against its content-addressed SHA-256 name while the snapshot lease remains active. It does not enumerate the file store or write to `client.realm`, the file store, or `game.ini`. The worker deletes the snapshot before completing the command.

This asset command is selection-based resolution for IDs and hashes the caller already knows. Catalog listing is handled separately by `library.catalog.search`.

## Opening an external replay

`ExternalLazerReplayOpenService` accepts the `LocalReplay` selected by the native library screen. It asks `ExternalLazerAssetClient` for that score ID and beatmap hash in one request. A playable result must contain one replay, one difficulty file, and one audio file. The service copies a background when the beatmap has one. Missing database rows and missing stored files have separate error codes.

The worker first copies and verifies its results in `aimmod-lazer-assets-*`. The service copies those files into an `aimmod-replay-open-*` directory. The difficulty and replay use fixed `.osu` and `.osr` names. Audio and background files keep their logical relative names so references inside the difficulty still work. The worker staging lease is deleted before `OpenAsync` returns. The caller keeps the returned `ExternalLazerPlayableReplayBundle` alive through import and playback setup, then disposes it to remove the second directory.
