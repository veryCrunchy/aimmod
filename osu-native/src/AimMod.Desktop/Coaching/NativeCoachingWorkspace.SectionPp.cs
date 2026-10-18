using System.Globalization;
using AimMod.Desktop.LocalLibrary;

namespace AimMod.Desktop.Coaching;

/// <summary>Loads where a play lost its pp, section by section, for the map page's list of sections to work on.</summary>
public partial class NativeCoachingWorkspace
{
    private Func<ISectionPpService?>? sectionPp;
    private readonly Dictionary<Guid, SectionPpReport?> sectionPpReports = [];
    private readonly HashSet<Guid> sectionPpLoading = [];
    private int sectionPpAnalyses = -1;
    private Guid? sectionPpShown;

    public void ConfigureSectionPp(Func<ISectionPpService?> service) => sectionPp = service;

    /// <summary>The play's breakdown when it is ready; otherwise starts working it out and redraws the page when done.</summary>
    private SectionPpReport? sectionPpReport(LocalReplay run)
    {
        if (sectionPp?.Invoke() is not { } service || run.RulesetShortName != "osu" || !analyses.ContainsKey(run.ScoreId)) return null;
        // New analyses can change section bests, so reports are worked out again.
        if (analyses.Count != sectionPpAnalyses) { sectionPpReports.Clear(); sectionPpAnalyses = analyses.Count; }
        sectionPpShown = run.ScoreId;
        if (sectionPpReports.TryGetValue(run.ScoreId, out var report)) return report;
        if (sectionPpLoading.Add(run.ScoreId))
        {
            var sameMap = allReplays.Where(r => r.BeatmapHash.Length > 0 && string.Equals(r.BeatmapHash, run.BeatmapHash, StringComparison.OrdinalIgnoreCase)).ToArray();
            var snapshot = analyses.ToDictionary();
            _ = Task.Run(() => service.CalculateAsync(run, snapshot, sameMap, CancellationToken.None)).ContinueWith(task => Schedule(() =>
            {
                sectionPpLoading.Remove(run.ScoreId);
                sectionPpReports[run.ScoreId] = task.IsCompletedSuccessfully ? task.Result : null;
                if (IsLoaded && sectionPpShown == run.ScoreId) renderCoachingMap();
            }));
        }
        return null;
    }

    private static string describe(SectionTally t) =>
        (t.Miss + t.SliderBreaks == 0 ? "no misses" : mistakes(t)) + ", " + hundreds(t);

    private static string mistakes(SectionTally t)
    {
        var parts = new List<string>();
        if (t.Miss > 0) parts.Add(t.Miss == 1 ? "1 miss" : $"{t.Miss} misses");
        if (t.LargeTickMiss > 0) parts.Add(t.LargeTickMiss == 1 ? "1 slider break" : $"{t.LargeTickMiss} slider breaks");
        if (t.SliderTailMiss > 0) parts.Add(t.SliderTailMiss == 1 ? "1 dropped slider end" : $"{t.SliderTailMiss} dropped slider ends");
        return string.Join(", ", parts);
    }

    private static string hundreds(SectionTally t) => t.Meh > 0 ? $"{t.Ok} × 100, {t.Meh} × 50" : $"{t.Ok} × 100";

    private static string points(double pp) => pp.ToString("N1", CultureInfo.InvariantCulture) + " pp";
    private static string gain(double pp) => "+" + Math.Max(0, pp).ToString("N1", CultureInfo.InvariantCulture) + " pp";
}
