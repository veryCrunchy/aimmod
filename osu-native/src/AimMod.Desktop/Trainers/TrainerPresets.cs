namespace AimMod.Desktop.Trainers;
public sealed record TrainerPreset(string Title, string Description, TrainerPattern Pattern, int Seconds = 30,
    TrainerAimStyle AimStyle = TrainerAimStyle.Balanced, int Spacing = 100,
    TrainerPathStyle Path = TrainerPathStyle.FigureEight, TrainerSliderStyle Sliders = TrainerSliderStyle.None,
    TrainerReactionDelay ReactionDelay = TrainerReactionDelay.Standard, ReactionMode ReactionMode = ReactionMode.Simple,
    int ReadingGroupSize = 4, bool ReadingHidden = false, int ApproachRate = 7, int ReadingComplexity = 0, int SpinnerSeconds = 4)
{
    // Presets describe the drill. Never replace the player's imported input or chosen song/tempo.
    public TrainerSettings Apply(TrainerSettings settings) => settings with
    {
        Pattern = Pattern, Seconds = Seconds, SpinnerSeconds = SpinnerSeconds, AimStyle = AimStyle, AimSpacing = Spacing,
        PathStyle = Path, Sliders = Sliders, SliderBeats = 1, CircleSize = 4, ApproachRate = ApproachRate,
        NoteSpeed = TrainerNoteSpeed.Default, RandomizePatterns = false, SkillLimits = null,
        MovementScale = 1, ReactionDelay = ReactionDelay,
        ReactionMode = ReactionMode, ReactionWindowMs = 1200, ReadingGroupSize = ReadingGroupSize, ReadingHidden = ReadingHidden, ReadingComplexity = ReadingComplexity,
    };
    public bool Matches(TrainerSettings settings) => Apply(settings) == settings;
}
public static class TrainerPresets
{
    public static IReadOnlyList<TrainerPreset> For(TrainerKind kind) => kind switch
    {
        TrainerKind.Steady => [new("Even taps", "30 s · keep a steady pulse", TrainerPattern.Standard),
            new("Restart cleanly", "30 s · rests, then back on beat", TrainerPattern.Gaps),
            new("Offbeat control", "60 s · tap between the beats", TrainerPattern.Offbeat, 60),
            new("Hold and release", "30 s · time the slider heads and exits", TrainerPattern.Standard, Sliders: TrainerSliderStyle.Mixed)],
        TrainerKind.Bursts => [new("Short bursts", "30 s · three taps, then recover", TrainerPattern.Standard),
            new("Five-note control", "30 s · finish every burst evenly", TrainerPattern.FiveNotes),
            new("Mixed burst lengths", "60 s · switch between 3–9 notes", TrainerPattern.MixedBursts, 60)],
        TrainerKind.Alternating => [new("Short streams", "30 s · eight-note groups with rests", TrainerPattern.PartialStreams),
            new("Build into streams", "60 s · transition into faster groups", TrainerPattern.BuildUp, 60),
            new("Longer streams", "60 s · keep 24-note groups even", TrainerPattern.LongStreams, 60),
            new("Stream exits", "30 s · streams with slider holds", TrainerPattern.PartialStreams, Sliders: TrainerSliderStyle.Mixed)],
        TrainerKind.Rhythm => [new("Rhythm switches", "30 s · change between two rhythms", TrainerPattern.Standard),
            new("Stop and restart", "30 s · hold the pulse through rests", TrainerPattern.Gaps),
            new("Triplet changes", "60 s · switch in and out of triplets", TrainerPattern.Triplets, 60),
            new("Slider rhythms", "30 s · hold, release, then re-enter", TrainerPattern.Standard, Sliders: TrainerSliderStyle.Mixed)],
        TrainerKind.Aim => [new("Controlled jumps", "30 s · land, tap, then move", TrainerPattern.Standard),
            new("Flowing aim", "30 s · follow curves and sliders", TrainerPattern.Standard,
                AimStyle: TrainerAimStyle.Flow, Sliders: TrainerSliderStyle.Mixed),
            new("Jumps with fills", "60 s · jumps with connecting notes", TrainerPattern.JumpFill, 60)],
        TrainerKind.Reading => [new("Changing shapes", "30 s · new angles and spacing", TrainerPattern.ReadingMix, ReadingGroupSize: 6, ApproachRate: 6, Sliders: TrainerSliderStyle.Mixed),
            new("Complex patterns", "60 s · crossings with phrase breaks", TrainerPattern.ReadingMix, 60, ReadingGroupSize: 6, ApproachRate: 7, ReadingComplexity: 2, Sliders: TrainerSliderStyle.Mixed),
            new("Hidden sequences", "60 s · mixed shapes, fading notes", TrainerPattern.ReadingMix, 60, ReadingGroupSize: 6, ReadingHidden: true, ReadingComplexity: 1, Sliders: TrainerSliderStyle.Mixed),
            new("Slider exits", "30 s · follow repeats, then find the next note", TrainerPattern.ReadingMix, ReadingGroupSize: 6, Sliders: TrainerSliderStyle.BackAndForth)],
        TrainerKind.Spinner => [new("Find your circle", "30 s · short spins with recovery time", TrainerPattern.Standard, SpinnerSeconds: 2),
            new("Steady rotations", "30 s · hold a comfortable, even speed", TrainerPattern.Standard, SpinnerSeconds: 4),
            new("Sustained control", "60 s · keep your speed through the finish", TrainerPattern.Standard, 60, SpinnerSeconds: 6)],
        _ => [new("Clean reactions", "30 s · respond without guessing", TrainerPattern.Standard),
            new("Choose the key", "30 s · press the displayed key", TrainerPattern.Standard, ReactionMode: ReactionMode.Choice),
            new("Tap or hold", "60 s · tap GO, hold on STOP", TrainerPattern.Standard, 60, ReactionMode: ReactionMode.GoNoGo)],
    };
}
