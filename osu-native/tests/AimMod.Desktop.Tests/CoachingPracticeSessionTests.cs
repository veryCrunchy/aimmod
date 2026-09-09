using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class CoachingPracticeSessionTests
{
    private static readonly DateTimeOffset epoch = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static PracticeAttempt attempt(int minute, bool original = false, double accuracy = .95,
        PracticeBreakdownVariant variant = PracticeBreakdownVariant.Original, string group = "section-a",
        string setup = "lazer / No mods / ", bool assisted = false, bool passed = true) =>
        new(Guid.NewGuid(), 0, epoch.AddMinutes(minute), accuracy, passed ? 0 : 4, passed, setup,
            original ? "Original map" : variant.ToString(), original, assisted, variant, group, 1000, 6000);

    private static PracticeSetProgress set(params PracticeAttempt[] attempts) => new(
        new("set-a", "Practice song", "Hard", PracticeDrillType.LongJumps, epoch, 1000, 6000, 60000, 6, 120,
            Tracking: new("Practice Player", 123, 42, "source-hash", Guid.Empty, "", false, [],
                [new("Compact", PracticeDrillType.LongJumps, "compact-hash", "compact-md5", 1000, 6000, true,
                    PracticeBreakdownVariant.ReducedMovement, "section-a"),
                 new("Aim", PracticeDrillType.LongJumps, "aim-hash", "aim-md5", 1000, 6000, true,
                    PracticeBreakdownVariant.AimFocus, "section-a", "RX")])), new(attempts));

    private static LocalReplay replay(int minute, string hash = "source-hash", string[]? mods = null) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Practice song", "Artist", "Hard", "osu", "Practice Player",
        epoch.AddMinutes(minute), 4, .95, 100000, 100, 0, null, mods ?? [], true, BeatmapHash: hash,
        OnlineBeatmapId: hash == "source-hash" ? 42 : 43);

    [Test]
    public void OptionalChecksDoNotBecomeTheRequiredNextStep()
    {
        var source = set(Enumerable.Range(1, 3).Select(i => attempt(i, variant: PracticeBreakdownVariant.ReducedMovement))
            .Concat(Enumerable.Range(4, 2).Select(i => attempt(i, variant: PracticeBreakdownVariant.CombinedEasier)))
            .Concat(Enumerable.Range(6, 3).Select(i => attempt(i, original: true))).ToArray());
        var review = CoachingPracticeSessionPlanner.Build(source, epoch.AddHours(1));
        Assert.Multiple(() =>
        {
            Assert.That(review.NextStage, Is.Null);
            Assert.That(review.Stages.Single(s => s.Stage == CoachingPracticeStage.Transfer).IsComplete, Is.False);
            Assert.That(review.Stages.Single(s => s.Stage == CoachingPracticeStage.Retention).IsComplete, Is.False);
        });
    }

    [Test]
    public void MissingStartingPointDoesNotTrapAPlayerWhoAlreadyPractised()
    {
        var source = set(attempt(1, variant: PracticeBreakdownVariant.ReducedMovement));
        var review = CoachingPracticeSessionPlanner.Build(source, epoch.AddHours(1));
        Assert.Multiple(() =>
        {
            Assert.That(review.NextStage, Is.EqualTo(CoachingPracticeStage.Isolate));
            Assert.That(review.Stages[0].Completed, Is.Zero);
            Assert.That(review.Stages[0].IsComplete, Is.False);
            Assert.That(review.Stages[0].Skipped, Is.False);
            Assert.That(review.Comparisons.Single(c => c.Label == "Original map").AccuracyDifference, Is.Null);
        });
        Assert.That(CoachingPracticeSessionPlanner.Build(set(), epoch).NextStage, Is.EqualTo(CoachingPracticeStage.Baseline));
    }

    [Test]
    public void MissingEvidenceHasNullMetricsAndSkippedStagesDoNotBecomeSuccesses()
    {
        var source = set();
        var state = CoachingPracticeSessionPlanner.Skip(CoachingPracticeSessionPlanner.Start(source, epoch), CoachingPracticeStage.Baseline);
        var review = CoachingPracticeSessionPlanner.Build(source, epoch.AddHours(1), state);
        Assert.Multiple(() =>
        {
            Assert.That(review.NextStage, Is.EqualTo(CoachingPracticeStage.Isolate));
            Assert.That(review.Stages[0].Skipped, Is.True);
            Assert.That(review.Stages[0].Completed, Is.Zero);
            Assert.That(review.Stages[0].IsComplete, Is.False);
            Assert.That(review.Comparisons.All(c => c.AccuracyDifference is null), Is.True);
        });
    }

    [Test]
    public void ComparisonRequiresRepeatedSameSectionSameSetupAndNeverUsesAssistedAccuracy()
    {
        var compact = Enumerable.Range(1, 3).Select(i => attempt(i, accuracy: .98, variant: PracticeBreakdownVariant.ReducedMovement)).ToArray();
        var wrong = Enumerable.Range(4, 3).Select(i => attempt(i, accuracy: 1, setup: "lazer / DT / DT"));
        var aided = Enumerable.Range(7, 3).Select(i => attempt(i, accuracy: 1, assisted: true));
        var review = CoachingPracticeSessionPlanner.Build(set(compact.Concat(wrong).Concat(aided).ToArray()), epoch.AddHours(1));
        Assert.That(review.Comparisons.First().AccuracyDifference, Is.Null);
        var originals = Enumerable.Range(10, 3).Select(i => attempt(i, accuracy: .92)).ToArray();
        review = CoachingPracticeSessionPlanner.Build(set(compact.Concat(originals).ToArray()), epoch.AddHours(1));
        Assert.Multiple(() =>
        {
            Assert.That(review.Comparisons.First().AccuracyDifference, Is.EqualTo(-.06).Within(.0001));
            Assert.That(review.Comparisons.First().Detail, Does.Contain("attempt order"));
            Assert.That(review.Comparisons.First().FirstCount, Is.EqualTo(3));
            Assert.That(review.Comparisons.First().SecondCount, Is.EqualTo(3));
        });
        review = CoachingPracticeSessionPlanner.Build(set(compact.Concat(originals.Select(a => a with { SourceEndMs = 7000 })).ToArray()), epoch.AddHours(1));
        Assert.That(review.Comparisons.First().AccuracyDifference, Is.Null);
    }

    [Test]
    public void FailedDuplicateAndFutureScoresCannotCompleteAStageOrFabricateZeroMetrics()
    {
        var failed = attempt(1, variant: PracticeBreakdownVariant.ReducedMovement, passed: false);
        var source = set(failed, failed, attempt(100, variant: PracticeBreakdownVariant.ReducedMovement),
            attempt(3, variant: PracticeBreakdownVariant.ReducedMovement, accuracy: double.NaN));
        var review = CoachingPracticeSessionPlanner.Build(source, epoch.AddMinutes(10));
        Assert.Multiple(() =>
        {
            Assert.That(review.Cohorts.Single().Attempts, Is.EqualTo(1));
            Assert.That(review.Cohorts.Single().Failed, Is.EqualTo(1));
            Assert.That(review.Cohorts.Single().MedianAccuracy, Is.Null);
            Assert.That(review.Stages.Single(s => s.Stage == CoachingPracticeStage.Isolate).IsComplete, Is.False);
        });
    }

    [Test]
    public void OtherSectionsDoNotCompleteCurrentCombinedStage()
    {
        var source = set(attempt(1, variant: PracticeBreakdownVariant.ReducedMovement),
            attempt(2, variant: PracticeBreakdownVariant.ReducedMovement),
            attempt(2, variant: PracticeBreakdownVariant.ReducedMovement),
            attempt(3, variant: PracticeBreakdownVariant.CombinedEasier, group: "other"),
            attempt(4, variant: PracticeBreakdownVariant.CombinedEasier, group: "other"));
        var review = CoachingPracticeSessionPlanner.Build(source, epoch.AddHours(1), breakdownGroupId: "section-a");
        Assert.That(review.Stages.Single(s => s.Stage == CoachingPracticeStage.Isolate).IsComplete, Is.True);
        Assert.That(review.Stages.Single(s => s.Stage == CoachingPracticeStage.Combine).Completed, Is.Zero);
    }

    [Test]
    public void FullMapAndSectionResultsRemainSeparate()
    {
        var source = set(Enumerable.Range(1, 3).Select(i => attempt(i)).ToArray());
        var review = CoachingPracticeSessionPlanner.Build(source, epoch.AddHours(1));
        Assert.That(review.Stages.Single(s => s.Stage == CoachingPracticeStage.Original).Completed, Is.Zero);
        Assert.That(review.Comparisons.Single(c => c.Label == "Original map").SecondCount, Is.Zero);
    }

    [Test]
    public void BaselineGrowsUntilPracticeStartsAndKnownRevisionDifferencesAreExcluded()
    {
        var originals = Enumerable.Range(1, 3).Select(i => attempt(i, original: true)).ToArray();
        var source = set(originals);
        var review = CoachingPracticeSessionPlanner.Build(source, epoch.AddHours(1));
        Assert.That(review.Stages[0].IsComplete, Is.True);
        Assert.That(review.Stages.Single(s => s.Stage == CoachingPracticeStage.Original).Completed, Is.Zero);
        source = set(originals.Append(attempt(4)).Append(attempt(5, original: true)).ToArray());
        review = CoachingPracticeSessionPlanner.Build(source, epoch.AddHours(1));
        Assert.That(review.Stages[0].Completed, Is.EqualTo(3));
        Assert.That(review.Stages.Single(s => s.Stage == CoachingPracticeStage.Original).Completed, Is.EqualTo(1));
        Assert.That(PracticeProgressTracker.SameSource(replay(2, "edited-hash") with { OnlineBeatmapId = 42 }, source.Map.Tracking!), Is.False);
    }

    [Test]
    public void AimFocusAcceptsOnlyRelaxAndPersistsAssistanceWithoutPollutingOriginalResults()
    {
        var source = set();
        var accepted = replay(1, "aim-hash", ["RX"]);
        var rows = new[] { accepted, replay(2, "aim-hash"), replay(3, "aim-hash", ["RX", "AT"]),
            replay(4, "source-hash", ["RX"]), replay(5, "compact-hash", ["RX"]) };
        var progress = PracticeProgressTracker.Reconcile(source.Map, PracticeProgress.Empty, rows, 123);
        Assert.Multiple(() =>
        {
            Assert.That(progress.Attempts, Has.Count.EqualTo(1));
            Assert.That(progress.Attempts.Single().Assisted, Is.True);
            Assert.That(progress.Attempts.Single().BreakdownVariant, Is.EqualTo(PracticeBreakdownVariant.AimFocus));
            Assert.That(progress.Attempts.Single().BreakdownGroupId, Is.EqualTo("section-a"));
        });
    }

    [Test]
    public void TransferRequiresExplicitDifferentMapAndIgnoresWrongModsPlayerOrAccount()
    {
        var source = set(attempt(1));
        var session = CoachingPracticeSessionPlanner.Start(source, epoch);
        Assert.Throws<ArgumentException>(() => CoachingPracticeSessionPlanner.StartCheck(session, source, CoachingPracticeStage.Transfer, replay(2), [], epoch.AddMinutes(10)));
        var other = replay(-1, "other-hash");
        session = CoachingPracticeSessionPlanner.StartCheck(session, source, CoachingPracticeStage.Transfer, other, [other], epoch.AddMinutes(10));
        var good = replay(11, "other-hash");
        var wrongMods = replay(12, "other-hash", ["HD"]);
        var wrongPlayer = replay(13, "other-hash") with { Player = "Another Player" };
        var unrelated = replay(14, "unrelated") with { OnlineBeatmapId = 99 };
        Assert.That(CoachingPracticeSessionPlanner.Reconcile(session, source, [good], 999, epoch.AddHours(1)).Checks.Single().Attempts, Is.Empty);
        session = CoachingPracticeSessionPlanner.Reconcile(session, source, [good, wrongMods, wrongPlayer, unrelated], 123, epoch.AddHours(1));
        Assert.That(session.Checks.Single().Attempts.Select(a => a.ScoreId), Is.EqualTo(new[] { good.ScoreId }));
    }

    [Test]
    public void RetentionNeedsExplicitNewSessionAndStopsWhenPracticeResumes()
    {
        var source = set(attempt(1));
        var session = CoachingPracticeSessionPlanner.Start(source, epoch);
        Assert.Throws<ArgumentException>(() => CoachingPracticeSessionPlanner.StartCheck(session, source, CoachingPracticeStage.Retention,
            replay(-1), [], epoch.AddMinutes(10)));
        session = CoachingPracticeSessionPlanner.StartCheck(session, source, CoachingPracticeStage.Retention, replay(-1), [], epoch.AddHours(2));
        source = source with { Progress = new(source.Progress.Attempts.Append(attempt(123)).ToArray()) };
        var cold = replay(121); var warm = replay(124);
        session = CoachingPracticeSessionPlanner.Reconcile(session, source, [cold, warm], 123, epoch.AddHours(3));
        Assert.That(session.Checks.Single().Attempts.Select(a => a.ScoreId), Is.EqualTo(new[] { cold.ScoreId }));
    }

    [Test]
    public void OtherMapAndTrainerWarmupsInvalidateLaterColdChecks()
    {
        var source = set(attempt(1));
        var session = CoachingPracticeSessionPlanner.Start(source, epoch);
        Assert.Throws<ArgumentException>(() => CoachingPracticeSessionPlanner.StartCheck(session, source, CoachingPracticeStage.Retention,
            replay(-1), [], epoch.AddHours(2), [epoch.AddMinutes(119)]));
        session = CoachingPracticeSessionPlanner.StartCheck(session, source, CoachingPracticeStage.Retention, replay(-1), [], epoch.AddHours(2));
        var warm = replay(122);
        var trained = CoachingPracticeSessionPlanner.Reconcile(session, source, [warm], 123, epoch.AddHours(3), [epoch.AddMinutes(121)]);
        Assert.That(trained.Checks.Single().Attempts, Is.Empty);
        var otherMap = CoachingPracticeSessionPlanner.Reconcile(session, source, [replay(121, "other-hash"), warm], 123, epoch.AddHours(3));
        Assert.That(otherMap.Checks.Single().Attempts, Is.Empty);
    }

    [Test]
    public void StoreRoundTripsSkippedAndBoundChecksAndHandlesMalformedData()
    {
        string directory = Directory.CreateTempSubdirectory("aimmod-guided-test-").FullName;
        string path = Path.Combine(directory, "sessions.json");
        try
        {
            var source = set(attempt(1));
            var state = CoachingPracticeSessionPlanner.Start(source, epoch);
            state = CoachingPracticeSessionPlanner.Skip(state, CoachingPracticeStage.Isolate);
            state = CoachingPracticeSessionPlanner.StartCheck(state, source, CoachingPracticeStage.Transfer, replay(-1, "other"), [], epoch.AddHours(1));
            new CoachingPracticeSessionStore(path).Save(state);
            var restored = new CoachingPracticeSessionStore(path).Load(source.Map.Id)!;
            Assert.That(restored.Skipped, Does.Contain(CoachingPracticeStage.Isolate));
            Assert.That(restored.Checks.Single().Target.SourceHash, Is.EqualTo("other"));
            File.WriteAllText(path, "[{\"PracticeSetId\":\"set-a\",\"Checks\":null}]");
            Assert.That(new CoachingPracticeSessionStore(path).Load(source.Map.Id), Is.Null);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task EmbeddedAttemptPersistsAtomicallyAndCannotBecomeAnOriginalMapScore()
    {
        string directory = Directory.CreateTempSubdirectory("aimmod-practice-record-").FullName;
        try
        {
            var source = set();
            var saved = source.Map with { Id = Guid.NewGuid().ToString("N") };
            string folder = Path.Combine(directory, saved.Id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "AimMod practice.osz"), "package");
            var library = new PracticeMapLibrary(directory);
            library.Save(saved);
            var row = attempt(1, variant: PracticeBreakdownVariant.ReducedMovement) with { Difficulty = "Compact" };
            await Task.WhenAll(library.RecordAttemptAsync(saved.Id, row), library.RecordAttemptAsync(saved.Id, row));
            var restored = new PracticeMapLibrary(directory).LoadProgress(saved.Id);
            Assert.That(restored.Attempts, Has.Count.EqualTo(1));
            Assert.That(restored.Attempts.Single().Original, Is.False);
            Assert.ThrowsAsync<ArgumentException>(async () => await library.RecordAttemptAsync(saved.Id, row with { Original = true }));
            Assert.ThrowsAsync<ArgumentException>(async () => await library.RecordAttemptAsync(saved.Id, row with { BreakdownGroupId = "another-section" }));
            Assert.ThrowsAsync<ArgumentException>(async () => await library.RecordAttemptAsync(saved.Id, row with { Assisted = true }));
        }
        finally { Directory.Delete(directory, true); }
    }
}
