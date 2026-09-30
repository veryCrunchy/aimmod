using AimMod.Desktop.LocalLibrary;

namespace AimMod.Desktop.Trainers;

public static class TrainerSongChoices
{
    public static LocalReplay[] FromSets(IEnumerable<LocalBeatmapSet> sets) => sets.SelectMany(set =>
        set.Difficulties.Where(d => d.RulesetShortName == "osu")
            .OrderByDescending(d => d.LengthMilliseconds).ThenBy(d => d.StarRating).ThenBy(d => d.Name, StringComparer.Ordinal)
            .Take(1).Select(d => new LocalReplay(Guid.Empty, set.SetId, d.BeatmapId, set.Title, set.Artist, d.Name, "osu", "", DateTimeOffset.UnixEpoch,
                d.StarRating, 0, 0, 0, 0, null, [], false, BeatmapHash: d.BeatmapHash, BeatmapPath: d.BeatmapPath, Origin: d.Origin, OnlineBeatmapId: d.OnlineId)))
        .ToArray();
}
