using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;

// Frozen copy of the v0.3.1 coaching algorithms. Tests compare the optimised implementation against it;
// do not change it to follow the production code.
namespace AimMod.Desktop.Tests.Reference;


/// <summary>
/// Builds empirical coaching estimates from the player's own stored osu!standard plays.
/// Values are descriptive, not claims about the cause of a result.
/// </summary>
internal static class ReferenceCoachingPredictionEngine
{
    private const double sustainable_accuracy = 0.92;
    private const int sustainable_misses = 2;

    public static CoachingIntelligence Build(
        IReadOnlyList<LocalReplay> runs,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        Guid? selectedScoreId = null)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(analyses);

        LocalReplay[] history = runs.Where(isStandardRun)
                                    .OrderBy(run => run.PlayedAt)
                                    .TakeLast(CoachingLimits.MaximumRuns)
                                    .ToArray();
        LocalReplay? selected = selectedScoreId is { } scoreId
            ? history.FirstOrDefault(run => run.ScoreId == scoreId)
            : null;

        int validAccuracyCount = history.Count(run => validAccuracy(run.Accuracy));
        int exactAnalysisCount = history.Count(run => validAnalysis(analyses.GetValueOrDefault(run.ScoreId)));
        var quality = new CoachingHistoryQuality(
            history.Length,
            validAccuracyCount,
            history.Select(setupKey).Distinct().Count(),
            exactAnalysisCount);

        return new CoachingIntelligence(
            quality,
            buildTrend(history),
            buildDifficultyFit(history),
            selected is null ? null : Predict(history, selected),
            selected is null ? null : BuildSetupBenchmark(history, selected),
            buildSessionDrift(history),
            buildMechanics(history, analyses),
            buildPpPlan(history),
            buildRecommendations(history, analyses));
    }

    /// <summary>
    /// Estimates a target play using prior plays only. Similar star ratings, matching mods,
    /// matching beatmaps, and recent plays receive more weight.
    /// </summary>
    public static CoachingAccuracyPrediction? Predict(IReadOnlyList<LocalReplay> runs, LocalReplay target)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(target);

        WeightedRun[] neighbours = runs.Where(run => run.ScoreId != target.ScoreId
                                                     && isStandardRun(run)
                                                     && validAccuracy(run.Accuracy)
                                                     && run.PlayedAt < target.PlayedAt)
                                           .OrderByDescending(run => run.PlayedAt)
                                           // The maximum weight is 5. From age 320, even that falls below 0.04.
                                           // Avoid parsing mod configurations for neighbours we must discard.
                                           .Take(320)
                                           .Select((run, age) => new WeightedRun(run, similarityWeight(run, target, age)))
                                           .Where(item => item.Weight >= 0.04)
                                           .OrderByDescending(item => item.Weight)
                                           .Take(CoachingLimits.PredictionNeighbourLimit)
                                           .ToArray();
        if (neighbours.Length == 0)
            return null;

        double weight = neighbours.Sum(item => item.Weight);
        double expectedAccuracy = neighbours.Sum(item => item.Run.Accuracy * item.Weight) / weight;
        double expectedMisses = neighbours.Sum(item => Math.Max(0, item.Run.MissCount) * item.Weight) / weight;
        double variance = neighbours.Sum(item => item.Weight * square(item.Run.Accuracy - expectedAccuracy)) / weight;
        double effectiveSampleSize = square(weight) / neighbours.Sum(item => square(item.Weight));
        int sameSetupCount = neighbours.Count(item => setupKey(item.Run) == setupKey(target));
        CoachingConfidence confidence = predictionConfidence(effectiveSampleSize, sameSetupCount);

        // This is a weighted historical spread, not a calibrated confidence interval.
        double halfBand = Math.Max(confidence == CoachingConfidence.Insufficient ? 0.03 : 0.005, 1.28 * Math.Sqrt(variance));
        return new CoachingAccuracyPrediction(
            target.ScoreId,
            target.BeatmapId,
            finiteOrZero(target.StarRating),
            expectedAccuracy,
            Math.Clamp(expectedAccuracy - halfBand, 0, 1),
            Math.Clamp(expectedAccuracy + halfBand, 0, 1),
            expectedMisses,
            neighbours.Length,
            effectiveSampleSize,
            sameSetupCount,
            confidence,
            "Weighted personal history recorded before the target play. Nearby star ratings, matching mods, the same beatmap, and recent plays count more. The range is historical spread, not a guarantee.");
    }

    /// <summary>
    /// Compares one play with earlier results on the same beatmap and exact mod set.
    /// The percentile is empirical: it is the share of earlier matching plays whose
    /// accuracy was no higher than the selected result.
    /// </summary>
    public static CoachingSetupBenchmark BuildSetupBenchmark(IReadOnlyList<LocalReplay> runs, LocalReplay target)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(target);

        LocalReplay[] prior = runs.Where(run => run.ScoreId != target.ScoreId
                                                && run.PlayedAt < target.PlayedAt
                                                && setupKey(run) == setupKey(target)
                                                && validAccuracy(run.Accuracy))
                                      .OrderBy(run => run.PlayedAt)
                                      .ToArray();
        if (prior.Length == 0 || !validAccuracy(target.Accuracy))
        {
            return new CoachingSetupBenchmark(
                target.ScoreId,
                prior.Length,
                null,
                null,
                null,
                null,
                null,
                null,
                "No earlier play with this beatmap and mod set is available for a direct comparison.");
        }

        double priorMedian = median(prior.Select(run => run.Accuracy));
        double bestAccuracy = prior.Max(run => run.Accuracy);
        int bestMisses = prior.Min(run => Math.Max(0, run.MissCount));
        double accuracyChange = target.Accuracy - bestAccuracy;
        int missChange = Math.Max(0, target.MissCount) - bestMisses;
        double percentile = (double)prior.Count(run => run.Accuracy <= target.Accuracy) / prior.Length;
        string summary = accuracyChange switch
        {
            > 0.00005 => $"This play set a matching-setup accuracy best by {accuracyChange * 100:0.00} points across {prior.Length + 1:N0} attempts.",
            >= -0.00005 => $"This play matched the best accuracy from {prior.Length:N0} earlier matching attempts.",
            _ => $"This play finished {Math.Abs(accuracyChange) * 100:0.00} accuracy points below the matching-setup best of {formatAccuracy(bestAccuracy, 2)}.",
        };
        return new CoachingSetupBenchmark(
            target.ScoreId,
            prior.Length,
            priorMedian,
            bestAccuracy,
            accuracyChange,
            bestMisses,
            missChange,
            percentile,
            summary);
    }

    private static CoachingPerformanceTrend buildTrend(IReadOnlyList<LocalReplay> history)
    {
        LocalReplay[] valid = history.Where(run => validAccuracy(run.Accuracy)).ToArray();
        int window = Math.Min(20, valid.Length / 2);
        double? recentChange = window >= 2
            ? valid.TakeLast(window).Average(run => run.Accuracy)
              - valid.Skip(Math.Max(0, valid.Length - window * 2)).Take(window).Average(run => run.Accuracy)
            : null;

        LocalReplay[][] matchedSetups = valid.GroupBy(setupKey)
                                               .Select(group => group.OrderBy(run => run.PlayedAt).ToArray())
                                               .Where(group => group.Length >= 2)
                                               .ToArray();
        double[] pairedChanges = matchedSetups.SelectMany(group => group.Zip(group.Skip(1),
            (previous, current) => current.Accuracy - previous.Accuracy)).ToArray();
        int comparisonCount = pairedChanges.Length;
        double? matchedChange = pairedChanges.Length == 0
            ? null
            : median(pairedChanges);
        const double steady_threshold = 0.0025;
        int improvedCount = pairedChanges.Count(change => change > steady_threshold);
        int declinedCount = pairedChanges.Count(change => change < -steady_threshold);
        int steadyCount = pairedChanges.Length - improvedCount - declinedCount;
        CoachingConfidence confidence = matchedSetups.Length switch
        {
            >= 12 when comparisonCount >= 24 => CoachingConfidence.High,
            >= 6 when comparisonCount >= 10 => CoachingConfidence.Medium,
            >= 2 => CoachingConfidence.Low,
            _ => CoachingConfidence.Insufficient,
        };
        double? signal = matchedChange ?? recentChange;
        string direction = signal switch
        {
            >= 0.01 => "Improving on repeated setups",
            <= -0.01 => "Results have slipped on repeated setups",
            not null => "Broadly steady",
            null when valid.Length > 0 => "More repeated plays are needed",
            _ => "No trend yet",
        };

        return new CoachingPerformanceTrend(
            window,
            recentChange,
            matchedSetups.Length,
            comparisonCount,
            matchedChange,
            improvedCount,
            steadyCount,
            declinedCount,
            direction,
            confidence);
    }

    private static CoachingDifficultyFit buildDifficultyFit(IReadOnlyList<LocalReplay> history)
    {
        CoachingDifficultyBand[] bands = history.Where(run => validAccuracy(run.Accuracy) && validStars(run.StarRating))
                                                .GroupBy(run => Math.Floor(run.StarRating / CoachingLimits.DifficultyBandWidth))
                                                .Select(group =>
                                                {
                                                    LocalReplay[] values = group.ToArray();
                                                    double minimum = group.Key * CoachingLimits.DifficultyBandWidth;
                                                    double average = values.Average(run => run.Accuracy);
                                                    return new CoachingDifficultyBand(
                                                        minimum,
                                                        minimum + CoachingLimits.DifficultyBandWidth,
                                                        values.Length,
                                                        average,
                                                        standardDeviation(values.Select(run => run.Accuracy)),
                                                        (double)values.Count(run => run.MissCount == 0) / values.Length,
                                                        (double)values.Count(run => run.Accuracy >= sustainable_accuracy && run.MissCount <= sustainable_misses) / values.Length);
                                                })
                                                .OrderBy(band => band.MinimumStars)
                                                .ToArray();

        CoachingDifficultyBand? sustainable = bands.Where(band => band.RunCount >= CoachingLimits.MinimumRunsPerDifficultyBand
                                                                   && band.AverageAccuracy >= sustainable_accuracy
                                                                   && band.SustainableResultRate >= 0.6)
                                                         .MaxBy(band => band.MinimumStars);
        CoachingDifficultyBand? best = sustainable
                                      ?? bands.Where(band => band.RunCount >= CoachingLimits.MinimumRunsPerDifficultyBand)
                                              .MaxBy(band => band.AverageAccuracy - band.AccuracyStandardDeviation);
        CoachingConfidence confidence = best?.RunCount switch
        {
            >= 20 => CoachingConfidence.High,
            >= 8 => CoachingConfidence.Medium,
            >= CoachingLimits.MinimumRunsPerDifficultyBand => CoachingConfidence.Low,
            _ => CoachingConfidence.Insufficient,
        };
        string summary = best is null
            ? bands.Length == 0
                ? "No valid star-rated plays yet."
                : "Each star band needs at least three plays before AimMod calls it a fit."
            : sustainable is not null
                ? $"{best.MinimumStars:0.0}-{best.MaximumStars:0.0} stars is the highest measured band where at least 60% of plays reached 92% accuracy with no more than two misses."
                : $"{best.MinimumStars:0.0}-{best.MaximumStars:0.0} stars is the most consistent measured band so far, but it has not met the repeatable-result threshold.";
        return new CoachingDifficultyFit(best, bands, confidence, summary);
    }

    private static CoachingSessionDrift buildSessionDrift(IReadOnlyList<LocalReplay> history)
    {
        var sessions = new List<List<LocalReplay>>();
        foreach (LocalReplay run in history.Where(run => validAccuracy(run.Accuracy)))
        {
            if (sessions.Count == 0
                || run.PlayedAt - sessions[^1][^1].PlayedAt > TimeSpan.FromMinutes(CoachingLimits.SessionGapMinutes))
                sessions.Add(new List<LocalReplay>());
            sessions[^1].Add(run);
        }

        SessionChange[] measured = sessions.Where(session => session.Count >= CoachingLimits.MinimumPlaysPerSession)
                                           .Select(session => new SessionChange(
                                               session.Take(2).Average(run => run.Accuracy),
                                               session.TakeLast(2).Average(run => run.Accuracy),
                                               session.Take(2).Average(run => Math.Max(0, run.MissCount)),
                                               session.TakeLast(2).Average(run => Math.Max(0, run.MissCount))))
                                           .ToArray();
        if (measured.Length == 0)
        {
            return new CoachingSessionDrift(
                0,
                null,
                null,
                CoachingConfidence.Insufficient,
                "No session has four comparable stored plays yet.");
        }

        double accuracyChange = measured.Average(session => session.LateAccuracy - session.EarlyAccuracy);
        double missChange = measured.Average(session => session.LateMisses - session.EarlyMisses);
        CoachingConfidence confidence = measured.Length switch
        {
            >= 8 => CoachingConfidence.High,
            >= 4 => CoachingConfidence.Medium,
            >= 2 => CoachingConfidence.Low,
            _ => CoachingConfidence.Insufficient,
        };
        string summary = accuracyChange switch
        {
            <= -0.015 => $"Accuracy averaged {Math.Abs(accuracyChange) * 100:0.0} points lower at the end of measured sessions. Map order may explain part of the change.",
            >= 0.015 => $"Accuracy averaged {accuracyChange * 100:0.0} points higher at the end of measured sessions.",
            _ => "Accuracy stayed broadly steady from the opening to the end of measured sessions.",
        };
        return new CoachingSessionDrift(measured.Length, accuracyChange, missChange, confidence, summary);
    }

    private static CoachingMechanicsProfile buildMechanics(
        IReadOnlyList<LocalReplay> history,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses)
    {
        ReplayAnalysisResult[] exact = history.Select(run => analyses.GetValueOrDefault(run.ScoreId))
                                              .Where(validAnalysis)
                                              .Cast<ReplayAnalysisResult>()
                                              .ToArray();
        ReplayObjectJudgement[] judgements = exact.SelectMany(analysis => analysis.Judgements).ToArray();
        double[] offsets = judgements.Where(isTapTimingSample)
                                     .Select(judgement => judgement.TimeOffsetMs)
                                     .ToArray();
        double[] cursorDistances = judgements.Where(judgement => !isMiss(judgement)
                                                                  && judgement.ObjectPosition is not null
                                                                  && judgement.CursorPosition is not null)
                                               .Select(judgement => distance(judgement.ObjectPosition!, judgement.CursorPosition!))
                                               .Where(double.IsFinite)
                                               .ToArray();
        ReplayObjectJudgement[] misses = judgements.Where(isMiss).ToArray();
        IReadOnlyDictionary<ReplayMissReason, int> missReasonCounts = misses.Where(miss => miss.MissAnalysis is not null)
                                                                               .Select(miss => miss.MissAnalysis!.Reason)
                                                                               .GroupBy(reason => reason)
                                                                               .ToDictionary(group => group.Key, group => group.Count());
        ReplayMissReason? dominantMissReason = missReasonCounts.Where(pair => pair.Key != ReplayMissReason.Unknown)
                                                               .OrderByDescending(pair => pair.Value)
                                                               .ThenBy(pair => pair.Key)
                                                               .Select(pair => (ReplayMissReason?)pair.Key)
                                                               .FirstOrDefault();
        CoachingMapSegment[] segments = buildMapSegments(exact);
        string? weakestSegment = segments.Where(segment => segment.PrimaryJudgementCount > 0)
                                         .OrderByDescending(segment => segment.MissRate)
                                         .ThenByDescending(segment => segment.MissCount)
                                         .FirstOrDefault() is { } weakest
            && segments.Sum(segment => segment.MissCount) >= 3
                ? weakest.Label.ToLowerInvariant()
                : null;
        return new CoachingMechanicsProfile(
            exact.Length,
            judgements.Length,
            offsets.Length,
            offsets.Length == 0 ? null : offsets.Average(),
            offsets.Length == 0 ? null : standardDeviation(offsets),
            cursorDistances.Length,
            cursorDistances.Length == 0 ? null : cursorDistances.Average(),
            misses.Length,
            weakestSegment,
            offsets.Length == 0 ? null : median(offsets),
            offsets.Length == 0 ? null : percentile(offsets.Select(Math.Abs), 0.9),
            cursorDistances.Length == 0 ? null : median(cursorDistances),
            cursorDistances.Length == 0 ? null : percentile(cursorDistances, 0.9),
            segments,
            missReasonCounts,
            dominantMissReason);
    }

    private static IReadOnlyList<CoachingRecommendation> buildRecommendations(
        IReadOnlyList<LocalReplay> history,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses)
    {
        if (history.Count == 0)
            return Array.Empty<CoachingRecommendation>();

        GlobalCoachingProfile globalProfile = ReferenceGlobalCoachingProfileBuilder.Build(history, analyses);
        var candidates = new List<RecommendationCandidate>();
        LocalReplay[][] recentSetups = history.Where(run => validAccuracy(run.Accuracy))
                                               .GroupBy(setupKey)
                                               .Select(group => group.OrderBy(run => run.PlayedAt).ToArray())
                                               .OrderByDescending(setup => setup[^1].PlayedAt)
                                               .Take(CoachingLimits.MaximumRecommendationCandidates)
                                               .ToArray();
        foreach (LocalReplay[] setup in recentSetups)
        {
            LocalReplay latest = setup[^1];
            LocalReplay forecastTarget = latest with
            {
                ScoreId = Guid.Empty,
                PlayedAt = history[^1].PlayedAt.AddTicks(1),
            };
            CoachingAccuracyPrediction? prediction = Predict(history, forecastTarget);
            CoachingSetupBenchmark benchmark = BuildSetupBenchmark(history, latest);
            string intent;
            string reason;
            double priority;
            CoachingConfidence recommendationConfidence = prediction?.Confidence ?? CoachingConfidence.Insufficient;
            int recommendationSampleCount = prediction?.SampleCount ?? 0;

            var setupMissReason = setup.SelectMany(run => analyses.GetValueOrDefault(run.ScoreId)?.Judgements
                                                                  .Where(isMiss)
                                                                  .Where(judgement => judgement.MissAnalysis is { Reason: not ReplayMissReason.Unknown })
                                                                  .Select(judgement => judgement.MissAnalysis!.Reason)
                                                      ?? Enumerable.Empty<ReplayMissReason>())
                                       .GroupBy(value => value)
                                       .Select(group => new { Reason = group.Key, Count = group.Count() })
                                       .OrderByDescending(group => group.Count)
                                       .FirstOrDefault();
            GlobalMissReasonShare? recurringReason = setupMissReason is null
                ? null
                : globalProfile.MissReasons.FirstOrDefault(item => item.Reason == setupMissReason.Reason
                                                                    && item.MapCount >= 2
                                                                    && item.RunCount >= 2
                                                                    && item.Confidence >= CoachingConfidence.Low);

            if (recurringReason is not null)
            {
                intent = mechanicsIntent(recurringReason.Reason);
                reason = mechanicsRecommendationDetail(recurringReason, setupMissReason!.Count);
                priority = 125
                           + recurringReason.MapCount * 4
                           + recurringReason.RunCount
                           + recurringReason.Share * 10
                           + Math.Min(5, setupMissReason.Count);
                recommendationConfidence = recurringReason.Confidence;
                recommendationSampleCount = recurringReason.Count;
            }
            else if (benchmark.AccuracyChangeFromBest is > 0.0025)
            {
                intent = "Confirm improvement";
                reason = $"The latest play beat the earlier matching-setup best by {benchmark.AccuracyChangeFromBest.Value * 100:0.00} accuracy points. Repeat it once to test whether that result holds.";
                priority = 110 + benchmark.AccuracyChangeFromBest.Value * 100 + latest.StarRating;
            }
            else if (benchmark.AccuracyChangeFromBest is < -0.01 && benchmark.BestPriorAccuracy is { } best)
            {
                intent = "Recover prior level";
                reason = $"The latest play was {Math.Abs(benchmark.AccuracyChangeFromBest.Value) * 100:0.00} accuracy points below your {formatAccuracy(best)} matching-setup best. Repeat the same map and mods for a direct recovery check.";
                priority = 100 + Math.Abs(benchmark.AccuracyChangeFromBest.Value) * 100 + latest.StarRating;
            }
            else if (latest.MissCount > 0
                     && benchmark.BestPriorMissCount is { } priorMisses
                     && latest.MissCount > priorMisses)
            {
                intent = "Clean up misses";
                reason = $"The latest play had {latest.MissCount:N0} misses; your earlier matching-setup best had {priorMisses:N0}. Aim for at most {priorMisses:N0} on the repeat.";
                priority = 90 + Math.Min(10, latest.MissCount - priorMisses) + latest.StarRating;
            }
            else if (prediction is { ExpectedAccuracy: >= 0.93 and <= 0.985 })
            {
                intent = "Build consistency";
                reason = $"Similar earlier plays estimate about {formatAccuracy(prediction.ExpectedAccuracy)}. Repeat this setup and compare the result with that personal-history estimate.";
                priority = 80 + latest.StarRating - Math.Abs(prediction.ExpectedAccuracy - 0.95) * 10;
            }
            else if (prediction is { ExpectedAccuracy: >= 0.87 and < 0.93 })
            {
                intent = "Controlled stretch";
                reason = $"Similar earlier plays estimate about {formatAccuracy(prediction.ExpectedAccuracy)}. This setup sits near the edge of your measured range.";
                priority = 60 + latest.StarRating;
            }
            else if (latest.MissCount > 0)
            {
                intent = "Clean up misses";
                reason = $"The latest stored play had {latest.MissCount:N0} misses. A repeat gives a direct comparison on the same map and mods.";
                priority = 40 + Math.Min(10, latest.MissCount);
            }
            else
            {
                intent = "Check repeatability";
                reason = "Repeat the same map and mods to establish whether the result is repeatable.";
                priority = 20 + latest.StarRating;
            }

            candidates.Add(new RecommendationCandidate(
                latest,
                prediction,
                intent,
                reason,
                priority,
                recommendationConfidence,
                recommendationSampleCount));
        }

        return candidates.OrderByDescending(candidate => candidate.Priority)
                         .ThenByDescending(candidate => candidate.Run.PlayedAt)
                         .Take(CoachingLimits.RecommendationLimit)
                         .Select((candidate, index) => new CoachingRecommendation(
                             index + 1,
                             candidate.Run.BeatmapId,
                             candidate.Run.ScoreId,
                             candidate.Run.Title,
                             candidate.Run.Difficulty,
                             candidate.Intent,
                             candidate.Reason,
                             candidate.Prediction?.ExpectedAccuracy,
                             candidate.Confidence,
                             candidate.SampleCount))
                         .ToArray();
    }

    private static CoachingPpPlan buildPpPlan(IReadOnlyList<LocalReplay> history)
    {
        LocalReplay[] ppRuns = history.Where(run => validAccuracy(run.Accuracy)
                                                    && validStars(run.StarRating)
                                                    && validPp(run.PerformancePoints))
                                      .OrderBy(run => run.PlayedAt)
                                      .ToArray();
        if (ppRuns.Length == 0)
        {
            return new CoachingPpPlan(
                0,
                null,
                null,
                null,
                CoachingConfidence.Insufficient,
                "No local osu!standard plays with PP values are available yet.",
                Array.Empty<CoachingPpOpportunity>());
        }

        LocalReplay[][] allSetups = ppRuns.GroupBy(setupKey)
                                                .Select(group => group.OrderBy(run => run.PlayedAt).ToArray())
                                                .ToArray();
        int poolSlice = Math.Max(1, CoachingLimits.MaximumPpCandidateSetups / 3);
        LocalReplay[] distinctSetupBestRuns = allSetups.Select(setup => setup.MaxBy(run => run.PerformancePoints)!)
                                                       .ToArray();
        LocalReplay[][] candidateSetups = allSetups.OrderByDescending(setup => setup[^1].PlayedAt)
                                                    .Take(poolSlice)
                                                    .Concat(allSetups.OrderByDescending(setup => setup.Max(run => run.PerformancePoints!.Value)
                                                                                                      - setup[^1].PerformancePoints!.Value)
                                                                     .Take(poolSlice))
                                                    .Concat(allSetups.OrderByDescending(setup => setup.Max(run => run.PerformancePoints!.Value))
                                                                     .Take(poolSlice))
                                                    .DistinctBy(setup => setupKey(setup[^1]))
                                                    .Take(CoachingLimits.MaximumPpCandidateSetups)
                                                    .ToArray();

        var candidates = new List<PpCandidate>();
        foreach (LocalReplay[] setup in candidateSetups)
        {
            LocalReplay latest = setup[^1];
            double currentPp = latest.PerformancePoints!.Value;
            LocalReplay[] priorSameSetup = setup.Take(setup.Length - 1).ToArray();
            LocalReplay[] similarStarRuns = distinctSetupBestRuns.Where(run => Math.Abs(run.StarRating - latest.StarRating) <= 0.75
                                                                              && modSimilarity(run.Mods, latest.Mods) >= 0.5)
                                                                .ToArray();
            double setupBestPp = setup.Max(run => run.PerformancePoints!.Value);
            double similarCeiling = similarStarRuns.Length == 0
                ? currentPp
                : percentile(similarStarRuns.Select(run => run.PerformancePoints!.Value), similarStarRuns.Length >= 12 ? 0.8 : 0.7);
            double observedCeiling = Math.Max(setupBestPp, similarCeiling);
            double recoverableGain = Math.Max(0, setupBestPp - currentPp);
            double stretchGain = Math.Max(0, observedCeiling - currentPp) * 0.45;
            double projectedPp = Math.Min(observedCeiling, currentPp + Math.Max(recoverableGain * 0.85, stretchGain));
            double realisticGain = Math.Max(0, projectedPp - currentPp);
            if (realisticGain < 1)
                continue;
            double profilePpGain = CoachingPpWeighting.CalculateProfileGain(ppRuns, latest.BeatmapId, projectedPp);
            if (profilePpGain < 0.05)
                continue;

            CoachingAccuracyPrediction? prediction = Predict(history, latest with
            {
                ScoreId = Guid.Empty,
                PlayedAt = history[^1].PlayedAt.AddTicks(1),
            });
            double? targetAccuracy = bestNullable(
                latest.Accuracy,
                priorSameSetup.Length == 0 ? null : priorSameSetup.Max(run => run.Accuracy),
                prediction?.UpperAccuracy);
            int? targetMissCount = priorSameSetup.Length == 0
                ? latest.MissCount
                : Math.Min(latest.MissCount, priorSameSetup.Min(run => Math.Max(0, run.MissCount)));
            CoachingConfidence opportunityConfidence = ppOpportunityConfidence(priorSameSetup.Length, similarStarRuns.Length, prediction?.Confidence);
            string reason = recoverableGain >= stretchGain
                ? $"A previous matching setup reached {setupBestPp:0.0}pp. The current local play is {currentPp:0.0}pp, so the model treats most of that gap as recoverable."
                : $"Nearby-star local plays with similar mods put this setup in a {observedCeiling:0.0}pp observed window. The projection uses only part of that gap.";
            candidates.Add(new PpCandidate(latest, currentPp, projectedPp, realisticGain, profilePpGain, targetAccuracy, targetMissCount, opportunityConfidence, priorSameSetup.Length, similarStarRuns.Length, reason));
        }

        CoachingPpOpportunity[] opportunities = candidates.GroupBy(candidate => candidate.Run.BeatmapId)
                                                          .Select(group => group.OrderByDescending(candidate => candidate.ProfilePpGain)
                                                                                .ThenByDescending(candidate => candidate.RealisticGain)
                                                                                .First())
                                                          .OrderByDescending(candidate => candidate.ProfilePpGain)
                                                          .ThenByDescending(candidate => candidate.RealisticGain)
                                                          .ThenByDescending(candidate => candidate.Run.StarRating)
                                                          .ThenByDescending(candidate => candidate.Run.PlayedAt)
                                                          .Take(CoachingLimits.RecommendationLimit)
                                                          .Select((candidate, index) => new CoachingPpOpportunity(
                                                              index + 1,
                                                              candidate.Run.BeatmapId,
                                                              candidate.Run.ScoreId,
                                                              candidate.Run.Title,
                                                              candidate.Run.Difficulty,
                                                              candidate.Run.StarRating,
                                                              candidate.CurrentPp,
                                                              candidate.ProjectedPp,
                                                              candidate.RealisticGain,
                                                              candidate.TargetAccuracy,
                                                              candidate.TargetMissCount,
                                                              candidate.Confidence,
                                                              candidate.SameSetupSampleCount,
                                                              candidate.SimilarStarSampleCount,
                                                              candidate.Reason,
                                                              candidate.ProfilePpGain))
                                                          .ToArray();
        double? bestGain = opportunities.FirstOrDefault()?.RealisticGain;
        double? topThree = opportunities.Length == 0 ? null : opportunities.Take(3).Sum(opportunity => opportunity.RealisticGain);
        double? bestProfileGain = opportunities.FirstOrDefault()?.ProfilePpGain;
        double? topThreeProfileGain = opportunities.Length == 0
            ? null
            : CoachingPpWeighting.CalculateCombinedProfileGain(
                ppRuns,
                opportunities.Take(3).Select(opportunity => (opportunity.BeatmapId, opportunity.ProjectedPp)));
        CoachingConfidence confidence = opportunities.Length == 0
            ? CoachingConfidence.Insufficient
            : opportunities.Max(opportunity => opportunity.Confidence);
        string summary = opportunities.Length == 0
            ? $"{ppRuns.Length:N0} PP-valued plays are available, but none show a clear recoverable PP gap yet."
            : $"Best target adds about +{bestProfileGain:0.0} profile pp; completing the top three targets adds about +{topThreeProfileGain:0.0} profile pp from weighted scores.";
        return new CoachingPpPlan(
            ppRuns.Length,
            ppRuns.Max(run => run.PerformancePoints!.Value),
            bestGain,
            topThree,
            confidence,
            summary,
            opportunities,
            bestProfileGain,
            topThreeProfileGain);
    }

    private static CoachingMapSegment[] buildMapSegments(IReadOnlyList<ReplayAnalysisResult> analyses)
    {
        const int segment_count = 3;
        int[] judgementCounts = new int[segment_count];
        int[] missCounts = new int[3];
        int[] sliderBreakCounts = new int[segment_count];
        foreach (ReplayAnalysisResult analysis in analyses)
        {
            double duration = analysis.Judgements.Count == 0 ? 0 : analysis.Judgements.Max(judgement => judgement.EndTimeMs);
            if (!double.IsFinite(duration) || duration <= 0)
                continue;

            foreach (ReplayObjectJudgement judgement in analysis.Judgements)
            {
                if (!double.IsFinite(judgement.StartTimeMs))
                    continue;

                int segment = Math.Clamp((int)(judgement.StartTimeMs / duration * segment_count), 0, segment_count - 1);
                if (judgement.NestedPath is null)
                {
                    judgementCounts[segment]++;
                    if (isMiss(judgement))
                        missCounts[segment]++;
                }

                if (isSliderBreak(judgement))
                    sliderBreakCounts[segment]++;
            }
        }

        string[] keys = { "opening", "middle", "closing" };
        return Enumerable.Range(0, segment_count).Select(index => new CoachingMapSegment(
            keys[index],
            $"{keys[index]} third",
            judgementCounts[index],
            missCounts[index],
            judgementCounts[index] == 0 ? null : (double)missCounts[index] / judgementCounts[index],
            sliderBreakCounts[index])).ToArray();
    }

    private static double median(IEnumerable<double> values)
    {
        double[] ordered = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
            return double.NaN;
        return ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;
    }

    private static double percentile(IEnumerable<double> values, double quantile)
    {
        double[] ordered = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
            return double.NaN;

        int nearestRank = Math.Clamp((int)Math.Ceiling(Math.Clamp(quantile, 0, 1) * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[nearestRank];
    }

    private static bool isSliderBreak(ReplayObjectJudgement judgement) =>
        judgement.Result switch
        {
            "LargeTickMiss" or "SmallTickMiss" or "SliderTailMiss" => true,
            _ => false,
        };

    private static double similarityWeight(LocalReplay run, LocalReplay target, int age)
    {
        double starWeight = validStars(run.StarRating) && validStars(target.StarRating)
            ? Math.Exp(-Math.Abs(run.StarRating - target.StarRating) / 0.75)
            : 0.5;
        double modWeight = 0.75 + 1.25 * (ScoreMods.Configuration(run) == ScoreMods.Configuration(target) ? 1 : 0);
        double beatmapWeight = run.BeatmapId != Guid.Empty && run.BeatmapId == target.BeatmapId ? 2.5 : 1;
        double recencyWeight = Math.Pow(0.985, age);
        return starWeight * modWeight * beatmapWeight * recencyWeight;
    }

    private static double modSimilarity(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        HashSet<string> a = (left ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> b = (right ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (a.Count == 0 && b.Count == 0)
            return 1;
        int union = a.Union(b, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 1 : (double)a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count() / union;
    }

    private static CoachingConfidence predictionConfidence(double effectiveSampleSize, int sameSetupCount) =>
        effectiveSampleSize switch
        {
            >= 20 when sameSetupCount >= 3 => CoachingConfidence.High,
            >= 8 when sameSetupCount >= 1 => CoachingConfidence.Medium,
            >= 3 => CoachingConfidence.Low,
            _ => CoachingConfidence.Insufficient,
        };

    private static string setupKey(LocalReplay run) => ScoreMods.SetupKey(run);

    private static bool isStandardRun(LocalReplay run) =>
        string.Equals(run.RulesetShortName, "osu", StringComparison.OrdinalIgnoreCase) && ScoreMods.IsManualPlay(run);

    private static bool validAccuracy(double value) => double.IsFinite(value) && value is >= 0 and <= 1;

    private static bool validStars(double value) => double.IsFinite(value) && value > 0;

    private static bool validPp(double? value) => value is { } pp && double.IsFinite(pp) && pp > 0;

    private static bool validAnalysis(ReplayAnalysisResult? analysis) =>
        analysis is { Judgements: not null, Summary: not null };

    private static bool isMiss(ReplayObjectJudgement judgement) =>
        string.Equals(judgement.Result, "Miss", StringComparison.OrdinalIgnoreCase);

    private static bool isTapTimingSample(ReplayObjectJudgement judgement) =>
        !isMiss(judgement)
        && double.IsFinite(judgement.TimeOffsetMs)
        && string.Equals(judgement.MaximumResult, "Great", StringComparison.OrdinalIgnoreCase)
        && judgement.ObjectType.EndsWith("Circle", StringComparison.OrdinalIgnoreCase);

    private static double distance(ReplayPoint left, ReplayPoint right) =>
        Math.Sqrt(square(left.X - right.X) + square(left.Y - right.Y));

    private static double standardDeviation(IEnumerable<double> values)
    {
        double[] samples = values.Where(double.IsFinite).ToArray();
        if (samples.Length == 0)
            return 0;
        double mean = samples.Average();
        return Math.Sqrt(samples.Average(value => square(value - mean)));
    }

    private static double finiteOrZero(double value) => double.IsFinite(value) ? value : 0;

    private static double? bestNullable(params double?[] values)
    {
        double[] finite = values.Where(value => value is { } number && double.IsFinite(number))
                                .Select(value => value!.Value)
                                .ToArray();
        return finite.Length == 0 ? null : finite.Max();
    }

    private static CoachingConfidence ppOpportunityConfidence(int sameSetupSampleCount, int similarStarSampleCount, CoachingConfidence? predictionConfidence)
    {
        if (sameSetupSampleCount >= 3 && similarStarSampleCount >= 12 && predictionConfidence is CoachingConfidence.Medium or CoachingConfidence.High)
            return CoachingConfidence.High;
        if (sameSetupSampleCount >= 1 && similarStarSampleCount >= 6)
            return CoachingConfidence.Medium;
        if (similarStarSampleCount >= 3)
            return CoachingConfidence.Low;
        return CoachingConfidence.Insufficient;
    }

    private static string formatAccuracy(double accuracy, int decimals = 1) =>
        (accuracy * 100).ToString(decimals == 2 ? "0.00" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";

    private static string mechanicsIntent(ReplayMissReason reason) => reason switch
    {
        ReplayMissReason.EarlyClick => "Train tap timing",
        ReplayMissReason.LateClick => "Train tap timing",
        ReplayMissReason.Undershoot => "Train aim control",
        ReplayMissReason.Overshoot => "Train aim control",
        ReplayMissReason.OnTargetNoClick => "Train aim-tap coordination",
        ReplayMissReason.AimDeviation => "Train aim precision",
        _ => "Review replay evidence",
    };

    private static string mechanicsRecommendationDetail(GlobalMissReasonShare recurring, int setupEvidenceCount)
    {
        string weakness = ReplayMissInsightPresenter.Label(recurring.Reason);
        string action = recurring.Reason switch
        {
            ReplayMissReason.EarlyClick => "Repeat at a readable rate and delay the press until cursor arrival.",
            ReplayMissReason.LateClick => "Repeat the pattern and commit as the cursor enters the target.",
            ReplayMissReason.Undershoot => "Repeat the longer movements and finish the full travel before tapping.",
            ReplayMissReason.Overshoot => "Repeat at a controlled rate and brake the cursor at the target centre.",
            ReplayMissReason.OnTargetNoClick => "Repeat while keeping aim and tapping rhythm coupled through each target.",
            ReplayMissReason.AimDeviation => "Repeat the affected spacing below your limit and prioritise a stable approach line.",
            _ => "Review the matching replay moments before repeating this setup.",
        };
        return $"{weakness} recur across {recurring.MapCount:N0} maps and {recurring.RunCount:N0} analysed plays; {setupEvidenceCount:N0} were classified on this setup. {action}";
    }

    private static double square(double value) => value * value;

    private sealed record WeightedRun(LocalReplay Run, double Weight);

    private sealed record SessionChange(double EarlyAccuracy, double LateAccuracy, double EarlyMisses, double LateMisses);

    private sealed record RecommendationCandidate(
        LocalReplay Run,
        CoachingAccuracyPrediction? Prediction,
        string Intent,
        string Reason,
        double Priority,
        CoachingConfidence Confidence,
        int SampleCount);

    private sealed record PpCandidate(
        LocalReplay Run,
        double CurrentPp,
        double ProjectedPp,
        double RealisticGain,
        double ProfilePpGain,
        double? TargetAccuracy,
        int? TargetMissCount,
        CoachingConfidence Confidence,
        int SameSetupSampleCount,
        int SimilarStarSampleCount,
        string Reason);
}
internal static class ReferenceGlobalCoachingProfileBuilder
{
    private const double centred_timing_ms = CoachingLimits.CentredTimingThresholdMilliseconds;

    public static GlobalCoachingProfile Build(
        IReadOnlyList<LocalReplay> runs,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(analyses);

        LocalReplay[] history = runs.Where(run => string.Equals(run.RulesetShortName, "osu", StringComparison.OrdinalIgnoreCase))
                                    .ToArray();
        if (history.Length == 0)
            return GlobalCoachingProfile.Empty;

        AnalysedRun[] exact = history.Select(run => new AnalysedRun(run, analyses.GetValueOrDefault(run.ScoreId)))
                                     .Where(item => valid(item.Analysis))
                                     .Select(item => item with { Analysis = item.Analysis! })
                                     .ToArray();
        ReplayObjectJudgement[] judgements = exact.SelectMany(item => item.Analysis!.Judgements).ToArray();
        ClassifiedMiss[] classifiedMisses = exact.SelectMany(item => item.Analysis!.Judgements
                                                                         .Where(isMiss)
                                                                         .Where(judgement => judgement.MissAnalysis is { Reason: not ReplayMissReason.Unknown })
                                                                         .Select(judgement => new ClassifiedMiss(item, judgement)))
                                                  .ToArray();
        ReplayObjectJudgement[] misses = judgements.Where(isMiss).ToArray();
        double[] timing = judgements.Where(isTimingSample).Select(item => item.TimeOffsetMs).ToArray();
        double[] cursorDistances = judgements.Where(item => !isMiss(item)
                                                             && item.ObjectPosition is not null
                                                             && item.CursorPosition is not null)
                                              .Select(item => distance(item.ObjectPosition!, item.CursorPosition!))
                                              .Where(double.IsFinite)
                                              .ToArray();

        int distinctMaps = history.Select(mapKey).Distinct(StringComparer.Ordinal).Count();
        int analysedMaps = exact.Select(item => mapKey(item.Run)).Distinct(StringComparer.Ordinal).Count();
        int replayAvailableRuns = Math.Max(history.Count(run => run.HasReplayFile), exact.Length);
        CoachingConfidence confidence = confidenceFor(exact.Length, analysedMaps, replayAvailableRuns);
        CoachingConfidence classificationCoverageConfidence = confidenceForCoverage(misses.Length == 0
            ? 0
            : (double)classifiedMisses.Length / misses.Length);
        var coverage = new GlobalCoachingCoverage(
            history.Length,
            replayAvailableRuns,
            exact.Length,
            distinctMaps,
            analysedMaps,
            judgements.Length,
            confidence,
            misses.Length,
            classifiedMisses.Length);

        GlobalMissReasonShare[] reasons = classifiedMisses.GroupBy(item => item.Judgement.MissAnalysis!.Reason)
                                                .Select(group =>
                                                {
                                                    ClassifiedMiss[] evidence = group.ToArray();
                                                    int runCount = evidence.Select(item => item.Run.Run.ScoreId).Distinct().Count();
                                                    int mapCount = evidence.Select(item => mapKey(item.Run.Run)).Distinct(StringComparer.Ordinal).Count();
                                                    double averageClassifierConfidence = evidence.Average(item => classifierConfidence(item.Judgement));
                                                    return new GlobalMissReasonShare(
                                                        group.Key,
                                                        evidence.Length,
                                                        classifiedMisses.Length == 0 ? 0 : (double)evidence.Length / classifiedMisses.Length,
                                                        runCount,
                                                        mapCount,
                                                        averageClassifierConfidence,
                                                        capConfidence(
                                                            confidenceForEvidence(evidence.Length, runCount, mapCount, averageClassifierConfidence),
                                                            confidence,
                                                            classificationCoverageConfidence));
                                                })
                                                .OrderByDescending(reasonEvidenceScore)
                                                .ThenByDescending(item => item.Count)
                                                .ThenBy(item => item.Reason)
                                                .ToArray();

        (string timingTendency, string timingDetail) = timingSummary(timing);
        (string aimTendency, string aimDetail) = aimSummary(cursorDistances, reasons);
        GlobalSkillAreaEvidence[] skillAreas = buildSkillAreas(
            classifiedMisses,
            analysedMaps,
            confidence,
            classificationCoverageConfidence);
        GlobalRecurringWeakness[] recurring = recurringWeaknesses(exact, reasons);
        GlobalCoachingPriority[] priorities = buildPriorities(coverage, reasons, recurring, timing);

        return new GlobalCoachingProfile(
            coverage,
            reasons,
            timingTendency,
            timingDetail,
            aimTendency,
            aimDetail,
            recurring,
            priorities,
            skillAreas);
    }

    private static GlobalRecurringWeakness[] recurringWeaknesses(
        IReadOnlyList<AnalysedRun> exact,
        IReadOnlyList<GlobalMissReasonShare> reasons)
    {
        var weaknesses = new List<GlobalRecurringWeakness>();
        foreach (GlobalMissReasonShare reason in reasons.Where(item => item.Count >= 2 && (item.MapCount >= 2 || item.RunCount >= 2)))
        {
            string label = ReplayMissInsightPresenter.Label(reason.Reason);
            weaknesses.Add(new GlobalRecurringWeakness(
                $"reason:{reason.Reason}",
                label,
                $"{reason.Count:N0} classified misses across {reason.RunCount:N0} analysed plays and {reason.MapCount:N0} maps.",
                reason.Count,
                reason.RunCount,
                reason.MapCount,
                reason.Confidence));
        }

        foreach (IGrouping<string, AnalysedRun> map in exact.GroupBy(item => mapKey(item.Run), StringComparer.Ordinal)
                                                            .Where(group => group.Count() >= 2))
        {
            var mapReasons = map.SelectMany(item => item.Analysis!.Judgements
                                                        .Where(isMiss)
                                                        .Where(judgement => judgement.MissAnalysis is { Reason: not ReplayMissReason.Unknown })
                                                        .Select(judgement => new { item.Run.ScoreId, Reason = judgement.MissAnalysis!.Reason }))
                                .ToArray();
            if (mapReasons.Length < 2)
                continue;

            var dominant = mapReasons.GroupBy(item => item.Reason)
                                     .Select(group => new
                                     {
                                         Reason = group.Key,
                                         Count = group.Count(),
                                         RunCount = group.Select(item => item.ScoreId).Distinct().Count(),
                                     })
                                     .Where(group => group.RunCount >= 2)
                                     .OrderByDescending(group => group.RunCount)
                                     .ThenByDescending(group => group.Count)
                                     .FirstOrDefault();
            if (dominant is null)
                continue;

            AnalysedRun latest = map.OrderByDescending(item => item.Run.PlayedAt).First();
            CoachingConfidence weaknessConfidence = capConfidence(
                confidenceForEvidence(dominant.Count, dominant.RunCount, 1, 1),
                confidenceFor(map.Count(), 1, map.Count()));
            weaknesses.Add(new GlobalRecurringWeakness(
                $"map:{map.Key}:{dominant.Reason}",
                $"Repeated on {latest.Run.Title}",
                $"{ReplayMissInsightPresenter.Label(dominant.Reason)} appeared {dominant.Count:N0} times across {dominant.RunCount:N0} analysed attempts on [{latest.Run.Difficulty}].",
                dominant.Count,
                dominant.RunCount,
                1,
                weaknessConfidence));
        }

        return weaknesses.OrderByDescending(item => item.MapCount)
                         .ThenByDescending(item => item.Confidence)
                         .ThenByDescending(item => item.EvidenceCount)
                         .Take(5)
                         .ToArray();
    }

    private static GlobalSkillAreaEvidence[] buildSkillAreas(
        IReadOnlyList<ClassifiedMiss> classifiedMisses,
        int analysedMapCount,
        CoachingConfidence profileConfidence,
        CoachingConfidence classificationCoverageConfidence)
    {
        return classifiedMisses.GroupBy(item => skillArea(item.Judgement.MissAnalysis!.Reason))
                               .Select(group =>
                               {
                                   ClassifiedMiss[] evidence = group.ToArray();
                                   int count = evidence.Length;
                                   int runCount = evidence.Select(item => item.Run.Run.ScoreId).Distinct().Count();
                                   int mapCount = evidence.Select(item => mapKey(item.Run.Run)).Distinct(StringComparer.Ordinal).Count();
                                   double share = classifiedMisses.Count == 0 ? 0 : (double)count / classifiedMisses.Count;
                                   double mapCoverage = analysedMapCount == 0 ? 0 : (double)mapCount / analysedMapCount;
                                   double averageClassifierConfidence = evidence.Average(item => classifierConfidence(item.Judgement));
                                   CoachingConfidence confidence = capConfidence(
                                       confidenceForEvidence(count, runCount, mapCount, averageClassifierConfidence),
                                       profileConfidence,
                                       classificationCoverageConfidence);
                                   CoachingSkillArea area = group.Key;
                                   return new GlobalSkillAreaEvidence(
                                       area,
                                       skillAreaLabel(area),
                                       count,
                                       runCount,
                                       mapCount,
                                       share,
                                       mapCoverage,
                                       confidence,
                                       $"{count:N0} classified misses across {runCount:N0} plays and {mapCount:N0} maps ({share * 100:0}% of classified misses).");
                               })
                               .OrderByDescending(item => item.MapCount)
                               .ThenByDescending(item => item.ShareOfClassifiedMisses)
                               .ThenByDescending(item => item.EvidenceCount)
                               .ToArray();
    }

    private static GlobalCoachingPriority[] buildPriorities(
        GlobalCoachingCoverage coverage,
        IReadOnlyList<GlobalMissReasonShare> reasons,
        IReadOnlyList<GlobalRecurringWeakness> recurring,
        IReadOnlyList<double> timing)
    {
        var priorities = new List<GlobalCoachingPriority>();
        GlobalMissReasonShare? dominant = reasons.OrderByDescending(reasonEvidenceScore).FirstOrDefault();
        if (dominant is not null)
        {
            priorities.Add(new GlobalCoachingPriority(
                practiceTitle(dominant.Reason),
                practiceDetail(dominant),
                $"{dominant.Share * 100:0}% of misses",
                dominant.Confidence));
        }

        if (timing.Count >= 10)
        {
            double median = percentile(timing, 0.5);
            double spread = standardDeviation(timing);
            if (Math.Abs(median) > centred_timing_ms || spread >= 25)
            {
                priorities.Add(new GlobalCoachingPriority(
                    Math.Abs(median) > centred_timing_ms ? "Correct timing bias" : "Stabilise tapping",
                    Math.Abs(median) > centred_timing_ms
                        ? $"Median hit timing is {formatSigned(median)} across {timing.Count:N0} exact taps. Use lower-density patterns and centre the hit window before adding speed."
                        : $"Timing spread is {spread:0.0} ms across {timing.Count:N0} exact taps. Build repeatability on comfortable BPM before increasing difficulty.",
                    Math.Abs(median) > centred_timing_ms ? formatSigned(median) : $"{spread:0.0} ms spread",
                    capConfidence(confidenceForSamples(timing.Count), coverage.Confidence)));
            }
        }

        GlobalRecurringWeakness? repeatedMap = recurring.FirstOrDefault(item => item.MapCount == 1);
        if (repeatedMap is not null)
        {
            priorities.Add(new GlobalCoachingPriority(
                "Review a repeated failure",
                repeatedMap.Detail,
                $"{repeatedMap.RunCount:N0} attempts",
                repeatedMap.Confidence));
        }

        if (priorities.Count == 0)
        {
            priorities.Add(new GlobalCoachingPriority(
                "Build replay evidence",
                coverage.ReplayAvailableRunCount == 0
                    ? "No saved local replays are available for object-level coaching. Submitted scores still contribute to performance trends."
                    : $"{coverage.AnalysedRunCount:N0} of {coverage.ReplayAvailableRunCount:N0} replay-backed plays are analysed. More maps will make mechanics priorities more reliable.",
                $"{coverage.ReplayCoverage * 100:0}% covered",
                coverage.Confidence));
        }

        return priorities.OrderByDescending(priority => priority.Confidence)
                         .Take(4)
                         .ToArray();
    }

    private static (string Value, string Detail) timingSummary(IReadOnlyList<double> timing)
    {
        if (timing.Count == 0)
            return ("Not measured", "Exact replay timing has not been measured yet.");

        double median = percentile(timing, 0.5);
        double spread = standardDeviation(timing);
        string value = median < -centred_timing_ms ? "Early bias" : median > centred_timing_ms ? "Late bias" : "Centred";
        return (value, $"Median {formatSigned(median)}, {spread:0.0} ms spread across {timing.Count:N0} taps.");
    }

    private static (string Value, string Detail) aimSummary(
        IReadOnlyList<double> distances,
        IReadOnlyList<GlobalMissReasonShare> reasons)
    {
        if (distances.Count == 0)
            return ("Not measured", "Cursor placement has not been measured yet.");

        double median = percentile(distances, 0.5);
        double p90 = percentile(distances, 0.9);
        GlobalMissReasonShare? aimReason = reasons.FirstOrDefault(item => item.Reason is ReplayMissReason.Undershoot
            or ReplayMissReason.Overshoot or ReplayMissReason.AimDeviation);
        string value = aimReason is null ? $"{median:0.0} unit median" : ReplayMissInsightPresenter.Label(aimReason.Reason);
        return (value, $"Successful hits: {median:0.0} playfield units median cursor error, {p90:0.0} at p90 across {distances.Count:N0} samples.");
    }

    private static CoachingConfidence confidenceFor(int analysedRuns, int analysedMaps, int replayRuns)
    {
        double coverage = replayRuns == 0 ? 0 : (double)analysedRuns / replayRuns;
        if (analysedRuns >= 20 && analysedMaps >= 10 && coverage >= 0.5)
            return CoachingConfidence.High;
        if (analysedRuns >= 8 && analysedMaps >= 4)
            return CoachingConfidence.Medium;
        if (analysedRuns >= 2)
            return CoachingConfidence.Low;
        return CoachingConfidence.Insufficient;
    }

    private static CoachingConfidence confidenceForEvidence(int evidenceCount, int runCount, int mapCount, double classifierConfidence)
    {
        CoachingConfidence confidence = (evidenceCount, runCount, mapCount) switch
        {
            (>= 15, >= 10, >= 5) => CoachingConfidence.High,
            (>= 8, >= 5, >= 3) => CoachingConfidence.Medium,
            (>= 3, >= 2, _) or (_, _, >= 2) => CoachingConfidence.Low,
            _ => CoachingConfidence.Insufficient,
        };

        if (classifierConfidence is > 0 and < 0.5 && confidence > CoachingConfidence.Low)
            return (CoachingConfidence)((int)confidence - 1);
        return confidence;
    }

    private static CoachingConfidence confidenceForSamples(int sampleCount) => sampleCount switch
    {
        >= 100 => CoachingConfidence.High,
        >= 30 => CoachingConfidence.Medium,
        >= 10 => CoachingConfidence.Low,
        _ => CoachingConfidence.Insufficient,
    };

    private static CoachingConfidence confidenceForCoverage(double coverage) => coverage switch
    {
        >= 0.75 => CoachingConfidence.High,
        >= 0.5 => CoachingConfidence.Medium,
        >= 0.25 => CoachingConfidence.Low,
        _ => CoachingConfidence.Insufficient,
    };

    private static CoachingConfidence capConfidence(params CoachingConfidence[] values) =>
        values.Length == 0 ? CoachingConfidence.Insufficient : values.Min();

    private static double reasonEvidenceScore(GlobalMissReasonShare reason) =>
        reason.MapCount * 1000 + reason.RunCount * 100 + reason.Count * 10 + reason.AverageClassifierConfidence;

    private static double classifierConfidence(ReplayObjectJudgement judgement)
    {
        double value = judgement.MissAnalysis?.Confidence ?? 0;
        return double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    }

    private static CoachingSkillArea skillArea(ReplayMissReason reason) => reason switch
    {
        ReplayMissReason.Undershoot or ReplayMissReason.Overshoot => CoachingSkillArea.AimControl,
        ReplayMissReason.AimDeviation => CoachingSkillArea.AimPrecision,
        ReplayMissReason.EarlyClick or ReplayMissReason.LateClick => CoachingSkillArea.TapTiming,
        ReplayMissReason.OnTargetNoClick => CoachingSkillArea.AimTapCoordination,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Only classified miss reasons have a skill area."),
    };

    private static string skillAreaLabel(CoachingSkillArea area) => area switch
    {
        CoachingSkillArea.AimControl => "Aim control",
        CoachingSkillArea.AimPrecision => "Aim precision",
        CoachingSkillArea.TapTiming => "Tap timing",
        CoachingSkillArea.AimTapCoordination => "Aim-tap coordination",
        _ => throw new ArgumentOutOfRangeException(nameof(area)),
    };

    private static string practiceTitle(ReplayMissReason reason) => reason switch
    {
        ReplayMissReason.EarlyClick => "Delay premature clicks",
        ReplayMissReason.LateClick => "Commit earlier",
        ReplayMissReason.Undershoot => "Finish jump travel",
        ReplayMissReason.Overshoot => "Control jump braking",
        ReplayMissReason.OnTargetNoClick => "Coordinate aim and tapping",
        ReplayMissReason.AimDeviation => "Improve approach precision",
        _ => "Review classified misses",
    };

    private static string practiceDetail(GlobalMissReasonShare reason) => reason.Reason switch
    {
        ReplayMissReason.EarlyClick => $"Early clicks account for {reason.Count:N0} classified misses across {reason.MapCount:N0} maps. Practise readable jump patterns below your limit and wait for cursor arrival.",
        ReplayMissReason.LateClick => $"Late clicks account for {reason.Count:N0} classified misses across {reason.MapCount:N0} maps. Practise committing as the cursor enters the target instead of correcting after arrival.",
        ReplayMissReason.Undershoot => $"Undershoots account for {reason.Count:N0} classified misses across {reason.MapCount:N0} maps. Isolate longer jumps and complete the full movement before tapping.",
        ReplayMissReason.Overshoot => $"Overshoots account for {reason.Count:N0} classified misses across {reason.MapCount:N0} maps. Use lower-BPM jump sections to train braking at the target centre.",
        ReplayMissReason.OnTargetNoClick => $"The cursor reached the target without a click on {reason.Count:N0} misses across {reason.MapCount:N0} maps. Prioritise hand synchronisation drills.",
        ReplayMissReason.AimDeviation => $"Aim deviation accounts for {reason.Count:N0} classified misses across {reason.MapCount:N0} maps. Practise the affected spacing at a controlled rate.",
        _ => $"Review {reason.Count:N0} classified misses across {reason.MapCount:N0} maps.",
    };

    private static bool valid(ReplayAnalysisResult? analysis) => analysis?.Summary is not null && analysis.Judgements is not null;

    private static bool isMiss(ReplayObjectJudgement judgement) =>
        string.Equals(judgement.Result, "Miss", StringComparison.OrdinalIgnoreCase);

    private static bool isTimingSample(ReplayObjectJudgement judgement) =>
        !isMiss(judgement)
        && string.Equals(judgement.MaximumResult, "Great", StringComparison.OrdinalIgnoreCase)
        && double.IsFinite(judgement.TimeOffsetMs);

    private static string mapKey(LocalReplay run) => run.BeatmapId != Guid.Empty
        ? run.BeatmapId.ToString("N")
        : $"{run.Title}\u001f{run.Difficulty}";

    private static double distance(ReplayPoint left, ReplayPoint right)
    {
        double x = left.X - right.X;
        double y = left.Y - right.Y;
        return Math.Sqrt(x * x + y * y);
    }

    private static double standardDeviation(IEnumerable<double> values)
    {
        double[] samples = values.Where(double.IsFinite).ToArray();
        if (samples.Length == 0)
            return 0;
        double mean = samples.Average();
        return Math.Sqrt(samples.Average(value => Math.Pow(value - mean, 2)));
    }

    private static double percentile(IEnumerable<double> values, double percentile)
    {
        double[] ordered = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
            return 0;
        double index = Math.Clamp(percentile, 0, 1) * (ordered.Length - 1);
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);
        return lower == upper ? ordered[lower] : ordered[lower] + (ordered[upper] - ordered[lower]) * (index - lower);
    }

    private static string formatSigned(double value) => $"{value:+0.0;-0.0;0.0} ms";

    private sealed record AnalysedRun(LocalReplay Run, ReplayAnalysisResult? Analysis);

    private sealed record ClassifiedMiss(AnalysedRun Run, ReplayObjectJudgement Judgement);
}


internal static class ReferenceCoachingReportBuilder
{
    public static CoachingReport Build(
        IReadOnlyList<LocalReplay> runs,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        Guid? selectedScoreId = null) => build(runs, analyses, selectedScoreId, selectLatestByDefault: true);

    public static CoachingReport BuildGlobal(
        IReadOnlyList<LocalReplay> runs,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses) => build(runs, analyses, null, selectLatestByDefault: false);

    private static CoachingReport build(
        IReadOnlyList<LocalReplay> runs,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        Guid? selectedScoreId,
        bool selectLatestByDefault)
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
            : selectLatestByDefault ? recent.FirstOrDefault() : null;

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
            Intelligence = ReferenceCoachingPredictionEngine.Build(recent, analyses, selected?.ScoreId),
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

        CoachingSetupBenchmark benchmark = ReferenceCoachingPredictionEngine.BuildSetupBenchmark(history, selected);
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
internal static class ReferenceCoachingWorkspaceModel
{
    private const int MaximumTrendRuns = NativeCoachingWorkspaceModel.MaximumTrendRuns;

    public static NativeCoachingWorkspaceModel Build(
        IReadOnlyList<LocalReplay> runs,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        Guid? selectedScoreId = null,
        CoachingTimeRange timeRange = CoachingTimeRange.Days30,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(analyses);

        DateTimeOffset reference = now ?? DateTimeOffset.Now;
        DateTimeOffset? earliest = timeRange switch
        {
            CoachingTimeRange.Days7 => reference.AddDays(-7),
            CoachingTimeRange.Days30 => reference.AddDays(-30),
            CoachingTimeRange.Days90 => reference.AddDays(-90),
            CoachingTimeRange.Year => reference.AddYears(-1),
            _ => null,
        };
        LocalReplay[] history = runs.Where(run => string.Equals(run.RulesetShortName, "osu", StringComparison.OrdinalIgnoreCase))
                                    .Where(ScoreMods.IsManualPlay)
                                    .GroupBy(run => run.ScoreId)
                                    .Select(group => group.OrderByDescending(run => run.PlayedAt).First())
                                    .Where(run => earliest is null || run.PlayedAt >= earliest)
                                    .OrderByDescending(run => run.PlayedAt)
                                    .Take(CoachingLimits.MaximumRuns)
                                    .ToArray();
        LocalReplay? selected = selectedScoreId is { } scoreId
            ? history.FirstOrDefault(run => run.ScoreId == scoreId)
            : null;

        LocalReplay[] trendRuns = history.OrderBy(run => run.PlayedAt)
                                         .TakeLast(MaximumTrendRuns)
                                         .ToArray();
        LocalReplay[] sessionRuns = selected is null
            ? Array.Empty<LocalReplay>()
            : findSession(history, selected.ScoreId);
        CoachingSessionSummary? session = sessionRuns.Length == 0 ? null : summariseSession(sessionRuns);
        GlobalCoachingSummary global = summariseGlobal(history, analyses);
        CoachingReport report = selected is null
            ? ReferenceCoachingReportBuilder.BuildGlobal(history, analyses)
            : ReferenceCoachingReportBuilder.Build(history, analyses, selected.ScoreId);

        return new NativeCoachingWorkspaceModel(history, trendRuns, sessionRuns, selected, session, global, report)
        {
            GlobalProfile = ReferenceGlobalCoachingProfileBuilder.Build(history, analyses),
        };
    }

    private static GlobalCoachingSummary summariseGlobal(
        IReadOnlyList<LocalReplay> history,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses)
    {
        double[] accuracies = history.Select(run => run.Accuracy)
                                     .Where(value => double.IsFinite(value) && value is >= 0 and <= 1)
                                     .OrderBy(value => value)
                                     .ToArray();
        double? median = accuracies.Length switch
        {
            0 => null,
            var count when count % 2 == 1 => accuracies[count / 2],
            var count => (accuracies[count / 2 - 1] + accuracies[count / 2]) / 2,
        };

        return new GlobalCoachingSummary(
            history.Count,
            history.Count(run => run.IsLocallyStored),
            history.Count(run => run.OnlineScoreId > 0),
            history.Select(run => run.BeatmapId).Where(id => id != Guid.Empty).Distinct().Count(),
            history.Count(run => analyses.TryGetValue(run.ScoreId, out ReplayAnalysisResult? analysis)
                                 && analysis.Summary is not null
                                 && analysis.Judgements is not null),
            history.Count == 0 ? null : history.Min(run => run.PlayedAt),
            history.Count == 0 ? null : history.Max(run => run.PlayedAt),
            median);
    }

    private static LocalReplay[] findSession(IReadOnlyList<LocalReplay> history, Guid selectedScoreId)
    {
        LocalReplay[] chronological = history.OrderBy(run => run.PlayedAt).ToArray();
        var sessions = new List<List<LocalReplay>>();
        foreach (LocalReplay run in chronological)
        {
            if (sessions.Count == 0
                || run.PlayedAt - sessions[^1][^1].PlayedAt > TimeSpan.FromMinutes(CoachingLimits.SessionGapMinutes))
            {
                sessions.Add(new List<LocalReplay>());
            }

            sessions[^1].Add(run);
        }

        return sessions.FirstOrDefault(group => group.Any(run => run.ScoreId == selectedScoreId))?.ToArray()
               ?? Array.Empty<LocalReplay>();
    }

    private static CoachingSessionSummary summariseSession(IReadOnlyList<LocalReplay> runs)
    {
        LocalReplay[] chronological = runs.OrderBy(run => run.PlayedAt).ToArray();
        double[] accuracies = chronological.Select(run => run.Accuracy)
                                           .Where(value => double.IsFinite(value) && value is >= 0 and <= 1)
                                           .OrderBy(value => value)
                                           .ToArray();
        double? median = accuracies.Length switch
        {
            0 => null,
            var count when count % 2 == 1 => accuracies[count / 2],
            var count => (accuracies[count / 2 - 1] + accuracies[count / 2]) / 2,
        };

        return new CoachingSessionSummary(
            chronological[0].PlayedAt,
            chronological[^1].PlayedAt,
            chronological.Length,
            chronological[^1].PlayedAt - chronological[0].PlayedAt,
            median);
    }
}
