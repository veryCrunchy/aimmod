using AimMod.Desktop.Practice;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests.Practice;

[TestFixture]
public sealed class PracticeMapPlannerTests
{
    private string directory = null!;

    [SetUp]
    public void SetUp() => directory = Directory.CreateTempSubdirectory("aimmod-practice-test-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(directory, true);

    [Test]
    public async Task PracticeSetPackagesEveryWeakSectionWithDistinctAudioAndHashes()
    {
        var source=read(map(objects:circleObjects(60,100,4))); write("audio.ogg","audio");
        var misses=new[]{analysis(miss(7,.9),miss(23,.9),miss(45,.8))};
        var plans=PracticeSetArtifactBuilder.Plan(source,misses,new(PracticeDrillType.Mixed),true);
        foreach(int index in new[]{7,23,45})
            Assert.That(plans.Any(plan=>plan.SourceSection.FirstObjectIndex<=index && plan.SourceSection.LastObjectIndex>=index),Is.True);
        string output=Directory.CreateTempSubdirectory("aimmod-set-test-").FullName;
        try
        {
            var artifact=await new PracticeSetArtifactBuilder(new CopyingAudioSlicer()).BuildAsync(source,plans,output);
            using var zip=System.IO.Compression.ZipFile.OpenRead(artifact.ArchivePath);
            Assert.That(zip.Entries.Count,Is.EqualTo(plans.Count*2));
            Assert.That(zip.Entries.Select(entry=>entry.FullName).Distinct().Count(),Is.EqualTo(zip.Entries.Count));
            Assert.That(artifact.Difficulties.Select(d=>d.Sha256).Distinct().Count(),Is.EqualTo(plans.Count));
            Assert.That(artifact.Plans.Select(plan=>plan.OutputSetTitle).Distinct().Count(),Is.EqualTo(1));
            foreach(var file in zip.Entries.Where(entry=>entry.Name.EndsWith(".osu")))
            {
                using var reader=new StreamReader(file.Open()); string content=reader.ReadToEnd();
                Assert.That(content,Does.Contain("BeatmapSetID:-1"));
                string audio=content.Split('\n').Single(line=>line.StartsWith("AudioFilename:")).Split(':',2)[1].Trim();
                Assert.That(zip.GetEntry(audio),Is.Not.Null);
            }
        }
        finally { Directory.Delete(output,true); }
    }

    [Test]
    public void PatternPracticeWithoutAnalysisDoesNotInventMissEvidence()
    {
        var source = read(map(objects: circleObjects(60, 100, 20)));
        Assert.That(PracticeMapPlanner.FindSections(source, [], new(PracticeDrillType.Mixed)), Is.Empty);
        var sections = PracticeMapPlanner.FindSections(source, [], new(PracticeDrillType.Mixed, AllowPatternPractice: true));
        Assert.That(sections, Has.Count.EqualTo(3));
        Assert.That(sections.All(section => section.WeakObjects.Count == 0 && section.WeaknessScore == 0), Is.True);
        var plan = PracticeMapPlanner.CreatePlans(source, [], new(PracticeDrillType.Mixed,
            FirstObjectIndex: sections[1].FirstObjectIndex, AllowPatternPractice: true)).Single();
        Assert.That(plan.SourceSection.FirstObjectIndex, Is.EqualTo(21), "Three original notes lead into the selected phrase.");
        Assert.That(plan.SourceSection.LastObjectIndex, Is.EqualTo(47));
    }

    [Test]
    public void TimingDrillKeepsLeadInWithoutInventingMisses()
    {
        var source = read(map(objects: circleObjects(14, 100, 20)));
        var notes = source.HitObjects.Select((o, i) => new ReplayObjectJudgement(i, null, "HitCircle",
            o.StartTimeMs, o.EndTimeMs, "Great", "Great", o.StartTimeMs - i * 8, -i * 8, 1, null, null, i, i + 1)).ToArray();
        var result = analysis(notes);
        var plan = PracticeMapPlanner.CreatePlans(source, [result], new(PracticeDrillType.Mixed, PlaybackRate: .75)).First();
        Assert.Multiple(() => {
            Assert.That(plan.SourceSection.WeakObjects, Is.Empty);
            Assert.That(plan.SourceSection.FirstObjectIndex, Is.Zero);
            Assert.That(plan.HitObjects[0].StartTimeMs, Is.GreaterThan(0));
            Assert.That(plan.AudioSlice.PlaybackRate, Is.EqualTo(.75));
        });
    }

    [Test]
    public void ReaderAcceptsStandardAndRejectsOtherRulesets()
    {
        PracticeSourceBeatmap source = read(map(mode: 0, objects: circleObjects(8, 100, 20)));
        Assert.Multiple(() =>
        {
            Assert.That(source.Metadata.Title, Is.EqualTo("Source Song"));
            Assert.That(source.HitObjects, Has.Count.EqualTo(8));
            Assert.That(source.TimingPoints, Has.Count.EqualTo(3));
        });

        string mania = write("mania.osu", map(mode: 3, objects: circleObjects(8, 100, 20)));
        Assert.That(() => OsuPracticeBeatmapReader.Read(mania), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void BuildsStreamDrillFromRepeatedMissOnRealSourcePattern()
    {
        PracticeSourceBeatmap source = read(map(objects: circleObjects(14, 100, 20)));
        ReplayAnalysisResult[] analyses = [analysis(miss(6, 0.9)), analysis(miss(6, 0.8)), analysis(miss(7, 0.7))];

        PracticeMapPlan plan = PracticeMapPlanner.CreatePlans(source, analyses,
            new PracticeMapOptions(PracticeDrillType.Streams, 1, 5, 5))[0];

        Assert.Multiple(() =>
        {
            Assert.That(plan.DrillType, Is.EqualTo(PracticeDrillType.Streams));
            Assert.That(plan.SourceSection.WeakObjects.Single(item => item.ObjectIndex == 6).MissRate, Is.EqualTo(2d / 3).Within(0.001));
            Assert.That(plan.HitObjects[0].StartTimeMs, Is.EqualTo(4000).Within(0.001));
            Assert.That(plan.AudioLeadInMs, Is.EqualTo(1500).Within(0.001));
            Assert.That(plan.HitObjects.Take(plan.SourceSection.HitObjects.Count).Select(item => (item.X, item.Y)),
                Is.EqualTo(plan.SourceSection.HitObjects.Select(item => (item.X, item.Y))));
            Assert.That(plan.RepeatCount, Is.GreaterThanOrEqualTo(6));
            Assert.That(plan.AudioSlice.OutputDurationMs, Is.GreaterThanOrEqualTo(60_000));
            Assert.That(plan.Attribution, Does.Contain("mapped by Mapper"));
        });
    }

    [Test]
    public void RepeatsObjectsTimingAndPaddedAudioOnOneCycleClock()
    {
        PracticeSourceBeatmap source = read(map(objects: circleObjects(10, 100, 20)));
        PracticeMapPlan plan = PracticeMapPlanner.CreatePlans(
            source,
            new[] { analysis(miss(5, 0.9)) },
            new PracticeMapOptions(PracticeDrillType.Streams, MaximumSections: 1))[0];
        int objectsPerRound = plan.SourceSection.HitObjects.Count;
        double cycle = plan.AudioSlice.CycleDurationMs;

        Assert.Multiple(() =>
        {
            Assert.That(plan.RepeatCount, Is.InRange(6, 12));
            Assert.That(plan.HitObjects, Has.Count.EqualTo(objectsPerRound * plan.RepeatCount));
            Assert.That(plan.AudioSlice.RepeatCount, Is.EqualTo(plan.RepeatCount));
            Assert.That(plan.HitObjects[objectsPerRound].StartTimeMs - plan.HitObjects[0].StartTimeMs,
                Is.EqualTo(cycle).Within(0.001));
            Assert.That((plan.HitObjects[objectsPerRound].Type & 4) != 0, Is.True, "Each round should begin a new combo.");
            Assert.That(plan.AudioLeadInMs + plan.AudioSlice.OutputDurationMs - plan.HitObjects[^1].EndTimeMs,
                Is.EqualTo(2_500).Within(0.001), "The final round should retain recovery audio after its last object.");
        });
    }

    [Test]
    public void BuildsLongJumpDrillOnlyWhenSourceContainsLongJumps()
    {
        string objects = string.Join('\n', Enumerable.Range(0, 12).Select(index =>
            $"{(index % 2 == 0 ? 40 : 460)},192,{10000 + index * 260},1,0,0:0:0:0:"));
        PracticeSourceBeatmap source = read(map(objects: objects));

        IReadOnlyList<PracticeMapPlan> plans = PracticeMapPlanner.CreatePlans(source,
            new[] { analysis(miss(5, 0.9)), analysis(miss(5, 0.9)) },
            new PracticeMapOptions(PracticeDrillType.LongJumps, 1, 4, 4));

        Assert.That(plans, Has.Count.EqualTo(1));
        Assert.That(plans[0].DrillType, Is.EqualTo(PracticeDrillType.LongJumps));
    }

    [Test]
    public void DoesNotInventRequestedPatternWhenSourceDoesNotContainIt()
    {
        PracticeSourceBeatmap source = read(map(objects: circleObjects(12, 400, 10)));
        IReadOnlyList<PracticeMapPlan> plans = PracticeMapPlanner.CreatePlans(source,
            new[] { analysis(miss(5, 0.9)) }, new PracticeMapOptions(PracticeDrillType.Streams));
        Assert.That(plans, Is.Empty);
    }

    [Test]
    public void JumpPracticeRepeatsThreeOriginalRunUpNotesBeforeTheSelectedContext()
    {
        string objects = string.Join('\n', Enumerable.Range(0, 30).Select(index =>
            $"{(index % 2 == 0 ? 40 : 460)},192,{10000 + index * 260},1,0,0:0:0:0:"));
        var source = read(map(objects: objects));
        var options = new PracticeMapOptions(PracticeDrillType.LongJumps, 1, 2, 4);
        var section = PracticeMapPlanner.FindSections(source, [analysis(miss(12, .9))], options).Single();
        var plan = PracticeMapPlanner.CreatePlans(source, [analysis(miss(12, .9))], options with { FirstObjectIndex = section.FirstObjectIndex }).Single();
        Assert.That(section.FirstObjectIndex, Is.EqualTo(7));
        Assert.That(section.WeakObjects.Single().ObjectIndex, Is.EqualTo(12));
        int count = section.HitObjects.Count;
        for (int round = 0; round < plan.RepeatCount; round++)
        {
            var actual = plan.HitObjects.Skip(round * count).Take(count).ToArray();
            Assert.That(actual.Select(o => (o.SourceIndex, o.X, o.Y)), Is.EqualTo(source.HitObjects.Skip(7).Take(count).Select(o => (o.SourceIndex, o.X, o.Y))));
            Assert.That(actual[5].StartTimeMs - actual[0].StartTimeMs, Is.EqualTo(5 * 260));
            Assert.That(actual[0].StartTimeMs, Is.EqualTo(plan.AudioLeadInMs + (section.SourceStartTimeMs - plan.AudioSlice.SourceStartTimeMs) + round * plan.AudioSlice.CycleDurationMs).Within(.01));
        }
    }

    [TestCase(.75, 2)]
    [TestCase(1, 3)]
    [TestCase(1.25, 3)]
    public void ExtraPlayableLeadUpNeverExceedsOneAndAHalfSecondsAtTheSelectedSpeed(double rate, int count)
    {
        var source = read(map(objects: circleObjects(25, 500, 10)));
        var plan = PracticeMapPlanner.CreatePlans(source, [analysis(miss(10, .9))],
            new(PracticeDrillType.Mixed, 1, 0, 4, PlaybackRate: rate)).Single();
        Assert.That(plan.SourceSection.FirstObjectIndex, Is.EqualTo(10 - count));
        Assert.That(plan.HitObjects[count].StartTimeMs - plan.HitObjects[0].StartTimeMs, Is.LessThanOrEqualTo(1500));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void LeadUpDoesNotReachAcrossABreakOrSpinner(bool spinner)
    {
        string objects = string.Join('\n', Enumerable.Range(0, 25).Select(index =>
            spinner && index == 9 ? "256,192,12340,8,0,12500,0:0:0:0:"
                : $"256,192,{10000 + index * 260 + (!spinner && index >= 10 ? 5000 : 0)},1,0,0:0:0:0:"));
        var source = read(map(objects: objects));
        var plan = PracticeMapPlanner.CreatePlans(source, [analysis(miss(10, .9))], new(PracticeDrillType.Mixed, 1, 0, 4)).Single();
        Assert.That(plan.SourceSection.FirstObjectIndex, Is.EqualTo(10));
    }

    [Test]
    public void OpeningSectionDoesNotInventNotesBeforeTheSong()
    {
        var source = read(map(objects: circleObjects(25, 250, 10)));
        var plan = PracticeMapPlanner.CreatePlans(source, [analysis(miss(1, .9))], new(PracticeDrillType.Mixed, 1, 0, 4)).Single();
        Assert.That(plan.SourceSection.FirstObjectIndex, Is.Zero);
        Assert.That(plan.SourceSection.HitObjects[0], Is.EqualTo(source.HitObjects[0]));
    }

    [Test]
    public void CarriesActiveTimingPointsAndShiftsSpinnerEndTime()
    {
        string objects = string.Join('\n', new[]
        {
            "256,192,10000,1,0,0:0:0:0:",
            "256,192,10500,8,0,11500,0:0:0:0:",
            "300,192,12000,1,0,0:0:0:0:",
        });
        PracticeSourceBeatmap source = read(map(objects: objects));
        PracticeMapPlan plan = PracticeMapPlanner.CreatePlans(source,
            new[] { analysis(miss(1, 0.9)) }, new PracticeMapOptions(PracticeDrillType.Mixed, 1, 1, 1))[0];

        Assert.Multiple(() =>
        {
            Assert.That(plan.TimingPoints.Any(point => point.Uninherited), Is.True);
            Assert.That(plan.TimingPoints.Any(point => !point.Uninherited), Is.True);
            Assert.That(plan.HitObjects[1].EndTimeMs - plan.HitObjects[1].StartTimeMs, Is.EqualTo(1000));
            Assert.That(double.Parse(plan.HitObjects[1].Fields[5], System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(plan.HitObjects[1].EndTimeMs));
        });
    }

    [Test]
    public void UsesOfficialSliderEndTimeForRecoveryAndRepetition()
    {
        string objects = string.Join('\n', new[]
        {
            "256,192,10000,1,0,0:0:0:0:",
            "256,192,10500,2,0,B|356:192,2,280",
            "300,192,13500,1,0,0:0:0:0:",
        });
        PracticeSourceBeatmap source = read(map(objects: objects));

        Assert.That(source.HitObjects[1].EndTimeMs, Is.GreaterThan(source.HitObjects[1].StartTimeMs));
        PracticeMapPlan plan = PracticeMapPlanner.CreatePlans(source,
            new[] { analysis(miss(1, 0.9)) }, new PracticeMapOptions(PracticeDrillType.Mixed, 1, 1, 1))[0];
        Assert.That(plan.AudioLeadInMs + plan.AudioSlice.OutputDurationMs - plan.HitObjects[^1].EndTimeMs,
            Is.GreaterThanOrEqualTo(2_500).Within(1));
    }

    [Test]
    public async Task ExportRequiresSlicedAudioAndNeverMutatesSource()
    {
        PracticeSourceBeatmap source = read(map(objects: circleObjects(10, 100, 20)));
        await File.WriteAllBytesAsync(Path.Combine(directory, "audio.ogg"), [1, 2, 3]);
        byte[] original = await File.ReadAllBytesAsync(source.SourcePath);
        PracticeMapPlan plan = PracticeMapPlanner.CreatePlans(source,
            new[] { analysis(miss(5, 0.9)) }, new PracticeMapOptions(PracticeDrillType.Streams))[0];
        string output = Directory.CreateTempSubdirectory("aimmod-practice-export-").FullName;

        try
        {
            PracticeMapExportResult result = await new PracticeMapExporter().ExportAsync(source, plan, output, new CopyingAudioSlicer());
            string exported = await File.ReadAllTextAsync(result.BeatmapPath);
            PracticeSourceBeatmap decodedExport = OsuPracticeBeatmapReader.Read(result.BeatmapPath);
            byte[] sourceAfterExport = await File.ReadAllBytesAsync(source.SourcePath);

            Assert.Multiple(() =>
            {
                Assert.That(sourceAfterExport, Is.EqualTo(original));
                Assert.That(exported, Does.Contain("AudioFilename:practice-audio.ogg"));
                Assert.That(exported, Does.Contain("AudioLeadIn:0"));
                Assert.That(exported, Does.Contain("Source:Practice drill derived from Artist - Source Song [Original], mapped by Mapper."));
                Assert.That(exported, Does.Contain("BeatmapID:0"));
                Assert.That(exported, Does.Contain("\n2,"), "Recovery gaps should be represented as osu! breaks.");
                Assert.That(exported, Does.Not.Contain("0,0,\"background.jpg\""));
                Assert.That(decodedExport.HitObjects, Has.Count.EqualTo(plan.HitObjects.Count),
                    "osu!'s decoder should accept every generated repetition.");
            });
        }
        finally
        {
            Directory.Delete(output, true);
        }
    }

    [Test]
    public void ExportRefusesOriginalDirectoryAndCleansFailedAudio()
    {
        PracticeSourceBeatmap source = read(map(objects: circleObjects(10, 100, 20)));
        PracticeMapPlan plan = PracticeMapPlanner.CreatePlans(source,
            new[] { analysis(miss(5, 0.9)) }, new PracticeMapOptions(PracticeDrillType.Streams))[0];
        var exporter = new PracticeMapExporter();

        Assert.That(async () => await exporter.ExportAsync(source, plan, directory, new CopyingAudioSlicer()),
            Throws.TypeOf<InvalidOperationException>());
        string output = Directory.CreateTempSubdirectory("aimmod-practice-failed-").FullName;
        try
        {
            Assert.That(async () => await exporter.ExportAsync(source, plan, output, new EmptyAudioSlicer()),
                Throws.TypeOf<InvalidDataException>());
            Assert.That(Directory.GetFiles(output), Is.Empty);
        }
        finally
        {
            Directory.Delete(output, true);
        }
    }

    [Test]
    public void ArtifactBuilderRemovesPartialOutputWhenAudioPreparationIsCancelled()
    {
        PracticeSourceBeatmap source = read(map(objects: circleObjects(10, 100, 20)));
        PracticeMapPlan plan = PracticeMapPlanner.CreatePlans(source,
            new[] { analysis(miss(5, 0.9)) }, new PracticeMapOptions(PracticeDrillType.Streams))[0];
        string output = Path.Combine(Path.GetTempPath(), $"aimmod-practice-cancelled-{Guid.NewGuid():N}");

        Assert.That(async () => await new PracticeMapArtifactBuilder(new CancellingAudioSlicer()).BuildAsync(
            source, plan, output), Throws.TypeOf<OperationCanceledException>());
        Assert.That(Directory.Exists(output), Is.False);
    }

    [Test]
    public void SeparateTriplesAreBurstsNotAStream()
    {
        string objects = string.Join('\n', Enumerable.Range(0, 15).Select(i =>
            $"{150 + i % 3 * 20},192,{10000 + i / 3 * 1000 + i % 3 * 100},1,0,0:0:0:0:"));
        PracticeSourceBeatmap source = read(map(objects: objects));
        var attempts = new[] { analysis(miss(7, .9)) };
        Assert.That(PracticeMapPlanner.FindSections(source, attempts, new(PracticeDrillType.Streams)), Is.Empty);
        Assert.That(PracticeMapPlanner.FindSections(source, attempts, new(PracticeDrillType.Bursts)), Is.Not.Empty);
    }

    [Test]
    public void FastSliderHeadsDoNotBecomeAStream()
    {
        string objects = string.Join('\n', Enumerable.Range(0, 12).Select(i =>
            $"{150 + i % 3 * 20},192,{10000 + i * 100},2,0,L|180:192,1,10"));
        PracticeSourceBeatmap source = read(map(objects: objects));
        var attempts = new[] { analysis(miss(5, .9)) };
        Assert.That(PracticeMapPlanner.FindSections(source, attempts, new(PracticeDrillType.Streams)), Is.Empty);
        Assert.That(PracticeMapPlanner.FindSections(source, attempts, new(PracticeDrillType.SliderControl)), Is.Not.Empty);
    }

    [TestCase(.75)]
    [TestCase(1.2)]
    public void SpeedScalesNotesTimingAndAudioTogether(double rate)
    {
        PracticeSourceBeatmap source = read(map(objects: circleObjects(14, 100, 20)));
        PracticeMapPlan plan = PracticeMapPlanner.CreatePlans(source, [analysis(miss(6, .9))],
            new(PracticeDrillType.Streams, PlaybackRate: rate))[0];
        Assert.Multiple(() =>
        {
            Assert.That(plan.HitObjects[0].StartTimeMs, Is.EqualTo(4000).Within(.001));
            Assert.That(plan.HitObjects[1].StartTimeMs - plan.HitObjects[0].StartTimeMs, Is.EqualTo(100 / rate).Within(.001));
            Assert.That(plan.HitObjects[plan.SourceSection.HitObjects.Count].StartTimeMs - plan.HitObjects[0].StartTimeMs,
                Is.EqualTo(plan.AudioSlice.CycleDurationMs).Within(.001));
            Assert.That(plan.AudioSlice.InitialSilenceMs, Is.EqualTo(plan.AudioLeadInMs));
            Assert.That(plan.AudioLeadInMs + plan.AudioSlice.OutputDurationMs - plan.HitObjects[^1].EndTimeMs, Is.EqualTo(2500).Within(.001));
            Assert.That(plan.AudioSlice.PlaybackRate, Is.EqualTo(rate));
        });
        PracticeSourceBeatmap decoded = read(PracticeMapExporter.Serialize(source, plan));
        Assert.That(decoded.HitObjects.Count, Is.EqualTo(plan.HitObjects.Count));
    }

    [Test]
    public void SelectedSectionIsNotReplacedByHighestPrioritySection()
    {
        PracticeSourceBeatmap source = read(map(objects: circleObjects(90, 100, 20)));
        var attempts = new[] { analysis(miss(6, .9), miss(65, .8)) };
        var sections = PracticeMapPlanner.FindSections(source, attempts, new(PracticeDrillType.Mixed));
        Assert.That(sections.Count, Is.GreaterThan(1));
        PracticeMapPlan plan = PracticeMapPlanner.CreatePlans(source, attempts,
            new(PracticeDrillType.Mixed, FirstObjectIndex: sections[1].FirstObjectIndex))[0];
        Assert.That(plan.SourceSection.FirstObjectIndex, Is.EqualTo(sections[1].FirstObjectIndex));
        Assert.That(PracticeMapPlanner.CreatePlans(source, attempts, new(PracticeDrillType.Mixed, FirstObjectIndex: 9999)), Is.Empty);
    }

    private PracticeSourceBeatmap read(string content) => OsuPracticeBeatmapReader.Read(write("source.osu", content));
    private string write(string name, string content)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string map(int mode = 0, string? objects = null) => $$"""
        osu file format v14

        [General]
        AudioFilename: audio.ogg
        Mode: {{mode}}

        [Editor]
        DistanceSpacing:1

        [Metadata]
        Title:Source Song
        Artist:Artist
        Creator:Mapper
        Version:Original
        Source:Original source
        BeatmapID:123
        BeatmapSetID:456

        [Difficulty]
        HPDrainRate:6
        CircleSize:4
        OverallDifficulty:8
        ApproachRate:9
        SliderMultiplier:1.4
        SliderTickRate:1

        [Events]
        0,0,"background.jpg",0,0

        [TimingPoints]
        0,500,4,2,1,50,1,0
        5000,-100,4,2,1,50,0,0
        11000,400,4,2,1,50,1,0

        [Colours]
        Combo1 : 255,0,0

        [HitObjects]
        {{objects ?? circleObjects(10, 100, 20)}}
        """;

    private static string circleObjects(int count, int interval, int step) => string.Join('\n', Enumerable.Range(0, count).Select(index =>
        $"{100 + index * step},192,{10000 + index * interval},1,0,0:0:0:0:"));

    private static ReplayObjectJudgement miss(int index, double confidence) => new(index, null, "HitCircle", 10000 + index * 100, 10000 + index * 100,
        "Miss", "Great", 10000 + index * 100, 0, 1, new ReplayPoint(256, 192), new ReplayPoint(300, 192), 0, 0,
        new ReplayMissAnalysis(ReplayMissReason.Undershoot, 32, 44, 0, new ReplayPoint(300, 192), null, null, null, 44,
            false, false, false, -1, Confidence: confidence));

    private static ReplayAnalysisResult analysis(params ReplayObjectJudgement[] judgements) => new(
        ReplayAnalysisProtocol.EngineVersion, "official", true, ReplayAnalysisProtocol.WallClockTimeoutMs,
        Array.Empty<int>(), judgements, new ReplayJudgementSummary(0, 0, 0, judgements.Length, 0, 0));

    private sealed class CopyingAudioSlicer : IPracticeAudioSlicer
    {
        public Task SliceAsync(PracticeAudioSliceRequest request, string destinationPath, CancellationToken cancellationToken = default)
        {
            File.Copy(request.SourceAudioPath, destinationPath);
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyAudioSlicer : IPracticeAudioSlicer
    {
        public Task SliceAsync(PracticeAudioSliceRequest request, string destinationPath, CancellationToken cancellationToken = default)
        {
            File.WriteAllBytes(destinationPath, []);
            return Task.CompletedTask;
        }
    }

    private sealed class CancellingAudioSlicer : IPracticeAudioSlicer
    {
        public Task SliceAsync(PracticeAudioSliceRequest request, string destinationPath, CancellationToken cancellationToken = default)
        {
            File.WriteAllBytes(destinationPath, new byte[128]);
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
