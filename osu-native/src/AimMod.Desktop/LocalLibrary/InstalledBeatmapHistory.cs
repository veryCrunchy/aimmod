namespace AimMod.Desktop.LocalLibrary;

/// <summary>The player's results on one set or difficulty, from local replays.</summary>
internal sealed record BeatmapPlaySummary(double BestAccuracy, double? BestPp, int Plays, DateTimeOffset LastPlayed);

/// <summary>
/// A bounded view of the player's recent local plays, used to show per-row status and to
/// compare a difficulty with what the player usually plays.
/// </summary>
internal sealed class InstalledBeatmapHistory
{
    public const int MinimumBaselinePlays = 3;
    private const int baseline_plays = 20;

    public static InstalledBeatmapHistory Empty { get; } = new([]);

    public IReadOnlyList<LocalReplay> Replays { get; }
    public IReadOnlyDictionary<Guid, BeatmapPlaySummary> BySet { get; }
    public IReadOnlyDictionary<Guid, BeatmapPlaySummary> ByDifficulty { get; }

    /// <summary>Median star rating of the player's best recent passes.</summary>
    public double? UsualStars { get; }

    /// <summary>Median accuracy of the player's most recent passes.</summary>
    public double? UsualAccuracy { get; }

    public InstalledBeatmapHistory(IReadOnlyList<LocalReplay> replays)
    {
        Replays = replays;
        BySet = summarise(replays, replay => replay.SetId);
        ByDifficulty = summarise(replays, replay => replay.BeatmapId);
        LocalReplay[] passes = replays.Where(replay => replay.Passed && double.IsFinite(replay.Accuracy)).ToArray();
        LocalReplay[] best = topPlays(passes);
        UsualStars = best.Length >= MinimumBaselinePlays ? median(best.Select(replay => replay.StarRating)) : null;
        LocalReplay[] recent = passes.OrderByDescending(replay => replay.PlayedAt).Take(30).ToArray();
        UsualAccuracy = recent.Length >= MinimumBaselinePlays ? median(recent.Select(replay => replay.Accuracy)) : null;
    }

    /// <summary>
    /// Average skill demand of the difficulties behind the player's best recent passes, when
    /// enough of them are in <paramref name="sets"/>. Returns null rather than guessing.
    /// </summary>
    public BeatmapSkillDemand? UsualSkills(IEnumerable<LocalBeatmapSet> sets)
    {
        Dictionary<Guid, LocalBeatmapDifficulty> difficulties = sets.SelectMany(set => set.Difficulties)
            .GroupBy(difficulty => difficulty.BeatmapId).ToDictionary(group => group.Key, group => group.First());
        BeatmapSkillDemand[] matched = topPlays(Replays.Where(replay => replay.Passed).ToArray())
            .Select(replay => difficulties.GetValueOrDefault(replay.BeatmapId))
            .OfType<LocalBeatmapDifficulty>()
            .Select(BeatmapSkillDemand.From)
            .ToArray();
        if (matched.Length < MinimumBaselinePlays)
            return null;
        return new BeatmapSkillDemand(
            matched.Average(skill => skill.Aim),
            matched.Average(skill => skill.Speed),
            matched.Average(skill => skill.Stamina),
            matched.Average(skill => skill.Reading),
            matched.Average(skill => skill.Precision));
    }

    private static LocalReplay[] topPlays(IReadOnlyList<LocalReplay> passes) => passes
        .GroupBy(replay => replay.BeatmapId)
        .Select(group => group.OrderByDescending(replay => replay.PerformancePoints ?? 0).ThenByDescending(replay => replay.Accuracy).First())
        .OrderByDescending(replay => replay.PerformancePoints ?? replay.StarRating * 10)
        .Take(baseline_plays)
        .ToArray();

    private static Dictionary<Guid, BeatmapPlaySummary> summarise(IReadOnlyList<LocalReplay> replays, Func<LocalReplay, Guid> key) => replays
        .Where(replay => key(replay) != Guid.Empty)
        .GroupBy(key)
        .ToDictionary(group => group.Key, group =>
        {
            LocalReplay[] passed = group.Where(replay => replay.Passed).ToArray();
            IEnumerable<LocalReplay> scored = passed.Length > 0 ? passed : group;
            double? pp = scored.Max(replay => replay.PerformancePoints);
            return new BeatmapPlaySummary(scored.Max(replay => replay.Accuracy), pp, group.Count(), group.Max(replay => replay.PlayedAt));
        });

    private static double median(IEnumerable<double> values)
    {
        double[] ordered = values.Order().ToArray();
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 1 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2;
    }
}
