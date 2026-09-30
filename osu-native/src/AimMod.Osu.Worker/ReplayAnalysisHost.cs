using osu.Framework.Testing;
using osu.Game.Beatmaps;
using osu.Game.Database;

namespace AimMod.Osu.Worker;

internal sealed class ReplayAnalysisHost : TestRunHeadlessGameHost
{
    public override IEnumerable<string> UserStoragePaths { get; }

    public ReplayAnalysisHost(ReplayWorkerStorage storage)
        : base(storage.Name, bypassCleanup: true, realtime: false)
    {
        // Ownership, crash recovery and deletion belong to ReplayWorkerStorage.
        UserStoragePaths = [storage.Root];
    }
}

internal sealed class ReplayOnlyBeatmapUpdater : IBeatmapUpdater
{
    public void Queue(Live<BeatmapSetInfo> beatmapSet, MetadataLookupScope lookupScope = MetadataLookupScope.LocalCacheFirst) { }
    public void Process(BeatmapSetInfo beatmapSet, MetadataLookupScope lookupScope = MetadataLookupScope.LocalCacheFirst) { }
    public void ProcessObjectCounts(BeatmapInfo beatmapInfo, MetadataLookupScope lookupScope = MetadataLookupScope.LocalCacheFirst) { }
    public void Dispose() { }
}
