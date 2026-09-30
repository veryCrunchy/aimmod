using AimMod.Desktop.Home;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Trainers;
using AimMod.Desktop.Updates;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class HomeDashboardModelTests
{
    private static readonly DateTimeOffset now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static LocalReplay play(double daysAgo, double accuracy, int misses = 2, double? pp = 80, bool passed = true, string player = "Player",
        string title = "Map", bool local = true, string[]? mods = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), title, "Artist", "Insane", "osu", player, now.AddDays(-daysAgo), 5, accuracy,
            500_000, 400, misses, pp, mods ?? [], true, IsLocallyStored: local, Passed: passed);

    private static HomeDashboardInputs inputs(IReadOnlyList<LocalReplay> runs, OsuProfile? profile = null) =>
        new(runs, profile, null, null, null, [], null);

    [Test]
    public void FormComparesWithThePreviousPeriodUsingFinishedPlays()
    {
        LocalReplay[] runs =
        [
            play(1, .97, misses: 1), play(2, .95, misses: 3), play(3, .50, passed: false, misses: 30),
            play(8, .93, misses: 5), play(10, .91, misses: 7),
        ];

        HomeFormSummary week = HomeDashboardBuilder.Summarise(runs, now, 7);

        Assert.Multiple(() =>
        {
            Assert.That(week.Plays, Is.EqualTo(3));
            Assert.That(week.PreviousPlays, Is.EqualTo(2));
            Assert.That(week.Accuracy, Is.EqualTo(.96).Within(1e-9), "A failed play must not drag the accuracy down.");
            Assert.That(week.PreviousAccuracy, Is.EqualTo(.92).Within(1e-9));
            Assert.That(week.MissesPerPlay, Is.EqualTo(2).Within(1e-9));
            Assert.That(week.Daily, Has.Count.EqualTo(7));
            Assert.That(week.Daily.Sum(day => day.Plays), Is.EqualTo(3));
        });
    }

    [Test]
    public void MissingPeriodsStayMissingInsteadOfZero()
    {
        HomeFormSummary week = HomeDashboardBuilder.Summarise([play(1, .97)], now, 7);

        Assert.Multiple(() =>
        {
            Assert.That(week.PreviousAccuracy, Is.Null);
            Assert.That(week.PreviousMissesPerPlay, Is.Null);
            Assert.That(week.Daily.Count(day => day.Accuracy is null), Is.EqualTo(6));
        });
    }

    [Test]
    public void DashboardKeepsOnlyTheAccountHoldersManualPlays()
    {
        var profile = new OsuProfile(1, "Player", "NL", null, null);
        LocalReplay[] runs = [play(1, .97), play(1, .99, player: "Someone else"), play(2, 1, mods: ["AT"]), play(3, .9, player: "", local: false)];

        HomeDashboardData data = HomeDashboardBuilder.Build(inputs(runs, profile), now);

        Assert.Multiple(() =>
        {
            Assert.That(data.TotalPlays, Is.EqualTo(2), "Downloaded replays and autoplay are not the player's form.");
            Assert.That(data.PlayerName, Is.EqualTo("Player"));
            Assert.That(data.RecentPlays[0].Accuracy, Is.EqualTo(.97));
        });
    }

    [Test]
    public void WithoutAnAccountTheMostFrequentLocalPlayerIsUsed() =>
        Assert.That(HomeDashboardBuilder.SelectPlayer([play(1, .9, player: "A"), play(2, .9, player: "B"), play(3, .9, player: "B")], null), Is.EqualTo("B"));

    [Test]
    public void EmptyHistoryRecommendsAFirstPlayAndAWarmUp()
    {
        HomeDashboardData data = HomeDashboardBuilder.Build(inputs([]), now);

        Assert.Multiple(() =>
        {
            Assert.That(data.HasPlays, Is.False);
            Assert.That(data.Recommendations.Select(r => r.Kind),
                Is.EqualTo(new[] { HomeRecommendationKind.PlayFirstMap, HomeRecommendationKind.WarmUp }));
        });
    }

    [Test]
    public void RepeatedMissesOnOneMapBecomeAPracticeRecommendation()
    {
        LocalReplay[] runs = [play(1, .9, misses: 9, title: "Hard"), play(2, .91, misses: 7, title: "Hard"), play(3, .98, misses: 0, title: "Easy"), play(4, .98, misses: 1, title: "Easy")];
        var guided = new TrainerGuidedPlan(Guid.NewGuid(), TrainerGuidedFocus.Spacing, new TrainerSettings(), new TrainerSettings(), 1);

        IReadOnlyList<HomeRecommendation> next = HomeDashboardBuilder.Recommend(new(runs, null, null, null, guided, [], null), runs, now);

        Assert.Multiple(() =>
        {
            Assert.That(next[0].Kind, Is.EqualTo(HomeRecommendationKind.PractiseMap));
            Assert.That(next[0].MapTitle, Is.EqualTo("Hard"));
            Assert.That(next.Select(r => r.Kind), Does.Contain(HomeRecommendationKind.FindPpTargets));
            Assert.That(next[^1].Kind, Is.EqualTo(HomeRecommendationKind.ResumeGuidedPractice));
            Assert.That(next[^1].Detail, Does.Contain("Step 2"));
        });
    }

    [Test]
    public void BestPpTargetIgnoresLowConfidenceEstimates()
    {
        var snapshot = OffscreenHomeCaptureTests.CreatePpTargets(now);
        HomeRecommendation? target = HomeDashboardBuilder.BestPpTarget(snapshot);
        var weak = snapshot with
        {
            ExactEstimates = snapshot.ExactEstimates.ToDictionary(pair => pair.Key, pair => pair.Value with { Confidence = AimMod.Desktop.PpTargets.PpTargetConfidence.Low }),
        };

        Assert.Multiple(() =>
        {
            Assert.That(target?.Value, Is.EqualTo("Kimi no Bouken [Extra]"));
            Assert.That(target?.Detail, Does.Contain("168 pp"));
            Assert.That(HomeDashboardBuilder.BestPpTarget(weak), Is.Null);
        });
    }

    [Test]
    public void ProfileChangeComparesWithAWeekOldSnapshot()
    {
        HomeProfileHistoryStore.Entry[] earlier =
        [
            new(now.AddDays(-20), 3_300, 52_000),
            new(now.AddDays(-8), 3_374, 48_825),
            new(now.AddDays(-2), 3_400, 48_300),
        ];

        HomeProfileChange? change = HomeProfileHistoryStore.Compare(earlier, new(now, 3_412.4, 48_213), now);

        Assert.Multiple(() =>
        {
            Assert.That(change?.PpDelta, Is.EqualTo(38.4).Within(1e-6));
            Assert.That(change?.RankDelta, Is.EqualTo(612), "Positive values are places gained.");
            Assert.That(change?.Since, Is.EqualTo(now.AddDays(-8)));
            Assert.That(HomeProfileHistoryStore.Compare([], new(now, 1, 1), now), Is.Null);
        });
    }

    [Test]
    public void ProfileStoreRecordsOneSnapshotPerDay()
    {
        string directory = Path.Combine(Path.GetTempPath(), "aimmod-home-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new HomeProfileHistoryStore(Path.Combine(directory, "history.json"));
            OsuProfile profile(double pp) => new(1, "Player", null, null, new OsuProfileStatistics(1000, 10, pp, 98, 1, 1, 1, 1, 1, 1));
            Assert.That(store.Record(profile(100), now.AddDays(-9)), Is.Null);
            store.Record(profile(110), now.AddMinutes(-5));
            HomeProfileChange? change = store.Record(profile(120), now);
            Assert.That(change?.PpDelta, Is.EqualTo(20).Within(1e-9), "Today's earlier snapshot is replaced, not compared with.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestCase(.97, .96, true, AimModHomeTrend.Better)]
    [TestCase(.95, .96, true, AimModHomeTrend.Worse)]
    [TestCase(3.0, 4.0, false, AimModHomeTrend.Better)]
    [TestCase(.9601, .96, true, AimModHomeTrend.Flat)]
    public void TrendColourFollowsWhetherTheChangeIsGood(double current, double previous, bool higherIsBetter, AimModHomeTrend expected) =>
        Assert.That(AimModHomeMetric.Classify(current, previous, .001, higherIsBetter), Is.EqualTo(expected));

    [Test]
    public void TrendWithoutAComparisonIsNone() =>
        Assert.That(AimModHomeMetric.Classify(.9, null, .001), Is.EqualTo(AimModHomeTrend.None));

    [Test]
    public void ChartRangeIsPaddedAndCappedAtOneHundredPercent()
    {
        (double low, double high) = AimModHomeTrendChart.ValueRange([.992, .995]);
        Assert.Multiple(() =>
        {
            Assert.That(high, Is.EqualTo(1).Within(1e-9));
            Assert.That(high - low, Is.GreaterThanOrEqualTo(.02 - 1e-9));
        });
    }

    [Test]
    public void UnavailableUpdatesAreExplainedWithoutAnAction()
    {
        var state = new NativeUpdateState(NativeUpdateStage.Unavailable, NativeUpdateChannel.Stable, "Updates unavailable", "Install AimMod.");
        var description = NativeUpdateSurface.Describe(state);
        Assert.Multiple(() =>
        {
            Assert.That(description.Title, Is.EqualTo("Automatic updates off"));
            Assert.That(description.Detail, Does.Contain("installer"));
            Assert.That(NativeUpdateSurface.ActionFor(state).Enabled, Is.False);
            Assert.That(NativeUpdateSurface.HeightFor(false, false), Is.LessThan(60), "The closed update status is a single row.");
            Assert.That(NativeUpdateSurface.HeightFor(true, true), Is.GreaterThan(NativeUpdateSurface.HeightFor(true, false)));
        });
    }

    [TestCase(NativeUpdateStage.Available, true)]
    [TestCase(NativeUpdateStage.ReadyToRestart, true)]
    [TestCase(NativeUpdateStage.Unavailable, false)]
    [TestCase(NativeUpdateStage.Current, false)]
    public void HeaderOffersOnlyActionableUpdates(NativeUpdateStage stage, bool shown) =>
        Assert.That(NativeHomeDashboard.UpdateBadge(new NativeUpdateState(stage, NativeUpdateChannel.Stable, "", "", "1.2.3")).Show, Is.EqualTo(shown));

    [Test]
    public void SubtitleSummarisesTodayWithoutRepeatingThePanels()
    {
        HomeDashboardData data = HomeDashboardBuilder.Build(inputs([play(.01, .97), play(.02, .95)]), now);
        Assert.That(NativeHomeDashboard.Subtitle(data, now), Does.StartWith("2 plays today"));
        Assert.That(NativeHomeDashboard.Subtitle(HomeDashboardBuilder.Build(inputs([]), now), now), Does.Contain("Connect osu!"));
    }
}
