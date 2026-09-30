using AimMod.Desktop.Practice;
using NUnit.Framework;

namespace AimMod.Desktop.Tests.Practice;

[TestFixture]
public sealed class PracticeBreakdownTests
{
    private string root = null!;
    private PracticeSourceBeatmap source = null!;
    private PracticeSourceSection section = null!;

    [SetUp]
    public void Setup()
    {
        root = Directory.CreateTempSubdirectory("aimmod-breakdown-").FullName;
        string path = Path.Combine(root, "source.osu");
        File.WriteAllText(path, """
            osu file format v14
            [General]
            AudioFilename:audio.ogg
            Mode:0
            [Metadata]
            Title:Practice fixture
            Artist:Fixture artist
            Creator:Fixture mapper
            Version:Mixed section
            [Difficulty]
            CircleSize:4
            OverallDifficulty:7
            ApproachRate:8
            SliderMultiplier:1.4
            SliderTickRate:1
            [TimingPoints]
            0,500,4,1,0,100,1,0
            1400,-50,4,1,0,100,0,0
            [HitObjects]
            40,80,1000,1,0,0:0:0:0:
            440,300,1500,1,0,0:0:0:0:
            60,100,2000,2,0,B|180:100|220:240,2,280
            450,320,3500,1,0,0:0:0:0:
            50,70,4000,1,0,0:0:0:0:
            """);
        File.WriteAllText(Path.Combine(root, "audio.ogg"), "audio fixture");
        source = OsuPracticeBeatmapReader.Read(path);
        section = new(PracticeDrillType.Mixed, 0, 4, 1000, 4000, 0, [], source.HitObjects);
    }

    [TearDown] public void Teardown()
    {
        Directory.Delete(root, true);
        if (Directory.Exists(root + "-package")) Directory.Delete(root + "-package", true);
    }

    [Test]
    public void FourVariantsShareSectionAndControlTheComparison()
    {
        var plans = PracticeBreakdownPlanner.CreatePlans(source, section, new(PracticeDrillType.Mixed));
        var original = plans.Single(p => p.BreakdownVariant == PracticeBreakdownVariant.Original);
        var compact = plans.Single(p => p.BreakdownVariant == PracticeBreakdownVariant.ReducedMovement);
        var aim = plans.Single(p => p.BreakdownVariant == PracticeBreakdownVariant.AimFocus);
        var combined = plans.Single(p => p.BreakdownVariant == PracticeBreakdownVariant.CombinedEasier);
        Assert.Multiple(() =>
        {
            Assert.That(plans.Select(p => p.BreakdownVariant).Distinct().Count(), Is.EqualTo(4));
            Assert.That(plans.Select(p => p.BreakdownGroupId).Distinct().Count(), Is.EqualTo(1));
            Assert.That(compact.AudioSlice, Is.EqualTo(original.AudioSlice));
            Assert.That(compact.HitObjects.Select(o => (o.StartTimeMs, o.EndTimeMs, o.Type)), Is.EqualTo(original.HitObjects.Select(o => (o.StartTimeMs, o.EndTimeMs, o.Type))));
            Assert.That(compact.HitObjects[1].X - compact.HitObjects[0].X, Is.EqualTo(280));
            Assert.That(aim.HitObjects, Is.EqualTo(original.HitObjects));
            Assert.That(aim.RequiredMods, Is.EqualTo("RX"));
            Assert.That(plans.Where(p => p != aim).All(p => p.RequiredMods.Length == 0), Is.True);
            Assert.That(combined.AudioSlice.PlaybackRate, Is.EqualTo(.85));
            Assert.That(original.HitObjects[0].StartTimeMs, Is.GreaterThanOrEqualTo(4000));
            Assert.That(source.HitObjects[0].X, Is.EqualTo(40), "Source data stays unchanged.");
        });
    }

    [Test]
    public void ScaledSliderRetainsItsDurationAndRepeatsWhenReparsed()
    {
        var compact = PracticeBreakdownPlanner.CreatePlans(source, section, new(PracticeDrillType.Mixed))[0];
        string path = Path.Combine(root, "compact.osu");
        File.WriteAllText(path, PracticeMapExporter.Serialize(source, compact));
        var decoded = OsuPracticeBeatmapReader.Read(path);
        var expected = compact.HitObjects.First(o => o.IsSlider);
        var actual = decoded.HitObjects.First(o => o.IsSlider);
        Assert.Multiple(() =>
        {
            Assert.That(actual.EndTimeMs - actual.StartTimeMs, Is.EqualTo(expected.EndTimeMs - expected.StartTimeMs).Within(.01));
            Assert.That(actual.Fields[6], Is.EqualTo("2"));
            Assert.That(actual.Fields[7], Is.EqualTo("196"));
            Assert.That(actual.Fields[5], Is.EqualTo("B|203:128|231:226"));
        });
    }

    [Test]
    public void ReducedMovementLeavesModeratelySpacedCirclesReadable()
    {
        string path = source.SourcePath;
        File.WriteAllText(path, File.ReadAllText(path)
            .Replace("40,80,1000", "180,192,1000")
            .Replace("440,300,1500", "290,192,1500"));
        var readableSource = OsuPracticeBeatmapReader.Read(path);
        var readableSection = section with { HitObjects = readableSource.HitObjects };
        var compact = PracticeBreakdownPlanner.CreatePlans(readableSource, readableSection, new(PracticeDrillType.Mixed))[0];
        double spacing = compact.HitObjects[1].X - compact.HitObjects[0].X;
        Assert.Multiple(() =>
        {
            // These CS4 circles are about 73 pixels across. The previous spacing
            // transform turned this ordinary 110-pixel gap into a 44-pixel overlap.
            Assert.That(spacing, Is.GreaterThanOrEqualTo(76));
            Assert.That(spacing, Is.LessThan(110), "Cursor travel should still be reduced.");
            Assert.That(compact.DifficultyOverrides?.ContainsKey("CircleSize") ?? false, Is.False);
        });
    }

    [Test]
    public void AtMinimumRateCombinedStillReducesMovement()
    {
        var plans = PracticeBreakdownPlanner.CreatePlans(source, section, new(PracticeDrillType.Mixed, PlaybackRate: .75));
        var combined = plans.Single(p => p.BreakdownVariant == PracticeBreakdownVariant.CombinedEasier);
        var original = plans.Single(p => p.BreakdownVariant == PracticeBreakdownVariant.Original);
        Assert.That(combined.AudioSlice.PlaybackRate, Is.EqualTo(.75));
        Assert.That(combined.HitObjects[1].X - combined.HitObjects[0].X, Is.LessThan(original.HitObjects[1].X - original.HitObjects[0].X));
    }

    [Test]
    public async Task PackagePreservesVariantIdentityAndNeverOverwritesSource()
    {
        var plans = PracticeBreakdownPlanner.CreatePlans(source, section, new(PracticeDrillType.Mixed));
        string output = root + "-package";
        var artifact = await new PracticeSetArtifactBuilder(new Slicer()).BuildAsync(source, plans, output);
        Assert.That(artifact.Difficulties.Select(d => d.BreakdownVariant), Is.EqualTo(plans.Select(p => p.BreakdownVariant)));
        Assert.That(artifact.Difficulties.Select(d => d.Sha256).Distinct().Count(), Is.EqualTo(4));
        Assert.That(artifact.Difficulties.Single(d => d.BreakdownVariant == PracticeBreakdownVariant.AimFocus).RequiredMods, Is.EqualTo("RX"));
        using var archive = System.IO.Compression.ZipFile.OpenRead(artifact.ArchivePath);
        Assert.That(archive.Entries.Count, Is.EqualTo(8));
        Assert.That(OsuPracticeBeatmapReader.Read(source.SourcePath).HitObjects[0].X, Is.EqualTo(40));
    }

    [Test]
    public void CancellationCleansOnlyNewOutput()
    {
        string output = Path.Combine(root, "cancelled");
        var plans = PracticeBreakdownPlanner.CreatePlans(source, section, new(PracticeDrillType.Mixed));
        Assert.ThrowsAsync<OperationCanceledException>(async () => await new PracticeSetArtifactBuilder(new Slicer()).BuildAsync(source, plans, output, token: new CancellationToken(true)));
        Assert.That(Directory.Exists(output), Is.False);
        Assert.That(File.Exists(source.SourcePath), Is.True);
    }

    private sealed class Slicer : IPracticeAudioSlicer
    {
        public Task SliceAsync(PracticeAudioSliceRequest request, string destinationPath, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            return File.WriteAllTextAsync(destinationPath, "audio fixture", cancellationToken);
        }
    }
}
