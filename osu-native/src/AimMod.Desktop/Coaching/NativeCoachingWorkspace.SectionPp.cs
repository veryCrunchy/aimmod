using System.Globalization;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Coaching;

/// <summary>Where a play lost its pp, section by section, and the player's best of each section.</summary>
public partial class NativeCoachingWorkspace
{
    private const int costliest_sections = 5;

    private Func<ISectionPpService?>? sectionPp;
    private readonly Dictionary<Guid, SectionPpReport?> sectionPpReports = [];
    private readonly HashSet<Guid> sectionPpLoading = [];
    private int sectionPpAnalyses = -1;
    private Guid? sectionPpShown;

    public void ConfigureSectionPp(Func<ISectionPpService?> service) => sectionPp = service;

    private void renderSectionPp(FillFlowContainer<Drawable> host, LocalReplay run)
    {
        if (sectionPp?.Invoke() is not { } service || run.RulesetShortName != "osu" || !analyses.ContainsKey(run.ScoreId)) return;
        // New analyses can change section bests, so reports are worked out again.
        if (analyses.Count != sectionPpAnalyses) { sectionPpReports.Clear(); sectionPpAnalyses = analyses.Count; }
        sectionPpShown = run.ScoreId;
        if (!sectionPpReports.TryGetValue(run.ScoreId, out var report))
        {
            host.Add(new SectionLine("Where the pp went", "osu!'s pp calculator"));
            host.Add(flow("Working out what each section cost this play...", 13, AimModPalette.Muted));
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
            return;
        }
        if (report is null) return;

        host.Add(new SectionLine("Where the pp went", report.Calculator));
        var body = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(10) };
        if (report.Pp is not { } pp)
        {
            body.Add(flow(report.Passed
                ? "This play's judgements do not cover the whole map, so its pp cannot be split by section."
                : "This play failed, so it has no pp to split. The sections it finished still count towards your section bests.", 13, AimModPalette.Muted));
        }
        else
        {
            string summary = $"This play: {points(pp)}.";
            if (report.SumOfBestPp is { } best)
                summary += $" Your best of every section in one play: {points(best)} ({plays(report.CountedPlays)} at this setup).";
            body.Add(flow(summary, 14, AimModPalette.Text));
            if (report.FullComboOkGoal is { } goal)
                body.Add(flow($"Goal: a full combo with at most {goal:N0} × 100 is worth more than this play.", 13, coachingAccent));
            var costly = report.Costliest(costliest_sections).ToArray();
            if (costly.Length == 0) body.Add(flow("No section cost this play any pp.", 13, AimModPalette.Success));
            foreach (var line in costly) body.Add(sectionPpRow(run, line));
            body.Add(flow("Each number is this play recalculated with only that section changed. pp is not additive, so they do not sum to a total.",
                11, AimModPalette.Muted));
        }
        host.Add(new CoachingCard(body, 16));
    }

    private Drawable sectionPpRow(LocalReplay run, SectionPpLine line)
    {
        var t = line.Tally;
        var parts = new List<string>();
        if (t.Miss + t.SliderBreaks > 0 && line.LostToMisses is { } misses)
            parts.Add($"{mistakes(t)} {gain(misses)}");
        if (t.Ok + t.Meh > 0 && line.LostToHits is { } hits)
            parts.Add($"{hundreds(t)} {gain(hits)}");
        string bestText = line.Best is { } best && line.GainFromBest is > 0.05
            ? $"Your best here: {describe(best.Tally)}, {gain(line.GainFromBest.Value)} for this play."
            : line.Best is not null ? "This is your best of this section." : "";
        var actions = new FillFlowContainer<Drawable> { AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(8) };
        if (canStartAdaptive(run))
            actions.Add(new CoachingButton("Practise", () => playAdaptive(run, line.Section.FirstObject), compact: true));
        if (run.HasReplayFile && openReplayMoment is not null)
            actions.Add(new CoachingButton($"Watch {AimModCoachTimeline.format(line.Section.StartMs)}", () => openReplayMoment(run, line.Section.StartMs), compact: true));
        return new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(4),
            Children =
            [
                label($"Section {line.Section.Index + 1} · {AimModCoachTimeline.format(line.Section.StartMs)}–{AimModCoachTimeline.format(line.Section.EndMs)} · {gain(line.Lost ?? 0)}",
                    13, AimModPalette.Text, "SemiBold"),
                flow(string.Join(" · ", parts), 12, AimModPalette.Muted),
                .. bestText.Length > 0 ? new Drawable[] { flow(bestText, 12, AimModPalette.Muted) } : [],
                actions,
            ],
        };
    }

    private static string describe(SectionTally t)
    {
        var parts = new List<string>();
        parts.Add(t.Miss + t.SliderBreaks == 0 ? "no misses" : mistakes(t));
        parts.Add(hundreds(t));
        return string.Join(", ", parts);
    }

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
    private static string plays(int count) => count == 1 ? "1 play" : $"{count:N0} plays";
}
