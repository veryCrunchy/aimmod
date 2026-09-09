using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;

namespace AimMod.Desktop.Trainers;

public partial class NativeTrainersWorkspace
{
    private TrainerGuidedPlan? guidedPlan;
    private TrainerGuidedRun? pendingGuidedRun;
    private TrainerHistoryStore? guidedStore;
    private int? guidedAccountId;
    private enum PracticeIntent { Quick, Compare, Build }
    private PracticeIntent practiceIntent;
    private TrainerGuidedFocus buildFocus = TrainerGuidedFocus.Endurance;
    private readonly Dictionary<PracticeIntent, AimModButton> intentButtons = [];
    private readonly Dictionary<TrainerGuidedFocus, AimModButton> buildButtons = [];
    private FillFlowContainer<Drawable> buildChoices = null!;
    private osu.Game.Graphics.Containers.OsuTextFlowContainer intentDescription = null!;
    private FillFlowContainer<Drawable> practiceOptions = null!;
    private AimModButton practiceOptionsToggle = null!;
    private AimModButton resumePractice = null!;

    internal void TogglePracticeOptions()
    {
        bool open = practiceOptions.Alpha == 0;
        practiceOptions.Alpha = open ? 1 : 0;
        practiceOptionsToggle.SetSelected(open);
        practiceOptionsToggle.SetCaption(settings.Kind == TrainerKind.Reaction ? open ? "Hide cue settings" : "Adjust cue settings"
            : open ? "Hide patterns & difficulty" : "Adjust patterns & difficulty");
    }

    private void buildGuidedControls(FillFlowContainer<Drawable> body)
    {
        body.Add(text("How do you want to practise?", 13, AimModPalette.Muted));
        var choices = flow();
        foreach (var (intent, caption) in new[] { (PracticeIntent.Quick, "Practise a skill"),
            (PracticeIntent.Compare, "Compare aim & tapping"), (PracticeIntent.Build, "Build consistency") })
        {
            var button = new AimModButton(caption, () => { practiceIntent = intent; refreshPracticeIntent(); });
            button.AutoSizeAxes = Axes.None;
            button.Width = 220;
            button.SetVisualContent(modeContent(caption, intent switch
            {
                PracticeIntent.Compare => "Less movement vs full movement",
                PracticeIntent.Build => "Repeat, then raise the challenge",
                _ => "One focused run at your pace",
            }, intent switch
            {
                PracticeIntent.Compare => FontAwesome.Solid.BalanceScale,
                PracticeIntent.Build => FontAwesome.Solid.ChartLine,
                _ => FontAwesome.Solid.Bullseye,
            }), 56);
            intentButtons[intent] = button; choices.Add(button);
        }
        body.Add(choices);
        body.Add(intentDescription = paragraph(""));
        body.Add(buildChoices = flow());
        foreach (var (focus, caption) in new[] { (TrainerGuidedFocus.Endurance, "Hold the rhythm longer"),
            (TrainerGuidedFocus.Spacing, "Increase aim distance"), (TrainerGuidedFocus.GroupLength, "Extend bursts / streams") })
        {
            var button = new AimModButton(caption, () => { buildFocus = focus; refreshPracticeIntent(); });
            buildButtons[focus] = button; buildChoices.Add(button);
        }
        body.Add(resumePractice = new AimModButton("Continue unfinished practice", ResumeGuidedPractice));
    }

    private void refreshPracticeIntent()
    {
        if (intentDescription is null) return;
        bool reaction = settings.Kind == TrainerKind.Reaction;
        bool singleRun = reaction || settings.Kind == TrainerKind.Spinner;
        if (practiceOptionsToggle is not null) practiceOptionsToggle.Alpha = settings.Kind == TrainerKind.Spinner ? 0 : 1;
        if (settings.Kind == TrainerKind.Spinner) practiceOptions.Hide();
        if (practiceOptionsToggle is not null) practiceOptionsToggle.SetCaption(reaction ? practiceOptions.Alpha > 0 ? "Hide cue settings" : "Adjust cue settings"
            : practiceOptions.Alpha > 0 ? "Hide patterns & difficulty" : "Adjust patterns & difficulty");
        if (singleRun) practiceIntent = PracticeIntent.Quick;
        bool groups = settings.Kind is TrainerKind.Bursts or TrainerKind.Alternating;
        if (!groups && buildFocus == TrainerGuidedFocus.GroupLength) buildFocus = TrainerGuidedFocus.Endurance;
        foreach (var (intent, button) in intentButtons)
        {
            button.Alpha = !singleRun || intent == PracticeIntent.Quick ? 1 : 0;
            button.SetSelected(intent == practiceIntent);
        }
        buildChoices.Alpha = practiceIntent == PracticeIntent.Build ? 1 : 0;
        foreach (var (focus, button) in buildButtons)
        {
            button.Alpha = focus != TrainerGuidedFocus.GroupLength || groups ? 1 : 0;
            button.SetSelected(buildFocus == focus);
        }
        resumePractice.Alpha = history().LoadGuidedPlan() is { Finished: false } ? 1 : 0;
        refreshMusicDescription();
        intentDescription.Text = practiceIntent switch
        {
            PracticeIntent.Compare => "Does moving the cursor affect your timing? Play six short runs with less and more movement, then compare your results.",
            PracticeIntent.Build => "Repeat three runs on the same song and pattern. Use your results to decide when to add distance, length or notes.",
            _ => "Pick a skill and play a short exercise. Your results show what to work on next. You can start with the settings below.",
        };
        intentDescription.Alpha = practiceIntent == PracticeIntent.Quick ? 0 : 1;
        if (!running) start?.SetCaption(practiceIntent switch
        {
            PracticeIntent.Compare => "Set up movement comparison",
            PracticeIntent.Build => "Set up progression",
            _ => "Start practice",
        });
    }

    private void startSelectedPractice()
    {
        if (practiceIntent == PracticeIntent.Quick) { Start(); return; }
        if (practiceIntent == PracticeIntent.Build && buildFocus == TrainerGuidedFocus.GroupLength && settings.RandomizePatterns)
        {
            // A progression needs one repeatable pattern. Preserve the saved randomizer preference.
            settings = settings with { RandomizePatterns = false };
            refreshPatternToggle();
        }
        StartGuidedPractice(practiceIntent == PracticeIntent.Compare ? TrainerGuidedFocus.MovementComparison : buildFocus);
    }

    public void StartGuidedPractice(TrainerGuidedFocus focus)
    {
        if (running || preparing) return;
        if (settings.Kind == TrainerKind.Reaction)
        { status.Text = "Choose a tapping, aim or reading exercise for guided practice."; return; }
        if (focus == TrainerGuidedFocus.GroupLength && settings.Kind is not (TrainerKind.Bursts or TrainerKind.Alternating))
        { status.Text = "Choose Burst control or Streams & alternating to build group length."; return; }
        if (focus == TrainerGuidedFocus.GroupLength && settings.RandomizePatterns)
        { status.Text = "Turn the skill randomizer off and choose one burst or stream pattern before building group length."; return; }
        if (!customSettings && inheritedSettings is {} inherited) ApplyOsuSettings(inherited);
        var baseline = TrainerSkillProfile.Apply(settings, currentSkillLimits());
        guidedPlan = TrainerGuidedPractice.Create(baseline, focus);
        guidedStore = history();
        guidedAccountId = CurrentSkillAccountId?.Invoke();
        saveGuidedPlan();
        showGuidedOverview();
    }

    public void ResumeGuidedPractice()
    {
        if (running || preparing) return;
        guidedStore = history();
        guidedAccountId = CurrentSkillAccountId?.Invoke();
        guidedPlan = guidedStore.LoadGuidedPlan();
        if (guidedPlan is null || guidedPlan.Finished)
        { status.Text = "No unfinished guided practice. Choose a focus to start one."; return; }
        showGuidedOverview();
    }

    private void showGuidedOverview()
    {
        if (guidedPlan is not {} plan) return;
        results.Clear(); showingResults = true; setup.Hide(); results.Show();
        contentScroll.ScrollTo(0, false);
        results.Add(text(plan.Focus switch { TrainerGuidedFocus.MovementComparison => "Compare aim & tapping",
            TrainerGuidedFocus.Spacing => "Build aim distance", TrainerGuidedFocus.GroupLength => "Build longer groups", _ => "Build endurance" }, 20, AimModPalette.Text));
        results.Add(paragraph($"{DisplayName(plan.Baseline.Kind)} · {plan.Baseline.TempoDescription} · {plan.Baseline.Seconds} seconds"));
        results.Add(paragraph(plan.Focus == TrainerGuidedFocus.MovementComparison
            ? "Six runs, alternating original and compact movement. Keep your setup unchanged and take a break whenever you need one. Only cursor travel changes."
            : "Repeat the same setup three times before considering a change. Each next step changes only the demand you chose. You decide when to continue."));
        results.Add(paragraph("Music and layout stay fixed for this practice. Keys and offset use the setup selected when you started."));
        addGuidedActions(null);
    }

    private void launchGuidedRun()
    {
        if (guidedPlan is not { Finished: false } plan || preparing || running) return;
        if (CurrentSkillAccountId?.Invoke() != guidedAccountId)
        { results.Add(paragraph("Your account changed. Resume guided practice for the current player before starting.")); return; }
        if (LaunchOsuSession is not {} launch)
        { results.Add(paragraph("The osu! player is unavailable. Open the app again and resume this practice.")); return; }
        var selected = TrainerGuidedPractice.SettingsFor(plan);
        if (selected.Music == "song" && (SelectedSong is null || settings.SongIdentity != selected.SongIdentity))
        { results.Add(paragraph($"Select {selected.SongTitle} in Practice settings, then resume this guided practice.")); return; }
        pendingGuidedRun = TrainerGuidedPractice.Stamp(plan);
        activeHistory = guidedStore ?? history();
        recordTraining = BeginTrainingSync?.Invoke();
        launch(selected, mouseButtons, volume);
    }

    private TrainerResult annotateGuidedResult(TrainerResult result)
    {
        var pending = pendingGuidedRun; pendingGuidedRun = null;
        if (pending is null || guidedPlan is not {} plan || plan.Id != pending.PlanId
            || result.Settings != TrainerGuidedPractice.SettingsFor(plan) || result.Assisted) return result;
        result = result with { GuidedRun = pending };
        if (plan.Focus == TrainerGuidedFocus.MovementComparison && TrainerGuidedPractice.IsUsable(result))
        {
            guidedPlan = plan with { Step = plan.Step + 1, Finished = plan.Step >= 5 };
            saveGuidedPlan();
        }
        return result;
    }

    private bool addGuidedActions(TrainerResult? latest)
    {
        if (guidedPlan is not {} plan || latest is not null && latest.GuidedRun?.PlanId != plan.Id)
        {
            if (latest?.GuidedRun is not {} previous) return false;
            if (previous.Focus == TrainerGuidedFocus.MovementComparison)
            {
                var comparison = TrainerGuidedPractice.CompareMovement(previous.PlanId, history().Load().Append(latest));
                results.Add(paragraph($"Movement comparison: original {ms(comparison.OriginalSpreadMs)} spread ({comparison.OriginalRuns} runs), compact {ms(comparison.CompactSpreadMs)} ({comparison.CompactRuns} runs)."));
                results.Add(paragraph(comparison.Observation));
            }
            var recordedActions = flow();
            recordedActions.Add(new AimModButton("Resume guided practice", ResumeGuidedPractice));
            recordedActions.Add(new AimModButton("Practice settings", returnToPracticeSettings));
            results.Add(recordedActions);
            return true;
        }
        var runs = (guidedStore ?? history()).Load().Concat(latest is null ? [] : new[] { latest }).DistinctBy(r => r.Id).ToArray();
        var buttons = flow();
        if (plan.Focus == TrainerGuidedFocus.MovementComparison)
        {
            var comparison = TrainerGuidedPractice.CompareMovement(plan.Id, runs);
            results.Add(text("Movement comparison", 16, AimModPalette.Text));
            var metrics = flow();
            metrics.Add(metric(ms(comparison.OriginalSpreadMs), $"original spread · {comparison.OriginalRuns} runs"));
            metrics.Add(metric(ms(comparison.CompactSpreadMs), $"compact spread · {comparison.CompactRuns} runs"));
            metrics.Add(metric(comparison.OriginalAccuracy is {} original ? $"{original:0.00}%" : "-", "original accuracy"));
            metrics.Add(metric(comparison.CompactAccuracy is {} compact ? $"{compact:0.00}%" : "-", "compact accuracy"));
            if (comparison.OriginalRuns + comparison.CompactRuns > 0)
            { results.Add(metrics); results.Add(paragraph(comparison.Observation)); }
            else metrics.Dispose();
            if (!plan.Finished)
            {
                results.Add(paragraph($"Next: {TrainerGuidedPractice.Condition(plan)} · run {plan.Step + 1}/6"));
                buttons.Add(new AimModButton("Start next run", launchGuidedRun, true));
                buttons.Add(new AimModButton("Skip this run", () =>
                { guidedPlan = plan with { Step = plan.Step + 1, Finished = plan.Step >= 5 }; saveGuidedPlan(); showGuidedOverview(); }));
            }
            else results.Add(paragraph("Comparison finished. Check these skills on a familiar map before drawing conclusions."));
        }
        else
        {
            var advice = TrainerGuidedPractice.Advise(plan, runs);
            results.Add(text(TrainerGuidedPractice.Condition(plan), 16, AimModPalette.Text));
            results.Add(paragraph(plan.Finished ? "Guided practice finished. Choose a new focus in Practice settings when you are ready." : advice.Explanation));
            if (!plan.Finished) buttons.Add(new AimModButton("Repeat this setup", launchGuidedRun, true));
            if (!plan.Finished && advice.Next is {} next)
                buttons.Add(new AimModButton("Try suggested step", () =>
                { guidedPlan = plan with { Current = next, Step = plan.Step + 1 }; saveGuidedPlan(); showGuidedOverview(); }));
        }
        if (plan.Finished) buttons.Add(new AimModButton("Try on a beatmap", openCoaching, true));
        buttons.Add(new AimModButton("Practice settings", returnToPracticeSettings));
        if (!plan.Finished) buttons.Add(new AimModButton("Finish guided practice", () =>
        { guidedPlan = plan with { Finished = true }; saveGuidedPlan(); pendingGuidedRun = null; returnToPracticeSettings(); }));
        results.Add(buttons);
        return true;
    }

    private void saveGuidedPlan()
    {
        try { (guidedStore ?? history()).SaveGuidedPlan(guidedPlan); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { status.Text = "Guided practice can continue, but it could not be saved for later."; }
    }
}
