using System.Text.Json;
using System.Text.Json.Serialization;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Tests;

/// <summary>Seeded synthetic score histories and replay analyses for coaching equivalence tests.</summary>
internal static class CoachingFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions json = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string[][] mod_sets =
    [
        [], [], ["HD"], ["HR"], ["HD", "HR"], ["DT"], ["HD", "DT"], ["hd"], ["RX"], ["EZ"],
    ];

    private static readonly string[] results = ["Great", "Great", "Great", "Great", "Ok", "Meh", "Miss", "LargeTickMiss", "SliderTailMiss"];

    public static string Serialise<T>(T value) => JsonSerializer.Serialize(value, json);

    public static (LocalReplay[] Runs, Dictionary<Guid, ReplayAnalysisResult> Analyses) History(int seed, int count, int maps = 24)
    {
        var random = new Random(seed);
        var runs = new List<LocalReplay>(count);
        var analyses = new Dictionary<Guid, ReplayAnalysisResult>();
        DateTimeOffset time = Now.AddDays(-400);
        for (int i = 0; i < count; i++)
        {
            // Some plays share a timestamp so ordering ties are exercised.
            if (random.NextDouble() > 0.12)
                time = time.AddMinutes(random.Next(1, 60 * 30));
            int map = random.Next(maps);
            string[] mods = mod_sets[random.Next(mod_sets.Length)];
            string modsJson = mods.Length > 0 && mods[0] == "DT" && random.NextDouble() < 0.5
                ? "[{\"acronym\":\"DT\",\"settings\":{\"speed_change\":1.2}}]"
                : string.Empty;
            double accuracy = random.NextDouble() < 0.02 ? double.NaN : 0.72 + random.NextDouble() * 0.28;
            double stars = random.NextDouble() < 0.03 ? double.NaN : 3 + map % 7 * 0.45 + random.NextDouble() * 0.2;
            double? pp = random.NextDouble() < 0.15 ? null : 40 + stars * 30 * accuracy + random.NextDouble() * 40;
            if (pp is { } value && !double.IsFinite(value))
                pp = null;
            var run = new LocalReplay(
                guid(seed, i + 1),
                guid(seed, 100_000 + map / 3),
                guid(seed, 200_000 + map),
                $"Synthetic Map {map}",
                "Synthetic Artist",
                $"Difficulty {map % 4}",
                random.NextDouble() < 0.04 ? "taiko" : "osu",
                random.NextDouble() < 0.9 ? "Synthetic Player" : "Other Player",
                time,
                stars,
                accuracy,
                random.Next(100_000, 5_000_000),
                random.Next(50, 900),
                random.Next(0, 12),
                pp,
                mods,
                random.NextDouble() < 0.7,
                BeatmapHash: random.NextDouble() < 0.8 ? $"{map:x32}" : string.Empty,
                ModsJson: modsJson,
                OnlineScoreId: random.NextDouble() < 0.4 ? 5_000 + i : 0,
                IsLocallyStored: random.NextDouble() < 0.85,
                Origin: random.NextDouble() < 0.2 ? LocalLibraryOrigin.Stable : LocalLibraryOrigin.Lazer,
                OnlineBeatmapId: 900 + map);
            runs.Add(run);
            if (run.HasReplayFile && random.NextDouble() < 0.6)
                analyses[run.ScoreId] = Analysis(random, random.Next(40, 160));
        }

        // A few duplicated score records with a later timestamp.
        for (int i = 0; i < Math.Min(5, runs.Count); i++)
        {
            LocalReplay duplicate = runs[random.Next(runs.Count)];
            runs.Add(duplicate with { PlayedAt = duplicate.PlayedAt.AddMinutes(5), Accuracy = Math.Clamp(duplicate.Accuracy + 0.01, 0, 1) });
        }

        return (runs.OrderBy(_ => random.Next()).ToArray(), analyses);
    }

    public static ReplayAnalysisResult Analysis(Random random, int objects)
    {
        var judgements = new List<ReplayObjectJudgement>(objects);
        double time = 500;
        for (int index = 0; index < objects; index++)
        {
            time += random.Next(80, 400);
            string result = results[random.Next(results.Length)];
            bool miss = result == "Miss";
            bool nested = result is "LargeTickMiss" or "SliderTailMiss";
            ReplayMissAnalysis? missAnalysis = miss && random.NextDouble() < 0.85
                ? new ReplayMissAnalysis(
                    (ReplayMissReason)random.Next(0, 7),
                    32,
                    random.NextDouble() * 80,
                    random.NextDouble() * 60 - 30,
                    new ReplayPoint(random.Next(512), random.Next(384)),
                    random.NextDouble() < 0.7 ? random.NextDouble() * 120 - 60 : null,
                    random.NextDouble() < 0.7 ? random.NextDouble() * 90 : null,
                    null,
                    random.NextDouble() * 70,
                    random.NextDouble() < 0.5,
                    random.NextDouble() < 0.5,
                    random.NextDouble() < 0.5,
                    random.NextDouble() * 4 - 2,
                    Confidence: random.NextDouble())
                : null;
            judgements.Add(new ReplayObjectJudgement(
                index,
                nested ? "tail" : null,
                random.NextDouble() < 0.65 ? "HitCircle" : "Slider",
                time,
                time + random.Next(0, 300),
                result,
                nested ? "LargeTickHit" : "Great",
                time + 5,
                random.NextDouble() < 0.03 ? double.NaN : random.NextDouble() * 60 - 30,
                1,
                random.NextDouble() < 0.9 ? new ReplayPoint(random.Next(512), random.Next(384)) : null,
                random.NextDouble() < 0.9 ? new ReplayPoint(random.Next(512), random.Next(384)) : null,
                index,
                miss ? 0 : index + 1,
                missAnalysis));
        }

        int count(string value) => judgements.Count(judgement => judgement.Result == value);
        return new ReplayAnalysisResult(
            ReplayAnalysisProtocol.EngineVersion,
            "officialRulesetPlayback",
            true,
            ReplayAnalysisProtocol.WallClockTimeoutMs,
            [],
            judgements,
            new ReplayJudgementSummary(count("Great"), count("Ok"), count("Meh"), count("Miss"),
                count("LargeTickMiss") + count("SliderTailMiss"), 0));
    }

    private static Guid guid(int seed, int value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, value);
        BitConverter.TryWriteBytes(bytes[8..], seed);
        return new Guid(bytes);
    }
}
