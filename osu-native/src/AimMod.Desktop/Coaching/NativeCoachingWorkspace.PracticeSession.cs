using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Desktop.Trainers;
using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Coaching;

public partial class NativeCoachingWorkspace
{
    private CoachingPracticeSessionStore? practiceSessionStore;
    private Func<IReadOnlyList<TrainerResult>>? trainerHistory;
    private readonly Dictionary<string, CoachingPracticeSession> practiceSessions = [];
    private readonly Dictionary<string, CoachingPracticeStage> viewedStages = [];
    private readonly Dictionary<string, string> viewedGroups = [];
    private string practiceRunStatus = "";
    private string? selectingTransferFor;
    private string checkMapSearch = "";
    private bool showPracticeComparisonDetails;
    private bool showPracticeSteps;

    public void SetPracticeRunStatus(string message)
    {
        practiceRunStatus = message;
        practiceWorkspace?.SetRunStatus(message);
        renderCoachingMap();
    }

    public void ConfigurePracticeSessions(CoachingPracticeSessionStore store, Func<IReadOnlyList<TrainerResult>> history)
    {
        practiceSessionStore = store;
        trainerHistory = history;
    }

    private CoachingPracticeSession sessionFor(PracticeSetProgress set)
    {
        if (!practiceSessions.TryGetValue(set.Map.Id, out var session))
        {
            session = practiceSessionStore?.Load(set.Map.Id) ?? CoachingPracticeSessionPlanner.Start(set, DateTimeOffset.UtcNow);
            savePracticeSession(session);
        }
        var next = CoachingPracticeSessionPlanner.Reconcile(session, set, allReplays, practiceAccountId(), DateTimeOffset.UtcNow,
            trainerHistory?.Invoke().Select(r => r.CompletedAt));
        if (System.Text.Json.JsonSerializer.Serialize(next) != System.Text.Json.JsonSerializer.Serialize(session)) savePracticeSession(next);
        return next;
    }

    private void savePracticeSession(CoachingPracticeSession session)
    {
        practiceSessions[session.PracticeSetId] = session;
        try { practiceSessionStore?.Save(session); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { practiceRunStatus = "Your session could not be saved. Keep AimMod open and try refreshing your results."; }
    }

    private void renderPracticeSession(FillFlowContainer<Drawable> host, PracticeSetProgress set, LocalReplay? run)
    {
        if (set.Map.Tracking is null) return;
        var session = sessionFor(set);
        var groups = set.Map.Tracking.Difficulties.Where(d => d.BreakdownGroupId.Length > 0).DistinctBy(d => d.BreakdownGroupId).ToArray();
        string groupId = viewedGroups.GetValueOrDefault(set.Map.Id) ?? groups.FirstOrDefault()?.BreakdownGroupId ?? "";
        var review = CoachingPracticeSessionPlanner.Build(set, DateTimeOffset.UtcNow, session, groupId);
        bool mapPracticeStarted = practiceSets.Where(s => CoachingMapKey(s.Map) == CoachingMapKey(set.Map)).Any(s => s.Progress.Attempts.Any(a => !a.Original));
        var nextStage = review.NextStage == CoachingPracticeStage.Baseline && mapPracticeStarted ? CoachingPracticeStage.Isolate : review.NextStage;
        var selected = viewedStages.GetValueOrDefault(set.Map.Id, nextStage ?? CoachingPracticeStage.Original);
        var body = pageFlow(); body.Spacing = new(8);
        var current = review.Stages.First(s => s.Stage == selected);
        body.Add(flow(current.Label, 20, AimModPalette.Text));
        body.Add(flow((selected >= CoachingPracticeStage.Transfer ? "Optional check" : $"Step {(int)selected + 1} of 4") + $" · {Math.Min(current.Completed, current.Required)} / {current.Required} plays completed"
            + (current.Skipped ? " · Skipped" : ""), 13, coachingAccent));
        if (practiceRunStatus.Length > 0) body.Add(flow(practiceRunStatus, 14, coachingAccent));
        renderSectionNavigation(body, set, groupId, run);
        var steps = actionFlow();
        foreach (var stage in review.Stages.Where(s => s.Stage < CoachingPracticeStage.Transfer))
        {
            string count = stage.Skipped ? "Skipped" : $"{Math.Min(stage.Completed, stage.Required)}/{stage.Required}";
            string label = stage.Stage switch { CoachingPracticeStage.Baseline => "Starting point", CoachingPracticeStage.Isolate => "Tapping & aim",
                CoachingPracticeStage.Combine => "Combine", CoachingPracticeStage.Original => "Your map",
                CoachingPracticeStage.Transfer => "Another map", _ => "Next session" };
            var button = new CoachingButton($"{label} · {count}", () => { viewedStages[set.Map.Id] = stage.Stage; renderCoachingMap(); }, compact: true);
            button.SetSelected(stage.Stage == selected); steps.Add(button);
        }
        bool baselineClosed = selected == CoachingPracticeStage.Baseline && mapPracticeStarted;
        body.Add(flow(baselineClosed ? "You have already started practising this map. Continue with the section exercises; new full-map plays will count as retests."
            : selected == CoachingPracticeStage.Transfer
            ? "Optional: after practising this map, try a familiar map with a similar pattern to see if the skill carries over. You can keep working on this map instead."
            : selected == CoachingPracticeStage.Isolate
            ? "Keep the rhythm and bring the notes closer together. Play three runs to work on tapping. For aim only, try the same section with automatic tapping (Relax)."
            : current.Detail, 14, AimModPalette.Muted));
        var actions = actionFlow();
        if (selected is CoachingPracticeStage.Baseline or CoachingPracticeStage.Original && !baselineClosed)
        {
            if (run is not null && openBeatmap is not null) actions.Add(new OpenBeatmapButton(() => run, openBeatmap));
        }
        if (selected is CoachingPracticeStage.Isolate or CoachingPracticeStage.Combine)
        {
            var wanted = selected == CoachingPracticeStage.Isolate
                ? new[] { PracticeBreakdownVariant.ReducedMovement, PracticeBreakdownVariant.AimFocus }
                : new[] { PracticeBreakdownVariant.CombinedEasier, PracticeBreakdownVariant.Original };
            foreach (var variant in wanted)
                if (set.Map.Tracking.Difficulties.FirstOrDefault(d => d.BreakdownGroupId == groupId && d.BreakdownVariant == variant) is { } difficulty
                    && practiceWorkspace?.StartDifficulty is { } play && !set.Map.PayloadRemoved)
                {
                    string title = variant switch { PracticeBreakdownVariant.ReducedMovement => "Practise tapping", PracticeBreakdownVariant.AimFocus => "Practise aim only",
                        PracticeBreakdownVariant.CombinedEasier => "Play combined", _ => "Play original section" };
                    actions.Add(new CoachingButton(title, () => play(set.Map, difficulty), variant == wanted[0], true));
                }
            if (actions.Count == 0 && practiceWorkspace is not null && run is not null)
                actions.Add(new CoachingButton("Create section breakdown", () => practiceWorkspace.OpenBreakdown(new PracticeMapCandidate(run, [run.ScoreId], 1, run.MissCount, 0)), true, true));
        }
        if (selected == CoachingPracticeStage.Transfer)
        {
            actions.Add(new CoachingButton("Choose comparison map", () => { selectingTransferFor = set.Map.Id; renderCoachingMap(); }, true, true));
            var bound = session.Checks.LastOrDefault(c => c.Stage == selected);
            var target = bound is null ? null : allReplays.FirstOrDefault(r => PracticeProgressTracker.SameSource(r, bound.Target));
            if (target is not null && openBeatmap is not null) actions.Add(new OpenBeatmapButton(() => target, openBeatmap));
        }
        if (selected == CoachingPracticeStage.Retention && run is not null)
        {
            actions.Add(new CoachingButton("Start next-session check", () => startPracticeCheck(set, session, selected, run), true, true));
            if (session.Checks.Any(c => c.Stage == selected) && openBeatmap is not null)
                actions.Add(new OpenBeatmapButton(() => run, openBeatmap));
        }
        body.Add(actions);
        var navigation = actionFlow();
        if ((current.IsComplete || current.Skipped || baselineClosed) && nextStage is { } next && next != selected)
            navigation.Add(new CoachingButton($"Continue: {review.Stages.First(s => s.Stage == next).Label}", () =>
            { viewedStages[set.Map.Id] = next; renderCoachingMap(); }, compact: true));
        navigation.Add(new CoachingButton(showPracticeSteps ? "Hide session steps" : "View all session steps", () =>
        { showPracticeSteps = !showPracticeSteps; renderCoachingMap(); }, compact: true));
        navigation.Add(new CoachingButton("Refresh results", load, compact: true));
        body.Add(navigation);
        steps.Add(new CoachingButton(current.Skipped ? "Restore step" : "Skip this step", () =>
        {
            savePracticeSession(current.Skipped ? CoachingPracticeSessionPlanner.Restore(session, selected) : CoachingPracticeSessionPlanner.Skip(session, selected));
            viewedStages.Remove(set.Map.Id); renderCoachingMap();
        }, compact: true));
        if (showPracticeSteps) body.Add(steps);
        else steps.Dispose();
        if (showPracticeSteps || selected >= CoachingPracticeStage.Transfer)
        {
            var optional = actionFlow();
            optional.Add(new CoachingButton("Optional: try another map", () => { viewedStages[set.Map.Id] = CoachingPracticeStage.Transfer; selectingTransferFor = null; renderCoachingMap(); }, compact: true));
            optional.Add(new CoachingButton("Optional: check next session", () => { viewedStages[set.Map.Id] = CoachingPracticeStage.Retention; selectingTransferFor = null; renderCoachingMap(); }, compact: true));
            if (selected >= CoachingPracticeStage.Transfer) optional.Add(new CoachingButton("Back to this map", () => { viewedStages.Remove(set.Map.Id); selectingTransferFor = null; renderCoachingMap(); }, compact: true));
            body.Add(optional);
        }
        if (selected == CoachingPracticeStage.Transfer && selectingTransferFor == set.Map.Id) renderTransferPicker(body, set, session);
        body.Add(new CoachingButton(showPracticeComparisonDetails ? "Hide results & comparisons" : "View results & comparisons", () =>
        { showPracticeComparisonDetails = !showPracticeComparisonDetails; renderCoachingMap(); }, compact: true));
        if (showPracticeComparisonDetails && review.Cohorts.Count > 0)
        {
            body.Add(flow("Section results", 15, AimModPalette.Text));
            foreach (var cohort in review.Cohorts.Where(c => c.GroupId == groupId).Take(6))
                body.Add(metricStrip((cohort.Label, cohort.Assisted ? "Assisted" : "Manual"), ("COMPLETED", $"{cohort.Completed}/{cohort.Attempts}"),
                    ("ACCURACY", cohort.Assisted ? "Assisted" : cohort.MedianAccuracy is { } a ? $"{a:P2}" : "Not measured"),
                    ("MISSES", cohort.MedianMisses is { } misses ? $"{misses:0.#}" : "Not measured")));
        }
        if (showPracticeComparisonDetails)
            foreach (var comparison in review.Comparisons)
            {
                body.Add(flow(comparison.Label, 15, AimModPalette.Text));
                body.Add(flow(comparison.Detail, 13, AimModPalette.Muted));
            }
        var trainerRuns = trainerHistory?.Invoke() ?? [];
        var guided = trainerRuns.LastOrDefault(r => r.GuidedRun?.Focus == TrainerGuidedFocus.MovementComparison)?.GuidedRun;
        if (showPracticeComparisonDetails && guided is not null)
        {
            var comparison = TrainerGuidedPractice.CompareMovement(guided.PlanId, trainerRuns);
            body.Add(flow("Trainer movement comparison", 15, AimModPalette.Text));
            body.Add(flow(comparison.Observation, 13, AimModPalette.Muted));
        }
        host.Add(new CoachingCard(body, 12));
    }

    private void renderTransferPicker(FillFlowContainer<Drawable> body, PracticeSetProgress set, CoachingPracticeSession session)
    {
        body.Add(flow("Choose a different map with the same kind of pattern.", 14, AimModPalette.Text));
        var searchBox = new AimModSearchBox { RelativeSizeAxes = Axes.X, PlaceholderText = "Search your maps by title or difficulty" };
        searchBox.Current.Value = checkMapSearch; body.Add(searchBox);
        var choices = pageFlow(); choices.Spacing = new(6); body.Add(choices);
        void fill()
        {
            choices.Clear();
            var targets = allReplays.Where(r => eligibleForCoaching(r) && PracticeProgressTracker.SamePlayer(r, set.Map.Tracking!.Player)
                && !PracticeProgressTracker.SameSource(r, set.Map.Tracking!)
                && (r.Title + " " + r.Difficulty).Contains(checkMapSearch, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.PlayedAt).DistinctBy(ScoreMods.SetupKey).Take(10).ToArray();
            foreach (var target in targets)
                choices.Add(new CoachingMapRow(target.Title, target.Difficulty, $"{target.Accuracy:P2} · {ScoreMods.Display(target)}", "Use this map",
                    () => startPracticeCheck(set, session, CoachingPracticeStage.Transfer, target)));
            if (targets.Length == 0) choices.Add(flow("No matching plays. Try another title or play a comparison map first.", 13, AimModPalette.Muted));
        }
        searchBox.Current.BindValueChanged(e => { checkMapSearch = e.NewValue; fill(); }); fill();
        body.Add(new CoachingButton("Close map chooser", () => { selectingTransferFor = null; renderCoachingMap(); }, compact: true));
    }

    private void startPracticeCheck(PracticeSetProgress set, CoachingPracticeSession session, CoachingPracticeStage stage, LocalReplay target)
    {
        try
        {
            savePracticeSession(CoachingPracticeSessionPlanner.StartCheck(session, set, stage, target, allReplays, DateTimeOffset.UtcNow,
                trainerHistory?.Invoke().Select(r => r.CompletedAt)));
            selectingTransferFor = null;
            practiceRunStatus = "Check started. Play the selected map with the saved mods and speed, then refresh your results.";
        }
        catch (ArgumentException error) { practiceRunStatus = error.Message; }
        renderCoachingMap();
    }

    private static FillFlowContainer<Drawable> actionFlow() => new()
    { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(8) };
}
