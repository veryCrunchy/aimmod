using System.Globalization;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime.Contracts;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Coaching;

/// <summary>Timing, key bias, pattern trends and recent misses across every analysed replay in the filter.</summary>
public partial class NativeStatisticsWorkspace
{
    private const int folder_set_limit = 5_000;
    private static readonly TimeSpan folder_set_lifetime = TimeSpan.FromMinutes(10);
    private IReadOnlyDictionary<Guid, ReplayAnalysisResult>? folderAnalyses;
    private Action<LocalReplay, double>? openFolderMoment;
    private FillFlowContainer<Drawable>? folderSection;
    private int folderRevision;
    private Task<IReadOnlyList<LocalBeatmapSet>>? folderSets;
    private DateTimeOffset folderSetsLoadedAt;
    private DateOnly? compareSince;
    private bool compareLoaded;

    /// <summary>Where the compare-since date is kept. Without one the date lasts until the app closes.</summary>
    public Func<StatisticsComparisonStore?>? ComparisonStore { get; set; }

    /// <summary>Turns on the replay analysis section. Analyses are read as they are, failed plays included.</summary>
    public void ConfigureReplayAnalysis(IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses, Action<LocalReplay, double>? openMoment)
    {
        folderAnalyses = analyses;
        openFolderMoment = openMoment;
        if (lastModel is { } model) renderFolder(model);
    }

    /// <summary>The date the replay analysis compares from, or null to compare earlier and recent halves of the plays.</summary>
    public DateOnly? CompareSince
    {
        get { ensureComparisonLoaded(); return compareSince; }
        set
        {
            compareLoaded = true;
            compareSince = value;
            ComparisonStore?.Invoke()?.Save(value);
            if (lastModel is { } model) renderFolder(model);
        }
    }

    /// <summary>Re-reads the saved date, for example after the osu! account changes.</summary>
    public void ReloadComparison()
    {
        compareLoaded = false;
        if (lastModel is { } model) renderFolder(model);
    }

    private void ensureComparisonLoaded()
    {
        if (compareLoaded) return;
        compareLoaded = true;
        compareSince = ComparisonStore?.Invoke()?.Load();
    }

    private FillFlowContainer<Drawable> createFolderSection() => folderSection = new FillFlowContainer<Drawable>
    {
        RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(AimModVisualStyle.RelatedSpacing),
    };

    private void renderFolder(StatisticsWorkspaceModel model)
    {
        if (folderAnalyses is not { } analyses || folderSection is null) return;
        int revision = ++folderRevision;
        var snapshot = analyses.ToDictionary();
        DateOnly? sinceDate = CompareSince;
        DateTimeOffset? since = sinceDate is { } date ? StatisticsComparisonStore.StartOf(date) : null;
        // A comparison reaches back before the selected period, so it reads every play matching the other filters.
        var filtered = (since is null ? model.Runs : model.MatchingRuns).ToArray();
        var sets = loadFolderSets();
        _ = Task.Run(async () =>
        {
            IReadOnlyList<LocalBeatmapSet> maps;
            try { maps = await sets.ConfigureAwait(false); }
            catch (Exception) { maps = []; }
            return ReplayFolderAnalysis.Build(filtered, snapshot, DateTimeOffset.UtcNow, maps, since);
        }).ContinueWith(task => Schedule(() =>
        {
            if (IsDisposed || revision != folderRevision || folderSection is null) return;
            folderSection.Clear();
            if (task.IsCompletedSuccessfully && task.Result.Plays > 0) showFolder(folderSection, task.Result, sinceDate);
        }));
    }

    /// <summary>Installed maps give each play's circle size, which tells jumps, bursts and streams apart.</summary>
    private Task<IReadOnlyList<LocalBeatmapSet>> loadFolderSets()
    {
        if (folderSets is { IsFaulted: false } loaded && DateTimeOffset.UtcNow - folderSetsLoadedAt < folder_set_lifetime) return loaded;
        folderSetsLoadedAt = DateTimeOffset.UtcNow;
        return folderSets = Task.Run(async () =>
        {
            var found = new List<LocalBeatmapSet>();
            int offset = 0;
            while (found.Count < folder_set_limit)
            {
                var page = await source.SearchBeatmapSetsAsync(new LocalLibraryQuery(RulesetShortName: "osu", Sort: LocalLibrarySort.RecentlyPlayed,
                    Offset: offset, Limit: Math.Min(200, folder_set_limit - found.Count))).ConfigureAwait(false);
                found.AddRange(page.Items);
                if (!page.HasMore || page.Items.Count == 0) break;
                offset += page.Items.Count;
            }
            return (IReadOnlyList<LocalBeatmapSet>)found.DistinctBy(set => set.SetId).ToArray();
        });
    }

    private Drawable comparisonControls(DateOnly? since)
    {
        var row = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(8) };
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        var halves = new AimModButton("Earlier vs recent plays", () => CompareSince = null) { Height = AimModVisualStyle.CompactControlHeight };
        var dated = new AimModButton("Before vs since a date", () => CompareSince ??= today.AddDays(-7)) { Height = AimModVisualStyle.CompactControlHeight };
        halves.SetSelected(since is null); dated.SetSelected(since is not null);
        row.Add(halves); row.Add(dated);
        if (since is { } date)
        {
            row.Add(new AimModButton("−7 days", () => CompareSince = date.AddDays(-7)) { Height = AimModVisualStyle.CompactControlHeight });
            row.Add(new AimModButton("−1 day", () => CompareSince = date.AddDays(-1)) { Height = AimModVisualStyle.CompactControlHeight });
            row.Add(new osu.Framework.Graphics.Containers.Container
            {
                AutoSizeAxes = Axes.X, Height = AimModVisualStyle.CompactControlHeight, Padding = new MarginPadding { Horizontal = 6 },
                Child = StatisticsChartFormat.Text($"Since {date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}", 13, AimModPalette.Text, "SemiBold")
                    .With(t => { t.Anchor = Anchor.CentreLeft; t.Origin = Anchor.CentreLeft; }),
            });
            if (date < today)
                row.Add(new AimModButton("+1 day", () => CompareSince = date.AddDays(1)) { Height = AimModVisualStyle.CompactControlHeight });
            if (date != today)
                row.Add(new AimModButton("Today", () => CompareSince = today) { Height = AimModVisualStyle.CompactControlHeight });
        }
        return row;
    }

    private void showFolder(FillFlowContainer<Drawable> host, ReplayFolderSummary summary, DateOnly? since)
    {
        string failed = summary.FailedPlays > 0 ? $", {summary.FailedPlays:N0} failed" : "";
        host.Add(new AimModSubsectionHeader("Replay analysis", $"{summary.Plays:N0} analysed {(summary.Plays == 1 ? "play" : "plays")}{failed} · {ReplayAnalysisProtocol.EngineVersion}"));
        host.Add(comparisonControls(since));
        var body = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(8), Padding = new MarginPadding(16) };

        if (summary.Comparison is { } comparison)
        {
            string day = since is { } d ? d.ToString("d MMM", CultureInfo.InvariantCulture) : comparison.Since.LocalDateTime.ToString("d MMM", CultureInfo.InvariantCulture);
            body.Add(note($"{comparison.PlaysBefore:N0} analysed {(comparison.PlaysBefore == 1 ? "play" : "plays")} before {day} · {comparison.PlaysSince:N0} since. "
                + "Every play matching your filters counts, whatever the period."));
            body.Add(line("Timing", $"before {timingText(comparison.Before)} → since {timingText(comparison.After)}"));
            var keyNames = comparison.KeysBefore.Select(k => k.Key).Union(comparison.KeysSince.Select(k => k.Key)).Order(StringComparer.Ordinal).ToArray();
            foreach (string key in keyNames)
            {
                var before = comparison.KeysBefore.FirstOrDefault(k => k.Key == key);
                var after = comparison.KeysSince.FirstOrDefault(k => k.Key == key);
                body.Add(line($"{key} key", $"before {keyText(before)} → since {keyText(after)}"));
            }
            if (keyNames.Length == 0) body.Add(note("Which key pressed each hit is recorded for plays analysed from this version on."));
        }
        else
        {
            var timing = summary.Timing;
            if (timing.UnstableRate is { } ur && timing.MeanOffsetMs is { } offset)
            {
                body.Add(line("Timing", $"UR {ci(ur, "0")} · {bias(offset)} · {(timing.EarlyShare ?? 0) * 100:0}% of {timing.Hits:N0} hits early · {plays(ur.Plays)}"));
            }
            else body.Add(line("Timing", "Not enough timed hits yet."));

            if (summary.Keys.Count > 0)
            {
                foreach (var key in summary.Keys)
                    body.Add(line($"{key.Key} key", $"{key.Share * 100:0}% of hits"
                        + (key.MeanOffsetMs is { } m ? $" · {bias(m)}" : "")
                        + (key.UnstableRate is { } u ? $" · UR {ci(u, "0")}" : "")
                        + $" · {plays(key.MeanOffsetMs?.Plays ?? 0)}"));
                if (summary.PlaysWithKeys < summary.Plays)
                    body.Add(note($"Key data is in {summary.PlaysWithKeys:N0} of {summary.Plays:N0} plays. Plays analysed before this version have none."));
            }
            else body.Add(note("Which key pressed each hit is recorded for plays analysed from this version on."));
        }

        if (summary.Patterns.Count > 0)
        {
            string span = summary.Comparison is not null && since is { } d
                ? $"PATTERNS · BEFORE {d.ToString("d MMM", CultureInfo.InvariantCulture).ToUpperInvariant()} → SINCE"
                : "PATTERNS · EARLIER HALF → RECENT HALF";
            body.Add(label(span));
            foreach (var p in summary.Patterns)
                body.Add(line(p.Pattern, $"accuracy {pair(p.EarlierAccuracy, p.RecentAccuracy, v => (v * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%")}"
                    + $" · misses {pair(p.EarlierMissRate, p.RecentMissRate, v => (v * 100).ToString("0.0", CultureInfo.InvariantCulture) + "/100")}"
                    + (p.RecentUnstableRate is not null || p.EarlierUnstableRate is not null ? $" · UR {pair(p.EarlierUnstableRate, p.RecentUnstableRate, v => v.ToString("0", CultureInfo.InvariantCulture))}" : "")
                    + $" · {plays(p.Plays)}"));
            body.Add(note(summary.Comparison is null
                ? "Ranges are 95% confidence intervals of the mean over plays. Halves that overlap may not differ at all."
                : "Ranges are 95% confidence intervals of the mean over plays. A side needs two plays with the pattern. Ranges that overlap may not differ at all."));
        }

        if (summary.RecentMisses.Count > 0)
        {
            body.Add(label("LATEST MISSES"));
            var misses = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(8) };
            foreach (var miss in summary.RecentMisses)
            {
                string text = $"{miss.Run.Title} {AimModCoachTimeline.format(miss.TimeMs)}" + (miss.Reason is { } r and not ReplayMissReason.Unknown ? $" · {reason(r)}" : "");
                misses.Add(miss.Run.HasReplayFile && openFolderMoment is { } open
                    ? new AimModButton(text, () => open(miss.Run, miss.TimeMs)) { Height = 30 }
                    : StatisticsChartFormat.Text(text, 12, AimModPalette.Muted));
            }
            body.Add(misses);
        }
        host.Add(new osu.Framework.Graphics.Containers.Container
        {
            RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Masking = true, CornerRadius = AimModVisualStyle.CardRadius,
            Children = [new osu.Framework.Graphics.Shapes.Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel }, body],
        });
    }

    private static string timingText(FolderTiming timing) => timing.UnstableRate is { } ur
        ? $"UR {ci(ur, "0")}" + (timing.MeanOffsetMs is { } offset ? $", {bias(offset)}" : "") + $" ({plays(ur.Plays)})"
        : timing.Plays == 0 ? "no plays" : "not enough plays";

    private static string keyText(FolderKey? key) => key?.UnstableRate is { } ur
        ? $"UR {ci(ur, "0")}" + (key.MeanOffsetMs is { } m ? $", {bias(m)}" : "") + $" ({plays(ur.Plays)})"
        : "not enough plays";

    private static Drawable line(string title, string text) => new FillFlowContainer
    {
        RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(2),
        Children = [StatisticsChartFormat.Text(title, 13, AimModPalette.Text), wrap(text, 12)],
    };

    private static Drawable wrap(string text, float size) => new osu.Game.Graphics.Containers.OsuTextFlowContainer(t =>
    {
        t.Font = new osu.Framework.Graphics.Sprites.FontUsage(size: size);
        t.Colour = AimModPalette.Muted;
    }) { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = text };

    private static Drawable label(string text) => StatisticsChartFormat.Text(text, 11, AimModPalette.Cyan);
    private static Drawable note(string text) => wrap(text, 11);

    private static string ci(Estimate e, string format) =>
        $"{e.Mean.ToString(format, CultureInfo.InvariantCulture)} ({e.Low.ToString(format, CultureInfo.InvariantCulture)}–{e.High.ToString(format, CultureInfo.InvariantCulture)})";

    private static string bias(Estimate offset)
    {
        string range = $"{Math.Abs(offset.Low).ToString("0.0", CultureInfo.InvariantCulture)}–{Math.Abs(offset.High).ToString("0.0", CultureInfo.InvariantCulture)}";
        if (offset.Low < 0 && offset.High > 0) return $"no clear early or late bias ({offset.Mean.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)} ms)";
        return $"{Math.Abs(offset.Mean).ToString("0.0", CultureInfo.InvariantCulture)} ms {(offset.Mean < 0 ? "early" : "late")} ({range})";
    }

    private static string pair(Estimate? earlier, Estimate? recent, Func<double, string> format) =>
        $"{(earlier is null ? "–" : format(earlier.Mean))} → {(recent is null ? "–" : $"{format(recent.Mean)} ({format(recent.Low)}–{format(recent.High)})")}";

    private static string plays(int count) => count == 1 ? "1 play" : $"{count:N0} plays";

    private static string reason(ReplayMissReason reason) => reason switch
    {
        ReplayMissReason.EarlyClick => "clicked early",
        ReplayMissReason.LateClick => "clicked late",
        ReplayMissReason.Undershoot => "undershot",
        ReplayMissReason.Overshoot => "overshot",
        ReplayMissReason.OnTargetNoClick => "on target, no click",
        ReplayMissReason.AimDeviation => "aim off target",
        _ => reason.ToString().ToLowerInvariant(),
    };
}
