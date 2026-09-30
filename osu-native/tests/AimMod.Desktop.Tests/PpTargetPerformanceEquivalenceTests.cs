using System.Text.Json;
using System.Text.Json.Serialization;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class PpTargetPerformanceEquivalenceTests
{
    private static readonly DateTimeOffset now = new(2026, 3, 15, 18, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions json = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private static readonly string[] words = ["Blue", "Night", "Storm", "Echo", "Rain", "Zero", "Light", "Drive"];
    private static readonly string[] statuses = ["ranked", "approved", "loved", "graveyard", "Ranked", "qualified"];
    private static readonly PpPatternFeatures jumps = new() { PointCount = 200, JumpFraction = .8, StreamFraction = .05, BurstFraction = .1, SharpTurnFraction = .2, NotesPerSecond = 5, JumpDistance = 200 };

    [TestCase(1, 0)]
    [TestCase(2, 1)]
    [TestCase(3, 40)]
    [TestCase(4, 400)]
    [TestCase(5, 2500)]
    [TestCase(6, 900)]
    [TestCase(7, 900)]
    public void RankingMatchesTheReferenceImplementation(int seed, int sets)
    {
        var random = new Random(seed);
        OfficialBeatmapSet[] catalog = catalogue(random, sets);
        int[] ids = catalog.SelectMany(s => s.Difficulties).Select(d => d.BeatmapId).Distinct().ToArray();
        PpTargetPreferenceProfile profile = syntheticProfile(random, ids);
        Dictionary<int, PpTargetEstimate> estimates = syntheticEstimates(random, ids, profile);

        PpTargetFilters[] filters =
        [
            new(Limit: 50_000),
            new(Limit: 7),
            new(SearchText: words[random.Next(words.Length)], MinimumStars: 4, MaximumStars: 7, Limit: 500),
            new(Statuses: ["ranked", "approved"], MinimumExpectedPp: 1, Limit: 50_000),
            new(MinimumRealisticMaximumPp: double.NaN, MaximumRealisticMaximumPp: 300, MinimumBpm: 150, MaximumLengthSeconds: 200),
        ];
        if (seed is 6 or 7)
        {
            PpTargetCandidate[] all = PpTargetRanker.Rank(profile, catalog, filters[0], estimates).Candidates.ToArray();
            Assert.That(all.Count(c => c.PassEstimate?.SameMap == true) * all.Count(c => c.EstimatedAccountGainPp is not null) * all.Count(c => c.Forecast is not null),
                Is.Positive, "Synthetic data must reach direct pass estimates, account gain and PP forecasts.");
        }
        foreach (PpTargetFilters filter in filters)
        {
            assertSame(ReferencePpTargetEngine.Rank(profile, catalog, filter, estimates), PpTargetRanker.Rank(profile, catalog, filter, estimates), $"seed {seed}, {filter}");
            assertSame(ReferencePpTargetEngine.Rank(profile, catalog, filter), PpTargetRanker.Rank(profile, catalog, filter), $"seed {seed}, no estimates, {filter}");
        }

        foreach (var variant in new[]
                 {
                     profile with { PatternProfile = null },
                     profile with { PatternProfile = profile.PatternProfile is { } p ? p with { ScoreEvidence = null } : null },
                     profile with { Opportunities = null },
                     profile with { PreferredModSetup = null, PreferredModsJson = null },
                     profile with { PreferredModsJson = "[{\"acronym\":\"DT\",\"settings\":{\"speed_change\":1.3}}]" },
                     profile with { LegacyScore = !profile.LegacyScore },
                     profile with { PerformanceSamples = [] },
                 })
            assertSame(ReferencePpTargetEngine.Rank(variant, catalog, filters[0], estimates), PpTargetRanker.Rank(variant, catalog, filters[0], estimates), $"seed {seed} variant");
    }

    [Test]
    public void EmptyProfileAndCatalogueMatchTheReferenceImplementation()
    {
        var random = new Random(99);
        OfficialBeatmapSet[] catalog = catalogue(random, 30);
        assertSame(ReferencePpTargetEngine.Rank(PpTargetPreferenceProfile.Empty, catalog), PpTargetRanker.Rank(PpTargetPreferenceProfile.Empty, catalog), "empty profile");
        assertSame(ReferencePpTargetEngine.Rank(PpTargetPreferenceProfile.Empty, []), PpTargetRanker.Rank(PpTargetPreferenceProfile.Empty, []), "empty catalogue");
    }

    [TestCase(11)]
    [TestCase(12)]
    [TestCase(13)]
    public void ScoreFitAccountGainAndPassEstimatesMatchTheReferenceImplementation(int seed)
    {
        var random = new Random(seed);
        int[] ids = Enumerable.Range(1, 60).ToArray();
        PpTargetPreferenceProfile profile = syntheticProfile(random, ids);
        PpPatternProfile patterns = profile.PatternProfile!;
        PpTargetOpportunityProfile opportunities = profile.Opportunities!;
        string[][] setups = [[], ["HD"], ["DT"], ["HD", "DT"], ["Hidden"], ["NF"]];
        for (int i = 0; i < 400; i++)
        {
            double stars = i % 17 == 0 ? double.NaN : Math.Round(1.5 + random.NextDouble() * 8, random.Next(3));
            string[] mods = setups[random.Next(setups.Length)];
            bool legacy = random.Next(3) == 0;
            int beatmap = random.Next(3) == 0 ? 0 : ids[random.Next(ids.Length)];
            double bpm = random.Next(15) == 0 ? double.NaN : Math.Round(120 + random.NextDouble() * 120);
            int seconds = random.Next(12) == 0 ? 0 : random.Next(40, 400);
            double pp = random.Next(15) == 0 ? double.NaN : Math.Round(random.NextDouble() * 500, random.Next(3));
            string? modsJson = random.Next(5) == 0 ? "[{\"acronym\":\"DT\",\"settings\":{\"speed_change\":1.3}}]" : null;
            Assert.Multiple(() =>
            {
                Assert.That(PpTargetPatternModel.ScoreFit(patterns, stars, mods, legacy), Is.EqualTo(ReferencePpTargetEngine.ScoreFit(patterns, stars, mods, legacy)));
                Assert.That(PpTargetOpportunityModel.AccountGain(opportunities, beatmap, pp), Is.EqualTo(ReferencePpTargetEngine.AccountGain(opportunities, beatmap, pp)));
                Assert.That(serialise(PpTargetOpportunityModel.EstimatePass(opportunities, stars, bpm, seconds, mods, beatmap, modsJson, legacy)),
                    Is.EqualTo(serialise(ReferencePpTargetEngine.EstimatePass(opportunities, stars, bpm, seconds, mods, beatmap, modsJson, legacy))));
            });
        }

        // Only shorter maps: the duration-adjusted survival path.
        var shortOnly = new PpTargetOpportunityProfile(now, [], Enumerable.Range(0, 40).Select(i => new PpTargetPassSample(i % 8 + 1,
            now.AddHours(-i * 5), Math.Round(4.6 + random.NextDouble() * .8, 2), 175 + random.Next(10), 80 + random.Next(20), "", random.Next(4) != 0,
            Accuracy: .9 + random.NextDouble() * .1)).ToArray());
        int adjusted = 0;
        for (int seconds = 100; seconds <= 420; seconds += 20)
        {
            var actual = PpTargetOpportunityModel.EstimatePass(shortOnly, 5, 180, seconds, []);
            adjusted += actual?.DurationAdjusted == true ? 1 : 0;
            Assert.That(serialise(actual), Is.EqualTo(serialise(ReferencePpTargetEngine.EstimatePass(shortOnly, 5, 180, seconds, []))), $"{seconds} s");
        }
        Assert.That(adjusted, Is.Positive);
    }

    [TestCase(21)]
    [TestCase(22)]
    [TestCase(23)]
    public void PatternProfileMatchesTheReferenceImplementation(int seed)
    {
        var random = new Random(seed);
        (LocalReplay[] replays, Dictionary<Guid, ReplayAnalysisResult> analyses, Dictionary<Guid, PpPatternContext> contexts, LocalBeatmapSet[] sets) = patternHistory(random, 80);
        PpPatternProfile reference = ReferencePpTargetPatternModel.BuildProfile(replays, analyses, contexts, now, localSets: sets);
        PpPatternProfile actual = PpTargetPatternModel.BuildProfile(replays, analyses, contexts, now, localSets: sets);
        Assert.That(actual.Evidence, Is.Not.Empty);
        Assert.That(content(actual), Is.EqualTo(content(reference)));

        var partial = replays.Where((_, i) => i % 3 != 0).ToArray();
        var fewer = analyses.Where((_, i) => i % 2 == 0).ToDictionary(p => p.Key, p => p.Value);
        foreach (var (cachedReference, cachedActual) in new[] { (reference, actual), (reference with { Evidence = reference.Evidence.Take(5).ToArray() }, actual with { Evidence = actual.Evidence.Take(5).ToArray() }) })
            Assert.That(content(PpTargetPatternModel.BuildProfile(partial, fewer, contexts, now.AddDays(2), localSets: sets, cachedProfile: cachedActual)),
                Is.EqualTo(content(ReferencePpTargetPatternModel.BuildProfile(partial, fewer, contexts, now.AddDays(2), localSets: sets, cachedProfile: cachedReference))));
        Assert.That(content(PpTargetPatternModel.BuildProfile(replays, analyses, null, now, 7)),
            Is.EqualTo(content(ReferencePpTargetPatternModel.BuildProfile(replays, analyses, null, now, 7))));
        Assert.That(content(PpTargetPatternModel.BuildProfile([], new Dictionary<Guid, ReplayAnalysisResult>(), now: now)),
            Is.EqualTo(content(ReferencePpTargetPatternModel.BuildProfile([], new Dictionary<Guid, ReplayAnalysisResult>(), now: now))));
    }

    [Test]
    public void ProfileIdentityIsStableAndChangesWithEveryReferenceIdentityChange()
    {
        var random = new Random(31);
        (LocalReplay[] replays, Dictionary<Guid, ReplayAnalysisResult> analyses, Dictionary<Guid, PpPatternContext> contexts, LocalBeatmapSet[] sets) = patternHistory(random, 40);
        PpPatternProfile build(IEnumerable<LocalReplay> runs, IReadOnlyDictionary<Guid, PpPatternContext>? context = null, DateTimeOffset? at = null, int days = 30) =>
            PpTargetPatternModel.BuildProfile(runs, analyses, context ?? contexts, at ?? now, days, sets);
        PpPatternProfile first = build(replays);
        PpPatternProfile again = build(replays.Reverse().ToArray());
        PpPatternProfile fresh = PpTargetPatternModel.BuildProfile(replays.Select(r => r with { }).ToArray(),
            analyses.ToDictionary(p => p.Key, p => p.Value with { Judgements = p.Value.Judgements.Select(j => j with { }).ToArray() }),
            contexts.ToDictionary(p => p.Key, p => p.Value with { }), now, 30, sets);
        int analysed = Array.FindIndex(replays, r => first.Evidence.Any(e => e.ScoreId == r.ScoreId));
        int scored = Array.FindIndex(replays, r => first.ScoreEvidence!.Any(e => e.ScoreId == r.ScoreId));
        var changedContext = contexts.ToDictionary(p => p.Key, p => p.Value);
        changedContext[replays[analysed].ScoreId] = new(29, 1);

        Assert.Multiple(() =>
        {
            Assert.That(first.Identity, Has.Length.EqualTo(64));
            Assert.That(again.Identity, Is.EqualTo(first.Identity));
            Assert.That(fresh.Identity, Is.EqualTo(first.Identity));
            Assert.That(build(replays).Identity, Is.EqualTo(first.Identity));
        });

        (string Name, PpPatternProfile Profile, PpPatternProfile Reference)[] changes =
        [
            ("accuracy", build(replace(replays, scored, r => r with { Accuracy = r.Accuracy - .01 })),
                ReferencePpTargetPatternModel.BuildProfile(replace(replays, scored, r => r with { Accuracy = r.Accuracy - .01 }), analyses, contexts, now, 30, sets)),
            ("stars", build(replace(replays, scored, r => r with { StarRating = r.StarRating + .01 })),
                ReferencePpTargetPatternModel.BuildProfile(replace(replays, scored, r => r with { StarRating = r.StarRating + .01 }), analyses, contexts, now, 30, sets)),
            ("played", build(replace(replays, analysed, r => r with { PlayedAt = r.PlayedAt.AddSeconds(-1) })),
                ReferencePpTargetPatternModel.BuildProfile(replace(replays, analysed, r => r with { PlayedAt = r.PlayedAt.AddSeconds(-1) }), analyses, contexts, now, 30, sets)),
            ("legacy", build(replace(replays, analysed, toggleLegacy)),
                ReferencePpTargetPatternModel.BuildProfile(replace(replays, analysed, toggleLegacy), analyses, contexts, now, 30, sets)),
            ("radius", build(replays, changedContext), ReferencePpTargetPatternModel.BuildProfile(replays, analyses, changedContext, now, 30, sets)),
            ("recency", build(replays, days: 29), ReferencePpTargetPatternModel.BuildProfile(replays, analyses, contexts, now, 29, sets)),
            ("next day", build(replays, at: now.AddDays(1)), ReferencePpTargetPatternModel.BuildProfile(replays, analyses, contexts, now.AddDays(1), 30, sets)),
            ("removed", build(replays.Skip(1).ToArray()), ReferencePpTargetPatternModel.BuildProfile(replays.Skip(1).ToArray(), analyses, contexts, now, 30, sets)),
        ];
        PpPatternProfile referenceFirst = ReferencePpTargetPatternModel.BuildProfile(replays, analyses, contexts, now, 30, sets);
        foreach (var (name, profile, reference) in changes)
        {
            Assert.That(reference.Identity, Is.Not.EqualTo(referenceFirst.Identity), name);
            Assert.That(profile.Identity, Is.Not.EqualTo(first.Identity), name);
        }

        PpPatternEvidence evidence = first.Evidence[0];
        var outcomes = evidence.Outcomes.ToDictionary(p => p.Key, p => p.Value);
        outcomes["Overall"] = outcomes["Overall"] with { Accuracy = outcomes["Overall"].Accuracy - 1e-9 };
        PpPatternProfile cached = first with { Evidence = [evidence with { Outcomes = outcomes }, .. first.Evidence.Skip(1)] };
        PpPatternProfile original = PpTargetPatternModel.BuildProfile([], analyses, now: now, cachedProfile: first);
        PpPatternProfile perturbed = PpTargetPatternModel.BuildProfile([], analyses, now: now, cachedProfile: cached);
        PpPatternProfile features = PpTargetPatternModel.BuildProfile([], analyses, now: now,
            cachedProfile: first with { Evidence = [evidence with { Features = evidence.Features with { MeanSpacing = (evidence.Features.MeanSpacing ?? 0) + 1e-9 } }, .. first.Evidence.Skip(1)] });
        Assert.Multiple(() =>
        {
            Assert.That(PpTargetPatternModel.BuildProfile([], analyses, now: now, cachedProfile: first).Identity, Is.EqualTo(original.Identity));
            Assert.That(perturbed.Identity, Is.Not.EqualTo(original.Identity));
            Assert.That(features.Identity, Is.Not.EqualTo(original.Identity));
        });
    }

    [Test]
    public void ProfileIdentityDoesNotDependOnTheClockWithinADay()
    {
        var run = new LocalReplay(id(8, 1), id(8, 2), id(8, 3), "Synthetic", "Artist", "Diff", "osu", "Synthetic player", now.AddMinutes(-30),
            5, .98, 1_000_000, 500, 0, 150, [], true, "hash-expiry");
        var judgements = Enumerable.Range(0, 32).Select(i => new ReplayObjectJudgement(i, null, "HitCircle", 1_000 + i * 125, 1_000 + i * 125,
            "Great", "Great", 1_000 + i * 125, 0, 1, new(i * 4, 100), null, i, i + 1)).ToArray();
        var analyses = new Dictionary<Guid, ReplayAnalysisResult> { [run.ScoreId] = new("synthetic", "officialRulesetPlayback", true, 0, [], judgements, ReplayJudgementSummary.Empty) };
        var contexts = new Dictionary<Guid, PpPatternContext> { [run.ScoreId] = new(32, 1) };
        PpPatternProfile active = PpTargetPatternModel.BuildProfile([run], analyses, contexts, now);
        PpPatternProfile later = PpTargetPatternModel.BuildProfile([run], analyses, contexts, now.AddMinutes(10));
        PpPatternProfile ended = PpTargetPatternModel.BuildProfile([run], analyses, contexts, now.AddMinutes(45));
        PpPatternProfile idle = PpTargetPatternModel.BuildProfile([run], analyses, contexts, now.AddMinutes(50));
        Assert.Multiple(() =>
        {
            Assert.That(active.Evidence, Has.Count.EqualTo(1));
            Assert.That(active.SessionForm!.Support, Is.Not.Empty);
            Assert.That(ended.SessionForm!.Support, Is.Empty);
            Assert.That(later.Identity, Is.EqualTo(active.Identity));
            Assert.That(ended.Identity, Is.EqualTo(active.Identity), "Support counts are display-only.");
            Assert.That(idle.Identity, Is.EqualTo(ended.Identity));
            Assert.That(ReferencePpTargetPatternModel.BuildProfile([run], analyses, contexts, now.AddMinutes(50)).Identity,
                Is.EqualTo(ReferencePpTargetPatternModel.BuildProfile([run], analyses, contexts, now).Identity));
        });
    }

    [Test]
    public void NonFiniteLocalMetadataNoLongerBreaksTheProfile()
    {
        var random = new Random(41);
        (LocalReplay[] replays, Dictionary<Guid, ReplayAnalysisResult> analyses, Dictionary<Guid, PpPatternContext> contexts, LocalBeatmapSet[] sets) = patternHistory(random, 30);
        LocalBeatmapSet[] broken = sets.Select(s => s with { Difficulties = s.Difficulties.Select(d => d with { Bpm = double.NaN, StarRating = double.PositiveInfinity }).ToArray() }).ToArray();
        // Non-positive or non-finite supplied context is ignored by the measurement, exactly like an absent value.
        var nonFinite = new Dictionary<Guid, PpPatternContext>(contexts);
        var absent = new Dictionary<Guid, PpPatternContext>(contexts);
        foreach (Guid id in replays.Select(r => r.ScoreId).Where(analyses.ContainsKey).Take(4))
        {
            nonFinite[id] = new(double.NaN, double.PositiveInfinity);
            absent[id] = new(null, null);
        }

        Assert.Throws<ArgumentException>(() => ReferencePpTargetPatternModel.BuildProfile(replays, analyses, contexts, now, localSets: broken));
        Assert.Throws<ArgumentException>(() => ReferencePpTargetPatternModel.BuildProfile(replays, analyses, nonFinite, now, localSets: sets));
        Assert.Multiple(() =>
        {
            Assert.That(content(PpTargetPatternModel.BuildProfile(replays, analyses, contexts, now, localSets: broken)),
                Is.EqualTo(content(ReferencePpTargetPatternModel.BuildProfile(replays, analyses, contexts, now, localSets: sets))));
            Assert.That(content(PpTargetPatternModel.BuildProfile(replays, analyses, nonFinite, now, localSets: sets)),
                Is.EqualTo(content(ReferencePpTargetPatternModel.BuildProfile(replays, analyses, absent, now, localSets: sets))));
        });
    }

    [Test, Explicit("Timing comparison only.")]
    public void MeasureRankingSpeedup()
    {
        var random = new Random(5);
        OfficialBeatmapSet[] catalog = catalogue(random, 8_000);
        int[] ids = catalog.SelectMany(s => s.Difficulties).Select(d => d.BeatmapId).Distinct().ToArray();
        PpTargetPreferenceProfile profile = syntheticProfile(random, ids, 2_000);
        Dictionary<int, PpTargetEstimate> estimates = syntheticEstimates(random, ids, profile);
        (LocalReplay[] replays, Dictionary<Guid, ReplayAnalysisResult> analyses, Dictionary<Guid, PpPatternContext> contexts, LocalBeatmapSet[] sets) = patternHistory(random, 600);
        var filter = new PpTargetFilters(Limit: 50_000);
        (string Name, Action Run)[] cases =
        [
            ("Rank reference", () => ReferencePpTargetEngine.Rank(profile, catalog, filter, estimates)),
            ("Rank optimised", () => PpTargetRanker.Rank(profile, catalog, filter, estimates)),
            ("Rank reference, no pattern profile", () => ReferencePpTargetEngine.Rank(profile with { PatternProfile = null }, catalog, filter, estimates)),
            ("Rank optimised, no pattern profile", () => PpTargetRanker.Rank(profile with { PatternProfile = null }, catalog, filter, estimates)),
            ("Rank reference, no attempts", () => ReferencePpTargetEngine.Rank(profile with { Opportunities = null }, catalog, filter, estimates)),
            ("Rank optimised, no attempts", () => PpTargetRanker.Rank(profile with { Opportunities = null }, catalog, filter, estimates)),
            ("Pass reference", () => { foreach (var d in catalog.SelectMany(s => s.Difficulties)) ReferencePpTargetEngine.EstimatePass(profile.Opportunities, d.StarRating, d.Bpm, d.TotalLengthSeconds, profile.PreferredModSetup ?? [], d.BeatmapId, profile.PreferredModsJson, profile.LegacyScore); }),
            ("Pass optimised", () => { var p = new PpTargetOpportunityModel.PassEstimator(profile.Opportunities!, profile.PreferredModSetup ?? [], profile.PreferredModsJson, profile.LegacyScore); foreach (var d in catalog.SelectMany(s => s.Difficulties)) p.Estimate(d.StarRating, d.Bpm, d.TotalLengthSeconds, d.BeatmapId); }),
            ("Gain reference", () => { foreach (var d in catalog.SelectMany(s => s.Difficulties)) ReferencePpTargetEngine.AccountGain(profile.Opportunities, d.BeatmapId, 200); }),
            ("Gain optimised", () => { var g = new PpTargetOpportunityModel.AccountGainIndex(profile.Opportunities); foreach (var d in catalog.SelectMany(s => s.Difficulties)) g.Gain(d.BeatmapId, 200); }),
            ("Learn reference", () => { foreach (var d in catalog.SelectMany(s => s.Difficulties)) ReferencePpTargetEngine.LearningPredict(profile.Opportunities, profile.PatternProfile, estimates.GetValueOrDefault(d.BeatmapId), d.BeatmapId, d.StarRating, d.Bpm, d.TotalLengthSeconds, profile.PreferredModSetup ?? [], profile.PreferredModsJson, profile.LegacyScore); }),
            ("Learn optimised", () => { var l = new PpTargetLearningModel.Predictor(profile.Opportunities, profile.PatternProfile, profile.PreferredModSetup ?? [], profile.PreferredModsJson, profile.LegacyScore); foreach (var d in catalog.SelectMany(s => s.Difficulties)) l.Predict(estimates.GetValueOrDefault(d.BeatmapId), d.BeatmapId, d.StarRating, d.Bpm, d.TotalLengthSeconds); }),
            ("BuildProfile reference", () => ReferencePpTargetPatternModel.BuildProfile(replays, analyses, contexts, now, localSets: sets)),
            ("BuildProfile optimised", () => PpTargetPatternModel.BuildProfile(replays, analyses, contexts, now, localSets: sets)),
        ];
        double[] best = cases.Select(_ => double.MaxValue).ToArray();
        for (int round = 0; round < 6; round++)
            for (int i = 0; i < cases.Length; i++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                cases[i].Run();
                if (round > 0) best[i] = Math.Min(best[i], watch.Elapsed.TotalMilliseconds);
            }
        for (int i = 0; i < cases.Length; i++)
            TestContext.Out.WriteLine($"TIMING {cases[i].Name}: {best[i]:N1} ms");
    }

    private static void assertSame(PpTargetRankingResult expected, PpTargetRankingResult actual, string message)
    {
        Assert.That(actual.FlattenedDifficultyCount, Is.EqualTo(expected.FlattenedDifficultyCount), message);
        Assert.That(actual.MatchingDifficultyCount, Is.EqualTo(expected.MatchingDifficultyCount), message);
        Assert.That(actual.Profile, Is.SameAs(expected.Profile), message);
        Assert.That(actual.Candidates, Has.Count.EqualTo(expected.Candidates.Count), message);
        for (int i = 0; i < expected.Candidates.Count; i++)
            Assert.That(serialise(actual.Candidates[i]), Is.EqualTo(serialise(expected.Candidates[i])), $"{message}, candidate {i}");
    }

    private static string serialise<T>(T value) => JsonSerializer.Serialize(value, json);

    private static string content(PpPatternProfile profile) =>
        serialise(new { profile.ReferenceTime, profile.RecencyDays, profile.Evidence, profile.ScoreEvidence, profile.SessionForm });

    private static LocalReplay toggleLegacy(LocalReplay replay) => replay.LegacyScore || replay.Origin == LocalLibraryOrigin.Stable
        ? replay with { LegacyScore = false, Origin = LocalLibraryOrigin.Lazer }
        : replay with { LegacyScore = true };

    private static LocalReplay[] replace(LocalReplay[] replays, int index, Func<LocalReplay, LocalReplay> change) =>
        replays.Select((r, i) => i == index ? change(r) : r).ToArray();

    private static Guid id(int kind, int number) => new(number, (short)kind, 0, new byte[8]);

    private static OfficialBeatmapSet[] catalogue(Random random, int count)
    {
        int idRange = Math.Max(1, count * 2);
        return Enumerable.Range(0, count).Select(setIndex =>
        {
            int setId = random.Next(4) == 0 ? random.Next(1, count + 1) : setIndex + 1;
            var difficulties = Enumerable.Range(0, random.Next(1, 5)).Select(_ =>
            {
                int roll = random.Next(40);
                double stars = roll == 0 ? double.NaN : roll == 1 ? 0 : Math.Round(1.5 + random.NextDouble() * 8, random.Next(1, 3));
                return new OfficialBeatmapDifficulty(random.Next(roll == 2 ? -3 : 1, idRange), $"{words[random.Next(words.Length)]} {random.Next(4)}",
                    roll == 3 ? "taiko" : "osu", stars, roll == 4 ? double.NaN : Math.Round(120 + random.NextDouble() * 140), random.Next(roll == 5 ? -5 : 30, 420),
                    4, 9, 8, 6, 1_000, 500, random.Next(3) == 0 ? null : random.Next(100, 2000));
            }).ToArray();
            string title = $"{words[random.Next(words.Length)]} {words[random.Next(words.Length)]}";
            return new OfficialBeatmapSet(setId, title, title, $"Artist {random.Next(6)}", "", $"Mapper {random.Next(6)}",
                random.Next(3) == 0 ? "" : $"Source {random.Next(3)}", statuses[random.Next(statuses.Length)], null, null,
                1_000, 100, false, false, null, null, null, null, difficulties);
        }).ToArray();
    }

    private static PpTargetPreferenceProfile syntheticProfile(Random random, int[] ids, int attempts = 600)
    {
        double star() => Math.Round(2 + random.NextDouble() * 7, random.Next(1, 3));
        var samples = new List<PpTargetPerformanceSample>();
        int sampleCount = random.Next(5) == 0 ? random.Next(0, 3) : random.Next(20, 400);
        for (int i = 0; i < sampleCount; i++)
        {
            int roll = random.Next(30);
            samples.Add(new(roll == 0 ? double.NaN : roll == 1 ? -1 : star(), roll == 2 ? double.NaN : roll == 3 ? 0 : Math.Round(50 + random.NextDouble() * 350, random.Next(3)),
                roll == 4 ? 1.4 : roll == 5 ? double.NaN : Math.Round(.85 + random.NextDouble() * .15, 2)));
            if (random.Next(6) == 0) samples.Add(samples[^1]);
        }
        PpTargetPreference[] pref(string prefix, int count) => Enumerable.Range(0, count)
            .Select(i => new PpTargetPreference($"{prefix} {i}", random.Next(1, 20), Math.Round(random.NextDouble(), 3))).ToArray();
        PpTargetPreference[] mods = new[] { "HD", "DT", "HR", "NC", "EZ", "Hidden" }.Where(_ => random.Next(2) == 0)
            .Select(m => new PpTargetPreference(m, random.Next(1, 20), Math.Round(random.NextDouble(), 3))).ToArray();
        string[][] setups = [[], ["HD"], ["DT", "HD"]];
        string?[] jsons = [null, "", null, "[{\"acronym\":\"DT\",\"settings\":{}}]"];
        bool legacy = random.Next(4) == 0;
        IReadOnlyList<string>? preferred = random.Next(4) == 0 ? null : setups[random.Next(setups.Length)];

        var scores = new List<PpScoreSkillEvidence>();
        for (int i = 0; i < random.Next(0, 500); i++)
        {
            scores.Add(new(id(1, i), $"map-{random.Next(80)}", new[] { "", "HD", "DT+HD", "HR" }[random.Next(4)], now.AddHours(-random.Next(700)),
                random.Next(25) == 0 ? double.NaN : star(), Math.Round(.8 + random.NextDouble() * .2, 3),
                random.Next(12) == 0 ? 0 : random.Next(25) == 0 ? double.PositiveInfinity : Math.Round(random.NextDouble(), 4), random.Next(4) == 0 ? !legacy : legacy));
            if (random.Next(8) == 0) scores.Add(scores[^1] with { ScoreId = id(1, 10_000 + i) });
        }

        var passSamples = new List<PpTargetPassSample>();
        var evidence = new List<PpPatternEvidence>();
        string setupMods(IReadOnlyList<string>? m) => string.Join(',', m ?? []);
        for (int i = 0; i < attempts; i++)
        {
            int roll = random.Next(40);
            string attemptMods = random.Next(3) == 0 ? new[] { "", "HD", "DT,HD", "NF", "HR" }[random.Next(5)] : setupMods(preferred);
            passSamples.Add(new(random.Next(5) == 0 ? 0 : ids.Length == 0 ? 1 : ids[random.Next(ids.Length)], now.AddHours(-random.Next(roll == 0 ? 2000 : 700)),
                roll == 1 ? double.NaN : star(), roll == 2 ? null : roll == 3 ? double.NaN : Math.Round(120 + random.NextDouble() * 140),
                roll == 4 ? null : roll == 5 ? 0 : random.Next(30, 420), attemptMods, random.Next(3) != 0,
                random.Next(6) == 0 ? id(2, random.Next(40)) : null, roll == 6 ? null : Math.Round(.8 + random.NextDouble() * .2, 3),
                random.Next(10) == 0 ? "[{\"acronym\":\"DT\",\"settings\":{\"speed_change\":1.3}}]" : "",
                id(3, i), random.Next(8) == 0 ? null : Math.Round(random.NextDouble() * 400, 2), random.Next(2) == 0, random.Next(5) == 0 ? !legacy : legacy));
        }
        // Retry sessions the learning model can use.
        for (int map = 1; map <= 5; map++) for (int session = 0; session < 2; session++) for (int i = 0; i < 4; i++)
        {
            Guid scoreId = id(4, map * 100 + session * 10 + i);
            DateTimeOffset time = now.AddDays(-6 + session).AddHours(map).AddMinutes(i * 3);
            double pp = new[] { 0, 80, 100, 140 }[(i + map) % 4];
            passSamples.Add(new(ids.Length == 0 ? map : ids[(map * 7) % ids.Length], time, 5, 180, 120, setupMods(preferred), pp > 0,
                Accuracy: .95, ModsJson: "", LocalScoreId: scoreId, Pp: pp, LocalAttempt: true, LegacyScore: legacy));
            evidence.Add(new(scoreId, $"map-{map}", string.Join('+', preferred ?? []), time, jumps, 1, new Dictionary<string, PpPatternOutcome>()));
        }

        var bestPlays = Enumerable.Range(0, random.Next(0, 150)).Select(_ => new PpTargetBestPlay(ids.Length == 0 ? 1 : ids[random.Next(ids.Length)],
            random.Next(3) == 0 ? Math.Round(random.NextDouble() * 400) : Math.Round(random.NextDouble() * 400, 3))).ToArray();
        var patterns = new PpPatternProfile("pattern-a", now, 30, evidence, random.Next(5) == 0 ? null : scores,
            new PpSessionForm(now.AddMinutes(30), []));
        return new PpTargetPreferenceProfile(
            samples.Count, samples.Count, samples.Count,
            random.Next(4) == 0 ? null : new PpTargetRange(4, 6), random.Next(4) == 0 ? null : new PpTargetRange(160, 200),
            random.Next(4) == 0 ? null : new PpTargetRange(90, 240), random.Next(4) == 0 ? null : .96,
            random.Next(4) == 0 ? null : 150, 300, (PpTargetConfidence)random.Next(4), mods,
            pref("Mapper", 3), pref("Source", 2), pref("Artist", 3),
            words.Where(_ => random.Next(2) == 0).Select(w => new PpTargetPreference(w.ToLowerInvariant(), 3, Math.Round(random.NextDouble(), 3))).ToArray(),
            samples, patterns, preferred, new PpTargetOpportunityProfile(now, bestPlays, passSamples),
            jsons[random.Next(jsons.Length)], legacy);
    }

    private static Dictionary<int, PpTargetEstimate> syntheticEstimates(Random random, int[] ids, PpTargetPreferenceProfile profile)
    {
        var estimates = new Dictionary<int, PpTargetEstimate>();
        foreach (int beatmap in ids.Where(_ => random.Next(3) != 0))
        {
            PpPatternPrediction? prediction = random.Next(3) switch
            {
                0 => null,
                1 => new(Math.Round(random.NextDouble(), 3), Math.Round(.85 + random.NextDouble() * .15, 3), Math.Round(random.NextDouble(), 3), [], [],
                    [new("Overall", Math.Round(random.NextDouble(), 3), .95, .5, 3)]),
                _ => new(null, null, Math.Round(random.NextDouble() * .5, 3), [], [],
                    [new("Overall", random.Next(2) == 0 ? null : Math.Round(random.NextDouble(), 3), null, .2, 1), new("Jumps", null, null, 0, 0)]),
            };
            double expected = random.Next(20) == 0 ? double.NaN : Math.Round(20 + random.NextDouble() * 400, 2);
            PpOutcomeEstimate? outcome = random.Next(3) == 0 ? null : new PpOutcomeEstimate(
                new PpOutcomeDistribution(Math.Round(random.NextDouble() * 4, 2), random.Next(2) == 0 ? 1.5 : PpOutcomeDistribution.PoissonShape,
                    .97, .01, .95, 1, Math.Round(random.NextDouble() * 20, 1), random.Next(1, 8), random.Next(1, 30), random.Next(4) == 0, random.Next(6) == 0, .9, -.012),
                [], [new(expected * .7, .25), new(expected * .95, .45), new(expected * 1.25, .3)], 2, random.Next(2) == 0 ? 1 : .93, random.Next(3));
            estimates[beatmap] = new PpTargetEstimate(expected, random.Next(20) == 0 ? 0 : expected * 1.4, new(expected * .8, expected * 1.2), random.Next(30),
                (PpTargetConfidence)random.Next(4), "synthetic",
                random.Next(12) == 0 ? beatmap + 1 : random.Next(2) == 0 ? beatmap : null,
                random.Next(3) switch { 0 => null, 1 => profile.PreferredModSetup ?? [], _ => ["HR"] },
                random.Next(10) == 0 ? profile.TypicalAccuracy : null, null, prediction,
                random.Next(3) switch { 0 => null, 1 => "pattern-a", _ => "stale" },
                random.Next(8) == 0 ? "[]" : profile.PreferredModsJson,
                random.Next(2) == 0 ? jumps : null, random.Next(8) == 0 ? !profile.LegacyScore : profile.LegacyScore, outcome);
        }
        return estimates;
    }

    private static (LocalReplay[], Dictionary<Guid, ReplayAnalysisResult>, Dictionary<Guid, PpPatternContext>, LocalBeatmapSet[]) patternHistory(Random random, int count)
    {
        var difficulties = Enumerable.Range(0, 20).Select(i => new LocalBeatmapDifficulty(id(5, i), i, $"Diff {i}", "osu", 5, 180, 90_000,
            (float)Math.Round(3 + random.NextDouble() * 3, 1), 9, 8, 5, 1, random.Next(4) == 0 ? "" : $"hash{i:D4}")).ToArray();
        LocalBeatmapSet[] sets = [new(id(6, 1), 1, "Synthetic", "Artist", "Mapper", "", now, null, difficulties, 1)];
        var replays = new List<LocalReplay>();
        var analyses = new Dictionary<Guid, ReplayAnalysisResult>();
        var contexts = new Dictionary<Guid, PpPatternContext>();
        string[][] mods = [[], ["HD"], ["DT"], ["HD", "DT"], ["RX"], ["Hidden"]];
        for (int i = 0; i < count; i++)
        {
            var difficulty = difficulties[random.Next(difficulties.Length)];
            int roll = random.Next(30);
            DateTimeOffset played = roll == 0 ? now.AddDays(-40) : roll == 1 ? now.AddHours(1) : random.Next(3) == 0
                ? now.AddMinutes(-random.Next(1, 150)) : now.AddHours(-random.Next(1, 700));
            var replay = new LocalReplay(id(7, i), id(6, 1), difficulty.BeatmapId, "Synthetic", "Artist", difficulty.Name, roll == 2 ? "taiko" : "osu",
                "Synthetic player", played, roll == 3 ? double.NaN : Math.Round(3 + random.NextDouble() * 4, 2), Math.Round(.85 + random.NextDouble() * .15, 4),
                roll == 4 ? 0 : 1_000_000, 500, random.Next(5), random.Next(3) == 0 ? null : 150, mods[random.Next(mods.Length)], true,
                random.Next(3) == 0 ? difficulty.BeatmapHash.ToUpperInvariant() : "",
                ModsJson: random.Next(10) == 0 ? "[{\"acronym\":\"CL\",\"settings\":{\"circle_size\":8}}]" : "",
                OnlineScoreId: random.Next(6) == 0 ? random.Next(1, 5) : 0, Origin: random.Next(5) == 0 ? LocalLibraryOrigin.Stable : LocalLibraryOrigin.Lazer,
                Passed: roll != 5, LegacyScore: random.Next(6) == 0);
            replays.Add(replay);
            if (random.Next(4) != 0)
                analyses[replay.ScoreId] = analysis(random);
            if (random.Next(3) != 0)
                contexts[replay.ScoreId] = new(random.Next(5) == 0 ? null : Math.Round(25 + random.NextDouble() * 20, 2), random.Next(5) == 0 ? null : random.Next(4) == 0 ? 1.5 : 1);
            if (random.Next(10) == 0)
                replays.Add(replay with { PlayedAt = replay.PlayedAt.AddMinutes(-5) });
        }
        return (replays.ToArray(), analyses, contexts, sets);
    }

    private static ReplayAnalysisResult analysis(Random random)
    {
        int count = random.Next(2, 80);
        double spacing = random.Next(2) == 0 ? 20 : 180;
        double interval = random.Next(2) == 0 ? 110 : 300;
        double? rate = random.Next(3) == 0 ? null : 1;
        ReplayMissReason[] reasons = Enum.GetValues<ReplayMissReason>();
        var judgements = new List<ReplayObjectJudgement>();
        for (int i = 0; i < count; i++)
        {
            double time = 1_000 + i * interval + random.Next(-10, 10);
            var position = new ReplayPoint((float)(100 + (i % 2 == 0 ? 0 : spacing) + random.Next(-15, 15)), (float)(150 + random.Next(-40, 40)));
            bool miss = random.Next(12) == 0;
            string result = miss ? "Miss" : new[] { "Great", "Great", "Great", "Ok", "Meh" }[random.Next(5)];
            judgements.Add(new(i + (i > count / 2 ? 1 : 0), null, random.Next(5) == 0 ? "SliderHeadCircle" : "HitCircle", time, time, result, "Great", time, 0, rate,
                position, null, i, i + 1, miss && random.Next(2) == 0 ? new ReplayMissAnalysis(reasons[random.Next(reasons.Length)], 32, 80, 0, new(0, 0), null, null, null, 80, false, false, false, 0) : null));
        }
        return new("synthetic", "officialRulesetPlayback", true, 0, [], judgements, ReplayJudgementSummary.Empty);
    }
}
