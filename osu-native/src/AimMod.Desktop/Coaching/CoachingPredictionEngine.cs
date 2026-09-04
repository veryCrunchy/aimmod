using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Coaching;

/// <summary>
/// Builds empirical coaching estimates from the player's own stored osu!standard plays.
/// Values are descriptive, not claims about the cause of a result.
/// </summary>
public static class CoachingPredictionEngine
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

        GlobalCoachingProfile globalProfile = GlobalCoachingProfileBuilder.Build(history, analyses);
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
        double modWeight = 0.75 + 1.25 * modSimilarity(run.Mods, target.Mods);
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

    private static string setupKey(LocalReplay run)
    {
        string beatmap = run.BeatmapId != Guid.Empty
            ? run.BeatmapId.ToString("N")
            : !string.IsNullOrWhiteSpace(run.BeatmapHash)
                ? $"hash:{run.BeatmapHash.Trim().ToUpperInvariant()}"
                : $"score:{run.ScoreId:N}";
        string mods = string.Join(',', (run.Mods ?? Array.Empty<string>())
            .Where(mod => !string.IsNullOrWhiteSpace(mod))
            .Select(mod => mod.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(mod => mod, StringComparer.Ordinal));
        return $"{beatmap}|{mods}";
    }

    private static bool isStandardRun(LocalReplay run) =>
        string.Equals(run.RulesetShortName, "osu", StringComparison.OrdinalIgnoreCase);

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
