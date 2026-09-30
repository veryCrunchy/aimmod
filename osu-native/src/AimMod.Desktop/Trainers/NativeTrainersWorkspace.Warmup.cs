using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
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

    private void renderWarmup()
    {
        warmupBody.Clear();
        warmupBody.Add(text("Warm up before your next score", 20, AimModPalette.Text));
        warmupBody.Add(paragraph("Find your timing, loosen up your aim and bring your fingers back up to pace. Four short drills with room to pause between them."));
        warmupBody.Add(warmupStatus = paragraph(warmupMessage));
        if (warmup is null)
        {
            var lengths = flow(); warmupBody.Add(lengths);
            foreach (int minutes in new[] { 2, 4, 8 })
            {
                var button = new AimModButton($"{minutes} minutes", () => { if (loadingWarmup) return; warmupMinutes = minutes; renderWarmup(); });
                button.SetSelected(minutes == warmupMinutes); lengths.Add(button);
            }
            warmupBody.Add(paragraph("Playing time, plus count-ins and any breaks you take. Uses a different AimMod song for each drill and your connected osu! controls."));
            warmupBody.Add(new AimModButton(loadingWarmup ? "Getting your warmup ready..." : "Prepare warmup", prepareWarmup, true));
            var preview = flow(); warmupBody.Add(preview);
            TrainerKind[] kinds = [TrainerKind.Steady, TrainerKind.Aim, TrainerKind.Bursts, TrainerKind.Rhythm];
            for (int i = 0; i < kinds.Length; i++)
                preview.Add(warmupCard($"{i + 1}. {DisplayName(kinds[i])}", new TrainerSettings(Kind: kinds[i]), $"{warmupMinutes * 15}s", false));
            warmupBody.Add(paragraph("Recent completed practice sets the pace. If you are new here, start with the gentle defaults. This session stays below your practice limits."));
            return;
        }
        var plan = warmup;
        warmupBody.Add(paragraph(plan.Advice));
        if (!plan.Finished)
        {
            var step = plan.Steps[plan.Step];
            var selected = plan.CurrentSettings();
            warmupBody.Add(text($"{plan.Step + 1} / 4 · {step.Title}", 18, AimModPalette.Text));
            warmupBody.Add(paragraph(step.Hint));
            warmupBody.Add(paragraph($"{selected.Seconds}s · {TrainerMusicCatalog.Songs[selected.Music]} · {selected.Bpm} BPM · up to {selected.SkillLimits!.MaxNps:0.#} taps/s"));
            var actions = flow(); warmupBody.Add(actions);
            actions.Add(new AimModButton("Start this drill", launchWarmup, true));
            actions.Add(new AimModButton("Make it easier", () => { if (pendingWarmup is not null || preparing) return; plan.Ease(); renderWarmup(); }));
            actions.Add(new AimModButton("Finish here", endWarmup));
            warmupBody.Add(paragraph("Continue when you feel ready. Escape stops a drill without counting it as complete."));
        }
        else
        {
            warmupBody.Add(text("4 / 4 drills completed", 18, AimModPalette.Text));
            var actions = flow(); warmupBody.Add(actions);
            actions.Add(new AimModButton("Choose a beatmap", () => (OpenBeatmaps ?? openCoaching)(), true));
            actions.Add(new AimModButton("Back to trainers", showSkillTrainers));
            actions.Add(new AimModButton("New warmup", endWarmup));
        }
        if (plan.Results.LastOrDefault() is {} last)
        {
            var metrics = flow(); warmupBody.Add(metrics);
            metrics.Add(metric($"{last.Accuracy:0.0}%", "last drill accuracy"));
            metrics.Add(metric($"{last.Misses}", "misses"));
            metrics.Add(metric(ms(last.SpreadMs), "timing spread"));
        }
        var stages = flow(); warmupBody.Add(stages);
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            string state = i < plan.Step ? $"Completed · {plan.Results[i].Accuracy:0.0}%" : i == plan.Step ? "Up next" : "Later";
            stages.Add(warmupCard($"{i + 1}. {step.Title}", step.Settings, state, i == plan.Step));
        }
    }

    private static Container warmupCard(string title, TrainerSettings drill, string caption, bool active)
    {
        var content = column(); content.Padding = new MarginPadding(12); content.Spacing = new(5);
        content.Add(choiceText(title, 13));
        content.Add(new TrainerDrillPreview(drill, true) { RelativeSizeAxes = Axes.X, Height = 35 });
        content.Add(choiceText(caption, 12));
        return new Container { Width = 230, Height = 102, Masking = true, CornerRadius = AimModVisualStyle.CardRadius,
            Children = [new Box { RelativeSizeAxes = Axes.Both, Colour = active ? AimModPalette.PanelRaised : AimModPalette.Panel }, content] };
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
