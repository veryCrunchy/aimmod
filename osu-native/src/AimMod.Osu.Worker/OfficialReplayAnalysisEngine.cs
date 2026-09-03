using AimMod.Osu.Runtime.Contracts;
using osu.Framework.Graphics;
using osu.Framework.Platform;
using osu.Game;
using osu.Game.Beatmaps;
using osu.Game.Online.API;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Judgements;
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
    public ValueTask<ReplayAnalysisResult> AnalyseAsync(ValidatedReplayInput input, CancellationToken cancellationToken)
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

            return ValueTask.FromResult(game.Result ?? throw new ReplayAnalysisException(
                "analysis_failed",
                "Official replay playback exited without a result."));
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
    private Score? score;
    private AnalysisReplayPlayer? player;
    private bool finished;

    public ReplayAnalysisResult? Result { get; private set; }
    public ReplayAnalysisException? Failure { get; private set; }

    public ReplayAnalysisGame(IBeatmap sourceBeatmap, string replayPath, CancellationToken cancellationToken)
    {
        this.sourceBeatmap = sourceBeatmap;
        this.replayPath = replayPath;
        this.cancellationToken = cancellationToken;

        var offlineApi = new DummyAPIAccess();
        offlineApi.Logout();
        API = offlineApi;
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

            var workingBeatmap = new TestWorkingBeatmap(sourceBeatmap, audioManager: Audio);
            workingBeatmap.LoadTrack();
            using (var replayStream = new FileStream(replayPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
                score = new SuppliedBeatmapScoreDecoder(workingBeatmap).Parse(replayStream);

            if (score.ScoreInfo.Pauses.Count > ReplayAnalysisProtocol.MaximumPauses)
                throw new ReplayAnalysisException("result_too_large", "The replay contains too many pause records to return safely.");

            Beatmap.Value = workingBeatmap;
            Ruleset.Value = score.ScoreInfo.Ruleset;
            SelectedMods.Value = score.ScoreInfo.Mods;

            var stack = new OsuScreenStack { RelativeSizeAxes = Axes.Both };
            Add(stack);
            player = new AnalysisReplayPlayer(score, complete, fail);
            stack.Push(player);
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

internal sealed partial class AnalysisReplayPlayer : ReplayPlayer
{
    private readonly Action<IReadOnlyList<ReplayObjectJudgement>> complete;
    private readonly Action<ReplayAnalysisException> fail;
    private readonly List<ReplayObjectJudgement> judgements = new();
    private Dictionary<HitObject, ObjectAddress> addresses = new(ReferenceEqualityComparer.Instance);

    protected override bool PauseOnFocusLost => false;

    public AnalysisReplayPlayer(
        Score score,
        Action<IReadOnlyList<ReplayObjectJudgement>> complete,
        Action<ReplayAnalysisException> fail)
        : base(score, new PlayerConfiguration
        {
            AllowPause = false,
            AllowSkipping = false,
            ShowResults = false,
        })
    {
        this.complete = complete;
        this.fail = fail;
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
        ScoreProcessor.NewJudgement += recordJudgement;
        ScoreProcessor.HasCompleted.BindValueChanged(completed =>
        {
            if (completed.NewValue)
                complete(judgements.ToArray());
        }, true);
    }

    private void recordJudgement(JudgementResult result)
    {
        if (judgements.Count >= ReplayAnalysisProtocol.MaximumJudgements)
        {
            fail(new ReplayAnalysisException("result_too_large", "The replay produced too many judgements to return safely."));
            return;
        }

        addresses.TryGetValue(result.HitObject, out ObjectAddress? address);
        Vector2? objectPosition = (result.HitObject as IHasPosition)?.Position;
        Vector2? cursorPosition = (result as OsuHitCircleJudgementResult)?.CursorPositionAtHit;

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
            result.ComboAfterJudgement));
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

internal sealed record ObjectAddress(int ObjectIndex, string? NestedPath);
