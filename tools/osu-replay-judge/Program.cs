using System.Text.Json;
using System.Text.Json.Serialization;
using osu.Framework.Graphics;
using osu.Framework.Platform;
using osu.Game;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Judgements;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Screens;
using osu.Game.Screens.Play;
using osu.Game.Tests;
using osu.Game.Tests.Beatmaps;
using osuTK;

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

if (args is ["--probe"])
{
    Console.Out.Write(JsonSerializer.Serialize(new
    {
        type = "probe",
        engineVersion = ReplayJudgeProtocol.EngineVersion,
        headlessAudioMuted = true,
        timeoutClock = "wall",
        timeoutMs = (int)ReplayJudgeProtocol.WallClockTimeout.TotalMilliseconds,
    }, jsonOptions));
    return 0;
}

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: osu-replay-judge <beatmap-file> <replay-file>");
    return 2;
}

string beatmapPath = Path.GetFullPath(args[0]);
string replayPath = Path.GetFullPath(args[1]);
if (!File.Exists(beatmapPath) || !File.Exists(replayPath))
{
    Console.Error.WriteLine("The beatmap or replay file was not found.");
    return 3;
}

try
{
    // Loading the ruleset assembly before LegacyBeatmapDecoder is constructed
    // lets AssemblyRulesetStore resolve Mode:0 to the official osu! ruleset.
    _ = typeof(OsuRuleset).Assembly;
    var sourceBeatmap = new FlatWorkingBeatmap(beatmapPath).Beatmap;
    var game = new ReplayJudgeGame(sourceBeatmap, replayPath);
    using (var host = new CleanRunHeadlessGameHost(realtime: false, callingMethodName: "aimmod-replay-judge"))
        host.Run(game);

    ReplayJudgeResponse response = game.Response ?? throw new InvalidOperationException("Official replay playback exited without a result.");
    Console.Out.Write(JsonSerializer.Serialize(response, jsonOptions));
    return response.Error is null ? 0 : 4;
}
catch (Exception error)
{
    Console.Out.Write(JsonSerializer.Serialize(new ReplayJudgeResponse(
        ReplayJudgeProtocol.EngineVersion,
        "officialRulesetPlayback",
        Array.Empty<int>(),
        Array.Empty<ObjectJudgement>(),
        new JudgementSummary(0, 0, 0, 0, 0, 0),
        error.Message), jsonOptions));
    return 4;
}

sealed class SuppliedBeatmapScoreDecoder : LegacyScoreDecoder
{
    private readonly WorkingBeatmap beatmap;

    public SuppliedBeatmapScoreDecoder(WorkingBeatmap beatmap)
    {
        this.beatmap = beatmap;
    }

    protected override Ruleset GetRuleset(int rulesetId)
    {
        if (rulesetId != 0)
            throw new NotSupportedException($"AimMod's exact replay judge currently supports osu!standard, not ruleset {rulesetId}.");
        return new OsuRuleset();
    }

    protected override WorkingBeatmap GetBeatmap(string md5Hash) => beatmap;
}

sealed partial class ReplayJudgeGame : OsuGameBase
{
    private readonly IBeatmap sourceBeatmap;
    private readonly string replayPath;
    private Score? score;
    private AnalysisReplayPlayer? player;
    private readonly CancellationTokenSource timeoutCancellation = new();
    private bool finished;

    public ReplayJudgeResponse? Response { get; private set; }

    public ReplayJudgeGame(IBeatmap sourceBeatmap, string replayPath)
    {
        this.sourceBeatmap = sourceBeatmap;
        this.replayPath = replayPath;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        try
        {
            // CleanRunHeadlessGameHost advances its game clock as fast as the CPU
            // permits. Keep analysis silent before any track or sample can be
            // created by ReplayPlayer.
            Audio.Volume.Value = 0;
            Audio.VolumeTrack.Value = 0;
            Audio.VolumeSample.Value = 0;

            var workingBeatmap = new TestWorkingBeatmap(sourceBeatmap, audioManager: Audio);
            workingBeatmap.LoadTrack();
            using (var replayStream = File.OpenRead(replayPath))
                score = new SuppliedBeatmapScoreDecoder(workingBeatmap).Parse(replayStream);

            Beatmap.Value = workingBeatmap;
            Ruleset.Value = score.ScoreInfo.Ruleset;
            SelectedMods.Value = score.ScoreInfo.Mods;

            var stack = new OsuScreenStack { RelativeSizeAxes = Axes.Both };
            Add(stack);
            player = new AnalysisReplayPlayer(score, complete);
            stack.Push(player);

            _ = enforceWallClockTimeout(timeoutCancellation.Token);
        }
        catch (Exception error)
        {
            fail(error.Message);
        }
    }

    private async Task enforceWallClockTimeout(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ReplayJudgeProtocol.WallClockTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        Schedule(() => fail("Official replay playback timed out."));
    }

    private void complete(IReadOnlyList<ObjectJudgement> judgements)
    {
        if (finished)
            return;
        finished = true;
        timeoutCancellation.Cancel();

        Response = new ReplayJudgeResponse(
            ReplayJudgeProtocol.EngineVersion,
            "officialRulesetPlayback",
            score!.ScoreInfo.Pauses.ToArray(),
            judgements,
            JudgementSummary.From(judgements),
            null);
        Host.Exit();
    }

    private void fail(string message)
    {
        if (finished)
            return;
        finished = true;
        timeoutCancellation.Cancel();
        Response = new ReplayJudgeResponse(
            ReplayJudgeProtocol.EngineVersion,
            "officialRulesetPlayback",
            score?.ScoreInfo.Pauses.ToArray() ?? Array.Empty<int>(),
            player?.Judgements ?? Array.Empty<ObjectJudgement>(),
            JudgementSummary.From(player?.Judgements ?? Array.Empty<ObjectJudgement>()),
            message);
        Host.Exit();
    }
}

sealed partial class AnalysisReplayPlayer : ReplayPlayer
{
    private readonly Action<IReadOnlyList<ObjectJudgement>> complete;
    private readonly List<ObjectJudgement> judgements = new();
    private Dictionary<HitObject, ObjectAddress> addresses = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyList<ObjectJudgement> Judgements => judgements;

    protected override bool PauseOnFocusLost => false;

    public AnalysisReplayPlayer(Score score, Action<IReadOnlyList<ObjectJudgement>> complete)
        : base(score, new PlayerConfiguration
        {
            AllowPause = false,
            AllowSkipping = false,
            ShowResults = false,
        })
    {
        this.complete = complete;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        if (!LoadedBeatmapSuccessfully)
            throw new InvalidOperationException("The official ruleset could not load the supplied beatmap.");

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
        addresses.TryGetValue(result.HitObject, out ObjectAddress? address);
        Vector2? objectPosition = (result.HitObject as IHasPosition)?.Position;
        Vector2? cursorPosition = (result as OsuHitCircleJudgementResult)?.CursorPositionAtHit;

        judgements.Add(new ObjectJudgement(
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
            objectPosition is null ? null : new Point(objectPosition.Value.X, objectPosition.Value.Y),
            cursorPosition is null ? null : new Point(cursorPosition.Value.X, cursorPosition.Value.Y),
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

sealed record ObjectAddress(int ObjectIndex, string? NestedPath);
sealed record Point(float X, float Y);
sealed record ObjectJudgement(
    int? ObjectIndex,
    string? NestedPath,
    string ObjectType,
    double StartTimeMs,
    double EndTimeMs,
    string Result,
    string MaximumResult,
    double JudgementTimeMs,
    double TimeOffsetMs,
    double? GameplayRate,
    Point? ObjectPosition,
    Point? CursorPosition,
    int ComboBefore,
    int ComboAfter);

sealed record JudgementSummary(int Great, int Ok, int Meh, int Miss, int SliderBreaks, int Other)
{
    public static JudgementSummary From(IReadOnlyList<ObjectJudgement> judgements)
    {
        int great = 0, ok = 0, meh = 0, miss = 0, sliderBreaks = 0, other = 0;
        foreach (ObjectJudgement judgement in judgements)
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
        return new JudgementSummary(great, ok, meh, miss, sliderBreaks, other);
    }
}

sealed record ReplayJudgeResponse(
    string EngineVersion,
    string TimeBasis,
    IReadOnlyList<int> Pauses,
    IReadOnlyList<ObjectJudgement> Judgements,
    JudgementSummary Summary,
    string? Error);

static class ReplayJudgeProtocol
{
    public const string EngineVersion = "ppy.osu.Game/2026.730.0";
    public static readonly TimeSpan WallClockTimeout = TimeSpan.FromMinutes(2);
}
