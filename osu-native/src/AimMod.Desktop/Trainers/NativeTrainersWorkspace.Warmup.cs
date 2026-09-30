using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Containers;

namespace AimMod.Desktop.Trainers;

public partial class NativeTrainersWorkspace
{
    public Action? OpenBeatmaps { get; set; }
    private Container warmupPage = null!;
    private FillFlowContainer<Drawable> warmupBody = null!;
    private AimModButton warmupEntry = null!;
    private OsuTextFlowContainer warmupStatus = null!;
    private TrainerWarmup? warmup;
    private TrainerSettings? pendingWarmup;
    private TrainerHistoryStore? warmupStore;
    private int? warmupAccount;
    private bool loadingWarmup;
    private int warmupMinutes = 4;
    private string warmupMessage = "";

    private void buildWarmupTab(FillFlowContainer<Drawable> tabs)
    {
        tabs.Add(warmupEntry = new AimModButton("Warmup", ShowWarmup));
        warmupBody = column();
        warmupBody.Padding = new MarginPadding { Right = 14, Bottom = 24 };
        AddInternal(warmupPage = new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Top = 120 }, Alpha = 0,
            Child = new AimModScrollContainer { RelativeSizeAxes = Axes.Both, Child = warmupBody } });
    }

    public void ShowWarmup()
    {
        if (preparing || pendingWarmup is not null || running) return;
        skillPage.Hide(); modPage.Hide(); warmupPage.Show();
        skillEntry.SetSelected(false); dtEntry.SetSelected(false); warmupEntry.SetSelected(true);
        if (warmup is not null && warmupAccount != CurrentSkillAccountId?.Invoke())
        { warmup = null; warmupStore = null; warmupMessage = "Choose a warmup for this player."; }
        renderWarmup();
    }

    private enum WarmupStepState { Planned, Next, Done }

    private void renderWarmup()
    {
        warmupBody.Clear();
        var plan = warmup;
        warmupBody.Add(new AimModSubsectionHeader(plan is null ? "Warm up before your next score" : plan.Finished ? "Warmup complete" : $"Warmup · drill {plan.Step + 1} of {plan.Steps.Count}",
            "timing · aim · speed · all together"));
        if (plan is null)
        {
            var lengths = flow(); warmupBody.Add(lengths);
            foreach (int minutes in new[] { 2, 4, 8 })
            {
                var button = new AimModButton($"{minutes} min", () => { if (loadingWarmup) return; warmupMinutes = minutes; renderWarmup(); });
                button.SetSelected(minutes == warmupMinutes); lengths.Add(button);
            }
            lengths.Add(new AimModButton(loadingWarmup ? "Getting your warmup ready..." : "Prepare warmup", prepareWarmup, true));
            warmupBody.Add(warmupStatus = paragraph(warmupMessage));
            warmupStatus.Alpha = warmupMessage.Length > 0 ? 1 : 0;
            var preview = flow(); preview.Spacing = new(0); warmupBody.Add(preview);
            TrainerKind[] kinds = [TrainerKind.Steady, TrainerKind.Aim, TrainerKind.Bursts, TrainerKind.Rhythm];
            for (int i = 0; i < kinds.Length; i++)
                preview.Add(warmupCard(i + 1, DisplayName(kinds[i]), new TrainerSettings(Kind: kinds[i]), $"{warmupMinutes * 15} s", WarmupStepState.Planned));
            var facts = flow(); facts.Spacing = new(6); warmupBody.Add(facts);
            facts.Add(new AimModTrainerChip($"{warmupMinutes} min of play", FontAwesome.Regular.Clock, tooltip: "Playing time. Count-ins and breaks are extra."));
            facts.Add(new AimModTrainerChip("A different AimMod song per drill", FontAwesome.Solid.Music));
            facts.Add(new AimModTrainerChip("Paced from your recent practice", FontAwesome.Solid.ChartLine, AimModTrainerChipTone.Accent,
                "New players start with gentle defaults. A warmup stays below your practice limits."));
            facts.Add(new AimModTrainerChip("Your osu! controls", FontAwesome.Solid.Keyboard));
            return;
        }
        var stages = flow(); stages.Spacing = new(0); warmupBody.Add(stages);
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            string caption = i < plan.Step ? $"{plan.Results[i].Accuracy:0.0}% accuracy" : i == plan.Step ? "Up next" : $"{step.Settings.Seconds} s";
            stages.Add(warmupCard(i + 1, step.Title, step.Settings, caption, i < plan.Step ? WarmupStepState.Done : i == plan.Step ? WarmupStepState.Next : WarmupStepState.Planned));
        }
        if (!plan.Finished)
        {
            var step = plan.Steps[plan.Step];
            var selected = plan.CurrentSettings();
            warmupBody.Add(text(step.Title, 20, AimModPalette.Text));
            warmupBody.Add(paragraph(step.Hint));
            var chips = flow(); chips.Spacing = new(6); warmupBody.Add(chips);
            chips.Add(new AimModTrainerChip($"{selected.Seconds} s", FontAwesome.Regular.Clock));
            chips.Add(new AimModTrainerChip(TrainerMusicCatalog.Songs[selected.Music], FontAwesome.Solid.Music));
            chips.Add(new AimModTrainerChip($"{selected.Bpm} BPM"));
            chips.Add(new AimModTrainerChip($"up to {selected.SkillLimits!.MaxNps:0.#} taps/s", tooltip: "Highest note rate in this drill."));
            if (plan.Pace < 1) chips.Add(new AimModTrainerChip($"{plan.Pace * 100:0}% pace", FontAwesome.Solid.ArrowDown, AimModTrainerChipTone.Info, "Eased after an earlier drill."));
            var actions = flow(); warmupBody.Add(actions);
            actions.Add(new AimModButton("Start this drill", launchWarmup, true));
            actions.Add(new AimModButton("Make it easier", () => { if (pendingWarmup is not null || preparing) return; plan.Ease(); renderWarmup(); }));
            actions.Add(new AimModButton("Finish here", endWarmup));
            warmupBody.Add(warmupStatus = paragraph(warmupMessage.Length > 0 ? warmupMessage : plan.Advice + " Escape stops a drill without counting it."));
        }
        else
        {
            var actions = flow(); warmupBody.Add(actions);
            actions.Add(new AimModButton("Choose a beatmap", () => (OpenBeatmaps ?? openCoaching)(), true));
            actions.Add(new AimModButton("Back to trainers", showSkillTrainers));
            actions.Add(new AimModButton("New warmup", endWarmup));
            warmupBody.Add(warmupStatus = paragraph(warmupMessage));
        }
        if (plan.Results.LastOrDefault() is {} last)
        {
            var before = plan.Results.Count >= 2 ? plan.Results[^2] : null;
            warmupBody.Add(new AimModSubsectionHeader(plan.Finished ? "Last drill" : "Previous drill", plan.Steps[plan.Results.Count - 1].Title));
            var metrics = flow(); warmupBody.Add(metrics);
            metrics.Add(new AimModTrainerKpi("HIT ACCURACY", $"{last.Accuracy:0.0}%", before is null ? null : $"{last.Accuracy - before.Accuracy:+0.0;-0.0;0}% vs drill before", last.Accuracy - before?.Accuracy, AimModTrainerTrend.HigherIsBetter));
            metrics.Add(new AimModTrainerKpi("MISSES", $"{last.Misses}", $"{last.Hits} of {last.Notes} hit"));
            metrics.Add(new AimModTrainerKpi("TIMING SPREAD", ms(last.SpreadMs), "lower is steadier", tooltip: "How much your tap timing varies (standard deviation)."));
        }
    }

    private static Container warmupCard(int number, string title, TrainerSettings drill, string caption, WarmupStepState state)
    {
        var content = column(); content.Padding = new MarginPadding(12); content.Spacing = new(6);
        var heading = new FillFlowContainer<Drawable> { AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(8) };
        heading.Add(new CircularContainer { Size = new(20), Masking = true, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Children = [
            new Box { RelativeSizeAxes = Axes.Both, Colour = state == WarmupStepState.Planned ? AimModPalette.PanelHover : AimModPalette.Accent },
            state == WarmupStepState.Done
                ? new SpriteIcon { Icon = FontAwesome.Solid.Check, Size = new(10), Anchor = Anchor.Centre, Origin = Anchor.Centre, Colour = AimModPalette.Canvas }
                : new osu.Game.Graphics.Sprites.OsuSpriteText { Text = $"{number}", Anchor = Anchor.Centre, Origin = Anchor.Centre, Font = new FontUsage(size: 11, weight: "Bold"),
                    Colour = state == WarmupStepState.Planned ? AimModPalette.Text : AimModPalette.Canvas },
        ] });
        heading.Add(new osu.Game.Graphics.Sprites.OsuSpriteText { Text = title, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Font = new FontUsage(size: 13, weight: "SemiBold"), Colour = AimModPalette.Text });
        content.Add(heading);
        content.Add(new TrainerDrillPreview(drill, true) { RelativeSizeAxes = Axes.X, Height = 35 });
        content.Add(new osu.Game.Graphics.Sprites.OsuSpriteText { Text = caption, Font = AimModVisualStyle.CaptionStrongFont,
            Colour = state == WarmupStepState.Done ? AimModPalette.Accent : state == WarmupStepState.Next ? AimModPalette.Text : AimModPalette.Muted });
        var card = new Container { RelativeSizeAxes = Axes.X, Height = 104, Masking = true, CornerRadius = AimModVisualStyle.CardRadius,
            BorderThickness = state == WarmupStepState.Next ? 1.5f : 0, BorderColour = AimModPalette.Accent.Opacity(.6f),
            Alpha = state == WarmupStepState.Done ? .85f : 1,
            Children = [new Box { RelativeSizeAxes = Axes.Both, Colour = state == WarmupStepState.Next ? AimModPalette.AccentMuted : AimModPalette.Panel }, content] };
        // Four across on wide pages, two across when narrow (see updateWarmupGrid).
        return new WarmupCell { RelativeSizeAxes = Axes.X, Width = .25f, AutoSizeAxes = Axes.Y, Padding = new MarginPadding { Right = 8, Bottom = 8 }, Child = card };
    }

    private partial class WarmupCell : Container;

    private void updateWarmupGrid()
    {
        if (warmupPage.Alpha <= 0 || warmupBody.DrawWidth <= 0) return;
        float fraction = warmupBody.DrawWidth >= 760 ? .25f : .5f;
        foreach (var grid in warmupBody.OfType<FillFlowContainer<Drawable>>())
            foreach (var cell in grid.Children.OfType<WarmupCell>())
                if (Math.Abs(cell.Width - fraction) > .001f) cell.Width = fraction;
    }

    private async void prepareWarmup()
    {
        if (loadingWarmup || preparing || pendingWarmup is not null) return;
        loadingWarmup = true; warmupMessage = "Getting your warmup ready..."; renderWarmup();
        var store = history(); var account = CurrentSkillAccountId?.Invoke();
        var evidence = SkillEvidenceAccountId is > 0 && SkillEvidenceAccountId == account ? SkillEvidence.ToArray() : [];
        var controls = settings; int minutes = warmupMinutes;
        try
        {
            var plan = await Task.Run(() => TrainerWarmup.Create(minutes, controls, store.Load(), evidence, DateTimeOffset.UtcNow));
            Schedule(() => {
                loadingWarmup = false;
                if (IsDisposed) return;
                if (account != CurrentSkillAccountId?.Invoke()) { warmupMessage = "Your account changed. Prepare a new warmup."; renderWarmup(); return; }
                warmup = plan; warmupStore = store; warmupAccount = account; warmupMessage = ""; renderWarmup();
            });
        }
        // async void: any escaping exception would terminate the app.
        catch (Exception)
        { Schedule(() => { loadingWarmup = false; if (IsDisposed) return; warmupMessage = "Warmup could not be prepared. Try again."; renderWarmup(); }); }
    }

    private void launchWarmup()
    {
        if (warmup is not { Finished: false } plan || preparing || pendingWarmup is not null || running) return;
        if (warmupAccount != CurrentSkillAccountId?.Invoke()) { ShowWarmup(); return; }
        if (LaunchOsuSession is not {} launch) { warmupStatus.Text = "The osu! player is unavailable. Reopen AimMod and try again."; return; }
        if (!customSettings && inheritedSettings is {} inherited) ApplyOsuSettings(inherited);
        plan.SetControls(settings.Keys, settings.OffsetMs);
        pendingGuidedRun = null;
        pendingWarmup = plan.CurrentSettings();
        recordTraining = BeginTrainingSync?.Invoke();
        warmupStatus.Text = "Starting your drill...";
        launch(pendingWarmup, mouseButtons, volume);
    }

    private bool completeWarmup(TrainerResult? result)
    {
        if (pendingWarmup is null) return false;
        var expected = pendingWarmup; pendingWarmup = null;
        bool sameAccount = warmupAccount == CurrentSkillAccountId?.Invoke();
        if (sameAccount && result is not null && result.Settings == expected && warmup?.Record(result) == true)
        {
            var recorded = warmup.Results[^1];
            warmupMessage = "";
            try { warmupStore!.Add(recorded); recordTraining?.Invoke(recorded); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { warmupMessage = "Your result is shown here, but history could not be saved."; }
        }
        else warmupMessage = "Drill stopped or incomplete. Repeat it when you are ready.";
        recordTraining = null;
        ShowWarmup();
        return true;
    }

    private void endWarmup()
    {
        if (pendingWarmup is not null || preparing) return;
        warmupMessage = warmup is { Finished: false } plan ? $"Finished after {plan.Step} of 4 drills. Completed drills are in your practice history." : "";
        warmup = null; warmupStore = null; renderWarmup();
    }
}
