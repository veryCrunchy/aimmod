using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Containers;

namespace AimMod.Desktop.Trainers;

/// <summary>The stream ladder: a fixed benchmark for the fastest stream a player can play cleanly.</summary>
public partial class NativeTrainersWorkspace
{
    private Container ladderPage = null!;
    private FillFlowContainer<Drawable> ladderBody = null!;
    private AimModButton ladderEntry = null!;
    private OsuTextFlowContainer ladderStatus = null!;
    private TrainerStreamLadder? ladder;
    private TrainerSettings? pendingLadder;
    private TrainerHistoryStore? ladderStore;
    private int? ladderAccount;
    private int? ladderEdge;
    private double? ladderLimit;
    private string ladderMessage = "";

    private void buildLadderTab(FillFlowContainer<Drawable> tabs)
    {
        tabs.Add(ladderEntry = new AimModButton("Stream ladder", ShowStreamLadder));
        ladderBody = column();
        ladderBody.Padding = new MarginPadding { Right = 14, Bottom = 24 };
        AddInternal(ladderPage = new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Top = 120 }, Alpha = 0,
            Child = new AimModScrollContainer { RelativeSizeAxes = Axes.Both, Child = ladderBody } });
    }

    private bool sessionPending => pendingWarmup is not null || pendingLadder is not null;

    public void ShowStreamLadder()
    {
        if (preparing || sessionPending || running) return;
        Suspend();
        skillPage.Hide(); modPage.Hide(); warmupPage.Hide(); ladderPage.Show();
        skillEntry.SetSelected(false); dtEntry.SetSelected(false); warmupEntry.SetSelected(false); ladderEntry.SetSelected(true);
        if (ladder is not null && ladderAccount != CurrentSkillAccountId?.Invoke())
        { ladder = null; ladderStore = null; ladderEdge = null; ladderLimit = null; ladderMessage = "Start a ladder for this player."; }
        renderLadder();
    }

    private void hideLadder() { ladderPage.Hide(); ladderEntry.SetSelected(false); }

    private void renderLadder()
    {
        ladderBody.Clear();
        var store = ladderStore ?? history();
        var saved = store.Load();
        var outcomes = TrainerStreamLadder.Outcomes(saved);
        var plan = ladder;
        ladderBody.Add(new AimModSubsectionHeader(plan is null ? "Stream ladder" : plan.Finished ? "Ladder complete" : $"Stream ladder · step {plan.Results.Count + 1}",
            "full streams · 15 s a step · 10 BPM apart"));
        if (plan is null) renderLadderSetup(saved);
        else if (!plan.Finished) renderLadderStep(plan);
        else renderLadderResult(plan, outcomes.FirstOrDefault(o => o.SessionId != plan.Id && o.Complete));
        if (plan is not null) ladderBody.Add(ladderSteps(plan));
        addLadderProgress(ladderBody, outcomes, showOpen: false);
    }

    private void renderLadderSetup(IReadOnlyList<TrainerResult> saved)
    {
        var defaults = TrainerStreamLadder.Defaults(saved, settings, DateTimeOffset.UtcNow);
        int edge = ladderEdge ?? defaults.EdgeBpm;
        double limit = ladderLimit ?? defaults.SpreadLimitPercent;
        bool custom = ladderEdge is not null || ladderLimit is not null;
        int start = TrainerStreamLadder.StartFor(edge);
        ladderBody.Add(paragraph("Finds the fastest stream you can play cleanly. You play full streams for 15 seconds, starting 20 BPM below your edge tempo. "
            + "A clean step goes 10 BPM faster until a step is not clean. If the first step is not clean, the ladder goes 10 BPM slower until one is. "
            + "Clean means no misses and even timing: spread within a share of the gap between notes, so slow and fast streams are judged alike. "
            + "Adaptive difficulty is off and the layout never changes, so ladders on different days compare fairly."));
        var steppers = flow(); steppers.Spacing = new(24, 8); ladderBody.Add(steppers);
        steppers.Add(stepper("EDGE TEMPO", $"{edge} BPM", "Where your streams start to fall apart. The ladder starts 20 BPM below it.",
            () => adjustLadder(-TrainerStreamLadder.BpmStep, 0, edge, limit), () => adjustLadder(TrainerStreamLadder.BpmStep, 0, edge, limit),
            edge - TrainerStreamLadder.BpmStep >= TrainerStreamLadder.MinimumBpm + TrainerStreamLadder.StartBelowEdge, edge < TrainerStreamLadder.MaximumBpm));
        steppers.Add(stepper("SPREAD LIMIT", $"{limit:0}% of gap",
            $"How uneven a step may be and still count as clean. At {start} BPM the notes are {TrainerStreamLadder.NoteGapMs(start):0} ms apart, "
            + $"so the limit is {TrainerStreamLadder.SpreadLimitMs(start, limit):0.0} ms.",
            () => adjustLadder(0, -1, edge, limit), () => adjustLadder(0, 1, edge, limit),
            limit - 1 >= TrainerStreamLadder.MinimumSpreadLimitPercent, limit + 1 <= TrainerStreamLadder.MaximumSpreadLimitPercent));
        var chips = flow(); chips.Spacing = new(6); ladderBody.Add(chips);
        chips.Add(new AimModTrainerChip($"Starts at {start} BPM", FontAwesome.Solid.Music, AimModTrainerChipTone.Accent));
        chips.Add(new AimModTrainerChip("15 s a step", FontAwesome.Regular.Clock));
        chips.Add(new AimModTrainerChip($"Clean: no misses, spread up to {limit:0}% of the note gap", FontAwesome.Solid.FlagCheckered));
        chips.Add(new AimModTrainerChip(custom ? "Your own start and limit" : defaults.Source, FontAwesome.Solid.ChartLine, AimModTrainerChipTone.Info,
            "Keep the same start and limit between ladders so their results compare."));
        chips.Add(new AimModTrainerChip("Your osu! controls", FontAwesome.Solid.Keyboard));
        var actions = flow(); ladderBody.Add(actions);
        actions.Add(new AimModButton("Start the ladder", () => beginLadder(edge, limit), true));
        if (custom) actions.Add(new AimModButton("Use suggested values", () => { ladderEdge = null; ladderLimit = null; renderLadder(); }));
        ladderBody.Add(ladderStatus = paragraph(ladderMessage));
        ladderStatus.Alpha = ladderMessage.Length > 0 ? 1 : 0;
    }

    private void adjustLadder(int bpm, int ms, int edge, double limit)
    {
        ladderEdge = Math.Clamp(edge + bpm, TrainerStreamLadder.MinimumBpm + TrainerStreamLadder.StartBelowEdge, TrainerStreamLadder.MaximumBpm);
        ladderLimit = Math.Clamp(limit + ms, TrainerStreamLadder.MinimumSpreadLimitPercent, TrainerStreamLadder.MaximumSpreadLimitPercent);
        renderLadder();
    }

    private static Drawable stepper(string label, string value, string hint, Action down, Action up, bool canDown, bool canUp)
    {
        var body = new FillFlowContainer<Drawable> { AutoSizeAxes = Axes.Both, Direction = FillDirection.Vertical, Spacing = new(6) };
        body.Add(text(label, 11, AimModPalette.Muted));
        var row = new FillFlowContainer<Drawable> { AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(8) };
        var lower = new AimModButton("−", () => { if (canDown) down(); }) { Height = AimModVisualStyle.CompactControlHeight, Alpha = canDown ? 1 : .4f };
        var higher = new AimModButton("+", () => { if (canUp) up(); }) { Height = AimModVisualStyle.CompactControlHeight, Alpha = canUp ? 1 : .4f };
        var shown = text(value, 18, AimModPalette.Text);
        shown.Anchor = shown.Origin = Anchor.CentreLeft; lower.Anchor = lower.Origin = Anchor.CentreLeft; higher.Anchor = higher.Origin = Anchor.CentreLeft;
        row.Add(lower); row.Add(new Container { Width = 84, AutoSizeAxes = Axes.Y, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Child = shown }); row.Add(higher);
        body.Add(row);
        body.Add(new OsuTextFlowContainer(t => { t.Font = new FontUsage(size: 12); t.Colour = AimModPalette.Muted; }) { Width = 260, AutoSizeAxes = Axes.Y, Text = hint });
        return body;
    }

    private void beginLadder(int edge, double limit)
    {
        if (preparing || sessionPending || running) return;
        if (!customSettings && inheritedSettings is { } inherited) ApplyOsuSettings(inherited);
        ladder = new TrainerStreamLadder(edge, limit, settings.Keys, settings.OffsetMs);
        ladderStore = history(); ladderAccount = CurrentSkillAccountId?.Invoke(); ladderMessage = "";
        renderLadder();
    }

    private void renderLadderStep(TrainerStreamLadder plan)
    {
        ladderBody.Add(text($"{plan.CurrentBpm} BPM", 28, AimModPalette.Text));
        ladderBody.Add(paragraph(plan.Results.Count == 0
            ? "Alternate evenly from the first note to the last. The count-in gives you four beats to settle."
            : plan.SteppingDown
                ? "Not clean yet. The ladder goes 10 BPM slower until a step is clean. Relax and keep the gaps even."
                : "Clean. Take a breath, then go 10 BPM faster. Keep the last notes as even as the first."));
        var chips = flow(); chips.Spacing = new(6); ladderBody.Add(chips);
        chips.Add(new AimModTrainerChip("Full streams", FontAwesome.Solid.Water));
        chips.Add(new AimModTrainerChip("15 s", FontAwesome.Regular.Clock));
        chips.Add(new AimModTrainerChip($"Clean: no misses, spread up to {TrainerStreamLadder.SpreadLimitMs(plan.CurrentBpm, plan.SpreadLimitPercent):0.0} ms",
            FontAwesome.Solid.Check, AimModTrainerChipTone.Accent, $"{plan.SpreadLimitPercent:0}% of the {TrainerStreamLadder.NoteGapMs(plan.CurrentBpm):0} ms between notes at this tempo."));
        chips.Add(new AimModTrainerChip(plan.Keys, FontAwesome.Solid.Keyboard));
        var actions = flow(); ladderBody.Add(actions);
        actions.Add(new AimModButton("Start this step", launchLadder, true));
        actions.Add(new AimModButton("Finish here", endLadder));
        ladderBody.Add(ladderStatus = paragraph(ladderMessage.Length > 0 ? ladderMessage
            : "Escape stops a step without counting it. Finishing early keeps the steps you played, but the ladder stays out of the trend."));
    }

    private void renderLadderResult(TrainerStreamLadder plan, TrainerLadderOutcome? previous)
    {
        var kpis = flow(); ladderBody.Add(kpis);
        int? highest = plan.HighestCleanBpm;
        string change = previous is null ? "first complete ladder"
            : highest is { } h && previous.HighestCleanBpm is { } p ? h == p ? "same as last ladder" : $"{h - p:+0;-0} BPM vs last ladder"
            : previous.HighestCleanBpm is null ? "last ladder had no clean step" : "no clean step this time";
        double? delta = highest is { } a && previous?.HighestCleanBpm is { } b ? a - b : null;
        kpis.Add(new AimModTrainerKpi("HIGHEST CLEAN BPM", highest is { } value ? $"{value}" : "--", change, delta, AimModTrainerTrend.HigherIsBetter,
            tooltip: "The fastest step with no misses and timing spread within your limit."));
        var top = plan.Results.LastOrDefault(r => r.LadderRun!.Clean);
        kpis.Add(new AimModTrainerKpi("SPREAD AT THAT TEMPO", ms(top?.SpreadMs),
            top is null ? $"limit {plan.SpreadLimitPercent:0}% of gap" : $"limit {TrainerStreamLadder.SpreadLimitMs(top.Settings.Bpm, plan.SpreadLimitPercent):0.0} ms",
            tooltip: "Timing spread of your fastest clean step. Lower is steadier."));
        kpis.Add(new AimModTrainerKpi("STEPS", $"{plan.Results.Count}", $"from {plan.StartBpm} BPM"));
        ladderBody.Add(paragraph(highest is null
            ? $"{plan.StopReason} Try a higher spread limit, or practise Streams & alternating at a slow tempo first."
            : plan.ReachedTop || plan.SteppingDown ? plan.StopReason : $"Ended: {plan.StopReason}."));
        var actions = flow(); ladderBody.Add(actions);
        actions.Add(new AimModButton("New ladder", endLadder, true));
        actions.Add(new AimModButton("Back to trainers", showSkillTrainers));
        ladderBody.Add(ladderStatus = paragraph(ladderMessage));
        ladderStatus.Alpha = ladderMessage.Length > 0 ? 1 : 0;
    }

    private static Drawable ladderSteps(TrainerStreamLadder plan)
    {
        var steps = flow(); steps.Spacing = new(6);
        foreach (var run in plan.Results)
        {
            bool clean = run.LadderRun!.Clean;
            string detail = clean ? $"clean · {run.SpreadMs:0.0} ms" : run.Misses > 0 ? $"{run.Misses} {(run.Misses == 1 ? "miss" : "misses")}" : $"{run.SpreadMs:0.0} ms";
            steps.Add(new AimModTrainerChip($"{run.Settings.Bpm} BPM · {detail}", clean ? FontAwesome.Solid.Check : FontAwesome.Solid.Times,
                clean ? AimModTrainerChipTone.Accent : AimModTrainerChipTone.Neutral));
        }
        if (!plan.Finished) steps.Add(new AimModTrainerChip($"{plan.CurrentBpm} BPM · up next", FontAwesome.Solid.ArrowRight, AimModTrainerChipTone.Info));
        return steps;
    }

    /// <summary>Highest clean BPM over time. Shared by the ladder page and the streams progress panel.</summary>
    private void addLadderProgress(FillFlowContainer<Drawable> host, IReadOnlyList<TrainerLadderOutcome> outcomes, bool showOpen)
    {
        var complete = outcomes.Where(o => o.Complete).ToArray();
        host.Add(new AimModSubsectionHeader("Highest clean BPM", complete.Length == 0 ? null
            : $"{complete.Length} {(complete.Length == 1 ? "ladder" : "ladders")}"));
        if (complete.Length == 0)
        {
            host.Add(paragraph(outcomes.Count > 0 ? "Finish a ladder to start your trend. Ladders ended early are listed but not charted."
                : "No ladders yet. One ladder takes a few minutes and gives you a number to beat."));
            if (showOpen) host.Add(new AimModButton("Open the stream ladder", ShowStreamLadder));
            if (outcomes.Count == 0) return;
        }
        else
        {
            var kpis = flow(); host.Add(kpis);
            var latest = complete[0];
            var best = complete.Where(o => o.HighestCleanBpm is not null).MaxBy(o => o.HighestCleanBpm!.Value);
            double? delta = complete.Length >= 2 && latest.HighestCleanBpm is { } now && complete[1].HighestCleanBpm is { } before ? now - before : null;
            kpis.Add(new AimModTrainerKpi("LATEST", latest.HighestCleanBpm is { } value ? $"{value} BPM" : "--",
                delta is { } d ? d == 0 ? "same as previous" : $"{d:+0;-0} BPM vs previous"
                : latest.HighestCleanBpm is null ? $"no clean step · {latest.CompletedAt.LocalDateTime:d MMM}" : latest.CompletedAt.LocalDateTime.ToString("d MMM"),
                delta, AimModTrainerTrend.HigherIsBetter));
            if (best is not null)
                kpis.Add(new AimModTrainerKpi("BEST", $"{best.HighestCleanBpm} BPM", best.CompletedAt.LocalDateTime.ToString("d MMM"), best: true));
            if (complete.Select(o => (o.StartBpm, o.SpreadLimitPercent)).Distinct().Count() > 1)
                host.Add(paragraph("Some ladders used a different start or limit. Compare ladders with the same settings."));
            if (complete.Length >= 2)
                host.Add(new TrainerProgressTrend(complete.Reverse().Select(o => new TrainerTrendPoint(o.CompletedAt, o.HighestCleanBpm, o.SpreadAtStartMs, null)).ToArray(),
                    "Highest clean BPM", " BPM", "Spread on the first step", " ms", "same tempo every ladder · lower is steadier"));
            if (showOpen) host.Add(new AimModButton("Open the stream ladder", ShowStreamLadder));
        }
        if (showOpen) return;
        host.Add(progressRow(["WHEN", "START", "HIGHEST", "LIMIT", "ENDED"], header: true));
        foreach (var outcome in outcomes.Take(8))
        {
            var row = progressRow([outcome.CompletedAt.LocalDateTime.ToString("d MMM HH:mm"), $"{outcome.StartBpm} BPM",
                outcome.HighestCleanBpm is { } h ? $"{h} BPM" : "no clean step", $"{outcome.SpreadLimitPercent:0}% of gap",
                outcome.Complete ? $"{outcome.Rungs} {(outcome.Rungs == 1 ? "step" : "steps")}" : "finished early"]);
            host.Add(new Container { RelativeSizeAxes = Axes.X, Height = 34, Child = row });
        }
    }

    private void launchLadder()
    {
        if (ladder is not { Finished: false } plan || preparing || sessionPending || running) return;
        if (ladderAccount != CurrentSkillAccountId?.Invoke()) { ShowStreamLadder(); return; }
        if (LaunchOsuSession is not { } launch) { ladderStatus.Text = "The osu! player is unavailable. Reopen AimMod and try again."; return; }
        if (!customSettings && inheritedSettings is { } inherited) ApplyOsuSettings(inherited);
        plan.SetControls(settings.Keys, settings.OffsetMs);
        pendingGuidedRun = null;
        pendingLadder = plan.CurrentSettings();
        recordTraining = BeginTrainingSync?.Invoke();
        ladderStatus.Text = "Starting the step...";
        launch(pendingLadder, mouseButtons, volume);
    }

    private bool completeLadder(TrainerResult? result)
    {
        if (pendingLadder is null) return false;
        var expected = pendingLadder; pendingLadder = null;
        bool sameAccount = ladderAccount == CurrentSkillAccountId?.Invoke();
        if (sameAccount && result is not null && result.Settings == expected && ladder?.Record(result) == true)
        {
            var recorded = ladder.Results[^1];
            ladderMessage = "";
            try { ladderStore!.Add(recorded); recordTraining?.Invoke(recorded); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { ladderMessage = "Your result is shown here, but history could not be saved."; }
        }
        else ladderMessage = "Step stopped or incomplete. Start it again when you are ready.";
        recordTraining = null;
        ShowStreamLadder();
        refreshHistory();
        return true;
    }

    private void endLadder()
    {
        if (sessionPending || preparing) return;
        ladderMessage = ladder is { Finished: false, Results.Count: > 0 } plan
            ? $"Finished after {plan.Results.Count} {(plan.Results.Count == 1 ? "step" : "steps")}. They are in your history, but this ladder is not charted." : "";
        ladder = null; ladderStore = null; renderLadder();
    }
}
