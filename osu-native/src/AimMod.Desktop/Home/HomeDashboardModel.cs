using System.Text.Json;
using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Desktop.Trainers;
using AimMod.Osu.Runtime;

namespace AimMod.Desktop.Home;

/// <summary>A day of the form chart. Accuracy is null when no play finished that day.</summary>
internal sealed record HomeDailyPoint(DateOnly Day, int Plays, double? Accuracy);

/// <summary>One period of the "Your form" strip compared with the period directly before it.</summary>
internal sealed record HomeFormSummary(
    int Days,
    int Plays,
    int PreviousPlays,
    double? Accuracy,
    double? PreviousAccuracy,
    double? MissesPerPlay,
    double? PreviousMissesPerPlay,
    LocalReplay? TopPlay,
    double? PreviousTopPp,
    IReadOnlyList<HomeDailyPoint> Daily);

/// <summary>Change of the account totals since an earlier locally recorded snapshot.</summary>
internal sealed record HomeProfileChange(double? PpDelta, int? RankDelta, DateTimeOffset Since);

internal enum HomeRecommendationKind
{
    ContinueCoaching,
    PractiseMap,
    PpTarget,
    FindPpTargets,
    ResumeGuidedPractice,
    WarmUp,
    PlayFirstMap,
}

/// <summary>A concrete next step. <see cref="Value"/> is the key fact, <see cref="Detail"/> gives it context.</summary>
internal sealed record HomeRecommendation(
    HomeRecommendationKind Kind,
    string Label,
    string Value,
    string Detail,
    string ActionLabel,
    string? MapTitle = null,
    double? StarRating = null);

internal sealed record HomeDashboardData(
    OsuProfile? Profile,
    HomeProfileChange? ProfileChange,
    string? PlayerName,
    int TotalPlays,
    HomeFormSummary Week,
    HomeFormSummary Month,
    IReadOnlyList<LocalReplay> RecentPlays,
    double? RecentAccuracyBaseline,
    IReadOnlyList<HomeRecommendation> Recommendations)
{
    public bool HasPlays => TotalPlays > 0;
}

/// <summary>Everything the dashboard reads, gathered off the update thread.</summary>
internal sealed record HomeDashboardInputs(
    IReadOnlyList<LocalReplay> Runs,
    OsuProfile? Profile,
    HomeProfileChange? ProfileChange,
    CoachingTrainingPlan? CoachingPlan,
    TrainerGuidedPlan? GuidedPlan,
    IReadOnlyList<TrainerResult> TrainerHistory,
    PpTargetWorkspaceSnapshot? PpTargets);

internal static class HomeDashboardBuilder
{
    public const int RecentPlayCount = 6;

    public static HomeDashboardData Build(HomeDashboardInputs inputs, DateTimeOffset now)
    {
        string? player = SelectPlayer(inputs.Runs, inputs.Profile);
        LocalReplay[] runs = inputs.Runs
            .Where(run => run.PlayedAt <= now.AddMinutes(5) && ScoreMods.IsManualPlay(run)
                                                          && (player is null || !run.IsLocallyStored || string.Equals(run.Player, player, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(run => run.ScoreId)
            .OrderByDescending(run => run.PlayedAt)
            .ToArray();

        HomeFormSummary week = Summarise(runs, now, 7);
        HomeFormSummary month = Summarise(runs, now, 30);
        LocalReplay[] recent = runs.Take(RecentPlayCount).ToArray();
        double? baseline = average(runs.Where(run => run.Passed && run.PlayedAt >= now.AddDays(-30)).Select(run => run.Accuracy));

        return new HomeDashboardData(inputs.Profile, inputs.ProfileChange, inputs.Profile?.Username ?? player, runs.Length,
            week, month, recent, baseline, Recommend(inputs, runs, now));
    }

    /// <summary>The account holder when known, otherwise the name on most local plays (downloaded replays belong to others).</summary>
    internal static string? SelectPlayer(IReadOnlyList<LocalReplay> runs, OsuProfile? profile)
    {
        if (profile is not null && runs.Any(run => string.Equals(run.Player, profile.Username, StringComparison.OrdinalIgnoreCase)))
            return profile.Username;
        return runs.Where(run => run.IsLocallyStored && !string.IsNullOrWhiteSpace(run.Player))
            .GroupBy(run => run.Player, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();
    }

    internal static HomeFormSummary Summarise(IReadOnlyList<LocalReplay> runs, DateTimeOffset now, int days)
    {
        DateTimeOffset start = now.AddDays(-days);
        DateTimeOffset previousStart = start.AddDays(-days);
        LocalReplay[] current = runs.Where(run => run.PlayedAt > start && run.PlayedAt <= now.AddMinutes(5)).ToArray();
        LocalReplay[] previous = runs.Where(run => run.PlayedAt > previousStart && run.PlayedAt <= start).ToArray();
        LocalReplay[] currentPassed = current.Where(run => run.Passed).ToArray();
        LocalReplay[] previousPassed = previous.Where(run => run.Passed).ToArray();

        DateOnly today = DateOnly.FromDateTime(now.ToLocalTime().Date);
        var byDay = current.GroupBy(run => DateOnly.FromDateTime(run.PlayedAt.ToLocalTime().Date)).ToDictionary(group => group.Key, group => group.ToArray());
        HomeDailyPoint[] daily = Enumerable.Range(0, days).Select(offset =>
        {
            DateOnly day = today.AddDays(offset - days + 1);
            LocalReplay[] plays = byDay.TryGetValue(day, out var found) ? found : [];
            return new HomeDailyPoint(day, plays.Length, average(plays.Where(run => run.Passed).Select(run => run.Accuracy)));
        }).ToArray();

        return new HomeFormSummary(days, current.Length, previous.Length,
            average(currentPassed.Select(run => run.Accuracy)), average(previousPassed.Select(run => run.Accuracy)),
            average(currentPassed.Select(run => (double)run.MissCount)), average(previousPassed.Select(run => (double)run.MissCount)),
            current.Where(run => run.Passed && run.PerformancePoints is > 0).MaxBy(run => run.PerformancePoints),
            previous.Where(run => run.Passed && run.PerformancePoints is > 0).Max(run => run.PerformancePoints),
            daily);
    }

    internal static IReadOnlyList<HomeRecommendation> Recommend(HomeDashboardInputs inputs, IReadOnlyList<LocalReplay> runs, DateTimeOffset now)
    {
        var result = new List<HomeRecommendation>(3);

        if (runs.Count == 0)
        {
            result.Add(new(HomeRecommendationKind.PlayFirstMap, "Your first play", "Play a map in osu!",
                "Your plays appear here automatically.", "Browse beatmaps"));
        }
        else if (inputs.CoachingPlan is { } plan)
        {
            result.Add(new(HomeRecommendationKind.ContinueCoaching, "Coaching session", plan.Focus,
                $"{plan.TargetTitle} · target {plan.TargetAccuracy:P1}, {plan.TargetMisses} {(plan.TargetMisses == 1 ? "miss" : "misses")} or fewer",
                "Continue"));
        }
        else if (MostMissedMap(runs, now) is { } map)
        {
            result.Add(new(HomeRecommendationKind.PractiseMap, "Fix your misses", map.Title,
                $"{map.AverageMisses:0.#} misses per play over {map.Plays} plays", "Practise map", map.Title, map.StarRating));
        }

        if (BestPpTarget(inputs.PpTargets) is { } target)
            result.Add(target);
        else if (runs.Count > 0)
            result.Add(new(HomeRecommendationKind.FindPpTargets, "PP targets", "Find your next PP play",
                "Maps ranked by the PP you can get.", "Find targets"));

        if (inputs.GuidedPlan is { Finished: false } guided)
        {
            result.Add(new(HomeRecommendationKind.ResumeGuidedPractice, "Guided practice", GuidedFocusName(guided.Focus),
                $"Step {guided.Step + 1} · continue where you left off", "Resume"));
        }
        else
        {
            TrainerResult? last = inputs.TrainerHistory.MaxBy(result => result.CompletedAt);
            string detail = last is null
                ? "A 1-minute timing drill before you play."
                : $"Last trainer session {Ago(last.CompletedAt, now)}.";
            result.Add(new(HomeRecommendationKind.WarmUp, "Warm up", "Tap and aim warm-up", detail, "Start warm-up"));
        }

        return result;
    }

    internal sealed record MissedMap(string Title, int Plays, double AverageMisses, double StarRating);

    /// <summary>The recently repeated map that costs the most misses; a single unlucky play is not a pattern.</summary>
    internal static MissedMap? MostMissedMap(IReadOnlyList<LocalReplay> runs, DateTimeOffset now) =>
        runs.Where(run => run.Passed && run.PlayedAt >= now.AddDays(-30) && run.IsLocallyStored && !string.IsNullOrWhiteSpace(run.Title))
            .GroupBy(run => (run.Title, run.Difficulty))
            .Where(group => group.Count() >= 2)
            .Select(group => new MissedMap(group.Key.Title, group.Count(), group.Average(run => run.MissCount), group.Max(run => run.StarRating)))
            .Where(map => map.AverageMisses >= 1)
            .OrderByDescending(map => map.AverageMisses * Math.Sqrt(map.Plays))
            .FirstOrDefault();

    /// <summary>The cached target with the most expected PP among estimates backed by enough evidence.</summary>
    internal static HomeRecommendation? BestPpTarget(PpTargetWorkspaceSnapshot? snapshot)
    {
        if (snapshot is null)
            return null;
        var difficulties = snapshot.Catalog
            .SelectMany(set => set.Difficulties.Select(difficulty => (set, difficulty)))
            .GroupBy(pair => pair.difficulty.BeatmapId)
            .ToDictionary(group => group.Key, group => group.First());
        var best = snapshot.ExactEstimates
            .Where(pair => pair.Value.Confidence >= PpTargetConfidence.Medium && double.IsFinite(pair.Value.ExpectedPp) && pair.Value.ExpectedPp > 0
                           && difficulties.ContainsKey(pair.Key))
            .OrderByDescending(pair => pair.Value.ExpectedPp)
            .Select(pair => (estimate: pair.Value, map: difficulties[pair.Key]))
            .FirstOrDefault();
        if (best.estimate is null)
            return null;
        string accuracy = best.estimate.ExpectedAccuracy is { } expected ? $" at {expected:P1}" : string.Empty;
        return new(HomeRecommendationKind.PpTarget, "PP target", $"{best.map.set.Title} [{best.map.difficulty.Name}]",
            $"About {best.estimate.ExpectedPp:N0} pp{accuracy}", "See targets", best.map.set.Title, best.map.difficulty.StarRating);
    }

    internal static string GuidedFocusName(TrainerGuidedFocus focus) => focus switch
    {
        TrainerGuidedFocus.MovementComparison => "Compare aim & tapping",
        TrainerGuidedFocus.Spacing => "Build aim distance",
        TrainerGuidedFocus.GroupLength => "Build longer groups",
        _ => "Build endurance",
    };

    public static string Ago(DateTimeOffset time, DateTimeOffset now)
    {
        TimeSpan age = now - time;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
        if (age < TimeSpan.FromDays(2)) return "yesterday";
        if (age < TimeSpan.FromDays(30)) return $"{(int)age.TotalDays} days ago";
        return time.ToLocalTime().ToString("d MMM yyyy");
    }

    private static double? average(IEnumerable<double> values)
    {
        double total = 0;
        int count = 0;
        foreach (double value in values)
        {
            if (!double.IsFinite(value)) continue;
            total += value;
            count++;
        }
        return count == 0 ? null : total / count;
    }
}

/// <summary>
/// Keeps one snapshot of the account totals per day so Home can show how PP and rank moved.
/// The osu! API returns only current totals; the history is recorded on this device.
/// </summary>
internal sealed class HomeProfileHistoryStore(string path)
{
    private const int maximum_entries = 120;

    internal sealed record Entry(DateTimeOffset RecordedAt, double? PerformancePoints, int? GlobalRank);

    public HomeProfileChange? Record(OsuProfile profile, DateTimeOffset now)
    {
        if (profile.Statistics is not { } statistics || statistics.PerformancePoints is null && statistics.GlobalRank is null)
            return null;
        List<Entry> entries = load();
        var today = new Entry(now, statistics.PerformancePoints, statistics.GlobalRank is > 0 ? statistics.GlobalRank : null);
        entries.RemoveAll(entry => entry.RecordedAt.ToLocalTime().Date == now.ToLocalTime().Date || entry.RecordedAt > now);
        HomeProfileChange? change = Compare(entries, today, now);
        entries.Add(today);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries.OrderBy(entry => entry.RecordedAt).TakeLast(maximum_entries)));
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return change;
    }

    /// <summary>Compares with the newest snapshot at least a week old, or else the oldest earlier day.</summary>
    internal static HomeProfileChange? Compare(IReadOnlyList<Entry> earlier, Entry current, DateTimeOffset now)
    {
        Entry? reference = earlier.Where(entry => entry.RecordedAt <= now.AddDays(-7)).MaxBy(entry => entry.RecordedAt)
                           ?? earlier.Where(entry => entry.RecordedAt < now.AddHours(-12)).MinBy(entry => entry.RecordedAt);
        if (reference is null)
            return null;
        double? pp = current.PerformancePoints is { } nowPp && reference.PerformancePoints is { } thenPp ? nowPp - thenPp : null;
        // A lower rank number is better; report positive values as places gained.
        int? rank = current.GlobalRank is { } nowRank && reference.GlobalRank is { } thenRank ? thenRank - nowRank : null;
        return pp is null && rank is null ? null : new HomeProfileChange(pp, rank, reference.RecordedAt);
    }

    private List<Entry> load()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 65536)
                return [];
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path))?.Where(entry => entry is not null).ToList() ?? [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
