using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Coaching;

public static class CoachingReportBuilder
{
    public static CoachingReport Build(
        IReadOnlyList<LocalReplay> runs,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        Guid? selectedScoreId = null)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(analyses);

        LocalReplay[] recent = runs.Where(isStandardRun)
                                  .GroupBy(run => run.ScoreId)
                                  .Select(group => group.OrderByDescending(run => run.PlayedAt).First())
                                  .OrderByDescending(run => run.PlayedAt)
                                  .Take(CoachingLimits.MaximumRuns)
                                  .ToArray();
        LocalReplay? selected = selectedScoreId is { } scoreId
            ? recent.FirstOrDefault(run => run.ScoreId == scoreId)
            : recent.FirstOrDefault();

        CoachingAccuracySummary accuracy = buildAccuracy(recent);
        CoachingMissSummary misses = buildMisses(recent, analyses);
        CoachingTimingSummary timing = buildTiming(recent, analyses);
        IReadOnlyList<CoachingChartSeries> series = buildSeries(recent, analyses);
        CoachingAdvice nextPlay = buildAdvice(selected, recent, analyses);

        return new CoachingReport(
            selected is null ? null : CoachingRunSearch.ToRecentRun(selected),
            accuracy,
            misses,
            timing,
            series,
            nextPlay)
        {
            Intelligence = CoachingPredictionEngine.Build(recent, analyses, selected?.ScoreId),
        };
    }

    private static CoachingAccuracySummary buildAccuracy(IReadOnlyList<LocalReplay> recent)
    {
        LocalReplay[] runs = recent.Where(run => validAccuracy(run.Accuracy)).ToArray();
        if (runs.Length == 0)
            return new CoachingAccuracySummary(0, null, null, null, null);

        double? change = null;
        if (runs.Length >= 4)
        {
            LocalReplay[] chronological = runs.OrderBy(run => run.PlayedAt).ToArray();
            int half = chronological.Length / 2;
            double older = chronological.Take(half).Average(run => run.Accuracy);
            double newer = chronological.Skip(chronological.Length - half).Average(run => run.Accuracy);
            change = newer - older;
        }

        return new CoachingAccuracySummary(
            runs.Length,
            runs.Average(run => run.Accuracy),
            runs.Max(run => run.Accuracy),
            runs.OrderByDescending(run => run.PlayedAt).First().Accuracy,
            change);
    }

    private static CoachingMissSummary buildMisses(
        IReadOnlyList<LocalReplay> recent,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses)
    {
        int total = recent.Sum(run => Math.Max(0, run.MissCount));
        ReplayAnalysisResult[] available = recent.Select(run => analyses.GetValueOrDefault(run.ScoreId))
                                                 .Where(analysis => validAnalysis(analysis))
                                                 .Cast<ReplayAnalysisResult>()
                                                 .ToArray();
        return new CoachingMissSummary(
            recent.Count,
            total,
            recent.Count == 0 ? null : (double)total / recent.Count,
            recent.Count(run => run.MissCount <= 0),
            available.Length,
            available.Sum(analysis => Math.Max(0, analysis.Summary.Miss)),
            available.Sum(analysis => Math.Max(0, analysis.Summary.SliderBreaks)));
    }

    private static CoachingTimingSummary buildTiming(
        IReadOnlyList<LocalReplay> recent,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses)
    {
        ReplayAnalysisResult[] available = recent.Select(run => analyses.GetValueOrDefault(run.ScoreId))
                                                 .Where(analysis => validAnalysis(analysis))
                                                 .Cast<ReplayAnalysisResult>()
                                                 .ToArray();
        double[] offsets = available.SelectMany(timingOffsets).ToArray();
        if (offsets.Length == 0)
            return new CoachingTimingSummary(available.Length, 0, null, null, null, 0, 0, 0);

        double mean = offsets.Average();
        double variance = offsets.Average(offset => Math.Pow(offset - mean, 2));
        double threshold = CoachingLimits.CentredTimingThresholdMilliseconds;
        return new CoachingTimingSummary(
            available.Length,
            offsets.Length,
            mean,
            offsets.Average(Math.Abs),
            Math.Sqrt(variance),
            offsets.Count(offset => offset < -threshold),
            offsets.Count(offset => Math.Abs(offset) <= threshold),
            offsets.Count(offset => offset > threshold));
    }

    private static IReadOnlyList<CoachingChartSeries> buildSeries(
        IReadOnlyList<LocalReplay> recent,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses)
    {
        LocalReplay[] chronological = recent.OrderBy(run => run.PlayedAt).ToArray();
        CoachingChartPoint[] accuracy = chronological.Where(run => validAccuracy(run.Accuracy))
                                                     .Select(run => new CoachingChartPoint(run.ScoreId, run.PlayedAt, run.Accuracy * 100))
                                                     .ToArray();
        CoachingChartPoint[] misses = chronological.Select(run => new CoachingChartPoint(run.ScoreId, run.PlayedAt, Math.Max(0, run.MissCount)))
                                                   .ToArray();
        double cumulativeScore = 0;
        CoachingChartPoint[] score = chronological.Select(run =>
                                                   {
                                                       cumulativeScore += Math.Max(0, run.TotalScore);
                                                       return new CoachingChartPoint(run.ScoreId, run.PlayedAt, cumulativeScore);
                                                   })
                                                   .ToArray();
        CoachingChartPoint[] playCount = chronological.Select((run, index) =>
                                                       new CoachingChartPoint(run.ScoreId, run.PlayedAt, index + 1))
                                                       .ToArray();
        CoachingChartPoint[] timing = chronological.Select(run =>
                                                     {
                                                         ReplayAnalysisResult? analysis = analyses.GetValueOrDefault(run.ScoreId);
                                                         double[] offsets = validAnalysis(analysis) ? timingOffsets(analysis!).ToArray() : Array.Empty<double>();
                                                         return offsets.Length == 0
                                                             ? null
                                                             : new CoachingChartPoint(run.ScoreId, run.PlayedAt, offsets.Average());
                                                     })
                                                     .Where(point => point is not null)
                                                     .Cast<CoachingChartPoint>()
                                                     .ToArray();

        return new[]
        {
            new CoachingChartSeries("accuracy", "Accuracy", "percent", accuracy),
            new CoachingChartSeries("misses", "Misses", "count", misses),
            new CoachingChartSeries("cumulativeScore", "Accumulated score", "score", score),
            new CoachingChartSeries("playCount", "Accumulated plays", "count", playCount),
            new CoachingChartSeries("timingOffset", "Average hit offset", "milliseconds", timing),
        };
    }

    private static CoachingAdvice buildAdvice(
        LocalReplay? selected,
        IReadOnlyList<LocalReplay> history,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses)
    {
        if (selected is null)
        {
            return new CoachingAdvice(
                "Play a track to get started",
                "Complete a local run with a saved replay, then return for a specific next step.",
                null,
                null,
                null);
        }

        CoachingSetupBenchmark benchmark = CoachingPredictionEngine.BuildSetupBenchmark(history, selected);
        double accuracyTarget = benchmark.PriorMedianAccuracy is { } median
            ? Math.Max(selected.Accuracy, median)
            : selected.Accuracy;
        accuracyTarget = double.IsFinite(accuracyTarget) ? Math.Clamp(accuracyTarget, 0, 1) : 0;

        ReplayAnalysisResult? analysis = analyses.GetValueOrDefault(selected.ScoreId);
        if (validAnalysis(analysis))
        {
            ReplayObjectJudgement? firstMiss = analysis!.Judgements
                                                         .Where(judgement => isMiss(judgement) && double.IsFinite(judgement.StartTimeMs))
                                                         .OrderBy(judgement => judgement.StartTimeMs)
                                                         .FirstOrDefault();
            if (firstMiss is not null)
            {
                double time = Math.Max(0, firstMiss.StartTimeMs);
                return advice(selected,
                    $"Retry {displayName(selected)}",
                    $"Open the replay at {formatTime(time)} and watch the lead-in to the first miss. This play had {missLabel(Math.Max(1, analysis.Summary.Miss))}. Retry with the same mods and target at most {missLabel(missTarget(selected, benchmark))} while keeping at least {formatAccuracy(accuracyTarget)} accuracy.",
                    time);
            }

            if (analysis.Summary.Miss > 0)
            {
                return advice(selected,
                    $"Retry {displayName(selected)}",
                    $"This play had {missLabel(Math.Max(0, analysis.Summary.Miss))}. Use the same mods and target at most {missLabel(missTarget(selected, benchmark))} while keeping at least {formatAccuracy(accuracyTarget)} accuracy.");
            }

            if (analysis.Summary.SliderBreaks > 0)
            {
                ReplayObjectJudgement? firstBreak = analysis.Judgements
                                                                  .Where(judgement => isSliderBreak(judgement)
                                                                                      && double.IsFinite(judgement.StartTimeMs))
                                                                  .OrderBy(judgement => judgement.StartTimeMs)
                                                                  .FirstOrDefault();
                double? reviewTime = firstBreak is null ? null : Math.Max(0, firstBreak.StartTimeMs);
                return advice(selected,
                    $"Retry {displayName(selected)}",
                    firstBreak is null
                        ? $"This play had {analysis.Summary.SliderBreaks:N0} slider breaks. Repeat the same setup and target at most {Math.Max(0, analysis.Summary.SliderBreaks - 1):N0} while holding the current accuracy."
                        : $"Open the replay at {formatTime(reviewTime!.Value)} and inspect the first broken slider. Repeat the same setup and target at most {Math.Max(0, analysis.Summary.SliderBreaks - 1):N0} slider breaks.",
                    reviewTime);
            }

            double[] offsets = timingOffsets(analysis).ToArray();
            if (offsets.Length >= CoachingLimits.MinimumTimingSamplesForDirectionAdvice)
            {
                double mean = offsets.Average();
                if (mean >= CoachingLimits.DirectionAdviceThresholdMilliseconds)
                {
                    return advice(selected,
                        $"Replay {displayName(selected)}",
                        $"The stored circle judgements averaged {mean:0.#} ms late. Keep the same settings for one repeat and check whether the late shift appears again before changing an offset.");
                }

                if (mean <= -CoachingLimits.DirectionAdviceThresholdMilliseconds)
                {
                    return advice(selected,
                        $"Replay {displayName(selected)}",
                        $"The stored circle judgements averaged {Math.Abs(mean):0.#} ms early. Keep the same settings for one repeat and check whether the early shift appears again before changing an offset.");
                }
            }

            int lowerJudgements = Math.Max(0, analysis.Summary.Ok) + Math.Max(0, analysis.Summary.Meh);
            if (lowerJudgements > 0)
            {
                return advice(selected,
                    $"Replay {displayName(selected)}",
                    $"Repeat the same setup and target at most {Math.Max(0, lowerJudgements - 1):N0} 100-or-50 judgements while keeping at least {formatAccuracy(accuracyTarget)} accuracy.");
            }

            return advice(selected,
                $"Repeat {displayName(selected)}",
                "Play it once more and see whether you can repeat the clean result.");
        }

        if (selected.MissCount > 0)
        {
            return advice(selected,
                $"Retry {displayName(selected)}",
                $"This play had {missLabel(selected.MissCount)}. Use the same mods and target at most {missLabel(missTarget(selected, benchmark))} while keeping at least {formatAccuracy(accuracyTarget)} accuracy.");
        }

        if (benchmark.AccuracyChangeFromBest is < -0.005 && benchmark.BestPriorAccuracy is { } best)
        {
            return advice(selected,
                $"Replay {displayName(selected)}",
                $"Your matching-setup best is {best:P2}. Repeat this map and aim to close the measured {Math.Abs(benchmark.AccuracyChangeFromBest.Value) * 100:0.00}-point gap.");
        }

        return selected.HasReplayFile
            ? advice(selected, $"Review {displayName(selected)}", "Analyse this replay to identify a specific pattern for the next attempt.")
            : advice(selected, $"Replay {displayName(selected)}", "Save the replay on the next run so you can review individual misses and hit timing.");
    }

    private static CoachingAdvice advice(LocalReplay run, string title, string detail, double? reviewTime = null) =>
        new(title, detail, run.ScoreId, run.BeatmapId, reviewTime);

    private static int missTarget(LocalReplay selected, CoachingSetupBenchmark benchmark)
    {
        int improveCurrent = Math.Max(0, selected.MissCount - 1);
        return benchmark.BestPriorMissCount is { } priorBest
            ? Math.Min(improveCurrent, priorBest)
            : improveCurrent;
    }

    private static string missLabel(int count) => $"{Math.Max(0, count):N0} {(count == 1 ? "miss" : "misses")}";

    private static string formatAccuracy(double accuracy) => $"{accuracy * 100:0.0}%";

    private static IEnumerable<double> timingOffsets(ReplayAnalysisResult analysis) =>
        analysis.Judgements.Where(judgement => !isMiss(judgement)
                                                && double.IsFinite(judgement.TimeOffsetMs)
                                                && string.Equals(judgement.MaximumResult, "Great", StringComparison.OrdinalIgnoreCase)
                                                && judgement.ObjectType.EndsWith("Circle", StringComparison.OrdinalIgnoreCase))
                .Select(judgement => judgement.TimeOffsetMs);

    private static bool validAnalysis(ReplayAnalysisResult? analysis) =>
        analysis is { Summary: not null, Judgements: not null };

    private static bool validAccuracy(double accuracy) => double.IsFinite(accuracy) && accuracy is >= 0 and <= 1;

    private static bool isStandardRun(LocalReplay run) =>
        string.Equals(run.RulesetShortName, "osu", StringComparison.OrdinalIgnoreCase);

    private static bool isMiss(ReplayObjectJudgement judgement) =>
        string.Equals(judgement.Result, "Miss", StringComparison.OrdinalIgnoreCase);

    private static bool isSliderBreak(ReplayObjectJudgement judgement) =>
        judgement.Result is "LargeTickMiss" or "SmallTickMiss" or "SliderTailMiss";

    private static string displayName(LocalReplay run) => $"{run.Title} [{run.Difficulty}]";

    private static string formatTime(double milliseconds)
    {
        TimeSpan time = TimeSpan.FromMilliseconds(milliseconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}"
            : $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}";
    }
}
