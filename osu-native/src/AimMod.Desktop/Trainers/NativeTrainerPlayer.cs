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
        if (SeekTime > 0) SetGameplayStartTime(SeekTime);
        OnReady?.Invoke();
    }

    protected override void Update()
    {
        base.Update();
        if (!reported && LoadedBeatmapSuccessfully && ScoreProcessor.HasCompleted.Value)
        {
            // Wall-clock grace also works when a short source track has stopped its clock.
            completedAt ??= Time.Current;
            double outro = Time.Current - completedAt.Value;
            OutroProgress?.Invoke(Math.Clamp((outro-1200)/600,0,1));
            if (outro < 1800) return;
            // Slider ticks, repeats and tails contribute to osu! accuracy, not tap offsets.
            var hits = judgements.Where(j => j.IsHit && j.HitObject is HitCircle).ToArray();
            var offsets = hits.Select(j => j.TimeOffset).ToArray();
            double? mean = offsets.Length > 0 ? offsets.Average() : null;
            double? spread = offsets.Length > 1 ? Math.Sqrt(offsets.Average(x => Math.Pow(x - mean!.Value, 2))) : null;
            double start = GameplayState.Beatmap.HitObjects[0].StartTime;
            double duration = GameplayState.Beatmap.HitObjects.Max(h => h is IHasDuration d ? d.EndTime : h.StartTime) - start;
            var early = hits.Where(j => j.HitObject.StartTime < start + duration / 3).ToArray();
            var late = hits.Where(j => j.HitObject.StartTime >= start + duration * 2 / 3).ToArray();
            double? drift = early.Length >= 3 && late.Length >= 3 ? late.Average(j => j.TimeOffset) - early.Average(j => j.TimeOffset) : null;
            report(new TrainerResult(Guid.NewGuid(), DateTimeOffset.Now, settings, GameplayState.Beatmap.HitObjects.Count,
                hits.Length, offsets.Count(o => Math.Abs(o) <= 25), 0, 0, mean, spread, drift,
                Engine: TrainerResult.EngineFor(settings), Accuracy: ScoreProcessor.Accuracy.Value * 100,
                PlayedSeconds: Math.Min(settings.Seconds, (duration + GameplayState.Beatmap.ControlPointInfo.TimingPointAt(start).BeatLength / 4) / 1000),
                Demand: TrainerSkillProfile.Measure(GameplayState.Beatmap),
                JudgementMisses: judgements.Count(j => j.Type == HitResult.Miss)));
        }
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
