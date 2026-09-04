using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Practice;

public static class PracticeMapPlanner
{
    private const double stream_interval_ms = 160;
    private const double jump_distance = 170;

    public static IReadOnlyList<PracticeMapPlan> CreatePlans(
        PracticeSourceBeatmap beatmap,
        IEnumerable<ReplayAnalysisResult> analyses,
        PracticeMapOptions options)
    {
        ArgumentNullException.ThrowIfNull(beatmap);
        ArgumentNullException.ThrowIfNull(analyses);
        ArgumentNullException.ThrowIfNull(options);
        PracticeMapOptions safe = options.Normalised();
        ReplayAnalysisResult[] attempts = analyses.Where(result => result.Judgements is not null).ToArray();
        if (attempts.Length == 0)
            return Array.Empty<PracticeMapPlan>();

        PracticeWeakObject[] weaknesses = aggregateWeaknesses(beatmap, attempts);
        var candidates = new List<PracticeSourceSection>();
        foreach (PracticeWeakObject weakness in weaknesses)
        {
            int first = Math.Max(0, weakness.ObjectIndex - safe.ContextObjectsBefore);
            int last = Math.Min(beatmap.HitObjects.Count - 1, weakness.ObjectIndex + safe.ContextObjectsAfter);
            IReadOnlyList<PracticeHitObject> objects = beatmap.HitObjects.Skip(first).Take(last - first + 1).ToArray();
            PracticeDrillType type = classify(objects, weakness.ObjectIndex - first);
            if (safe.DrillType != PracticeDrillType.Mixed && type != safe.DrillType)
                continue;
            PracticeWeakObject[] included = weaknesses.Where(item => item.ObjectIndex >= first && item.ObjectIndex <= last).ToArray();
            candidates.Add(new PracticeSourceSection(type, first, last, objects[0].StartTimeMs,
                objects[^1].EndTimeMs, included.Sum(item => item.WeightedSeverity), included, objects));
        }

        PracticeSourceSection[] selected = candidates.OrderByDescending(section => section.WeaknessScore)
                                                      .ThenBy(section => section.SourceStartTimeMs)
                                                      .Aggregate(new List<PracticeSourceSection>(), addNonOverlapping)
                                                      .Take(safe.MaximumSections)
                                                      .ToArray();
        return selected.Select((section, index) => compose(beatmap, section, safe, index + 1)).ToArray();
    }

    private static PracticeWeakObject[] aggregateWeaknesses(PracticeSourceBeatmap beatmap, IReadOnlyCollection<ReplayAnalysisResult> analyses) =>
        analyses.SelectMany(result => result.Judgements
            .Where(judgement => judgement.ObjectIndex is >= 0
                                && judgement.ObjectIndex < beatmap.HitObjects.Count
                                && string.IsNullOrEmpty(judgement.NestedPath)
                                && string.Equals(judgement.Result, "Miss", StringComparison.OrdinalIgnoreCase)))
        .GroupBy(judgement => judgement.ObjectIndex!.Value)
        .Select(group => new PracticeWeakObject(
            group.Key,
            beatmap.HitObjects[group.Key].StartTimeMs,
            group.Count(),
            analyses.Count,
            group.Sum(judgement => 1 + Math.Clamp(judgement.MissAnalysis?.Confidence ?? 0.25, 0, 1)) * (1 + group.Count() / (double)analyses.Count),
            group.GroupBy(judgement => judgement.MissAnalysis?.Reason ?? ReplayMissReason.Unknown)
                 .ToDictionary(reason => reason.Key, reason => reason.Count())))
        .OrderByDescending(weakness => weakness.WeightedSeverity)
        .ThenBy(weakness => weakness.ObjectIndex)
        .ToArray();

    private static List<PracticeSourceSection> addNonOverlapping(List<PracticeSourceSection> selected, PracticeSourceSection candidate)
    {
        if (selected.All(existing => candidate.LastObjectIndex < existing.FirstObjectIndex || candidate.FirstObjectIndex > existing.LastObjectIndex))
            selected.Add(candidate);
        return selected;
    }

    private static PracticeDrillType classify(IReadOnlyList<PracticeHitObject> objects, int weakOffset)
    {
        int streamLinks = 0;
        int jumpLinks = 0;
        int from = Math.Max(1, weakOffset - 5);
        int to = Math.Min(objects.Count - 1, weakOffset + 5);
        for (int index = from; index <= to; index++)
        {
            PracticeHitObject previous = objects[index - 1];
            PracticeHitObject current = objects[index];
            double interval = current.StartTimeMs - previous.StartTimeMs;
            double distance = Math.Sqrt(Math.Pow(current.X - previous.X, 2) + Math.Pow(current.Y - previous.Y, 2));
            if (interval is > 0 and <= stream_interval_ms && distance <= 130)
                streamLinks++;
            if (interval is >= 90 and <= 650 && distance >= jump_distance)
                jumpLinks++;
        }
        if (streamLinks >= 4 && streamLinks >= jumpLinks)
            return PracticeDrillType.Streams;
        if (jumpLinks >= 2)
            return PracticeDrillType.LongJumps;
        return PracticeDrillType.Mixed;
    }

    private static PracticeMapPlan compose(PracticeSourceBeatmap beatmap, PracticeSourceSection section, PracticeMapOptions options, int number)
    {
        double audioStart = Math.Max(0, section.SourceStartTimeMs - options.AudioPaddingMs);
        double audioEnd = section.SourceEndTimeMs + options.AudioPaddingMs;
        double audioLeadIn = Math.Max(0, options.LeadInMs - (section.SourceStartTimeMs - audioStart));
        double shift = -audioStart + audioLeadIn;
        double cycleDuration = audioEnd - audioStart;
        if (!double.IsFinite(cycleDuration) || cycleDuration <= 0)
            throw new InvalidDataException("The selected practice phrase has no usable duration.");
        int repetitions = Math.Clamp(
            (int)Math.Ceiling(options.TargetDurationMs / cycleDuration),
            options.MinimumRepetitions,
            options.MaximumRepetitions);
        PracticeHitObject[] objects = Enumerable.Range(0, repetitions)
                                                .SelectMany(repetition => section.HitObjects.Select((item, index) =>
                                                    shiftObject(item, shift + repetition * cycleDuration, index == 0)))
                                                .ToArray();
        PracticeTimingPoint[] sourceTiming = selectTimingPoints(beatmap.TimingPoints, audioStart, audioEnd).ToArray();
        PracticeTimingPoint[] timing = Enumerable.Range(0, repetitions)
                                                 .SelectMany(repetition => sourceTiming.Select(point => shiftTimingPoint(
                                                     point,
                                                     shift + repetition * cycleDuration,
                                                     audioStart,
                                                     audioLeadIn + repetition * cycleDuration)))
                                                 .ToArray();
        string type = section.DrillType switch
        {
            PracticeDrillType.LongJumps => "Long jumps",
            PracticeDrillType.Streams => "Streams",
            _ => "Mixed pattern",
        };
        string version = $"AimMod {type} x{repetitions} drill {number} - {beatmap.Metadata.Version}";
        const string outputAudio = "practice-audio.ogg";
        string sourceDirectory = Path.GetDirectoryName(beatmap.SourcePath)!;
        string sourceAudio = Path.GetFullPath(Path.Combine(sourceDirectory, beatmap.Metadata.AudioFilename));
        string sourcePrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory)) + Path.DirectorySeparatorChar;
        if (!sourceAudio.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The source beatmap audio path escapes its beatmap directory.");
        return new PracticeMapPlan(section.DrillType, section, beatmap.SourcePath, beatmap.Metadata.Title, beatmap.Metadata.Artist,
            beatmap.Metadata.Creator, beatmap.Metadata.Version, version, shift, audioLeadIn, timing, objects,
            new PracticeAudioSliceRequest(sourceAudio, audioStart, audioEnd, outputAudio, repetitions),
            $"Practice drill derived from {beatmap.Metadata.Artist} - {beatmap.Metadata.Title} [{beatmap.Metadata.Version}], mapped by {beatmap.Metadata.Creator}. The looped source excerpt and geometry repeat {repetitions} times with a lead-up and recovery between rounds.",
            repetitions);
    }

    private static IEnumerable<PracticeTimingPoint> selectTimingPoints(IReadOnlyList<PracticeTimingPoint> points, double start, double end)
    {
        PracticeTimingPoint? red = points.LastOrDefault(point => point.TimeMs <= start && point.Uninherited);
        PracticeTimingPoint? green = points.LastOrDefault(point => point.TimeMs <= start && !point.Uninherited);
        return new[] { red, green }.Where(point => point is not null).Cast<PracticeTimingPoint>()
            .Concat(points.Where(point => point.TimeMs > start && point.TimeMs <= end))
            .Distinct()
            .OrderBy(point => point.TimeMs);
    }

    private static PracticeHitObject shiftObject(PracticeHitObject item, double shift, bool forceNewCombo)
    {
        string[] fields = item.Fields.ToArray();
        fields[2] = format(item.StartTimeMs + shift);
        int type = forceNewCombo ? item.Type | 4 : item.Type;
        fields[3] = type.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (item.IsSpinner && fields.Length > 5)
            fields[5] = format(item.EndTimeMs + shift);
        return item with { StartTimeMs = item.StartTimeMs + shift, EndTimeMs = item.EndTimeMs + shift, Type = type, Fields = fields };
    }

    private static PracticeTimingPoint shiftTimingPoint(
        PracticeTimingPoint point,
        double shift,
        double sourceCycleStart,
        double outputCycleStart)
    {
        string[] fields = point.Fields.ToArray();
        double time = point.TimeMs <= sourceCycleStart ? outputCycleStart : point.TimeMs + shift;
        fields[0] = format(time);
        return point with { TimeMs = time, Fields = fields };
    }

    internal static string format(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
