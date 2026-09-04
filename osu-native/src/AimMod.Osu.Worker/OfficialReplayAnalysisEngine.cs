using System.Collections.Concurrent;
using AimMod.Osu.Runtime.Contracts;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Audio.Track;
using osu.Framework.Platform;
using osu.Framework.Timing;
using osu.Game;
using osu.Game.Beatmaps;
using osu.Game.Online.API;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Configuration;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Judgements;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Rulesets.Osu.Scoring;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Screens;
using osu.Game.Screens.Play;
using osu.Game.Tests;
using osu.Game.Tests.Beatmaps;
using osuTK;

namespace AimMod.Osu.Worker;

internal sealed class OfficialReplayAnalysisEngine : IReplayAnalysisEngine
{
    public async ValueTask<ReplayAnalysisResult> AnalyseAsync(ValidatedReplayInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<ReplayAnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var analysisThread = new Thread(() => runAnalysis(input, cancellationToken, completion))
        {
            IsBackground = true,
            Name = "AimMod osu! replay analysis",
        };

        analysisThread.Start();
        return await completion.Task.ConfigureAwait(false);
    }

    private static void runAnalysis(
        ValidatedReplayInput input,
        CancellationToken cancellationToken,
        TaskCompletionSource<ReplayAnalysisResult> completion)
    {
        try
        {
            completion.SetResult(analyse(input, cancellationToken));
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    private static ReplayAnalysisResult analyse(
        ValidatedReplayInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // Load the ruleset assembly before constructing LegacyBeatmapDecoder.
            // AssemblyRulesetStore can then resolve Mode:0 without scanning plugins.
            _ = typeof(OsuRuleset).Assembly;

            IBeatmap sourceBeatmap = new FlatWorkingBeatmap(input.BeatmapPath).Beatmap;
            var game = new ReplayAnalysisGame(sourceBeatmap, input.ReplayPath, cancellationToken);

            // The official headless host uses the "No sound" device, a dummy
            // renderer, no window and no input handlers. Its isolated temporary
            // storage never opens the user's live osu! Realm.
            using (var host = new CleanRunHeadlessGameHost(realtime: false, callingMethodName: "aimmod-replay-analysis"))
            using (cancellationToken.Register(host.Exit))
                host.Run(game);

            cancellationToken.ThrowIfCancellationRequested();

            if (game.Failure is not null)
                throw game.Failure;

            return game.Result ?? throw new ReplayAnalysisException(
                "analysis_failed",
                "Official replay playback exited without a result.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ReplayAnalysisException exception)
        {
            throw new ReplayAnalysisException(exception.Code, boundedError(exception, input));
        }
        catch (Exception exception)
        {
            throw new ReplayAnalysisException("analysis_failed", boundedError(exception, input));
        }
    }

    private static string boundedError(Exception exception, ValidatedReplayInput input)
    {
        string message = exception.Message
                                  .Replace(input.StagingDirectory, "<staging>", StringComparison.Ordinal)
                                  .Replace(input.BeatmapPath, "<beatmap>", StringComparison.Ordinal)
                                  .Replace(input.ReplayPath, "<replay>", StringComparison.Ordinal)
                                  .Replace('\r', ' ')
                                  .Replace('\n', ' ')
                                  .Trim();

        if (message.Length == 0)
            message = "The official replay engine rejected the staged files.";
        if (message.Length > 300)
            message = message[..300];

        return message;
    }
}

internal sealed class SuppliedBeatmapScoreDecoder(WorkingBeatmap beatmap) : LegacyScoreDecoder
{
    protected override Ruleset GetRuleset(int rulesetId)
    {
        if (rulesetId != 0)
            throw new ReplayAnalysisException("unsupported_ruleset", $"Replay analysis currently supports osu!standard, not ruleset {rulesetId}.");

        return new OsuRuleset();
    }

    protected override WorkingBeatmap GetBeatmap(string md5Hash) => beatmap;
}

internal sealed partial class ReplayAnalysisGame : OsuGameBase
{
    private readonly IBeatmap sourceBeatmap;
    private readonly string replayPath;
    private readonly CancellationToken cancellationToken;
    private BackgroundScreenStack? backgroundStack;
    private Score? score;
    private AnalysisReplayPlayer? player;
    private bool finished;

    public ReplayAnalysisResult? Result { get; private set; }
    public ReplayAnalysisException? Failure { get; private set; }

    public ReplayAnalysisGame(
        IBeatmap sourceBeatmap,
        string replayPath,
        CancellationToken cancellationToken)
    {
        this.sourceBeatmap = sourceBeatmap;
        this.replayPath = replayPath;
        this.cancellationToken = cancellationToken;

        var offlineApi = new DummyAPIAccess();
        offlineApi.Logout();
        API = offlineApi;
    }

    protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
    {
        var dependencies = new DependencyContainer(base.CreateChildDependencies(parent));
        dependencies.CacheAs<IRulesetConfigCache>(new HeadlessRulesetConfigCache());
        backgroundStack = new BackgroundScreenStack { RelativeSizeAxes = Axes.Both };
        dependencies.CacheAs(backgroundStack);
        return dependencies;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // These values are defensive. HeadlessGameHost already has no sound
            // device and TestWorkingBeatmap supplies a TrackVirtual rather than
            // opening the beatmap's audio file.
            Audio.Volume.Value = 0;
            Audio.VolumeTrack.Value = 0;
            Audio.VolumeSample.Value = 0;

            var workingBeatmap = new AnalysisWorkingBeatmap(sourceBeatmap, Audio, Clock);
            workingBeatmap.LoadTrack();
            using (var replayStream = new FileStream(replayPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
                score = new SuppliedBeatmapScoreDecoder(workingBeatmap).Parse(replayStream);

            RulesetInfo availableRuleset = RulesetStore.GetRuleset(0)
                                               ?? throw new ReplayAnalysisException(
                                                   "ruleset_unavailable",
                                                   "The isolated osu! ruleset store did not register osu!standard.");
            score.ScoreInfo.Ruleset = availableRuleset;

            if (score.ScoreInfo.Pauses.Count > ReplayAnalysisProtocol.MaximumPauses)
                throw new ReplayAnalysisException("result_too_large", "The replay contains too many pause records to return safely.");

            Beatmap.Value = workingBeatmap;
            Ruleset.Value = score.ScoreInfo.Ruleset;
            SelectedMods.Value = score.ScoreInfo.Mods;

            var stack = new OsuScreenStack { RelativeSizeAxes = Axes.Both };
            AddRange(new Drawable[]
            {
                backgroundStack ?? throw new ReplayAnalysisException("analysis_failed", "The headless background stack was not initialised."),
                stack,
            });
            player = new AnalysisReplayPlayer(score, complete, fail, cancellationToken);
            Task playerLoad = LoadComponentAsync(player, loadedPlayer =>
            {
                if (finished)
                    return;

                stack.Push(loadedPlayer);
            }, cancellationToken);
            _ = playerLoad.ContinueWith(task =>
            {
                Exception error = task.Exception?.GetBaseException() ?? new InvalidOperationException("Replay player loading failed.");
                fail(new ReplayAnalysisException("player_load_failed", error.Message));
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (OperationCanceledException)
        {
            fail(new ReplayAnalysisException("analysis_cancelled", "Replay analysis was cancelled."));
        }
        catch (ReplayAnalysisException exception)
        {
            fail(exception);
        }
        catch (Exception exception)
        {
            fail(new ReplayAnalysisException("analysis_failed", exception.Message));
        }
    }

    private void complete(IReadOnlyList<ReplayObjectJudgement> judgements)
    {
        if (finished)
            return;

        finished = true;
        Result = new ReplayAnalysisResult(
            ReplayAnalysisProtocol.EngineVersion,
            "officialRulesetPlayback",
            true,
            ReplayAnalysisProtocol.WallClockTimeoutMs,
            score!.ScoreInfo.Pauses.ToArray(),
            judgements,
            createSummary(judgements));
        Host.Exit();
    }

    private void fail(ReplayAnalysisException exception)
    {
        if (finished)
            return;

        finished = true;
        Failure = exception;
        Host.Exit();
    }

    private static ReplayJudgementSummary createSummary(IReadOnlyList<ReplayObjectJudgement> judgements)
    {
        int great = 0;
        int ok = 0;
        int meh = 0;
        int miss = 0;
        int sliderBreaks = 0;
        int other = 0;

        foreach (ReplayObjectJudgement judgement in judgements)
        {
            switch (judgement.Result)
            {
                case "Great": great++; break;
                case "Ok": ok++; break;
                case "Meh": meh++; break;
                case "Miss": miss++; break;
                case "LargeTickMiss":
                case "SmallTickMiss":
                case "SliderTailMiss": sliderBreaks++; break;
                default: other++; break;
            }
        }

        return new ReplayJudgementSummary(great, ok, meh, miss, sliderBreaks, other);
    }
}

internal sealed class HeadlessRulesetConfigCache : IRulesetConfigCache
{
    private readonly ConcurrentDictionary<string, IRulesetConfigManager?> configs = new();

    public IRulesetConfigManager? GetConfigFor(Ruleset ruleset) =>
        configs.GetOrAdd(ruleset.ShortName, _ => ruleset.CreateConfig(null));
}

internal sealed class AnalysisWorkingBeatmap : TestWorkingBeatmap
{
    private readonly double trackLength;
    private readonly IClock analysisClock;

    public AnalysisWorkingBeatmap(IBeatmap beatmap, osu.Framework.Audio.AudioManager? audioManager, IClock? analysisClock = null)
        : base(beatmap, audioManager: audioManager)
    {
        this.analysisClock = analysisClock ?? new StopwatchClock();
        trackLength = Math.Max(1_000, beatmap.HitObjects.Select(hitObject => hitObject.GetEndTime()).DefaultIfEmpty(0).Max() + 10_000);
    }

    protected override Track GetBeatmapTrack() => new HeadlessAnalysisTrack(trackLength, analysisClock);
}

internal sealed class HeadlessAnalysisTrack : Track
{
    private readonly object stateLock = new();
    private readonly IClock clock;
    private double clockStart;
    private double seekOffset;
    private bool running;

    public HeadlessAnalysisTrack(double length, IClock clock)
        : base("aimmod-analysis")
    {
        Length = length;
        this.clock = clock;
    }

    public override double CurrentTime
    {
        get
        {
            lock (stateLock)
                return Math.Min(Length, seekOffset + (running ? (clock.CurrentTime - clockStart) * Rate : 0));
        }
    }

    public override bool IsRunning
    {
        get
        {
            lock (stateLock)
                return running;
        }
    }

    public override bool Seek(double seek)
    {
        lock (stateLock)
        {
            seekOffset = Math.Clamp(seek, 0, Length);
            clockStart = clock.CurrentTime;
            return seekOffset == seek;
        }
    }

    public override Task<bool> SeekAsync(double seek) => Task.FromResult(Seek(seek));

    public override void Start()
    {
        lock (stateLock)
        {
            if (running || seekOffset >= Length)
                return;

            clockStart = clock.CurrentTime;
            running = true;
        }
    }

    public override Task StartAsync()
    {
        Start();
        return Task.CompletedTask;
    }

    public override void Stop()
    {
        lock (stateLock)
        {
            if (!running)
                return;

            seekOffset = Math.Min(Length, seekOffset + (clock.CurrentTime - clockStart) * Rate);
            running = false;
        }
    }

    public override Task StopAsync()
    {
        Stop();
        return Task.CompletedTask;
    }
}

internal sealed partial class AnalysisReplayPlayer : ReplayPlayer
{
    private const double judgement_settling_time = 2_000;

    private readonly Action<IReadOnlyList<ReplayObjectJudgement>> complete;
    private readonly Action<ReplayAnalysisException> fail;
    private readonly double replayEndTime;
    private readonly OsuReplayFrame[] replayFrames;
    private readonly CancellationToken cancellationToken;
    private readonly List<ReplayObjectJudgement> judgements = new();
    private Dictionary<HitObject, ObjectAddress> addresses = new(ReferenceEqualityComparer.Instance);
    private ReplayAnalysisCompletionWatchdog? completionWatchdog;
    private bool finished;
    protected override bool PauseOnFocusLost => false;

    public AnalysisReplayPlayer(
        Score score,
        Action<IReadOnlyList<ReplayObjectJudgement>> complete,
        Action<ReplayAnalysisException> fail,
        CancellationToken cancellationToken)
        : base(score, new PlayerConfiguration
        {
            AllowPause = false,
            AllowSkipping = false,
            ShowResults = false,
        })
    {
        this.complete = complete;
        this.fail = fail;
        replayFrames = score.Replay.Frames.OfType<OsuReplayFrame>()
                            .Where(frame => double.IsFinite(frame.Time))
                            .OrderBy(frame => frame.Time)
                            .ToArray();
        replayEndTime = score.Replay.Frames.Select(frame => frame.Time)
                             .Where(time => double.IsFinite(time) && time >= 0)
                             .DefaultIfEmpty(double.PositiveInfinity)
                             .Max();
        this.cancellationToken = cancellationToken;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        if (!LoadedBeatmapSuccessfully)
        {
            fail(new ReplayAnalysisException("beatmap_load_failed", "The official ruleset could not load the staged beatmap."));
            return;
        }

        addresses = indexObjects(GameplayState.Beatmap);
        completionWatchdog = new ReplayAnalysisCompletionWatchdog(
            GameplayState.Beatmap.GetLastObjectTime(),
            judgement_settling_time,
            replayEndTime);
        ScoreProcessor.NewJudgement += recordJudgement;
        ScoreProcessor.HasCompleted.BindValueChanged(completed =>
        {
            if (completed.NewValue)
                finish();
        }, true);

    }

    protected override void Update()
    {
        base.Update();

        if (finished)
            return;

        if (cancellationToken.IsCancellationRequested)
        {
            finished = true;
            fail(new ReplayAnalysisException("analysis_cancelled", "Replay analysis was cancelled."));
            return;
        }

        if (completionWatchdog?.ShouldComplete(
                GameplayClockContainer.CurrentTime,
                ScoreProcessor.HasCompleted.Value,
                GameplayState.HasFailed) == true)
        {
            finish();
        }
    }

    private void finish()
    {
        if (finished)
            return;

        finished = true;
        GameplayClockContainer.Stop();
        complete(judgements.ToArray());
    }

    private void recordJudgement(JudgementResult result)
    {
        if (judgements.Count >= ReplayAnalysisProtocol.MaximumJudgements)
        {
            fail(new ReplayAnalysisException("result_too_large", "The replay produced too many judgements to return safely."));
            return;
        }

        addresses.TryGetValue(result.HitObject, out ObjectAddress? address);
        Vector2? objectPosition = result.HitObject is OsuHitObject osuObject
            ? osuObject.StackedPosition
            : (result.HitObject as IHasPosition)?.Position;
        Vector2? cursorPosition = (result as OsuHitCircleJudgementResult)?.CursorPositionAtHit;
        ReplayMissAnalysis? missAnalysis = null;
        if (result.Type == HitResult.Miss && objectPosition is { } missTarget)
        {
            var hitWindows = new OsuHitWindows();
            hitWindows.SetDifficulty(GameplayState.Beatmap.Difficulty.OverallDifficulty);
            double hitRadius = result.HitObject is OsuHitObject hitObject ? hitObject.Radius : OsuHitObject.OBJECT_RADIUS;
            missAnalysis = ReplayMissAnalyzer.Analyse(
                replayFrames,
                missTarget,
                result.HitObject.StartTime,
                hitRadius,
                hitWindows.WindowFor(HitResult.Meh));
        }

        judgements.Add(new ReplayObjectJudgement(
            address?.ObjectIndex,
            address?.NestedPath,
            result.HitObject.GetType().Name,
            result.HitObject.StartTime,
            result.HitObject.GetEndTime(),
            result.Type.ToString(),
            result.Judgement.MaxResult.ToString(),
            result.TimeAbsolute,
            result.TimeOffset,
            result.GameplayRate,
            objectPosition is null ? null : new ReplayPoint(objectPosition.Value.X, objectPosition.Value.Y),
            cursorPosition is null ? null : new ReplayPoint(cursorPosition.Value.X, cursorPosition.Value.Y),
            result.ComboAtJudgement,
            result.ComboAfterJudgement,
            missAnalysis));
    }

    private static Dictionary<HitObject, ObjectAddress> indexObjects(IBeatmap beatmap)
    {
        var result = new Dictionary<HitObject, ObjectAddress>(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < beatmap.HitObjects.Count; index++)
        {
            HitObject topLevel = beatmap.HitObjects[index];
            result[topLevel] = new ObjectAddress(index, null);
            indexNested(topLevel, index, string.Empty, result);
        }

        return result;
    }

    private static void indexNested(HitObject parent, int objectIndex, string parentPath, Dictionary<HitObject, ObjectAddress> result)
    {
        for (int index = 0; index < parent.NestedHitObjects.Count; index++)
        {
            HitObject nested = parent.NestedHitObjects[index];
            string path = string.IsNullOrEmpty(parentPath) ? index.ToString() : $"{parentPath}.{index}";
            result[nested] = new ObjectAddress(objectIndex, path);
            indexNested(nested, objectIndex, path, result);
        }
    }
}

internal sealed class ReplayAnalysisCompletionWatchdog
{
    private readonly double terminalGameplayTime;
    private int terminalObservations;

    public ReplayAnalysisCompletionWatchdog(double lastObjectTime, double settlingTime, double replayEndTime = double.PositiveInfinity)
    {
        if (!double.IsFinite(lastObjectTime) || lastObjectTime < 0)
            throw new ArgumentOutOfRangeException(nameof(lastObjectTime));
        if (!double.IsFinite(settlingTime) || settlingTime < 0)
            throw new ArgumentOutOfRangeException(nameof(settlingTime));
        if (double.IsNaN(replayEndTime) || replayEndTime < 0)
            throw new ArgumentOutOfRangeException(nameof(replayEndTime));

        terminalGameplayTime = Math.Min(lastObjectTime, replayEndTime) + settlingTime;
    }

    public double TerminalGameplayTime => terminalGameplayTime;

    public bool ShouldComplete(
        double gameplayTime,
        bool scoreProcessorHasCompleted,
        bool officialPlaybackTerminated = false)
    {
        if (scoreProcessorHasCompleted)
            return true;

        if (!officialPlaybackTerminated
            && (!double.IsFinite(gameplayTime) || gameplayTime < terminalGameplayTime))
        {
            terminalObservations = 0;
            return false;
        }

        // The Player updates before its frame-stable children. Waiting for a second
        // observation guarantees that the terminal replay frame has been applied.
        return ++terminalObservations >= 2;
    }
}

internal sealed record ObjectAddress(int ObjectIndex, string? NestedPath);
