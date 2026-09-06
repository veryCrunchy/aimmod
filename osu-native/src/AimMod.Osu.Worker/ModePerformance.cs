using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Taiko;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Scoring;

namespace AimMod.Osu.Worker;

internal static class ModePerformance
{
    public static Ruleset CreateRuleset(int id) => id switch {
        0 => new OsuRuleset(), 1 => new TaikoRuleset(), 2 => new CatchRuleset(), 3 => new ManiaRuleset(),
        _ => throw new ArgumentException("This score mode is not supported.")
    };

    public static int Judged(int mode, IReadOnlyDictionary<HitResult, int> stats) {
        HitResult[] results = mode switch {
            1 => [HitResult.Great, HitResult.Ok, HitResult.Meh, HitResult.Miss],
            2 => [HitResult.Great, HitResult.LargeTickHit, HitResult.LargeTickMiss, HitResult.SmallTickHit, HitResult.SmallTickMiss, HitResult.Miss],
            3 => [HitResult.Perfect, HitResult.Great, HitResult.Good, HitResult.Ok, HitResult.Meh, HitResult.Miss],
            _ => throw new ArgumentException("Invalid mode.")
        };
        return checked(results.Sum(result => stats.GetValueOrDefault(result)));
    }

    public static ModeResult Calculate(WorkingBeatmap working, Ruleset ruleset, Mod[] mods,
        Dictionary<HitResult,int> stats, double accuracy, int combo, bool lazer, bool passed,
        long totalScore, long? legacyTotalScore, CancellationToken token)
    {
        int mode = ruleset.RulesetInfo.OnlineID;
        if (working.BeatmapInfo.Ruleset.OnlineID != 0 && working.BeatmapInfo.Ruleset.OnlineID != mode)
            throw new ArgumentException("The score mode does not match this beatmap.");
        var playable = working.GetPlayableBeatmap(ruleset.RulesetInfo, mods, token);
        using var processor = ruleset.CreateScoreProcessor();
        processor.Mods.Value = mods;
        processor.ApplyBeatmap(playable);
        var perfect = processor.MaximumStatistics;
        // Stable judges a mania hold once; lazer judges its head and tail separately.
        if (!lazer && mode == 3) perfect[HitResult.Perfect] = playable.HitObjects.Count;
        int count = Judged(mode, perfect);
        int judged = Judged(mode, stats);
        if (count <= 0 || count > 100000 || stats.Values.Any(n => n < 0) || (judged > count && !(mode == 2 && !lazer)) || (passed && judged != count && !(mode == 2 && !lazer)))
            throw new ArgumentException("Score judgements do not match this beatmap revision.");
        var calculator = ruleset.CreateDifficultyCalculator(working);
        var attributes = calculator.Calculate(mods, token);
        if (combo < 0 || combo > attributes.MaxCombo) throw new ArgumentException("Score combo exceeds the beatmap maximum.");
        if (mode == 2 && !lazer) {
            // Stable tiny-droplet rounding differs from lazer. Validate combo objects
            // independently and retain the recorded tiny-droplet total for perfect PP.
            int comboJudged = checked(stats.GetValueOrDefault(HitResult.Great) + stats.GetValueOrDefault(HitResult.LargeTickHit)
                + stats.GetValueOrDefault(HitResult.Miss) + stats.GetValueOrDefault(HitResult.LargeTickMiss));
            if (judged > 100000 || comboJudged > attributes.MaxCombo || (passed && comboJudged != attributes.MaxCombo))
                throw new ArgumentException("Score judgements do not match this beatmap revision.");
            if (passed) {
                perfect[HitResult.SmallTickHit] = checked(stats.GetValueOrDefault(HitResult.SmallTickHit) + stats.GetValueOrDefault(HitResult.SmallTickMiss));
                count = Judged(mode, perfect);
            }
        }
        double CalculatePp(Dictionary<HitResult,int> hits, double acc, int maxCombo) {
            var score = new ScoreInfo(working.BeatmapInfo, ruleset.RulesetInfo) {
                Mods = mods, Statistics = hits, MaximumStatistics = perfect, Accuracy = acc,
                MaxCombo = maxCombo, IsLegacyScore = !lazer, Passed = passed,
                TotalScore = totalScore, LegacyTotalScore = legacyTotalScore
            };
            return (ruleset.CreatePerformanceCalculator() ?? throw new ArgumentException("This mode has no PP calculator.")).Calculate(score, attributes).Total;
        }
        // These rulesets use played judgement counts for length/accuracy scaling.
        // Retain full-map difficulty as the official score calculation does.
        double pp = judged == 0 ? 0 : CalculatePp(stats, accuracy, combo);
        double maxPp = CalculatePp(perfect, 1, attributes.MaxCombo);
        if (!double.IsFinite(pp) || !double.IsFinite(maxPp) || pp < 0 || maxPp < 0)
            throw new ArgumentException("Calculation returned invalid PP.");
        return new(pp, maxPp, attributes.StarRating, count, attributes.MaxCombo, calculator.Version);
    }
}
internal sealed record ModeResult(double Pp, double MaxPp, double Stars, int ObjectCount, int MaxCombo, int RulesetVersion);
