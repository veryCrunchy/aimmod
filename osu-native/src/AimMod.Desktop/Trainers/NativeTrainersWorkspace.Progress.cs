using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Trainers;

public partial class NativeTrainersWorkspace
{
    private static readonly float[] progressColumns = [.27f, .13f, .2f, .2f, .2f];

    private void renderProgress()
    {
        recent.Clear();
        var kind = settings.Kind;
        var runs = history().Load().Where(r => !r.Assisted && r.Settings.Kind == kind).ToArray();
        historyHeader.Title = $"Progress · {DisplayName(kind)}";
        recentRunsLink.Show(runs.Where(r => r.Settings.Keys == settings.Keys && r.Settings.OffsetMs == settings.OffsetMs).Take(5).Reverse()
            .Select(r => kind == TrainerKind.Reaction ? 100.0 * r.Hits / Math.Max(1, r.Notes) : r.UsesOsuJudgements ? r.Accuracy : null).ToArray(),
            kind == TrainerKind.Reaction ? "correct" : "accuracy");
        if (runs.Length == 0)
        {
            historyHeader.Detail = null;
            recent.Add(paragraph("No sessions yet. Your accuracy and timing spread appear here after your first run."));
            recent.Add(new AimModButton("Start your first session", startSelectedPractice));
            return;
        }
        double minutes = runs.Sum(r => r.PlayedSeconds ?? r.Settings.Seconds) / 60.0;
        historyHeader.Detail = $"{runs.Length} {(runs.Length == 1 ? "session" : "sessions")} · {minutes:0} min";
        // Only runs made with the current keys and offset are compared, so a control change never shifts the trend.
        var comparable = runs.Where(r => r.Settings.Keys == settings.Keys && r.Settings.OffsetMs == settings.OffsetMs).Take(30).Reverse().ToArray();
        bool reactionKind = kind == TrainerKind.Reaction, spinnerKind = kind == TrainerKind.Spinner;
        var bestPrimary = comparable.Where(r => primary(r) is not null).MaxBy(r => primary(r)!.Value);
        var bestSecondary = comparable.Where(r => secondary(r) is not null).MinBy(r => secondary(r)!.Value);
        var kpis = flow();
        if (bestPrimary is not null)
            kpis.Add(new AimModTrainerKpi(reactionKind ? "BEST CORRECT" : "BEST ACCURACY", format(primary(bestPrimary), "%", "0.0"), bestPrimary.CompletedAt.LocalDateTime.ToString("d MMM"), best: true));
        if (bestSecondary is not null)
            kpis.Add(new AimModTrainerKpi(reactionKind ? "FASTEST MEDIAN" : spinnerKind ? "SMOOTHEST SPIN" : "STEADIEST TIMING", format(secondary(bestSecondary), spinnerKind ? "%" : " ms", "0.0"),
                reactionKind ? "median response" : spinnerKind ? "speed variation" : "spread · lower is steadier", best: true,
                tooltip: reactionKind || spinnerKind ? null : "Timing spread is how much your tap timing varies (standard deviation, in ms). Lower is steadier."));
        var week = runs.Where(r => r.CompletedAt >= DateTimeOffset.Now.AddDays(-7)).ToArray();
        kpis.Add(new AimModTrainerKpi("LAST 7 DAYS", $"{week.Length}", $"{week.Sum(r => r.PlayedSeconds ?? r.Settings.Seconds) / 60.0:0} min practised"));
        if (comparable.Length >= 2 && primary(comparable[^1]) is { } latest && primary(comparable[^2]) is { } before)
            kpis.Add(new AimModTrainerKpi("LATEST", format(latest, "%", "0.0"), $"{latest - before:+0.0;-0.0;0} vs previous", latest - before, AimModTrainerTrend.HigherIsBetter));
        if (kpis.Count > 0) recent.Add(kpis);
        if (comparable.Length >= 2)
            recent.Add(new TrainerProgressTrend(comparable.Select(r => new TrainerTrendPoint(r.CompletedAt, primary(r), secondary(r),
                    reactionKind || spinnerKind || r.Settings.Music == "song" ? null : r.Settings.Bpm)).ToArray(),
                reactionKind ? "Correct responses" : "Accuracy", "%",
                reactionKind ? "Median response" : spinnerKind ? "Speed variation" : "Timing spread", spinnerKind ? "%" : " ms",
                reactionKind ? "lower is faster" : spinnerKind ? "lower is smoother" : "lower is steadier"));
        if (comparable.Length < runs.Length)
            recent.Add(paragraph($"{runs.Length - comparable.Length} older {(runs.Length - comparable.Length == 1 ? "run uses" : "runs use")} other keys or offset and {(runs.Length - comparable.Length == 1 ? "is" : "are")} not charted."));
        recent.Add(progressRow(reactionKind ? ["WHEN", "LENGTH", "DRILL", "MEDIAN", "CORRECT"]
            : spinnerKind ? ["WHEN", "LENGTH", "RPM", "HELD", "VARIATION"] : ["WHEN", "LENGTH", "TEMPO", "ACCURACY", "SPREAD"], header: true));
        foreach (var run in runs.Take(8))
        {
            string when = $"{run.CompletedAt.LocalDateTime:d MMM HH:mm}{(run.WarmupRun is null ? "" : " · warmup")}";
            string length = $"{run.PlayedSeconds ?? run.Settings.Seconds:0} s";
            string[] cells = reactionKind
                ? [when, length, ReactionSession.Name(run.Settings.ReactionMode), ms(run.Reaction?.MedianMs ?? run.ResponseMs), $"{run.Hits}/{run.Notes}"]
                : spinnerKind && run.SpinnerPractice is { } spin
                ? [when, length, $"{spin.MeanRpm:0}", $"{spin.HeldPercent:0}%", spin.SpeedVariationPercent is { } v ? $"{v:0}%" : "--"]
                : [when, length, run.Settings.TempoDescription, run.UsesOsuJudgements ? $"{run.Accuracy:0.00}%" : $"{run.OnTimePercent:0.0}% on time", ms(run.SpreadMs)];
            var row = new AimModButton(when, () => { if (!running) { results.Clear(); showResult(run); showingResults = true; setup.Hide(); results.Show(); } });
            row.SetVisualContent(progressRow(cells, bestAccuracy: ReferenceEquals(run, bestPrimary), bestSpread: ReferenceEquals(run, bestSecondary)), 34);
            row.AutoSizeAxes = Axes.None; row.RelativeSizeAxes = Axes.X;
            recent.Add(row);
        }
        var more = flow();
        more.Add(new AimModButton("Try it on a beatmap", openCoaching) { Height = AimModVisualStyle.CompactControlHeight });
        recent.Add(more);

        double? primary(TrainerResult r) => reactionKind ? 100.0 * r.Hits / Math.Max(1, r.Notes) : r.UsesOsuJudgements ? r.Accuracy : null;
        double? secondary(TrainerResult r) => reactionKind ? r.Reaction?.MedianMs ?? r.ResponseMs : spinnerKind ? r.SpinnerPractice?.SpeedVariationPercent : r.SpreadMs;
    }

    private static string format(double? value, string unit, string pattern) => value is { } v ? v.ToString(pattern) + unit : "--";

    private static Container progressRow(string[] cells, bool header = false, bool bestAccuracy = false, bool bestSpread = false)
    {
        var row = new Container { RelativeSizeAxes = header ? Axes.X : Axes.Both, Height = header ? 20 : 1 };
        float x = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            var cell = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.Y, RelativePositionAxes = Axes.X, X = x,
                AutoSizeAxes = Axes.X, Direction = FillDirection.Horizontal, Spacing = new(6), Padding = new MarginPadding { Left = 12 } };
            var label = new OsuSpriteText { Text = cells[i], Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft,
                Font = header ? AimModVisualStyle.CaptionStrongFont : new FontUsage(size: 13, weight: i >= 3 ? "SemiBold" : "Regular"),
                Colour = header ? AimModPalette.Muted : i == 0 ? AimModPalette.Muted : AimModPalette.Text };
            cell.Add(label);
            if (bestAccuracy && i == 3 || bestSpread && i == 4)
                cell.Add(new AimModPill("Best", AimModPillTone.Accent) { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft });
            if (header && cells[i] == "SPREAD")
                row.Add(new TooltipArea("Timing spread: how much your tap timing varies, in ms. Lower is steadier.")
                    { RelativeSizeAxes = Axes.Y, RelativePositionAxes = Axes.X, X = x, Width = .2f });
            row.Add(cell);
            x += progressColumns[Math.Min(i, progressColumns.Length - 1)];
        }
        if (header) row.Add(new Box { RelativeSizeAxes = Axes.X, Height = 1, Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, Colour = AimModPalette.Border });
        return row;
    }

    private partial class TooltipArea(LocalisableString tooltip) : Container, IHasTooltip
    {
        public LocalisableString TooltipText => tooltip;
    }
}
