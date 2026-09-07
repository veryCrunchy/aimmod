using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Osu.Runtime;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class PpTargetEngineTests
{
    [Test]
    public void EmptyAndInvalidHistoryDoesNotInventPreferencesOrPp()
    {
        LocalReplay[] history =
        {
            replay(1, 1, 5, 0.95, 200) with { RulesetShortName = "taiko" },
            replay(2, 2, double.NaN, 0.95, 200),
            replay(3, 3, 5, 1.5, 200),
        };

        PpTargetPreferenceProfile profile = PpTargetPreferenceProfiler.Build(history);
        PpTargetRankingResult result = PpTargetRanker.Rank(profile, [set(1, "ranked", difficulty(10, 5))]);

        Assert.Multiple(() =>
        {
            Assert.That(profile, Is.EqualTo(PpTargetPreferenceProfile.Empty));
            Assert.That(result.Candidates, Has.Count.EqualTo(1));
            Assert.That(result.Candidates[0].Estimate, Is.Null);
            Assert.That(result.Candidates[0].EstimatedAttainableGainPp, Is.Null);
            Assert.That(result.Candidates[0].SuggestedMods, Is.Empty);
        });
    }

    [Test]
    public void ProfileLearnsDistinctSetupsAndEnrichedMetadataDeterministically()
    {
        Guid setId = id(100);
        Guid beatmapId = id(101);
        LocalReplay[] history =
        {
            replay(1, 101, 5.1, 0.94, 180, "Hidden") with { SetId = setId, BeatmapId = beatmapId, Title = "Stream Practice", Artist = "Composer" },
            replay(2, 101, 5.1, 0.97, 220, "hidden") with { SetId = setId, BeatmapId = beatmapId, Title = "Stream Practice", Artist = "Composer" },
            replay(3, 102, 5.5, 0.96, 230, "HardRock") with { SetId = setId, Title = "Stream Burst", Artist = "Composer" },
        };
        LocalBeatmapSet localSet = local(setId, "Mapper", "Game OST", beatmapId, 185, 125);

        PpTargetPreferenceProfile first = PpTargetPreferenceProfiler.Build(history, [localSet]);
        PpTargetPreferenceProfile second = PpTargetPreferenceProfiler.Build(history.Reverse(), [localSet]);

        Assert.Multiple(() =>
        {
            Assert.That(first with
            {
                CommonMods = [], PreferredCreators = [], PreferredSources = [], PreferredArtists = [], PreferredTitleSignals = [], PerformanceSamples = [], PreferredModSetup = [],
            }, Is.EqualTo(second with
            {
                CommonMods = [], PreferredCreators = [], PreferredSources = [], PreferredArtists = [], PreferredTitleSignals = [], PerformanceSamples = [], PreferredModSetup = [],
            }));
            Assert.That(first.CommonMods, Is.EqualTo(second.CommonMods));
            Assert.That(first.PreferredCreators, Is.EqualTo(second.PreferredCreators));
            Assert.That(first.PreferredSources, Is.EqualTo(second.PreferredSources));
            Assert.That(first.PreferredArtists, Is.EqualTo(second.PreferredArtists));
            Assert.That(first.PreferredTitleSignals, Is.EqualTo(second.PreferredTitleSignals));
            Assert.That(first.PerformanceSamples, Is.EqualTo(second.PerformanceSamples));
            Assert.That(first.PreferredModSetup, Is.EqualTo(second.PreferredModSetup));
            Assert.That(first.ValidRunCount, Is.EqualTo(3));
            Assert.That(first.DistinctSetupCount, Is.EqualTo(2), "Retries of one map/mod setup are not independent preference evidence.");
            Assert.That(first.PpSampleCount, Is.EqualTo(2));
            Assert.That(first.CommonMods.Select(item => item.Value),
                Is.EquivalentTo(new[] { "Hidden", "HardRock" }).IgnoreCase);
            Assert.That(first.PreferredCreators.Single().Value, Is.EqualTo("Mapper"));
            Assert.That(first.PreferredSources.Single().Value, Is.EqualTo("Game OST"));
            Assert.That(first.PreferredArtists.Single().Value, Is.EqualTo("Composer"));
            Assert.That(first.PreferredTitleSignals.Select(item => item.Value), Does.Contain("stream"));
            Assert.That(first.PreferredBpmRange, Is.Not.Null);
            Assert.That(first.PreferredLengthSecondsRange, Is.Not.Null);
        });
    }

    [Test]
    public void SparseHistoryDoesNotPretendHistoricalPpIsBeatmapPp()
    {
        PpTargetPreferenceProfile profile = PpTargetPreferenceProfiler.Build([
            replay(1, 1, 5, 0.95, 200, "Hidden"),
        ]);

        PpTargetCandidate candidate = PpTargetRanker.Rank(profile, [set(1, "ranked", difficulty(10, 5))]).Candidates.Single();

        Assert.Multiple(() =>
        {
            Assert.That(profile.Confidence, Is.EqualTo(PpTargetConfidence.Insufficient));
            Assert.That(candidate.Estimate, Is.Null);
        });
    }

    [Test]
    public void DenseHistoryBuildsProfileButDoesNotInventPerDifficultyPp()
    {
        LocalReplay[] history = Enumerable.Range(1, 100)
            .Select(index => replay(index, index, 5, 0.95, index <= 70 ? 15 : 80 + index - 70))
            .ToArray();

        PpTargetPreferenceProfile profile = PpTargetPreferenceProfiler.Build(history);
        PpTargetCandidate candidate = PpTargetRanker.Rank(
            profile,
            [set(1, "ranked", difficulty(1000, 5))]).Candidates.Single();

        Assert.Multiple(() =>
        {
            Assert.That(profile.CompetitivePpFloor, Is.GreaterThan(75),
                "Failed and exploratory plays must not define the expected PP baseline.");
            Assert.That(candidate.Estimate, Is.Null);
        });
    }

    [Test]
    public void EveryPerformanceSampleIsRetainedWithoutBecomingAMapCeiling()
    {
        LocalReplay[] lowStar = Enumerable.Range(1, 30)
            .Select(index => replay(index, index, 3.7 + index % 3 * 0.05, 0.95, 40 + index))
            .ToArray();
        LocalReplay highStar = replay(40, 100, 10.5, 0.98, 864, "Classic");

        PpTargetPreferenceProfile profile = PpTargetPreferenceProfiler.Build(lowStar.Append(highStar));
        PpTargetCandidate candidate = PpTargetRanker.Rank(
            profile,
            [set(1, "ranked", difficulty(1000, 3.7))]).Candidates.Single();

        Assert.Multiple(() =>
        {
            Assert.That(profile.PerformanceSamples, Has.Count.EqualTo(31), "Every valid sample remains in the model.");
            Assert.That(profile.HistoricalBestPp, Is.EqualTo(864));
            Assert.That(candidate.Estimate, Is.Null, "Map PP must come from that difficulty's official calculation.");
        });
    }

    [Test]
    public void FlattensManyDifficultiesDeduplicatesAndRejectsInvalidOrNonOsuMaps()
    {
        PpTargetPreferenceProfile profile = profileWithHistory();
        OfficialBeatmapSet first = set(1, "ranked",
            difficulty(10, 5),
            difficulty(11, 5.2),
            difficulty(12, 5.3) with { RulesetShortName = "mania" },
            difficulty(13, double.NaN));
        OfficialBeatmapSet duplicate = set(2, "loved", difficulty(10, 5.1), difficulty(14, 5.4));

        PpTargetRankingResult result = PpTargetRanker.Rank(profile, [duplicate, first]);

        Assert.Multiple(() =>
        {
            Assert.That(result.FlattenedDifficultyCount, Is.EqualTo(3));
            Assert.That(result.Candidates.Select(item => item.BeatmapId), Is.EquivalentTo(new[] { 10, 11, 14 }));
            Assert.That(result.Candidates.Single(item => item.BeatmapId == 10).BeatmapSetId, Is.EqualTo(1));
        });
    }

    [Test]
    public void FiltersAllSupportedMetadataAndEstimateFields()
    {
        PpTargetPreferenceProfile profile = withPassEvidence(profileWithHistory(), 5.2);
        OfficialBeatmapSet matching = set(1, "Ranked", difficulty(10, 5.2) with { Bpm = 180, TotalLengthSeconds = 130 }) with
        {
            Title = "Target Song", Artist = "Composer", Creator = "Mapper", Source = "Game OST",
        };
        OfficialBeatmapSet wrongStatus = set(2, "loved", difficulty(11, 5.2) with { Bpm = 180, TotalLengthSeconds = 130 });
        var official = new PpTargetEstimate(210, 340, new PpTargetRange(180, 240), 1, PpTargetConfidence.High,
            "Official osu! ruleset ppy.osu.Game/2026.730.0");
        PpTargetFilters filters = new(
            SearchText: "target mapper game",
            MinimumStars: 5, MaximumStars: 5.5,
            MinimumExpectedPp: 100,
            MaximumExpectedPp: official.ExpectedPp,
            MinimumRealisticMaximumPp: official.RealisticMaximumPp - 1,
            MaximumRealisticMaximumPp: official.RealisticMaximumPp + 1,
            MinimumLengthSeconds: 120, MaximumLengthSeconds: 140,
            MinimumBpm: 170, MaximumBpm: 190,
            Statuses: ["ranked"]);

        PpTargetRankingResult result = PpTargetRanker.Rank(
            profile,
            [wrongStatus, matching],
            filters,
            new Dictionary<int, PpTargetEstimate> { [10] = official });

        Assert.That(result.Candidates.Select(item => item.BeatmapId), Is.EqualTo(new[] { 10 }));
    }

    [Test]
    public void MissingPpDoesNotInventAStarBasedEstimate()
    {
        PpTargetPreferenceProfile profile = PpTargetPreferenceProfiler.Build([
            replay(1, 1, 5, 0.95, null),
        ]);

        PpTargetRankingResult result = PpTargetRanker.Rank(
            profile,
            [set(1, "ranked", difficulty(10, 5))],
            new PpTargetFilters());
        PpTargetCandidate candidate = result.Candidates.Single();

        Assert.Multiple(() =>
        {
            Assert.That(result.FlattenedDifficultyCount, Is.EqualTo(1));
            Assert.That(result.MatchingDifficultyCount, Is.EqualTo(1));
            Assert.That(candidate.Estimate, Is.Null);
            Assert.That(candidate.EstimatedAttainableGainPp, Is.Null);
        });
    }

    [Test]
    public void SeveralSetupsWithoutPpStillDoNotInventAnEstimate()
    {
        LocalReplay[] history = Enumerable.Range(1, 12)
                                          .Select(index => replay(index, index, 4.8 + index % 4 * 0.2, 0.94 + index % 3 * 0.01, null))
                                          .ToArray();

        PpTargetCandidate candidate = PpTargetRanker.Rank(
            PpTargetPreferenceProfiler.Build(history),
            [set(1, "ranked", difficulty(10, 5.2))]).Candidates.Single();

        Assert.That(candidate.Estimate, Is.Null);
    }

    [Test]
    public void OfficialDifficultyCalculationAloneDoesNotEnableExpectedPpFiltering()
    {
        PpTargetPreferenceProfile profile = PpTargetPreferenceProfiler.Build([
            replay(1, 1, 5, 0.95, null),
        ]);
        var official = new PpTargetEstimate(
            321, 456, new PpTargetRange(290, 350), 1, PpTargetConfidence.High,
            "Official osu! ruleset ppy.osu.Game/2026.730.0");

        PpTargetRankingResult result = PpTargetRanker.Rank(
            profile,
            [set(1, "ranked", difficulty(10, 5))],
            new PpTargetFilters(MinimumExpectedPp: 320, MaximumExpectedPp: 322),
            new Dictionary<int, PpTargetEstimate> { [10] = official });

        Assert.That(result.Candidates, Is.Empty);
        var browsing = PpTargetRanker.Rank(profile, [set(1, "ranked", difficulty(10, 5))],
            exactEstimates: new Dictionary<int, PpTargetEstimate> { [10] = official }).Candidates.Single();
        Assert.That(browsing.Estimate, Is.SameAs(official));
        Assert.That(browsing.ExpectedEarnedPp, Is.Null);
    }

    [TestCase(1.0, 0, 1000)]
    [TestCase(0.7, 1, 840)]
    [TestCase(0.5, 2, 680)]
    [TestCase(0.1, 3, 520)]
    public void ExactProjectionUsesStableAttainabilityScenarios(double attainability, int misses, int combo)
    {
        Assert.That(PpTargetExactCalculationService.ExpectedScoreShape(attainability, 1000),
            Is.EqualTo((misses, combo)));
    }

    [Test]
    public void ReversedAndMalformedFilterBoundsAreNormalised()
    {
        PpTargetPreferenceProfile profile = profileWithHistory();
        OfficialBeatmapSet maps = set(1, "ranked", difficulty(10, 5), difficulty(11, 7));

        PpTargetRankingResult result = PpTargetRanker.Rank(profile, [maps], new PpTargetFilters(
            MinimumStars: 5.5, MaximumStars: 4.5,
            MinimumBpm: double.NaN,
            Limit: -20));

        Assert.Multiple(() =>
        {
            Assert.That(result.MatchingDifficultyCount, Is.EqualTo(1));
            Assert.That(result.Candidates.Single().BeatmapId, Is.EqualTo(10));
        });
    }

    [Test]
    public void RankingFavoursPreferenceFitAndAttainabilityWithStableTieBreaks()
    {
        Guid setId = id(500);
        LocalBeatmapSet localSet = local(setId, "Preferred Mapper", "Preferred Source", id(1), 180, 120);
        LocalReplay[] history = Enumerable.Range(1, 12).Select(index => replay(index, index, 5.2, 0.96, 200 + index, "Hidden") with
        {
            SetId = setId,
            Title = "Preferred Stream",
            Artist = "Preferred Artist",
        }).ToArray();
        PpTargetPreferenceProfile profile = PpTargetPreferenceProfiler.Build(history, [localSet]);
        OfficialBeatmapSet poorFit = set(2, "ranked", difficulty(20, 7.5)) with
        {
            Title = "Other", Artist = "Other", Creator = "Other", Source = "Other",
        };
        OfficialBeatmapSet goodFit = set(1, "ranked", difficulty(10, 5.2) with { Bpm = 180, TotalLengthSeconds = 120 }) with
        {
            Title = "Preferred Stream", Artist = "Preferred Artist", Creator = "Preferred Mapper", Source = "Preferred Source",
        };
        OfficialBeatmapDifficulty tieA = difficulty(31, 5.2) with { Bpm = 180, TotalLengthSeconds = 120 };
        OfficialBeatmapDifficulty tieB = difficulty(30, 5.2) with { Bpm = 180, TotalLengthSeconds = 120 };
        OfficialBeatmapSet ties = goodFit with { BeatmapSetId = 3, Difficulties = [tieA, tieB] };

        PpTargetRankingResult result = PpTargetRanker.Rank(profile, [poorFit, ties, goodFit]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Candidates[0].BeatmapId, Is.EqualTo(10));
            Assert.That(result.Candidates[0].PreferenceFit, Is.GreaterThan(result.Candidates[^1].PreferenceFit));
            Assert.That(result.Candidates[0].Attainability, Is.GreaterThan(result.Candidates[^1].Attainability));
            Assert.That(result.Candidates.IndexOf(result.Candidates.Single(item => item.BeatmapId == 30)),
                Is.LessThan(result.Candidates.IndexOf(result.Candidates.Single(item => item.BeatmapId == 31))));
            Assert.That(result.Candidates[0].SuggestedMods, Does.Contain("HD"));
        });
    }

    [Test]
    public void HighPpAndExtremeDifficultyEvidenceIsRetainedWithoutArbitraryCeilings()
    {
        PpTargetPreferenceProfile profile = PpTargetPreferenceProfiler.Build([
            replay(1, 1, 23.5, 0.99, 2_450, "Hidden"),
        ]);

        PpTargetRankingResult result = PpTargetRanker.Rank(
            profile,
            [set(1, "ranked", difficulty(10, 24.1))],
            new PpTargetFilters(MinimumStars: 20, MinimumRealisticMaximumPp: 2_000),
            new Dictionary<int, PpTargetEstimate>
            {
                [10] = new(2_200, 2_900, new PpTargetRange(2_000, 2_500), 1, PpTargetConfidence.High,
                    "Official osu! ruleset", 10, ["HD"], 0.99),
            });

        Assert.Multiple(() =>
        {
            Assert.That(profile.PpSampleCount, Is.EqualTo(1));
            Assert.That(profile.HistoricalBestPp, Is.EqualTo(2_450));
            Assert.That(result.Candidates.Single().BeatmapId, Is.EqualTo(10));
            Assert.That(result.Candidates.Single().Estimate!.ExpectedPp, Is.EqualTo(2_200));
        });
    }

    [Test]
    public void GainBaselineUsesScoresNearTheCandidateDifficulty()
    {
        LocalReplay[] history = Enumerable.Range(1, 12)
            .Select(index => replay(index, index, 5 + index % 3 * 0.05, 0.96, 190 + index))
            .Concat(Enumerable.Range(20, 12)
                .Select(index => replay(index, index, 8 + index % 3 * 0.05, 0.96, 580 + index)))
            .ToArray();
        PpTargetPreferenceProfile profile = withPassEvidence(PpTargetPreferenceProfiler.Build(history), 5.05, 8.05);
        PpTargetRankingResult result = PpTargetRanker.Rank(
            profile,
            [set(1, "ranked", difficulty(10, 5.05), difficulty(11, 8.05))],
            exactEstimates: new Dictionary<int, PpTargetEstimate>
            {
                [10] = new(300, 350, new PpTargetRange(270, 330), 1, PpTargetConfidence.High, "Official osu! ruleset"),
                [11] = new(650, 720, new PpTargetRange(620, 680), 1, PpTargetConfidence.High, "Official osu! ruleset"),
            });

        PpTargetCandidate fiveStar = result.Candidates.Single(candidate => candidate.BeatmapId == 10);
        PpTargetCandidate eightStar = result.Candidates.Single(candidate => candidate.BeatmapId == 11);
        Assert.Multiple(() =>
        {
            Assert.That(fiveStar.GainBaselinePp, Is.InRange(190, 205));
            Assert.That(eightStar.GainBaselinePp, Is.InRange(580, 610));
            Assert.That(fiveStar.EstimatedAttainableGainPp, Is.Not.Null);
            Assert.That(eightStar.EstimatedAttainableGainPp, Is.Not.Null);
            Assert.That(fiveStar.EstimatedAttainableGainPp!.Value, Is.GreaterThan(eightStar.EstimatedAttainableGainPp!.Value));
            Assert.That(fiveStar.ScoreEvidence, Is.GreaterThan(0));
            Assert.That(eightStar.ScoreEvidence, Is.GreaterThan(0));
        });
    }

    [Test]
    public void EstimateWithDifferentDifficultyModsOrAccuracyIsNotApplied()
    {
        PpTargetPreferenceProfile profile = PpTargetPreferenceProfiler.Build(
            Enumerable.Range(1, 8).Select(index => replay(index, index, 5, 0.97, 200 + index, "Hidden")));
        var wrongDifficulty = new PpTargetEstimate(300, 400, new PpTargetRange(270, 330), 1, PpTargetConfidence.High,
            "Official osu! ruleset", 999, ["HD"], 0.97, 0.8);
        var wrongMods = wrongDifficulty with { BeatmapId = 10, Mods = ["HR"] };
        var wrongAccuracy = wrongDifficulty with { BeatmapId = 10, ExpectedAccuracy = 0.95 };
        var wrongAttainability = wrongDifficulty with { BeatmapId = 10, Attainability = 0 };

        PpTargetCandidate difficultyCandidate = PpTargetRanker.Rank(profile, [set(1, "ranked", difficulty(10, 5))],
            exactEstimates: new Dictionary<int, PpTargetEstimate> { [10] = wrongDifficulty }).Candidates.Single();
        PpTargetCandidate modsCandidate = PpTargetRanker.Rank(profile, [set(1, "ranked", difficulty(10, 5))],
            exactEstimates: new Dictionary<int, PpTargetEstimate> { [10] = wrongMods }).Candidates.Single();
        PpTargetCandidate accuracyCandidate = PpTargetRanker.Rank(profile, [set(1, "ranked", difficulty(10, 5))],
            exactEstimates: new Dictionary<int, PpTargetEstimate> { [10] = wrongAccuracy }).Candidates.Single();
        PpTargetCandidate attainabilityCandidate = PpTargetRanker.Rank(profile, [set(1, "ranked", difficulty(10, 5))],
            exactEstimates: new Dictionary<int, PpTargetEstimate> { [10] = wrongAttainability }).Candidates.Single();

        Assert.Multiple(() =>
        {
            Assert.That(difficultyCandidate.Estimate, Is.Null);
            Assert.That(modsCandidate.Estimate, Is.Null);
            Assert.That(accuracyCandidate.Estimate, Is.Null);
            Assert.That(attainabilityCandidate.Estimate, Is.Null);
        });
    }

    [Test]
    public void SuggestedModsAreCanonicalAndMutuallyCompatible()
    {
        PpTargetPreferenceProfile profile = PpTargetPreferenceProfile.Empty with
        {
            CommonMods =
            [
                new PpTargetPreference("DoubleTime", 9, 0.9),
                new PpTargetPreference("HalfTime", 8, 0.8),
                new PpTargetPreference("Hidden", 7, 0.7),
                new PpTargetPreference("HardRock", 6, 0.6),
            ],
        };

        PpTargetCandidate candidate = PpTargetRanker.Rank(
            profile,
            [set(1, "ranked", difficulty(10, 5))]).Candidates.Single();

        Assert.Multiple(() =>
        {
            Assert.That(candidate.SuggestedMods, Is.EqualTo(new[] { "DT", "HD", "HR" }));
            Assert.That(candidate.SuggestedMods, Does.Not.Contain("HT"));
            Assert.That(candidate.ModCompatibility, Is.GreaterThan(0));
        });
    }

    [Test]
    public void SuggestedSetupWasActuallyPlayedRatherThanCombiningSeparatePreferences()
    {
        var history = Enumerable.Range(1, 6).Select(i => replay(i, i, 5, 0.97, 200, "Hidden"))
            .Concat(Enumerable.Range(7, 5).Select(i => replay(i, i, 5, 0.97, 200, "HardRock")));
        var profile = PpTargetPreferenceProfiler.Build(history);
        var candidate = PpTargetRanker.Rank(profile, [set(1, "ranked", difficulty(10, 5))]).Candidates.Single();
        Assert.That(candidate.SuggestedMods, Is.EqualTo(new[] { "HD" }));
        Assert.That(candidate.SuggestedMods, Does.Not.Contain("HR"));
    }

    [Test]
    public void RepeatedRetriesDoNotIncreaseProfileConfidenceOrCreateMapPp()
    {
        LocalReplay[] repeated = Enumerable.Range(1, 40).Select(index => replay(index, 1, 5, 0.95, 200, "Hidden")).ToArray();
        PpTargetPreferenceProfile profile = PpTargetPreferenceProfiler.Build(repeated);
        PpTargetCandidate candidate = PpTargetRanker.Rank(profile, [set(1, "ranked", difficulty(10, 5))]).Candidates.Single();

        Assert.Multiple(() =>
        {
            Assert.That(profile.DistinctSetupCount, Is.EqualTo(1));
            Assert.That(profile.PpSampleCount, Is.EqualTo(1));
            Assert.That(profile.Confidence, Is.EqualTo(PpTargetConfidence.Insufficient));
            Assert.That(candidate.Estimate, Is.Null);
        });
    }

    [Test]
    public void SkillFitOutweighsHigherRewardAndPreferredMetadata()
    {
        PpTargetPreferenceProfile profile = profileWithHistory() with
        {
            PreferredCreators = [new PpTargetPreference("Favourite", 20, 1)],
        };
        OfficialBeatmapSet comfortable = set(1, "ranked", difficulty(10, 5));
        OfficialBeatmapSet stretch = set(2, "ranked", difficulty(11, 6.2)) with { Creator = "Favourite" };
        var estimates = new Dictionary<int, PpTargetEstimate>
        {
            [10] = new(180, 250, new(160, 200), 1, PpTargetConfidence.High, "test"),
            [11] = new(900, 1000, new(800, 950), 1, PpTargetConfidence.High, "test"),
        };

        var result = PpTargetRanker.Rank(profile, [stretch, comfortable], exactEstimates: estimates);

        Assert.That(result.Candidates[0].BeatmapId, Is.EqualTo(10));
        Assert.That(result.Candidates[0].Attainability, Is.GreaterThan(result.Candidates[1].Attainability));
    }

    [Test]
    public void SameStarTargetsPrioritizeMeasuredPatternsOverPp()
    {
        var good = new PpPatternPrediction(0.92, 0.985, 0.7, ["Jumps"], [], [new("Jumps", 0.92, 0.985, 0.7, 8)]);
        var weak = new PpPatternPrediction(0.35, 0.85, 0.7, [], ["Streams"], [new("Streams", 0.35, 0.85, 0.7, 8)]);
        var estimates = new Dictionary<int, PpTargetEstimate>
        {
            [10] = new(180, 250, new(160, 200), 1, PpTargetConfidence.High, "test", PatternPrediction: good),
            [11] = new(900, 1000, new(800, 950), 1, PpTargetConfidence.High, "test", PatternPrediction: weak),
        };
        var result = PpTargetRanker.Rank(profileWithHistory(), [set(1, "ranked", difficulty(10, 5), difficulty(11, 5))], exactEstimates: estimates);
        Assert.That(result.Candidates[0].BeatmapId, Is.EqualTo(10));
        Assert.That(result.Candidates[0].Attainability, Is.EqualTo(0.92));
        Assert.That(result.Candidates[1].Attainability, Is.EqualTo(0.35));
    }

    [Test]
    public void PartialCoverageDoesNotHideAKnownWeakness()
    {
        var prediction = new PpPatternPrediction(null, null, 0.2, [], [],
            [new("Jumps", 0.2, 0.8, 0.6, 5), new("Streams", null, null, 0, 0)]);
        var estimate = new PpTargetEstimate(180, 250, new(160, 200), 1, PpTargetConfidence.High, "test", PatternPrediction: prediction);
        var candidate = PpTargetRanker.Rank(profileWithHistory(), [set(1, "ranked", difficulty(10, 5))],
            exactEstimates: new Dictionary<int, PpTargetEstimate> { [10] = estimate }).Candidates.Single();
        Assert.That(candidate.Attainability, Is.EqualTo(0.2));
        Assert.That(candidate.RecommendationConfidence, Is.EqualTo(PpTargetConfidence.Insufficient));
    }

    [Test]
    public void EstimateFromAnotherPatternProfileIsNotReused()
    {
        var estimate = new PpTargetEstimate(180, 250, new(160, 200), 1, PpTargetConfidence.High, "test",
            PatternProfileIdentity: "old-profile");
        var result = PpTargetRanker.Rank(profileWithHistory(), [set(1, "ranked", difficulty(10, 5))],
            exactEstimates: new Dictionary<int, PpTargetEstimate> { [10] = estimate });
        Assert.That(result.Candidates[0].Estimate, Is.Null);
    }

    [Test]
    public void RecentScoreEvidenceSurvivesUnknownPatternsWithoutClaimingMeasuredSkill()
    {
        LocalReplay[] runs = Enumerable.Range(1, 8).Select(i => replay(i, i, 5, .98, null, "HD")).ToArray();
        var profile = PpTargetPreferenceProfiler.Build(runs) with
        {
            PatternProfile = PpTargetPatternModel.BuildProfile(runs, new Dictionary<Guid, AimMod.Osu.Runtime.Contracts.ReplayAnalysisResult>(),
                now: runs.Max(r => r.PlayedAt)),
        };
        var prediction = new PpPatternPrediction(null, null, 0, [], [], [new("Jumps", null, null, 0, 0)]);
        var estimate = new PpTargetEstimate(180, 250, new(160, 200), 0, PpTargetConfidence.Low, "test", PatternPrediction: prediction);
        var candidate = PpTargetRanker.Rank(profile, [set(1, "ranked", difficulty(10, 5))],
            exactEstimates: new Dictionary<int, PpTargetEstimate> { [10] = estimate }).Candidates.Single();
        Assert.That(candidate.ScoreEvidence, Is.GreaterThan(0).And.LessThanOrEqualTo(.3));
        Assert.That(candidate.RecommendationConfidence, Is.EqualTo(PpTargetConfidence.Low));
        Assert.That(candidate.Estimate!.PatternPrediction!.Fit, Is.Null);
        Assert.That(candidate.Attainability, Is.GreaterThan(.5));
    }

    [Test]
    public void BroadScanCoversSelectedStarBandsBeforeFillingEasyBand()
    {
        var catalog = Enumerable.Range(1, 300).Select(i => set(i, "ranked", difficulty(i, 3)))
            .Concat([set(900, "ranked", difficulty(900, 5.2)), set(901, "ranked", difficulty(901, 6.2))]);
        var selected = PpTargetScanPlanner.Select(profileWithHistory(), catalog, new PpTargetFilters(MinimumStars: 3, MaximumStars: 6.5), 10);
        Assert.That(selected, Has.Count.EqualTo(10));
        Assert.That(selected.Select(candidate => candidate.BeatmapId), Does.Contain(900).And.Contain(901));
        var highOnly = PpTargetScanPlanner.Select(profileWithHistory(), catalog, new PpTargetFilters(MinimumStars: 5, MaximumStars: 7), 500);
        Assert.That(highOnly, Has.Count.EqualTo(2));
        Assert.That(highOnly.All(candidate => candidate.StarRating >= 5), Is.True);
    }

    [Test]
    public void ExpandedPlanRetainsTwoThousandUniqueCandidatesAndFiltersBeforeScoring()
    {
        var catalog = Enumerable.Range(1, 6000).Select(i => set(i, "ranked", difficulty(i, i % 3 == 0 ? 10 : 5.2))).ToArray();
        var selected = PpTargetScanPlanner.Select(profileWithHistory(), catalog, new PpTargetFilters(MaximumStars:6), 2000);
        Assert.That(selected, Has.Count.EqualTo(2000));
        Assert.That(selected.Select(c => c.BeatmapId).Distinct().Count(), Is.EqualTo(2000));
        Assert.That(selected.All(c => c.StarRating <= 6), Is.True);
    }

    [Test]
    public void StatusCategoryContractUsesOfficialCategoryNames()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PpTargetStatus.FromCategory(OfficialBeatmapCategory.Ranked), Is.EqualTo("ranked"));
            Assert.That(PpTargetStatus.FromCategory(OfficialBeatmapCategory.Graveyard), Is.EqualTo("graveyard"));
            Assert.That(PpTargetStatus.FromCategory(OfficialBeatmapCategory.Any), Is.Empty);
        });
    }

    private static PpTargetPreferenceProfile profileWithHistory() => PpTargetPreferenceProfiler.Build(
        Enumerable.Range(1, 20).Select(index => replay(index, index, 4.8 + index % 5 * 0.15, 0.94 + index % 4 * 0.01, 180 + index * 3, "Hidden")));

    private static PpTargetPreferenceProfile withPassEvidence(PpTargetPreferenceProfile profile, params double[] bands)
    {
        var now = DateTimeOffset.UtcNow;
        return profile with { Opportunities = new(now, [new(5000, 100)], bands.SelectMany((stars, band) =>
            Enumerable.Range(1, 12).Select(i => new PpTargetPassSample(1000 + band * 100 + i, now.AddDays(-1),
                stars, 180, 120, string.Join(',', profile.PreferredModSetup ?? []), i <= 10, Accuracy: .96))).ToArray()) };
    }

    [Test]
    public void UsefulGainOutranksSameSkillMapWithAnUnimprovableKnownBest()
    {
        var profile = withPassEvidence(profileWithHistory(), 5.2);
        profile = profile with { Opportunities = profile.Opportunities! with { BestPlays = [new(10, 300)] } };
        var estimate = new PpTargetEstimate(220, 300, new(180, 250), 1, PpTargetConfidence.Low, "test");
        var ranked = PpTargetRanker.Rank(profile, [set(1, "ranked", difficulty(10,5.2), difficulty(11,5.2))],
            exactEstimates:new Dictionary<int,PpTargetEstimate> { [10] = estimate, [11] = estimate });
        Assert.That(ranked.Candidates.First().BeatmapId, Is.EqualTo(11));
        Assert.That(ranked.Candidates.Single(c => c.BeatmapId == 10).EstimatedAccountGainPp, Is.Zero);
    }

    [Test]
    public void UnsupportedTenStarRewardCannotOutrankSupportedFiveStarTarget()
    {
        var profile = withPassEvidence(profileWithHistory(), 5.2);
        var result = PpTargetRanker.Rank(profile, [set(1, "ranked", difficulty(10, 5.2), difficulty(11, 10.36))],
            exactEstimates: new Dictionary<int, PpTargetEstimate> {
                [10] = new(220, 300, new(180, 250), 1, PpTargetConfidence.Low, "Official osu! ruleset"),
                [11] = new(969, 1596, new(562, 1376), 1, PpTargetConfidence.Low, "Official osu! ruleset"),
            });
        var playable = result.Candidates.Single(c => c.BeatmapId == 10);
        var extreme = result.Candidates.Single(c => c.BeatmapId == 11);
        Assert.Multiple(() => {
            Assert.That(result.Candidates.First().BeatmapId, Is.EqualTo(10));
            Assert.That(playable.ExpectedEarnedPp, Is.GreaterThan(0).And.LessThan(220));
            Assert.That(playable.EstimatedAccountGainPp, Is.GreaterThan(0));
            Assert.That(extreme.ExpectedEarnedPp, Is.Null);
            Assert.That(extreme.EstimatedAccountGainPp, Is.Null);
            Assert.That(extreme.ReadinessLabel, Is.EqualTo("Pass unverified"));
            Assert.That(extreme.Estimate!.RealisticMaximumPp, Is.EqualTo(1596));
            Assert.That(playable.ExpectedEarnedPp, Is.EqualTo(220 * playable.PassEstimate!.Probability));
            Assert.That(playable.EstimatedAccountGainPp, Is.EqualTo(
                PpTargetOpportunityModel.AccountGain(profile.Opportunities, playable.BeatmapId, 220) * playable.PassEstimate.Probability));
        });
        foreach (var sort in Enum.GetValues<NativePpTargetsWorkspace.TargetSort>())
            Assert.That(NativePpTargetsWorkspace.OrderTargets(result.Candidates, sort).First().BeatmapId, Is.EqualTo(10), sort.ToString());
        var filtered = PpTargetRanker.Rank(profile, [set(1, "ranked", difficulty(11, 10.36))],
            new PpTargetFilters(MinimumExpectedPp: 800), new Dictionary<int, PpTargetEstimate> { [11] = extreme.Estimate! });
        Assert.That(filtered.Candidates, Is.Empty);
    }

    private static LocalReplay replay(int day, int beatmap, double stars, double accuracy, double? pp, params string[] mods) => new(
        id(10_000 + day), id(1_000 + beatmap), id(beatmap), $"Map {beatmap}", "Artist", "Insane", "osu", "Player",
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(day), stars, accuracy, 1_000_000, 500, 0, pp, mods, true);

    private static LocalBeatmapSet local(Guid setId, string creator, string source, Guid beatmapId, double bpm, int lengthSeconds) => new(
        setId, 100, "Local", "Artist", creator, source, DateTimeOffset.UtcNow, null,
        [new LocalBeatmapDifficulty(beatmapId, 1, "Insane", "osu", 5.2, bpm, lengthSeconds * 1_000, 4, 9, 8, 6, 1)],
        1);

    private static OfficialBeatmapSet set(int id, string status, params OfficialBeatmapDifficulty[] difficulties) => new(
        id, $"Set {id}", $"Set {id}", "Artist", "Artist", "Creator", "Source", status, null, null,
        1_000, 100, false, false, null, null, null, null, difficulties);

    private static OfficialBeatmapDifficulty difficulty(int id, double stars) => new(
        id, $"Difficulty {id}", "osu", stars, 180, 120, 4, 9, 8, 6, 1_000, 500, 800);

    private static Guid id(int value) => new(value, 0, 0, new byte[8]);
}

internal static class PpTargetTestListExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> values, T value)
    {
        for (int index = 0; index < values.Count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(values[index], value))
                return index;
        }

        return -1;
    }
}
