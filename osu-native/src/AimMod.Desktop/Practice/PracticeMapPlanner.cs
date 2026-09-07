using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Practice;

public static class PracticeMapPlanner
{
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
        IReadOnlyList<PracticeSourceSection> sections = FindSections(beatmap, analyses, safe);
        return sections.Where(section => safe.FirstObjectIndex is null || section.FirstObjectIndex == safe.FirstObjectIndex)
            .Take(safe.MaximumSections).Select((section, index) => compose(beatmap, section, safe, index + 1)).ToArray();
    }

    public static IReadOnlyList<PracticeSourceSection> FindSections(
        PracticeSourceBeatmap beatmap, IEnumerable<ReplayAnalysisResult> analyses, PracticeMapOptions options)
    {
        PracticeMapOptions safe = options.Normalised();
        ReplayAnalysisResult[] attempts = analyses.Where(result => result.Judgements is not null).ToArray();
        if (attempts.Length == 0)
            return safe.AllowPatternPractice ? patternSections(beatmap, safe) : Array.Empty<PracticeSourceSection>();

        PracticeWeakObject[] weaknesses = aggregateWeaknesses(beatmap, attempts);
        var candidates = new List<PracticeSourceSection>();
        foreach (PracticeWeakObject weakness in weaknesses)
        {
            int first = Math.Max(0, weakness.ObjectIndex - safe.ContextObjectsBefore);
            int last = Math.Min(beatmap.HitObjects.Count - 1, weakness.ObjectIndex + safe.ContextObjectsAfter);
            IReadOnlyList<PracticeHitObject> objects = beatmap.HitObjects.Skip(first).Take(last - first + 1).ToArray();
            IReadOnlyList<PracticeDrillType> types = DetectPatterns(beatmap, objects, weakness.ObjectIndex - first);
            if (safe.DrillType != PracticeDrillType.Mixed && !types.Contains(safe.DrillType))
                continue;
            PracticeDrillType type = safe.DrillType == PracticeDrillType.Mixed ? types[0] : safe.DrillType;
            PracticeWeakObject[] included = weaknesses.Where(item => item.ObjectIndex >= first && item.ObjectIndex <= last).ToArray();
            candidates.Add(withPlayableLeadUp(beatmap, new PracticeSourceSection(type, first, last, objects[0].StartTimeMs,
                objects.Max(item => item.EndTimeMs), included.Sum(item => item.WeightedSeverity), included, objects), safe.PlaybackRate));
        }

        // Timing loss can occur in a full combo. Keep its evidence separate from actual missed objects.
        foreach (var lesson in attempts.Select(AimMod.Desktop.Coaching.TappingCoaching.Build).Where(lesson => lesson is not null))
        {
            int first = Math.Max(0, lesson!.FirstObjectIndex - safe.ContextObjectsBefore);
            int last = Math.Min(beatmap.HitObjects.Count - 1, lesson.FirstObjectIndex + 7 + safe.ContextObjectsAfter);
            if (first > last || lesson.FirstObjectIndex >= beatmap.HitObjects.Count) continue;
            var objects = beatmap.HitObjects.Skip(first).Take(last - first + 1).ToArray();
            var types = DetectPatterns(beatmap, objects, lesson.FirstObjectIndex - first);
            if (safe.DrillType != PracticeDrillType.Mixed && !types.Contains(safe.DrillType)) continue;
            var type = safe.DrillType == PracticeDrillType.Mixed ? types[0] : safe.DrillType;
            candidates.Add(withPlayableLeadUp(beatmap, new PracticeSourceSection(type, first, last, objects[0].StartTimeMs,
                objects.Max(item => item.EndTimeMs), 1, [], objects), safe.PlaybackRate));
        }

        if (safe.IncludeOverlappingSections && candidates.Count > 0)
            return candidates.DistinctBy(section => (section.FirstObjectIndex, section.LastObjectIndex)).OrderBy(section => section.SourceStartTimeMs).ToArray();
        PracticeSourceSection[] selected = candidates.OrderByDescending(section => section.WeaknessScore)
                                                      .ThenBy(section => section.SourceStartTimeMs)
                                                      .Aggregate(new List<PracticeSourceSection>(), addNonOverlapping)
                                                      .ToArray();
        return selected.Length == 0 && safe.AllowPatternPractice ? patternSections(beatmap, safe) : selected;
    }

    private static IReadOnlyList<PracticeSourceSection> patternSections(PracticeSourceBeatmap beatmap, PracticeMapOptions options)
    {
        var sections = new List<PracticeSourceSection>();
        const int phraseObjects = 24;
        for (int first = 0; first < beatmap.HitObjects.Count; first += phraseObjects)
        {
            var objects = beatmap.HitObjects.Skip(first).Take(phraseObjects).ToArray();
            if (objects.Length < 2 || objects.All(item => item.IsSpinner)) continue;
            var types = DetectPatterns(beatmap, objects, objects.Length / 2);
            if (options.DrillType != PracticeDrillType.Mixed && !types.Contains(options.DrillType)) continue;
            sections.Add(withPlayableLeadUp(beatmap, new PracticeSourceSection(options.DrillType, first, first + objects.Length - 1,
                objects[0].StartTimeMs, objects.Max(item => item.EndTimeMs), 0, [], objects), options.PlaybackRate));
        }
        return sections;
    }

    private static PracticeSourceSection withPlayableLeadUp(PracticeSourceBeatmap beatmap, PracticeSourceSection section, double rate)
    {
        // Keep original notes and rhythm. Audio padding alone cannot prepare the player's tapping and aim.
        int first = section.FirstObjectIndex;
        for (int added = 0; added < 3 && first > 0; added++)
        {
            var previous = beatmap.HitObjects[first - 1];
            var next = beatmap.HitObjects[first];
            if (previous.IsSpinner || next.IsSpinner
                || section.SourceStartTimeMs - previous.StartTimeMs > 1500 * rate
                || next.StartTimeMs - previous.EndTimeMs > 750 * rate
                || previous.EndTimeMs > next.StartTimeMs)
                break;
            first--;
        }
        if (first == section.FirstObjectIndex) return section;
        return section with
        {
            FirstObjectIndex = first,
            SourceStartTimeMs = beatmap.HitObjects[first].StartTimeMs,
            HitObjects = beatmap.HitObjects.Skip(first).Take(section.LastObjectIndex - first + 1).ToArray(),
        };
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

    public static IReadOnlyList<PracticeDrillType> DetectPatterns(PracticeSourceBeatmap beatmap, IReadOnlyList<PracticeHitObject> objects, int weakOffset)
    {
        int run = 1;
        int longestRun = 1;
        int jumpLinks = 0;
        int rhythmChanges = 0;
        double previousInterval = 0;
        int from = Math.Max(1, weakOffset - 7);
        int to = Math.Min(objects.Count - 1, weakOffset + 7);
        for (int index = from; index <= to; index++)
        {
            PracticeHitObject previous = objects[index - 1];
            PracticeHitObject current = objects[index];
            double interval = current.StartTimeMs - previous.StartTimeMs;
            double distance = Math.Sqrt(Math.Pow(current.X - previous.X, 2) + Math.Pow(current.Y - previous.Y, 2));
            PracticeTimingPoint? timing = beatmap.TimingPoints.LastOrDefault(point => point.Uninherited && point.TimeMs <= current.StartTimeMs);
            double beatLength = timing is not null && double.TryParse(timing.Fields[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value) && value > 0 ? value : 500;
            // Require a contiguous circle run, not several unrelated fast links or slider heads.
            bool fastLink = previous.IsCircle && current.IsCircle && interval >= 40
                && interval <= Math.Min(200, beatLength * 0.4) && distance <= 130;
            run = fastLink && (run == 1 || Math.Abs(interval - previousInterval) <= interval * 0.25) ? run + 1 : fastLink ? 2 : 1;
            longestRun = Math.Max(longestRun, run);
            if (previousInterval > 0 && interval >= 60 && interval <= beatLength
                && Math.Max(interval, previousInterval) / Math.Min(interval, previousInterval) >= 1.7)
                rhythmChanges++;
            previousInterval = interval;
            if (interval is >= 90 and <= 650 && distance >= jump_distance)
                jumpLinks++;
        }
        var result = new List<PracticeDrillType>();
        if (longestRun >= 8)
            result.Add(PracticeDrillType.Streams);
        else if (longestRun >= 3)
            result.Add(PracticeDrillType.Bursts);
        if (jumpLinks >= 2)
            result.Add(PracticeDrillType.LongJumps);
        if (objects.Skip(Math.Max(0, weakOffset - 2)).Take(5).Any(item => item.IsSlider))
            result.Add(PracticeDrillType.SliderControl);
        if (rhythmChanges >= 2)
            result.Add(PracticeDrillType.RhythmChanges);
        result.Add(PracticeDrillType.Mixed);
        return result;
    }

    public static string Label(PracticeDrillType type) => type switch
    {
        PracticeDrillType.LongJumps => "Long jumps",
        PracticeDrillType.Streams => "Streams",
        PracticeDrillType.Bursts => "Bursts",
        PracticeDrillType.SliderControl => "Slider control",
        PracticeDrillType.RhythmChanges => "Rhythm changes",
        _ => "Original phrase",
    };

    internal static PracticeMapPlan CreateSectionPlan(PracticeSourceBeatmap source, PracticeSourceSection section, PracticeMapOptions options) =>
        compose(source, section, options.Normalised(), 1);

    private static PracticeMapPlan compose(PracticeSourceBeatmap beatmap, PracticeSourceSection section, PracticeMapOptions options, int number)
    {
        double rate = options.PlaybackRate;
        // Work on the original song clock, then scale every timestamp and red-line beat length together.
        options = options with { AudioPaddingMs = options.AudioPaddingMs * rate, LeadInMs = options.LeadInMs * rate, TargetDurationMs = options.TargetDurationMs * rate };
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
        string type = Label(section.DrillType);
        string version = $"AimMod {type} x{repetitions} {rate * 100:0}% drill {number} - {beatmap.Metadata.Version}";
        const string outputAudio = "practice-audio.ogg";
        string sourceDirectory = Path.GetDirectoryName(beatmap.SourcePath)!;
        string sourceAudio = Path.GetFullPath(Path.Combine(sourceDirectory, beatmap.Metadata.AudioFilename));
        string sourcePrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory)) + Path.DirectorySeparatorChar;
        if (!sourceAudio.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The source beatmap audio path escapes its beatmap directory.");
        return new PracticeMapPlan(section.DrillType, section, beatmap.SourcePath, beatmap.Metadata.Title, beatmap.Metadata.Artist,
            beatmap.Metadata.Creator, beatmap.Metadata.Version, version, shift / rate, audioLeadIn / rate,
            timing.Select(point => scaleTiming(point, rate)).ToArray(), objects.Select(item => scaleObject(item, rate)).ToArray(),
            new PracticeAudioSliceRequest(sourceAudio, audioStart, audioEnd, outputAudio, repetitions, rate, audioLeadIn / rate),
            $"Practice drill derived from {beatmap.Metadata.Artist} - {beatmap.Metadata.Title} [{beatmap.Metadata.Version}], mapped by {beatmap.Metadata.Creator}. The looped source excerpt and geometry repeat {repetitions} times with a lead-up and recovery between rounds.",
            repetitions);
    }

    private static PracticeHitObject scaleObject(PracticeHitObject item, double rate)
    {
        string[] fields = item.Fields.ToArray();
        fields[2] = format(item.StartTimeMs / rate);
        if (item.IsSpinner && fields.Length > 5) fields[5] = format(item.EndTimeMs / rate);
        return item with { StartTimeMs = item.StartTimeMs / rate, EndTimeMs = item.EndTimeMs / rate, Fields = fields };
    }

    private static PracticeTimingPoint scaleTiming(PracticeTimingPoint point, double rate)
    {
        string[] fields = point.Fields.ToArray();
        fields[0] = format(point.TimeMs / rate);
        if (point.Uninherited) fields[1] = format(double.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture) / rate);
        return point with { TimeMs = point.TimeMs / rate, Fields = fields };
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
