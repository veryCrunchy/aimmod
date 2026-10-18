using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

public sealed class SectionPpServiceTests
{
    private const int objects = 120;
    private string directory = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "aimmod-section-pp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "map"));
        File.WriteAllText(Path.Combine(directory, "map", "map.osu"), beatmap());
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, true);

    [Test]
    public async Task OsusCalculatorPutsTheLostPpOnTheSectionWithTheMiss()
    {
        var run = replay(Guid.NewGuid(), missAt: 70, okAt: [10, 11]);
        var earlier = replay(Guid.NewGuid(), missAt: null, okAt: [10, 11, 80, 81, 82]) with { PlayedAt = run.PlayedAt.AddDays(-1) };
        var analyses = new Dictionary<Guid, ReplayAnalysisResult> { [run.ScoreId] = analysis(70, [10, 11]), [earlier.ScoreId] = analysis(null, [10, 11, 80, 81, 82]) };
        var service = new SectionPpService(new SectionLedgerStore(Path.Combine(directory, "ledger.json")),
            (_, _) => Task.FromResult<IBeatmapSourceLease>(new Lease(Path.Combine(directory, "map", "map.osu"))),
            () => SidecarRuntimeClient.Start(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "AimMod.exe" : "AimMod")));

        var report = await service.CalculateAsync(run, analyses, [earlier], CancellationToken.None);

        Assert.That(report, Is.Not.Null);
        Assert.That(report!.Sections, Has.Count.EqualTo(2));
        var first = report.Sections[0];
        var second = report.Sections[1];
        Assert.Multiple(() =>
        {
            Assert.That(report.Pp, Is.GreaterThan(0));
            Assert.That(report.Calculator, Does.StartWith(PpCalculationProtocol.EngineVersion));
            Assert.That(second.Tally.Miss, Is.EqualTo(1));
            Assert.That(second.LostToMisses, Is.GreaterThan(first.LostToHits!.Value), "a miss in the middle costs more than two 100s");
            Assert.That(report.Costliest(5).First().Section.Index, Is.EqualTo(1));
            Assert.That(second.Best!.ScoreId, Is.EqualTo(earlier.ScoreId), "the earlier play had no miss there");
            Assert.That(second.GainFromBest, Is.GreaterThan(0));
            Assert.That(report.SumOfBestPp, Is.GreaterThan(report.Pp!.Value));
            Assert.That(report.CountedPlays, Is.EqualTo(2));
            Assert.That(report.FullComboOkGoal, Is.GreaterThan(2));
        });
    }

    [Test]
    public async Task AFailedPlayIsNotGivenPpButStillCountsItsFinishedSections()
    {
        var run = replay(Guid.NewGuid(), missAt: null, okAt: []) with { Passed = false };
        var judgements = analysis(null, []);
        var analyses = new Dictionary<Guid, ReplayAnalysisResult> { [run.ScoreId] = judgements with { Judgements = judgements.Judgements.Take(80).ToArray() } };
        var service = new SectionPpService(new SectionLedgerStore(Path.Combine(directory, "ledger.json")),
            (_, _) => Task.FromResult<IBeatmapSourceLease>(new Lease(Path.Combine(directory, "map", "map.osu"))),
            () => throw new AssertionException("a failed play needs no pp calculation"));

        var report = await service.CalculateAsync(run, analyses, [], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(report!.Pp, Is.Null);
            Assert.That(report.Sections[0].Best, Is.Not.Null);
            Assert.That(report.Sections[1].Best, Is.Null);
        });
    }

    private static LocalReplay replay(Guid id, int? missAt, int[] okAt)
    {
        int ok = okAt.Length, miss = missAt is null ? 0 : 1;
        double accuracy = (300.0 * (objects - ok - miss) + 100.0 * ok) / (300.0 * objects);
        return new LocalReplay(id, Guid.Empty, Guid.Empty, "Synthetic", "AimMod", "Test", "osu", "player",
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), 5, accuracy, 1_000_000, missAt ?? objects, miss, null, [], true,
            BeatmapHash: "synthetic", HitStatistics: new PpScoreStatistics(objects - ok - miss, ok, 0, miss, 0, 0));
    }

    private static ReplayAnalysisResult analysis(int? missAt, int[] okAt)
    {
        var judgements = new List<ReplayObjectJudgement>();
        int combo = 0;
        for (int i = 0; i < objects; i++)
        {
            string result = i == missAt ? "Miss" : okAt.Contains(i) ? "Ok" : "Great";
            int after = result == "Miss" ? 0 : combo + 1;
            double time = 1_000 + i * 180;
            judgements.Add(new(i, null, "HitCircle", time, time, result, "Great", time, 0, 1, null, null, combo, after));
            combo = after;
        }
        return new ReplayAnalysisResult(ReplayAnalysisProtocol.EngineVersion, "map", true, 0, [], judgements, new ReplayJudgementSummary(0, 0, 0, 0, 0, 0));
    }

    /// <summary>120 circles with a new combo every eight, so the map splits into two practice sections.</summary>
    private static string beatmap() => "osu file format v14\n\n[General]\nAudioFilename: audio.mp3\nMode: 0\n\n[Metadata]\nTitle:Section Test\nArtist:AimMod\nCreator:AimMod\nVersion:Test\n\n"
        + "[Difficulty]\nHPDrainRate:6\nCircleSize:4\nOverallDifficulty:8\nApproachRate:9\nSliderMultiplier:1.4\nSliderTickRate:1\n\n[TimingPoints]\n0,500,4,2,1,50,1,0\n\n[HitObjects]\n"
        + string.Join('\n', Enumerable.Range(0, objects).Select(i => $"{(i % 2 == 0 ? 64 : 448)},{(i % 4 < 2 ? 64 : 320)},{1000 + i * 180},{(i % 8 == 0 ? 5 : 1)},0,0:0:0:0:")) + "\n";

    private sealed class Lease(string path) : IBeatmapSourceLease
    {
        public string BeatmapPath => path;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
