using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Difficulty;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Tests.Beatmaps;
using osu.Game.Online.API;
using osu.Game.Rulesets;
using Newtonsoft.Json;

namespace AimMod.Osu.Worker;

internal sealed record ValidatedPpInput(
    string StagingDirectory,
    string BeatmapPath,
    IReadOnlyList<string> Mods,
    double Accuracy,
    int MissCount,
    int? MaxCombo,
    PpScoreStatistics? Statistics,
    string? ModsJson);

internal static class PpInputValidator
{
    public static ValidatedPpInput Validate(PpWhatIfRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string stagingDirectory = validateDirectory(request.StagingDirectory);
        string beatmapPath = validateFile(stagingDirectory, request.BeatmapPath);
        if (!string.Equals(Path.GetExtension(beatmapPath), ".osu", StringComparison.OrdinalIgnoreCase))
            throw new RuntimeCommandException("input_invalid", "PP calculation requires a staged .osu beatmap file.");

        var mods = new List<string>();
        foreach (string mod in request.Mods ?? Array.Empty<string>())
        {
            string acronym = (mod ?? string.Empty).Trim().ToUpperInvariant();
            if (acronym.Length == 0)
                continue;
            if (acronym.Length > PpCalculationProtocol.MaximumModAcronymLength || acronym.Any(character => !char.IsAsciiLetterOrDigit(character)))
                throw new RuntimeCommandException("input_invalid", "PP calculation received an invalid mod acronym.");
            if (!mods.Contains(acronym, StringComparer.Ordinal))
                mods.Add(acronym);
        }

        if (mods.Count > PpCalculationProtocol.MaximumMods)
            throw new RuntimeCommandException("input_invalid", "PP calculation received too many mods.");
        if (!double.IsFinite(request.Accuracy) || request.Accuracy is < 0 or > 1)
            throw new RuntimeCommandException("input_invalid", "PP calculation accuracy must be between 0 and 1.");
        if (request.MissCount < 0)
            throw new RuntimeCommandException("input_invalid", "PP calculation miss count cannot be negative.");
        if (request.MaxCombo is < 0)
            throw new RuntimeCommandException("input_invalid", "PP calculation max combo cannot be negative.");
        if (request.Statistics is { } statistics
            && new[] { statistics.Great, statistics.Ok, statistics.Meh, statistics.Miss, statistics.SliderTailHit, statistics.LargeTickMiss }.Any(value => value < 0))
            throw new RuntimeCommandException("input_invalid", "PP calculation statistics cannot be negative.");
        if (request.ModsJson is { Length: > 16_384 })
            throw new RuntimeCommandException("input_invalid", "PP calculation mod settings are too large.");

        return new ValidatedPpInput(stagingDirectory, beatmapPath, mods, request.Accuracy, request.MissCount, request.MaxCombo, request.Statistics, request.ModsJson);
    }

    private static string validateDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new RuntimeCommandException("staged_path_invalid", "PP calculation requires an existing absolute staging directory.");

        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        rejectReparsePointAncestors(fullPath);
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new RuntimeCommandException("staged_path_invalid", "PP calculation staging cannot use a symbolic-link or junction directory.");
        return fullPath;
    }

    private static string validateFile(string stagingDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new RuntimeCommandException("input_missing", "PP calculation requires an existing staged beatmap file.");

        string fullPath = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(stagingDirectory, fullPath);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new RuntimeCommandException("staged_path_invalid", "PP calculation beatmap must be a real file inside the staging directory.");
        }

        var info = new FileInfo(fullPath);
        if (info.Length is <= 0 or > PpCalculationProtocol.MaximumBeatmapBytes)
            throw new RuntimeCommandException("input_invalid", "PP calculation beatmap is empty or too large.");
        return fullPath;
    }

    private static void rejectReparsePointAncestors(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new RuntimeCommandException("staged_path_invalid", "PP calculation cannot use symbolic-link or junction path components.");
        }
    }
}

internal sealed class OfficialPpWhatIfCalculator : IPpWhatIfCalculator
{
    public ValueTask<PpWhatIfResult> CalculateAsync(ValidatedPpInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            _ = typeof(OsuRuleset).Assembly;

            var ruleset = new OsuRuleset();
            Mod[] mods = createMods(ruleset, input);
            var workingBeatmap = new FlatWorkingBeatmap(input.BeatmapPath);
            DifficultyAttributes attributes = ruleset.CreateDifficultyCalculator(workingBeatmap).Calculate(mods, cancellationToken);
            if (attributes is not OsuDifficultyAttributes osuAttributes)
                throw new RuntimeCommandException("unsupported_ruleset", "PP calculation currently supports osu!standard only.");

            int objectCount = osuAttributes.HitCircleCount + osuAttributes.SliderCount + osuAttributes.SpinnerCount;
            PpGeneratedStatistics statistics = input.Statistics is { } supplied
                ? validateStatistics(supplied, objectCount, input.Accuracy)
                : GenerateStatistics(objectCount, input.Accuracy, input.MissCount);
            int maxCombo = Math.Clamp(input.MaxCombo ?? osuAttributes.MaxCombo, 0, osuAttributes.MaxCombo);
            var score = new ScoreInfo(workingBeatmap.BeatmapInfo, ruleset.RulesetInfo)
            {
                Accuracy = statistics.Accuracy,
                MaxCombo = maxCombo,
                Mods = mods,
                Statistics = new Dictionary<HitResult, int>
                {
                    [HitResult.Great] = statistics.Great,
                    [HitResult.Ok] = statistics.Ok,
                    [HitResult.Meh] = statistics.Meh,
                    [HitResult.Miss] = statistics.Miss,
                    [HitResult.SliderTailHit] = statistics.SliderTailHit ?? osuAttributes.SliderCount,
                    [HitResult.LargeTickMiss] = statistics.LargeTickMiss ?? 0,
                },
            };

            PerformanceAttributes performance = ruleset.CreatePerformanceCalculator().Calculate(score, attributes);
            var osuPerformance = performance as OsuPerformanceAttributes;
            return ValueTask.FromResult(new PpWhatIfResult(
                PpCalculationProtocol.EngineVersion,
                ruleset.CreateDifficultyCalculator(workingBeatmap).Version,
                osuAttributes.StarRating,
                osuAttributes.MaxCombo,
                objectCount,
                statistics.Great,
                statistics.Ok,
                statistics.Meh,
                statistics.Miss,
                statistics.Accuracy,
                performance.Total,
                osuPerformance?.Aim,
                osuPerformance?.Speed,
                osuPerformance?.Accuracy,
                osuPerformance?.Flashlight,
                osuPerformance?.Reading,
                osuPerformance?.EffectiveMissCount));
        }
        catch (RuntimeCommandException)
        {
            throw;
        }
        catch (ArgumentException exception)
        {
            throw new RuntimeCommandException("input_invalid", boundedError(exception, input));
        }
        catch (Exception exception)
        {
            throw new RuntimeCommandException("pp_calculation_failed", boundedError(exception, input));
        }
    }

    internal static PpGeneratedStatistics GenerateStatistics(int objectCount, double targetAccuracy, int requestedMisses)
    {
        int miss = Math.Clamp(requestedMisses, 0, Math.Max(0, objectCount));
        int remaining = Math.Max(0, objectCount - miss);
        if (objectCount == 0)
            return new PpGeneratedStatistics(0, 0, 0, 0, 0, null, null);

        int bestGreat = remaining;
        int bestOk = 0;
        int bestMeh = 0;
        double bestAccuracy = accuracyFor(bestGreat, bestOk, bestMeh, miss, objectCount);
        double bestError = Math.Abs(bestAccuracy - targetAccuracy);
        double targetNumerator = targetAccuracy * objectCount * 6;
        for (int meh = 0; meh <= remaining; meh++)
        {
            double idealOk = (6.0 * remaining - 5.0 * meh - targetNumerator) / 4.0;
            int roundedOk = Math.Clamp((int)Math.Round(idealOk), 0, remaining - meh);
            for (int ok = Math.Max(0, roundedOk - 1); ok <= Math.Min(remaining - meh, roundedOk + 1); ok++)
            {
                int great = remaining - ok - meh;
                double candidateAccuracy = accuracyFor(great, ok, meh, miss, objectCount);
                double error = Math.Abs(candidateAccuracy - targetAccuracy);
                if (error < bestError)
                {
                    bestGreat = great;
                    bestOk = ok;
                    bestMeh = meh;
                    bestAccuracy = candidateAccuracy;
                    bestError = error;
                }
            }
        }

        return new PpGeneratedStatistics(bestGreat, bestOk, bestMeh, miss, bestAccuracy, null, null);
    }

    private static PpGeneratedStatistics validateStatistics(PpScoreStatistics statistics, int objectCount, double storedAccuracy)
    {
        if (statistics.Great + statistics.Ok + statistics.Meh + statistics.Miss != objectCount)
            throw new RuntimeCommandException("statistics_incomplete", "The stored score judgement counts do not match the beatmap object count.");
        double accuracy = accuracyFor(statistics.Great, statistics.Ok, statistics.Meh, statistics.Miss, objectCount);
        if (Math.Abs(accuracy - storedAccuracy) > 0.0001)
            accuracy = storedAccuracy;
        return new PpGeneratedStatistics(
            statistics.Great, statistics.Ok, statistics.Meh, statistics.Miss, accuracy,
            statistics.SliderTailHit, statistics.LargeTickMiss);
    }

    private static double accuracyFor(int great, int ok, int meh, int miss, int objectCount) =>
        objectCount == 0 ? 0 : (great * 6 + ok * 2 + meh) / (double)(objectCount * 6);

    private static string boundedError(Exception exception, ValidatedPpInput input)
    {
        string message = exception.Message
                                  .Replace(input.StagingDirectory, "<staging>", StringComparison.Ordinal)
                                  .Replace(input.BeatmapPath, "<beatmap>", StringComparison.Ordinal)
                                  .Replace('\r', ' ')
                                  .Replace('\n', ' ')
                                  .Trim();
        return message.Length switch
        {
            0 => "The PP calculator rejected the staged beatmap.",
            > 300 => message[..300],
            _ => message,
        };
    }

    private static Mod[] createMods(Ruleset ruleset, ValidatedPpInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.ModsJson))
        {
            APIMod[] apiMods = JsonConvert.DeserializeObject<APIMod[]>(input.ModsJson)
                               ?? throw new RuntimeCommandException("input_invalid", "PP calculation mod settings are invalid.");
            if (apiMods.Length > PpCalculationProtocol.MaximumMods)
                throw new RuntimeCommandException("input_invalid", "PP calculation received too many mods.");
            Mod[] configured = apiMods.Select(mod => mod.ToMod(ruleset)).ToArray();
            if (configured.Any(mod => mod is UnknownMod))
                throw new RuntimeCommandException("unsupported_mod", "PP calculation contains a mod unsupported by this ruleset version.");
            return configured;
        }

        return input.Mods.Select(acronym => ruleset.CreateModFromAcronym(acronym)
                                  ?? throw new RuntimeCommandException("unsupported_mod", $"PP calculation does not support the {acronym} mod."))
                         .ToArray();
    }
}

internal sealed record PpGeneratedStatistics(
    int Great, int Ok, int Meh, int Miss, double Accuracy, int? SliderTailHit, int? LargeTickMiss);
