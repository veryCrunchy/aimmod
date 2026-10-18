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
    private IReadOnlyDictionary<Guid, ReplayAnalysisResult>? folderAnalyses;
    private Action<LocalReplay, double>? openFolderMoment;
    private FillFlowContainer<Drawable>? folderSection;
    private int folderRevision;

    /// <summary>Turns on the replay analysis section. Analyses are read as they are, failed plays included.</summary>
    public void ConfigureReplayAnalysis(IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses, Action<LocalReplay, double>? openMoment)
    {
        folderAnalyses = analyses;
        openFolderMoment = openMoment;
        if (lastModel is { } model) renderFolder(model.Runs);
    }

    private FillFlowContainer<Drawable> createFolderSection() => folderSection = new FillFlowContainer<Drawable>
    {
        RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(AimModVisualStyle.RelatedSpacing),
    };

    private void renderFolder(IReadOnlyList<LocalReplay> runs)
    {
        if (folderAnalyses is not { } analyses || folderSection is null) return;
        int revision = ++folderRevision;
        var snapshot = analyses.ToDictionary();
        var filtered = runs.ToArray();
        _ = Task.Run(() => ReplayFolderAnalysis.Build(filtered, snapshot, DateTimeOffset.UtcNow)).ContinueWith(task => Schedule(() =>
        {
            if (IsDisposed || revision != folderRevision || folderSection is null) return;
            folderSection.Clear();
            if (task.IsCompletedSuccessfully && task.Result.Plays > 0) showFolder(folderSection, task.Result);
        }));
    }

    private void showFolder(FillFlowContainer<Drawable> host, ReplayFolderSummary summary)
    {
        string failed = summary.FailedPlays > 0 ? $", {summary.FailedPlays:N0} failed" : "";
        host.Add(new AimModSubsectionHeader("Replay analysis", $"{summary.Plays:N0} analysed {(summary.Plays == 1 ? "play" : "plays")}{failed} · {ReplayAnalysisProtocol.EngineVersion}"));
        var body = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(8), Padding = new MarginPadding(16) };

        var timing = summary.Timing;
        if (timing.UnstableRate is { } ur && timing.MeanOffsetMs is { } offset)
        {
            body.Add(line("Timing", $"UR {ci(ur, "0")} · {bias(offset)} · {timing.EarlyShare:P0} of {timing.Hits:N0} hits early · {plays(ur.Plays)}"));
        }
        else body.Add(line("Timing", "Not enough timed hits yet."));

        if (summary.Keys.Count > 0)
        {
            foreach (var key in summary.Keys)
                body.Add(line($"{key.Key} key", $"{key.Share:P0} of hits"
                    + (key.MeanOffsetMs is { } m ? $" · {bias(m)}" : "")
                    + (key.UnstableRate is { } u ? $" · UR {ci(u, "0")}" : "")
                    + $" · {plays(key.MeanOffsetMs?.Plays ?? 0)}"));
            if (summary.PlaysWithKeys < summary.Plays)
                body.Add(note($"Key data is in {summary.PlaysWithKeys:N0} of {summary.Plays:N0} plays. Plays analysed before this version have none."));
        }
        else body.Add(note("Which key pressed each hit is recorded for plays analysed from this version on."));

        if (summary.Patterns.Count > 0)
        {
            body.Add(label("PATTERNS · EARLIER HALF → RECENT HALF"));
            foreach (var p in summary.Patterns)
                body.Add(line(p.Pattern, $"accuracy {pair(p.EarlierAccuracy, p.RecentAccuracy, v => v.ToString("P1", CultureInfo.InvariantCulture))}"
                    + $" · misses {pair(p.EarlierMissRate, p.RecentMissRate, v => (v * 100).ToString("0.0", CultureInfo.InvariantCulture) + "/100")}"
                    + (p.RecentUnstableRate is not null ? $" · UR {pair(p.EarlierUnstableRate, p.RecentUnstableRate, v => v.ToString("0", CultureInfo.InvariantCulture))}" : "")
                    + $" · {plays(p.Plays)}"));
            body.Add(note("Ranges are 95% confidence intervals of the mean over plays. Halves that overlap may not differ at all."));
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
