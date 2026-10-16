using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Screens;
using osu.Framework.Utils;
using osu.Game.Graphics.Sprites;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Screens.Play.Leaderboards;
using osu.Game.Screens.Ranking;

namespace AimMod.Desktop.Practice;

public abstract record AdaptiveAttemptEvent
{
    public sealed record Failed(int Section, double AtMs) : AdaptiveAttemptEvent;
    /// <summary>The drill finished without a fail. The figures describe its focused section or spot.</summary>
    public sealed record Completed(AdaptiveResult Result) : AdaptiveAttemptEvent;
    public sealed record Quit : AdaptiveAttemptEvent;
}

/// <summary>
/// One drill: a section, a spot inside one, two sections joined, or the whole map, at one speed. osu!'s own health
/// decides a fail; instead of the fail animation the attempt stops and hands control back to the session.
/// </summary>
public partial class AdaptivePracticePlayer : Player
{
    private const double end_delay_ms = 1_200;

    [Cached(typeof(IGameplayLeaderboardProvider))]
    private readonly SoloGameplayLeaderboardProvider leaderboard = new();
    private readonly AdaptiveAttemptPlan plan;
    private readonly Action<AdaptiveAttemptEvent> report;
    // Objects of the drill's focused section, nested ones included. Connect drills judge the joined section.
    private readonly HashSet<HitObject> focused = [];
    private readonly List<double> missTimes = [];
    private int comboBreaks, objects, notes, offbeat;
    private double baseScore, maximumBaseScore;
    private bool ended;
    private double? completedAt;
    private AdaptiveSectionHud? hud;

    public AdaptivePracticePlayer(AdaptiveAttemptPlan plan, Action<AdaptiveAttemptEvent> report)
        : base(new PlayerConfiguration { AllowPause = false, AllowRestart = false, AllowSkipping = false,
            ShowResults = false, ShowLeaderboard = false, ShowFailingOverlay = false })
    {
        this.plan = plan;
        this.report = report;
        AddInternal(leaderboard);
    }

    public Action? OnReady { get; init; }
    public bool Ready => IsLoaded && LoadedBeatmapSuccessfully && GameplayClockContainer is not null;

    // Health still reaches zero normally; this is the moment osu! would fail the play.
    protected override bool CheckModsAllowFailure()
    {
        if (!ended && Ready)
        {
            double time = GameplayClockContainer.CurrentTime;
            end(new AdaptiveAttemptEvent.Failed(AdaptivePracticeCoach.SectionAt(plan.Sections, plan.Drill, time), time));
        }
        return false;
    }

    protected override Task ImportScore(Score score) => Task.CompletedTask;
    protected override ResultsScreen CreateResults(ScoreInfo score) => throw new InvalidOperationException("Adaptive practice shows its own progress.");

    protected override void LoadComplete()
    {
        base.LoadComplete();
        if (!LoadedBeatmapSuccessfully) { end(new AdaptiveAttemptEvent.Quit()); return; }
        foreach (var obj in GameplayState.Beatmap.HitObjects)
            if (AdaptivePracticeCoach.SectionAt(plan.Sections, plan.Drill, obj.StartTime) == plan.Drill.Focus) { add(obj); objects++; }
        DrawableRuleset.NewResult += onResult;
        SetGameplayStartTime(plan.LeadInStart);
        AddInternal(hud = new AdaptiveSectionHud(plan) { Depth = -100 });
        OnReady?.Invoke();
    }

    private void add(HitObject obj)
    {
        focused.Add(obj);
        foreach (var nested in obj.NestedHitObjects) add(nested);
    }

    private void onResult(JudgementResult result)
    {
        if (ended || !focused.Contains(result.HitObject)) return;
        // Notes are hits judged 300/100/50; ticks and slider ends only count toward combo breaks.
        if (result.Judgement.MaxResult == HitResult.Great)
        {
            notes++;
            if (result.Type is HitResult.Ok or HitResult.Meh) offbeat++;
        }
        if (result.Type.BreaksCombo())
        {
            comboBreaks++;
            missTimes.Add(result.HitObject.StartTime);
        }
        // The same weighting as osu!'s own accuracy, including slider heads, ticks and tails.
        if (result.Judgement.MaxResult.AffectsAccuracy()) maximumBaseScore += ScoreProcessor.GetBaseScoreForResult(result.Judgement.MaxResult);
        if (result.Type.AffectsAccuracy()) baseScore += ScoreProcessor.GetBaseScoreForResult(result.Type);
    }

    protected override void Update()
    {
        base.Update();
        if (ended || !Ready) return;
        hud?.Show(AdaptivePracticeCoach.SectionAt(plan.Sections, plan.Drill, GameplayClockContainer.CurrentTime));
        if (!ScoreProcessor.HasCompleted.Value) return;
        // Wall-clock grace, so the final judgements and hit sounds land before the session takes over.
        completedAt ??= Time.Current;
        if (Time.Current - completedAt.Value >= end_delay_ms)
            end(new AdaptiveAttemptEvent.Completed(new(comboBreaks, notes, offbeat, maximumBaseScore > 0 ? baseScore / maximumBaseScore : 1, missTimes.ToArray(), objects)));
    }

    /// <summary>Stops gameplay without reporting; used when the session itself ends.</summary>
    public void Abandon()
    {
        ended = true;
        if (Ready) GameplayClockContainer.Stop();
    }

    public override bool OnExiting(ScreenExitEvent e)
    {
        bool blocked = base.OnExiting(e);
        if (!blocked) end(new AdaptiveAttemptEvent.Quit());
        return blocked;
    }

    private void end(AdaptiveAttemptEvent outcome)
    {
        if (ended) return;
        ended = true;
        if (Ready) GameplayClockContainer.Stop();
        report(outcome);
    }
}

/// <summary>A non-interactive strip of the map's sections; the current one is outlined.</summary>
public partial class AdaptiveSectionStrip : FillFlowContainer
{
    private readonly Container[] cells;

    public AdaptiveSectionStrip(int count)
    {
        Direction = FillDirection.Horizontal;
        float gap = count > 40 ? .5f : 1.5f;
        cells = Enumerable.Range(0, count).Select(_ => new Container
        {
            RelativeSizeAxes = Axes.Both, Masking = true, CornerRadius = 2, BorderColour = AimModPalette.Text,
            Child = new Box { RelativeSizeAxes = Axes.Both },
        }).ToArray();
        Children = cells.Select(cell => new Container { RelativeSizeAxes = Axes.Both, Width = 1f / Math.Max(1, count),
            Padding = new MarginPadding { Horizontal = gap }, Child = cell }).ToArray();
    }

    public void Set(AdaptivePracticeProgress progress, int current)
    {
        for (int i = 0; i < cells.Length && i < progress.Sections.Length; i++)
        {
            cells[i].BorderThickness = i == current ? 2 : 0;
            cells[i].Child.Colour = SpeedColour(progress.Sections[i]);
        }
    }

    /// <summary>Full speed is mint; slowed sections shade from amber toward red as they slow down.</summary>
    public static Colour4 SpeedColour(AdaptiveSectionProgress section)
    {
        if (!section.Tried) return AimModPalette.Border;
        if (section.ClearedAtFullSpeed && section.Speed >= 1) return AimModPalette.Accent;
        double slow = Math.Clamp((1 - section.Speed) / (1 - AdaptivePracticeCoach.MinimumSpeed), 0, 1);
        return Interpolation.ValueAt(slow, AimModPalette.Yellow, AimModPalette.Danger, 0d, 1d);
    }
}

internal partial class AdaptiveSectionHud : Container
{
    private readonly AdaptiveAttemptPlan plan;
    private readonly OsuSpriteText label;
    private readonly AdaptiveSectionStrip strip;
    private int shown = -1;

    public AdaptiveSectionHud(AdaptiveAttemptPlan plan)
    {
        this.plan = plan;
        Anchor = Anchor.TopCentre; Origin = Anchor.TopCentre;
        Y = 12; Width = 360; Height = 38;
        Masking = true; CornerRadius = 8;
        Children =
        [
            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas.Opacity(.85f) },
            label = new OsuSpriteText { Position = new(12, 4), Font = new(size: 13, weight: "SemiBold"), Colour = AimModPalette.Text },
            strip = new AdaptiveSectionStrip(plan.Sections.Count) { RelativeSizeAxes = Axes.X, Height = 6, Y = 25, Padding = new MarginPadding { Horizontal = 12 } },
        ];
    }

    public void Show(int section)
    {
        if (section == shown) return;
        shown = section;
        label.Text = $"{AdaptiveBreather.Label(plan.Drill, plan.Sections.Count)}  ·  {AdaptivePracticeCoach.Percent(plan.Drill.Speed)} speed";
        strip.Set(plan.Progress, section);
    }
}
