using AimMod.Desktop.Coaching;
using AimMod.Desktop.Discovery;
using AimMod.Desktop.LocalLibrary;

namespace AimMod.Desktop.PpTargets;

/// <summary>
/// Developer probe: <c>AimMod --pp-backtest</c> runs the chronological backtest on the local osu! libraries
/// and prints aggregate metrics only; no titles, paths or player names are written.
/// </summary>
internal static class PpTargetBacktestProbe
{
    public static int Run()
    {
        try
        {
            return runAsync().GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            Console.Error.WriteLine($"PP backtest could not read the local library ({error.GetType().Name}).");
            return 1;
        }
    }

    private static async Task<int> runAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        OsuHostPlatform platform = OperatingSystem.IsWindows() ? OsuHostPlatform.Windows
            : OperatingSystem.IsMacOS() ? OsuHostPlatform.MacOS : OsuHostPlatform.Linux;
        var environment = new OsuDiscoveryEnvironment(
            HomeDirectory: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            XdgDataHome: Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
            AppData: Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ExplicitDataRoot: Environment.GetEnvironmentVariable(OsuLazerDiscoveryService.DataRootEnvironmentVariable),
            LocalAppData: Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ExplicitStableRoot: Environment.GetEnvironmentVariable(OsuStableDiscoveryService.InstallRootEnvironmentVariable),
            CurrentUserName: Environment.UserName,
            RegisteredStableRoots: WindowsStableInstallationPaths.Read());
        var files = new PhysicalOsuDiscoveryFileSystem();
        var sources = new List<ILocalLibrarySource>();
        string? root = null;
        if (new OsuLazerDiscoveryService(files).Discover(platform, environment).CompleteDataRoots.FirstOrDefault() is { } lazer)
        {
            sources.Add(new ExternalLazerLocalLibrarySource(lazer.CanonicalPath));
            root = lazer.CanonicalPath;
        }
        if (new OsuStableDiscoveryService(files).Discover(platform, environment).CompleteInstallations.FirstOrDefault() is { } stable)
        {
            sources.Add(new OsuStableLocalLibrarySource(stable.CanonicalPath, stable.SongsPath));
            root ??= stable.CanonicalPath;
        }
        if (sources.Count == 0)
        {
            Console.Error.WriteLine("PP backtest: no osu!lazer or osu!stable library was found.");
            return 1;
        }
        ILocalLibrarySource source = sources.Count == 1 ? sources[0] : new CompositeLocalLibrarySource(sources);
        StatisticsHistoryLoadResult history = await StatisticsHistoryLoader.LoadAsync(source, timeout.Token).ConfigureAwait(false);
        string? player = history.Runs.GroupBy(r => r.Player.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        IReadOnlyList<LocalReplay> runs = PpTargetSkillHistory.ForPlayer(history.Runs, player);
        // Stable scores carry no PP. Reuse AimMod's hydrated PP through a private copy so the app cache is never modified.
        string temporary = Path.Combine(Path.GetTempPath(), $"aimmod-pp-backtest-{Guid.NewGuid():N}.json");
        try
        {
            string saved = Path.Combine(applicationData(), "aimmod", "cache", "local-score-pp-v1.json");
            if (File.Exists(saved)) File.Copy(saved, temporary);
            runs = (await new LocalScorePpHydrationService(root!, temporary).HydrateAsync(runs, timeout.Token).ConfigureAwait(false)).Runs;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        var sets = new List<LocalBeatmapSet>();
        for (int offset = 0; sets.Count < 20_000;)
        {
            var page = await source.SearchBeatmapSetsAsync(new LocalLibraryQuery(RulesetShortName: "osu", Offset: offset, Limit: 200), timeout.Token).ConfigureAwait(false);
            sets.AddRange(page.Items);
            if (!page.HasMore || page.Items.Count == 0) break;
            offset += page.Items.Count;
        }
        var passHistory = PpTargetSkillHistory.PassHistory(runs, [], sets);
        DateTimeOffset newest = runs.Count == 0 ? DateTimeOffset.UtcNow : runs.Max(r => r.PlayedAt);
        var outcomes = PpTargetOutcomeModel.Build(passHistory, runs, sets, newest);
        var curves = PpTargetAnchoredCurve.Index(outcomes.Observations);
        var report = PpTargetBacktest.Run(outcomes.Observations, play => curves.GetValueOrDefault(PpTargetAnchoredCurve.Key(play)));
        Console.WriteLine($"Local plays: {runs.Count}; score observations: {outcomes.Observations.Count}; with recorded PP: {outcomes.Observations.Count(o => o.Pp is > 0)}.");
        Console.WriteLine("Calculator stand-in: each map's curve is anchored on its own recorded PP with a fixed osu!-like shape (see docs/pp-target-skill-fit.md).");
        Console.Write(PpTargetBacktest.Format(report));
        return 0;
    }

    // The desktop host's storage root, as osu!framework resolves it.
    private static string applicationData()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows()) return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Application Support");
        return Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".local", "share");
    }
}

/// <summary>
/// Stand-in for the official calculator when backtesting without beatmap files: a fixed relative shape
/// (osu!-style miss penalty, accuracy and combo scaling) anchored on the map's own recorded PP.
/// </summary>
public sealed class PpTargetAnchoredCurve(double anchor, int objectCount, int maximumCombo) : IPpScenarioCurve
{
    public int ObjectCount { get; } = objectCount;
    public int MaximumCombo { get; } = maximumCombo;

    public double Pp(double accuracy, int misses, int combo) => anchor * Relative(accuracy, misses, combo, ObjectCount, MaximumCombo);

    // Current osu!standard aim/speed miss penalty 0.96 / (m / (4 ln(n)^0.94) + 1), accuracy^4 for the
    // accuracy-scaled components and a mild combo term for slider breaks.
    public static double Relative(double accuracy, int misses, int combo, int objects, int maximumCombo)
    {
        double miss = misses <= 0 ? 1 : .96 / (misses / (4 * Math.Pow(Math.Log(Math.Max(2, objects)), .94)) + 1);
        double comboScale = Math.Pow(Math.Clamp(combo / (double)Math.Max(1, maximumCombo), .01, 1), .1);
        return Math.Pow(Math.Clamp(accuracy, 0, 1), 4) * miss * comboScale;
    }

    public static string Key(PpOutcomeObservation play) => $"{play.MapKey}|{play.Setup}|{play.LegacyScore}";

    public static Dictionary<string, PpTargetAnchoredCurve> Index(IEnumerable<PpOutcomeObservation> observations)
    {
        var curves = new Dictionary<string, PpTargetAnchoredCurve>(StringComparer.Ordinal);
        foreach (var group in observations.GroupBy(Key))
        {
            var plays = group.ToArray();
            int objects = plays.Select(p => p.ObjectCount ?? 0).DefaultIfEmpty(0).Max();
            if (objects <= 0 && plays.FirstOrDefault(p => p.LengthSeconds is > 0) is { } timed) objects = (int)(timed.LengthSeconds!.Value * 2.2);
            int combo = Math.Max(plays.Select(p => p.MapMaxCombo ?? 0).DefaultIfEmpty(0).Max(), plays.Select(p => p.Combo ?? 0).DefaultIfEmpty(0).Max());
            if (objects <= 0 || combo <= 0) continue;
            // Only near-full-combo scores anchor the curve: the shape is least certain where PP has collapsed.
            var clean = plays.Where(p => p.Passed && p.Pp is > 0 && p.Combo is > 0 && p.Accuracy >= .8).ToArray();
            int fewest = clean.Select(p => p.Misses).DefaultIfEmpty(int.MaxValue).Min();
            double[] anchors = clean.Where(p => p.Misses <= Math.Max(fewest, Math.Max(3, objects / 100)))
                .Select(p => p.Pp!.Value / Relative(p.Accuracy, p.Misses, p.Combo!.Value, objects, combo)).Where(double.IsFinite).Order().ToArray();
            if (anchors.Length == 0) continue;
            curves[group.Key] = new PpTargetAnchoredCurve(anchors[anchors.Length / 2], objects, combo);
        }
        return curves;
    }
}
