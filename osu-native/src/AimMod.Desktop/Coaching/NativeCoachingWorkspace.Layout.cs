using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Coaching;

public partial class NativeCoachingWorkspace
{
    private static readonly Colour4 coachingAccent = AimModPalette.Accent;
    private readonly AimModScrollContainer[] coachingPages;
    private readonly CoachingButton[] navigationButtons;
    private readonly FillFlowContainer<Drawable> pageNavigation;
    private int selectedCoachingPage;
    private Guid? coachingTargetScoreId;
    private readonly FillFlowContainer<Drawable> reviewHost;
    private readonly FillFlowContainer<Drawable> coachingDetails;
    private readonly Drawable coachingFilters;
    private bool detailsOpen;

    private static FillFlowContainer<Drawable> pageFlow() => new()
    {
        RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical,
        Spacing = new(10), Padding = new MarginPadding { Right = 12, Bottom = 18 },
    };

    private static CoachingButton visualEntry(string title, string description, PracticeSketchKind sketch, Action action)
    {
        var button = new CoachingButton(title, action) { AutoSizeAxes = Axes.None, Width = 245 };
        button.SetVisualContent(new AimModVisualChoiceContent(title, description, sketch), 125);
        return button;
    }

    private sealed partial class CoachingMapRow : AimModInteractiveSurface
    {
        public CoachingMapRow(string title, string difficulty, string statistics, string actionLabel, Action action)
        {
            RelativeSizeAxes = Axes.X; Height = 78; Action = action;
            Child = new Container {
                RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Horizontal = 14, Vertical = 10 },
                Children = [
                    new Container {
                        RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Right = 144 },
                        Children = [
                            new TruncatingSpriteText { RelativeSizeAxes = Axes.X, Text = title, Font = new osu.Framework.Graphics.Sprites.FontUsage(size:17,weight:"SemiBold"), Colour = AimModPalette.Text },
                            new TruncatingSpriteText { RelativeSizeAxes = Axes.X, Y = 23, Text = difficulty, Font = new osu.Framework.Graphics.Sprites.FontUsage(size:12), Colour = coachingAccent },
                            new TruncatingSpriteText { RelativeSizeAxes = Axes.X, Y = 43, Text = statistics, Font = new osu.Framework.Graphics.Sprites.FontUsage(size:12), Colour = AimModPalette.Muted },
                        ],
                    },
                    new CoachingButton(actionLabel,action,compact:true) { Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight },
                ],
            };
        }
    }

    private void showCoachingPage(int index)
    {
        selectedCoachingPage = Math.Clamp(index, 0, coachingPages.Length - 1);
        coachingFilters.Alpha = selectedCoachingPage == 2 ? 1 : 0;
        for (int i = 0; i < coachingPages.Length; i++)
        {
            coachingPages[i].Alpha = i == selectedCoachingPage ? 1 : 0;
            navigationButtons[i].SetSelected(i == selectedCoachingPage || (i == 3 && selectedCoachingPage != 2));
        }
    }

    private void startTraining(CoachingTrainingPlan plan, NativeCoachingWorkspaceModel model)
    {
        if (activeTraining?.TargetScoreId == plan.TargetScoreId) return;
        activeTraining = plan with { StartedAt = DateTimeOffset.UtcNow };
        saveTraining();
        renderSession(model);
    }

    private void chooseCoachingRun(Guid scoreId)
    {
        coachingTargetScoreId = scoreId;
        renderSession(workspace ?? buildWorkspace());
        openCoachingMap(null, allReplays.FirstOrDefault(r => r.ScoreId == scoreId));
    }

    private static bool eligibleForCoaching(LocalReplay run) => run.Passed && ScoreMods.IsManualPlay(run)
        && double.IsFinite(run.Accuracy) && run.Accuracy is >= .7 and <= 1
        && run.MissCount >= 0 && run.PlayedAt <= DateTimeOffset.UtcNow;

    private CoachingTrainingPlan? chosenPlan(NativeCoachingWorkspaceModel model)
    {
        Guid? id = coachingTargetScoreId ?? activeTraining?.TargetScoreId;
        if (activeTraining is not null && id == activeTraining.TargetScoreId) return activeTraining;
        var run = allReplays.FirstOrDefault(r => r.ScoreId == id);
        if (run is null || !eligibleForCoaching(run)) return null;
        var matching = allReplays.Where(r => r.Player == run.Player && ScoreMods.SetupKey(r) == ScoreMods.SetupKey(run)).ToArray();
        var scoped = NativeCoachingWorkspaceModel.Build(matching, analyses, run.ScoreId, CoachingTimeRange.All);
        return CoachingTrainingPlanner.Build(scoped) is { } plan
            ? plan with { TargetScoreId = run.ScoreId, TargetTitle = run.Title, Difficulty = run.Difficulty }
            : null;
    }

    private void renderSession(NativeCoachingWorkspaceModel model)
    {
        trainingHost.Clear(); reviewHost.Clear();
        trainingHost.Add(new CoachingButton("Back to beatmap", () => { renderCoachingMap(); showCoachingPage(4); }));
        var plan = chosenPlan(model);
        if (plan is null)
        {
            trainingHost.Add(flow("Choose a completed play to prepare your practice plan.", 23, AimModPalette.Text));
            trainingHost.Add(new CoachingButton("Choose a play", () => showCoachingPage(2), true));
        }
        else
        {
            var run = allReplays.FirstOrDefault(r => r.ScoreId == plan.TargetScoreId);
            var tapping = run is null ? null : TappingCoaching.Build(analyses.GetValueOrDefault(run.ScoreId));
            var body = pageFlow(); body.Spacing = new(8);
            body.Add(label("YOUR SELECTED PLAY", 13, coachingAccent, "Bold"));
            body.Add(flow($"{plan.TargetTitle}  /  {plan.Difficulty}", 20, AimModPalette.Text));
            body.Add(flow($"{run?.Accuracy:P2} accuracy  ·  {run?.MissCount} misses  ·  {plan.ModLabel}", 15, AimModPalette.Muted));
            body.Add(flow(tapping?.Pattern ?? plan.Focus, 18, AimModPalette.Text));
            body.Add(flow(tapping?.Cue ?? plan.Cue, 14, AimModPalette.Text));
            if (tapping is not null)
                body.Add(flow($"Section at {TimeSpan.FromMilliseconds(tapping.StartTimeMs):m\\:ss}. {tapping.Observation}", 15, AimModPalette.Muted));
            body.Add(flow("Check your starting point, practise the parts, then return to your map.", 14, AimModPalette.Muted));
            if (run is not null && practiceWorkspace is not null)
                body.Add(new CoachingButton("Break down this section", () => {
                    startTraining(plan, model);
                    practiceWorkspace.OpenBreakdown(new PracticeMapCandidate(run, [run.ScoreId], 1, run.MissCount, 0), tapping?.FirstObjectIndex);
                }, true));
            var secondary = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Full, Spacing = new(10) };
            secondary.Add(new CoachingButton("Choose another play", () => showCoachingPage(2)));
            if (compareMovement is not null) secondary.Add(new CoachingButton("Compare movement", compareMovement));
            if (run?.HasReplayFile == true) secondary.Add(new CoachingButton("Watch replay", () => openReplay(run)));
            body.Add(secondary);
            trainingHost.Add(new CoachingCard(body));
            if (run is not null) renderReplayObservations(trainingHost, run);
        }

        var review = pageFlow();
        review.Add(flow("Check your tapping after practice", 26, AimModPalette.Text));
        if (activeTraining is not { } tracked)
        {
            review.Add(flow("Prepare a practice map first. After practising, return to the original map and complete a few plays with the same mods and speed.", 18, AimModPalette.Muted));
            review.Add(new CoachingButton("Go to practice plan", () => showCoachingPage(0), true));
        }
        else
        {
            review.Add(flow($"{tracked.TargetTitle}  /  {tracked.Difficulty}", 20, AimModPalette.Text));
            review.Add(flow("Play the original map with the same mods and speed. Then refresh here to check your new attempts.", 18, AimModPalette.Muted));
            var run = allReplays.FirstOrDefault(r => r.ScoreId == tracked.TargetScoreId);
            if (run is not null && openBeatmap is not null) review.Add(new OpenBeatmapButton(() => run, openBeatmap));
            review.Add(new CoachingButton("Refresh my results", load, true));
            review.Add(flow(CoachingTrainingPlanner.Review(tracked, allReplays).Message, 17, AimModPalette.Text));
            review.Add(flow($"Starting point: {tracked.BaselineAccuracy:P2} accuracy, {tracked.BaselineMisses} misses across {tracked.BaselineCount} comparable plays. Include your worse attempts when comparing.", 15, AimModPalette.Muted));
        }
        if (trainingSaveFailed) review.Add(flow("This session could not be saved. Keep AimMod open while you practise.", 15, AimModPalette.Danger));
        reviewHost.Add(new CoachingCard(review));
        if (practiceLibrary is not null)
            reviewHost.Add(new CoachingButton("View saved practice sets and stats", () => showCoachingPage(3), true));
    }

    private sealed partial class CoachingColumns : Container
    {
        private readonly Drawable left;
        private readonly Drawable right;
        public CoachingColumns(Drawable left, Drawable right)
        {
            this.left = left; this.right = right;
            RelativeSizeAxes = Axes.X; AutoSizeAxes = Axes.Y;
            Children = [left, right];
        }
        protected override void Update()
        {
            base.Update();
            bool stacked = DrawWidth < 1000;
            left.Width = stacked ? 1 : .57f;
            right.Width = stacked ? 1 : .41f;
            left.Margin = new MarginPadding();
            right.Position = new(stacked ? 0 : DrawWidth * .59f, stacked ? left.DrawHeight + 16 : 0);
        }
    }

    private sealed partial class CoachingCard : Container
    {
        public CoachingCard(Drawable content, float padding = 24)
        {
            RelativeSizeAxes = Axes.X; AutoSizeAxes = Axes.Y;
            Masking = true; CornerRadius = 8; BorderThickness = 1; BorderColour = Colour4.White.Opacity(.06f);
            Children = [new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                new Container { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding(padding), Child = content }];
        }
    }

    private sealed partial class CoachingButton : AimModButton
    {
        public CoachingButton(string text, Action action, bool primary = false, bool compact = false) : base(text, action, primary)
        { if (compact) Height = AimModVisualStyle.CompactControlHeight; }
    }
}
