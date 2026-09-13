using osu.Framework.Screens;
using osu.Framework.Allocation;
using osu.Game.Screens.Play.Leaderboards;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Screens.Ranking;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Objects.Types;

namespace AimMod.Desktop.Trainers;

public partial class NativeTrainerPlayer : Player
{
    [Cached(typeof(IGameplayLeaderboardProvider))]
    private readonly SoloGameplayLeaderboardProvider leaderboard = new();
    private readonly TrainerSettings settings;
    private readonly Action<TrainerResult?> finished;
    private readonly List<JudgementResult> judgements = [];
    private bool reported;
    public bool Ready => IsLoaded && LoadedBeatmapSuccessfully && GameplayClockContainer is not null;
    internal osu.Game.Beatmaps.IBeatmap PracticeBeatmap => GameplayState.Beatmap;
    public Action? OnReady { get; init; }
    public Action<double>? OutroProgress { get; init; }
    private double? completedAt;
    public double SeekTime { get; init; }
    public double CurrentTime => Ready ? GameplayClockContainer.CurrentTime : 0;

    public NativeTrainerPlayer(TrainerSettings settings, Action<TrainerResult?> finished)
        : base(new PlayerConfiguration { AllowPause = false, AllowRestart = false, AllowSkipping = false,
            ShowResults = false, ShowLeaderboard = false, ShowFailingOverlay = false })
    { this.settings = settings; this.finished = finished; AddInternal(leaderboard); }

    protected override bool CheckModsAllowFailure() => false;
    protected override Task ImportScore(Score score) => Task.CompletedTask;
    protected override ResultsScreen CreateResults(ScoreInfo score) => throw new InvalidOperationException("Trainer results are shown in the workspace.");

    protected override void LoadComplete()
    {
        base.LoadComplete();
        if (!LoadedBeatmapSuccessfully) { report(null); return; }
        DrawableRuleset.NewResult += r => judgements.Add(r);
        loadPracticeGuide();
        if (SeekTime > 0) SetGameplayStartTime(SeekTime);
        OnReady?.Invoke();
    }

    protected override void Update()
    {
        base.Update();
        if (!reported && Ready) updatePracticeGuide();
        if (!reported && LoadedBeatmapSuccessfully && ScoreProcessor.HasCompleted.Value)
        {
            // Wall-clock grace also works when a short source track has stopped its clock.
            completedAt ??= Time.Current;
            double outro = Time.Current - completedAt.Value;
            OutroProgress?.Invoke(Math.Clamp((outro-1200)/600,0,1));
            if (outro < 1800) return;
            report(CreateResult(settings, GameplayState.Beatmap, judgements,
                ScoreProcessor.Accuracy.Value * 100,
                spinMetrics.Result().Attempts > 0 ? spinMetrics.Result() : null));
        }
    }

    internal static bool IsTapObject(osu.Game.Rulesets.Objects.HitObject obj) => obj is HitCircle and not SliderEndCircle;

    internal static TrainerResult CreateResult(TrainerSettings settings, osu.Game.Beatmaps.IBeatmap beatmap,
        IReadOnlyList<JudgementResult> judgements, double accuracy, SpinnerPracticeSummary? spinnerPractice = null)
    {
        // Slider ends inherit HitCircle too. Count heads as taps, but keep tails and
        // repeats only in osu! scoring so successful sliders cannot inflate Hits above Notes.
        var hits = judgements.Where(j => j.IsHit && IsTapObject(j.HitObject)).ToArray();
        var offsets = hits.Select(j => j.TimeOffset).ToArray();
        double? mean = offsets.Length > 0 ? offsets.Average() : null;
        double? spread = offsets.Length > 1 ? Math.Sqrt(offsets.Average(x => Math.Pow(x - mean!.Value, 2))) : null;
        double start = beatmap.HitObjects[0].StartTime;
        double duration = beatmap.HitObjects.Max(h => h is IHasDuration d ? d.EndTime : h.StartTime) - start;
        var early = hits.Where(j => j.HitObject.StartTime < start + duration / 3).ToArray();
        var late = hits.Where(j => j.HitObject.StartTime >= start + duration * 2 / 3).ToArray();
        double? drift = early.Length >= 3 && late.Length >= 3 ? late.Average(j => j.TimeOffset) - early.Average(j => j.TimeOffset) : null;
        return new TrainerResult(Guid.NewGuid(), DateTimeOffset.Now, settings, beatmap.HitObjects.Count,
            hits.Length + judgements.Count(j => j.IsHit && j.HitObject is Spinner), offsets.Count(o => Math.Abs(o) <= 25), 0, 0, mean, spread, drift,
            Engine: TrainerResult.EngineFor(settings), Accuracy: accuracy,
            PlayedSeconds: Math.Min(settings.Seconds, (duration + beatmap.ControlPointInfo.TimingPointAt(start).BeatLength / 4) / 1000),
            Demand: TrainerSkillProfile.Measure(beatmap),
            JudgementMisses: judgements.Count(j => j.Type == HitResult.Miss),
            TapTargets: beatmap.HitObjects.Count(h => h is HitCircle or Slider),
            SpinnerPractice: spinnerPractice,
            ReadingWindows: settings.Kind != TrainerKind.Reading ? null : judgements.Where(j => IsTapObject(j.HitObject))
                .GroupBy(j => (int)((j.HitObject.StartTime - start) / 10000))
                .Select(g => new ReadingWindowResult(g.Key * 10, g.Count(), g.Count(j => !j.IsHit))).OrderBy(w => w.StartSeconds).ToArray());
    }

    public void StopSession()
    {
        if (Ready) GameplayClockContainer.Stop();
        report(null);
    }

    public override bool OnExiting(ScreenExitEvent e)
    {
        bool result = base.OnExiting(e);
        report(null);
        return result;
    }

    private void report(TrainerResult? result)
    {
        if (reported) return;
        reported = true;
        if (Ready) GameplayClockContainer.Stop();
        finished(result);
    }
}
