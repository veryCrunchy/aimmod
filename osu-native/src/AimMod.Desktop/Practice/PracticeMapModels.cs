using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Practice;

public enum PracticeDrillType
{
    LongJumps,
    Streams,
    Mixed,
}

public sealed record PracticeMapMetadata(
    string Title,
    string Artist,
    string Creator,
    string Version,
    string AudioFilename,
    int Mode);

public sealed record PracticeHitObject(
    int SourceIndex,
    int X,
    int Y,
    double StartTimeMs,
    double EndTimeMs,
    int Type,
    IReadOnlyList<string> Fields)
{
    public bool IsCircle => (Type & 1) != 0;
    public bool IsSlider => (Type & 2) != 0;
    public bool IsSpinner => (Type & 8) != 0;
}

public sealed record PracticeTimingPoint(
    double TimeMs,
    bool Uninherited,
    IReadOnlyList<string> Fields);

public sealed record PracticeSourceBeatmap(
    string SourcePath,
    PracticeMapMetadata Metadata,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Sections,
    IReadOnlyList<PracticeTimingPoint> TimingPoints,
    IReadOnlyList<PracticeHitObject> HitObjects);

public sealed record PracticeWeakObject(
    int ObjectIndex,
    double StartTimeMs,
    int MissCount,
    int AttemptCount,
    double WeightedSeverity,
    IReadOnlyDictionary<ReplayMissReason, int> Reasons)
{
    public double MissRate => AttemptCount == 0 ? 0 : MissCount / (double)AttemptCount;
}

public sealed record PracticeSourceSection(
    PracticeDrillType DrillType,
    int FirstObjectIndex,
    int LastObjectIndex,
    double SourceStartTimeMs,
    double SourceEndTimeMs,
    double WeaknessScore,
    IReadOnlyList<PracticeWeakObject> WeakObjects,
    IReadOnlyList<PracticeHitObject> HitObjects);

public sealed record PracticeAudioSliceRequest(
    string SourceAudioPath,
    double SourceStartTimeMs,
    double SourceEndTimeMs,
    string OutputFilename,
    int RepeatCount = 1)
{
    public double CycleDurationMs => SourceEndTimeMs - SourceStartTimeMs;

    public double OutputDurationMs => CycleDurationMs * RepeatCount;
}

public sealed record PracticeMapPlan(
    PracticeDrillType DrillType,
    PracticeSourceSection SourceSection,
    string SourceBeatmapPath,
    string SourceTitle,
    string SourceArtist,
    string SourceCreator,
    string SourceVersion,
    string OutputVersion,
    double TimeShiftMs,
    double AudioLeadInMs,
    IReadOnlyList<PracticeTimingPoint> TimingPoints,
    IReadOnlyList<PracticeHitObject> HitObjects,
    PracticeAudioSliceRequest AudioSlice,
    string Attribution,
    int RepeatCount);

public sealed record PracticeMapOptions(
    PracticeDrillType DrillType,
    int MaximumSections = 3,
    int ContextObjectsBefore = 6,
    int ContextObjectsAfter = 10,
    double LeadInMs = 4_000,
    double AudioPaddingMs = 2_500,
    double TargetDurationMs = 60_000,
    int MinimumRepetitions = 6,
    int MaximumRepetitions = 12)
{
    public PracticeMapOptions Normalised() => this with
    {
        MaximumSections = Math.Clamp(MaximumSections, 1, 10),
        ContextObjectsBefore = Math.Clamp(ContextObjectsBefore, 0, 32),
        ContextObjectsAfter = Math.Clamp(ContextObjectsAfter, 0, 64),
        LeadInMs = Math.Clamp(LeadInMs, 1_000, 10_000),
        AudioPaddingMs = Math.Clamp(AudioPaddingMs, 0, 5_000),
        TargetDurationMs = Math.Clamp(TargetDurationMs, 20_000, 120_000),
        MinimumRepetitions = Math.Clamp(MinimumRepetitions, 2, 20),
        MaximumRepetitions = Math.Clamp(MaximumRepetitions, Math.Clamp(MinimumRepetitions, 2, 20), 24),
    };
}
