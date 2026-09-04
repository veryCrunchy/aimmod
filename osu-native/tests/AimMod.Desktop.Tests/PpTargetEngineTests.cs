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
                CommonMods = [], PreferredCreators = [], PreferredSources = [], PreferredArtists = [], PreferredTitleSignals = [], PerformanceSamples = [],
            }, Is.EqualTo(second with
            {
                CommonMods = [], PreferredCreators = [], PreferredSources = [], PreferredArtists = [], PreferredTitleSignals = [], PerformanceSamples = [],
            }));
            Assert.That(first.CommonMods, Is.EqualTo(second.CommonMods));
            Assert.That(first.PreferredCreators, Is.EqualTo(second.PreferredCreators));
            Assert.That(first.PreferredSources, Is.EqualTo(second.PreferredSources));
            Assert.That(first.PreferredArtists, Is.EqualTo(second.PreferredArtists));
            Assert.That(first.PreferredTitleSignals, Is.EqualTo(second.PreferredTitleSignals));
            Assert.That(first.PerformanceSamples, Is.EqualTo(second.PerformanceSamples));
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
        PpTargetPreferenceProfile profile = profileWithHistory();
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
            MinimumExpectedPp: official.ExpectedPp - 1,
            MaximumExpectedPp: official.ExpectedPp + 1,
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
    public void OfficialDifficultyEstimateEnablesPpFiltering()
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

        Assert.Multiple(() =>
        {
            Assert.That(result.Candidates, Has.Count.EqualTo(1));
            Assert.That(result.Candidates.Single().Estimate, Is.SameAs(official));
            Assert.That(result.Candidates.Single().EstimatedAttainableGainPp, Is.Null);
        });
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
            new PpTargetFilters(MinimumStars: 20, MinimumExpectedPp: 2_000),
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
        PpTargetPreferenceProfile profile = PpTargetPreferenceProfiler.Build(history);
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
