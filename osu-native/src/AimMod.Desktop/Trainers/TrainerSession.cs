using System.Text.Json;
using osuTK.Input;

namespace AimMod.Desktop.Trainers;

public enum TrainerKind { Steady, Alternating, Bursts, Rhythm, Aim, Reading, Reaction, Spinner }
public enum TrainerSpinnerFrequency { None, Occasional }

public sealed record TrainerSettings(TrainerKind Kind = TrainerKind.Steady, int Bpm = 120,
    int Seconds = 30, int OffsetMs = 0, string Keys = "Z / X", string Music = "cues",
    string Cue = "pulse", string SongIdentity = "", int SongStartSeconds = 0, string SongTitle = "",
    TrainerAimStyle AimStyle = TrainerAimStyle.Balanced, int AimSpacing = 100, int CircleSize = 4, int PatternSeed = 0,
    TrainerPattern Pattern = TrainerPattern.Standard, TrainerNoteSpeed NoteSpeed = TrainerNoteSpeed.Default,
    TrainerSliderStyle Sliders = TrainerSliderStyle.None, int SliderBeats = 1, TrainerPathStyle PathStyle = TrainerPathStyle.FigureEight,
    bool RandomizePatterns = false, int ApproachRate = 7, TrainerReactionDelay ReactionDelay = TrainerReactionDelay.Standard, TrainerSkillLimits? SkillLimits = null,
    double MovementScale = 1, int ReadingGroupSize = 4, bool ReadingHidden = false,
    ReactionMode ReactionMode = ReactionMode.Simple, int ReactionWindowMs = 1200, int ReadingComplexity = 0,
    TrainerSpinnerFrequency Spinners = TrainerSpinnerFrequency.None, int SpinnerSeconds = 4, bool GuidedCues = false)
{
    public string TempoDescription => Music == "song" ? "Song tempo" : $"{Bpm} BPM";
    public TrainerSettings ComparisonKey()
    {
        if (Kind == TrainerKind.Reaction) return this with
        {
            Bpm = 120, OffsetMs = 0, Music = "cues", Cue = "pulse", SongIdentity = "", SongTitle = "", SongStartSeconds = 0,
            PatternSeed = 0, Pattern = TrainerPattern.Standard, NoteSpeed = TrainerNoteSpeed.Default, Sliders = TrainerSliderStyle.None,
            SliderBeats = 1, PathStyle = TrainerPathStyle.FigureEight, AimStyle = TrainerAimStyle.Balanced, AimSpacing = 100,
            CircleSize = 4, ApproachRate = 7, RandomizePatterns = false, SkillLimits = null, MovementScale = 1,
            ReadingGroupSize = 4, ReadingHidden = false, ReadingComplexity = 0, Spinners = TrainerSpinnerFrequency.None, SpinnerSeconds = 4,
        };
        return this with
    {
        SongTitle = "",
        PatternSeed = 0,
        SkillLimits = RandomizePatterns && SkillLimits is {} limits ? limits with { EvidenceCount = 0 } : null,
        AimStyle = Kind == TrainerKind.Aim ? AimStyle : TrainerAimStyle.Balanced,
        ReactionDelay = Kind == TrainerKind.Reaction ? ReactionDelay : TrainerReactionDelay.Standard,
        ReactionMode = Kind == TrainerKind.Reaction ? ReactionMode : ReactionMode.Simple,
        ReactionWindowMs = Kind == TrainerKind.Reaction ? ReactionWindowMs : 1200,
        ReadingGroupSize = Kind == TrainerKind.Reading ? ReadingGroupSize : 4,
        ReadingHidden = Kind == TrainerKind.Reading && ReadingHidden,
        ReadingComplexity = Kind == TrainerKind.Reading ? ReadingComplexity : 0,
        SliderBeats = Sliders == TrainerSliderStyle.None ? 1 : SliderBeats,
        SpinnerSeconds = Kind == TrainerKind.Spinner || Spinners != TrainerSpinnerFrequency.None ? SpinnerSeconds : 4,
        Bpm = Music == "song" ? 120 : Bpm,
        Cue = Music == "cues" ? Cue : "pulse",
        SongIdentity = Music == "song" ? SongIdentity : "",
        SongStartSeconds = Music == "song" ? SongStartSeconds : 0,
    };
    }
    public static Key[] ParseKeys(string keys) => keys.Split(" / ").Select(s => Enum.TryParse<Key>(s, out var k) && Enum.IsDefined(k) && k is not (Key.Unknown or Key.Escape) ? (Key?)k : null)
        .Where(k => k is not null).Select(k => k!.Value).ToArray();
    public void Validate()
    {
        SkillLimits?.Validate();
        if (!Enum.IsDefined(Spinners) || SpinnerSeconds is not (2 or 4 or 6)
            || !double.IsFinite(MovementScale) || MovementScale is < .4 or > 1
            || !Enum.IsDefined(ReactionMode) || ReactionWindowMs is not (600 or 1000 or 1200 or 1500 or 2000)
            || ReadingComplexity is < 0 or > 2
            || ReadingGroupSize is not (4 or 6 or 8 or 12)
            || !Enum.IsDefined(Kind) || Bpm is < 60 or > 240 || Seconds is not (15 or 30 or 60 or 120 or 180)
            || !Enum.IsDefined(AimStyle) || AimSpacing is not (70 or 85 or 100 or 120 or 140) || CircleSize is < 3 or > 6
            || !TrainerPatterns.Choices(Kind).Values.Contains(Pattern) || !Enum.IsDefined(NoteSpeed) || !Enum.IsDefined(Sliders)
            || !Enum.IsDefined(PathStyle) || !Enum.IsDefined(ReactionDelay) || SliderBeats is < 1 or > 4 || ApproachRate is < 3 or > 10
            || TrainerMusicCatalog.IsSong(Music) && !TrainerMusicCatalog.Tempos.Contains(Bpm)
            || Music is not ("cues" or "song") && !TrainerMusicCatalog.IsSong(Music)
            || Cue is not ("pulse" or "glass" or "snap") || SongStartSeconds is < 0 or > 3600
            || OffsetMs is < -500 or > 500 || ParseKeys(Keys) is not { Length: 2 } pair || pair[0] == pair[1])
            throw new ArgumentOutOfRangeException(nameof(TrainerSettings));
    }
}

public sealed record TrainerNote(double TimeMs, int Phrase, TrainerPattern Pattern = TrainerPattern.Standard);
public sealed record TrainerHit(int NoteIndex, double OffsetMs, int Key);
public sealed record TrainerResult(Guid Id, DateTimeOffset CompletedAt, TrainerSettings Settings,
    int Notes, int Hits, int Within25, int Extras, int RepeatedKeys, double? MeanMs,
    double? SpreadMs, double? DriftMs, double? ResponseMs = null, string Engine = "cue", double? Accuracy = null, double? PlayedSeconds = null, TrainerDemand? Demand = null,
    TrainerGuidedRun? GuidedRun = null, int? JudgementMisses = null, bool Assisted = false,
    ReactionSummary? Reaction = null, ReadingWindowResult[]? ReadingWindows = null,
    SpinnerPracticeSummary? SpinnerPractice = null, int? TapTargets = null)
{
    public bool UsesOsuJudgements => Engine is "osu" or "osu-moving-v2" or "osu-patterns-v3" or "osu-adaptive-v4" or "osu-reading-v2" or "osu-reading-v3" or "osu-reading-v4" or "osu-spinner-v1";
    public static string EngineFor(TrainerSettings s) => s.Kind == TrainerKind.Reaction ? "reaction-v2"
        : s.Kind == TrainerKind.Spinner ? "osu-spinner-v1"
        : s.Kind == TrainerKind.Reading ? "osu-reading-v4"
        : s.RandomizePatterns ? "osu-adaptive-v4"
        : s.Pattern != TrainerPattern.Standard || s.NoteSpeed != TrainerNoteSpeed.Default || s.Sliders != TrainerSliderStyle.None || s.RandomizePatterns ? "osu-patterns-v3"
        : s.Kind <= TrainerKind.Rhythm ? "osu-moving-v2" : "osu";
    public double OnTimePercent => 100.0 * Within25 / Math.Max(1, (TapTargets ?? Notes) + Extras);
    public int Misses => JudgementMisses ?? Notes - Hits;
}

/// <summary>All times are in the reference audio's timeline, in milliseconds.</summary>
public sealed class TrainerSession
{
    public TrainerSettings Settings { get; }
    public IReadOnlyList<TrainerNote> Notes { get; }
    public List<TrainerHit> Hits { get; } = [];
    public int Extras { get; private set; }
    public int RepeatedKeys { get; private set; }
    public double StartMs => 500 + 4 * BeatMs;
    public double EndMs => StartMs + Settings.Seconds * 1000;
    public double BeatMs => 60000.0 / Settings.Bpm;
    public double WindowMs => Math.Min(100, BeatMs / (Settings.Kind == TrainerKind.Steady ? 2 : 4) * .45);
    private readonly bool[] judged;

    public TrainerSession(TrainerSettings settings)
    {
        settings = TrainerSkillProfile.Apply(settings, settings.SkillLimits ?? new());
        settings.Validate(); Settings = settings;
        var notes = new List<TrainerNote>();
        for (int bar = 0; StartMs + bar * BeatMs * 4 < EndMs; bar++)
        {
            foreach (var (beat, phrase) in TrainerPatterns.Bar(settings, bar))
            {
                double time = StartMs + (bar * 4 + beat) * BeatMs;
                if (time < EndMs) notes.Add(new(time, phrase, TrainerPatterns.PatternAt(settings,bar)));
            }
        }
        Notes = TrainerReadingPatterns.ConstrainTiming(settings, TrainerSkillProfile.ConstrainNotes(settings,notes)); judged = new bool[Notes.Count];
    }

    public TrainerHit? Tap(double audioTimeMs, int key)
    {
        double time = audioTimeMs + Settings.OffsetMs;
        if (!double.IsFinite(time) || key is < 0 or > 1 || time < StartMs - WindowMs || time > EndMs + WindowMs) return null;
        int best = -1;
        double distance = WindowMs + .001;
        for (int i = 0; i < Notes.Count; i++)
        {
            if (judged[i]) continue;
            double delta = Math.Abs(time - Notes[i].TimeMs);
            if (delta < distance) { best = i; distance = delta; }
        }
        if (best < 0) { Extras++; return null; }
        if (Settings.Kind != TrainerKind.Steady && Hits.LastOrDefault() is { } previous
            && previous.Key == key && (Settings.Kind != TrainerKind.Bursts || Notes[previous.NoteIndex].Phrase == Notes[best].Phrase))
            RepeatedKeys++;
        judged[best] = true;
        var hit = new TrainerHit(best, time - Notes[best].TimeMs, key);
        Hits.Add(hit); return hit;
    }

    public TrainerResult Result(DateTimeOffset now)
    {
        double[] offsets = Hits.Select(h => h.OffsetMs).ToArray();
        double? mean = offsets.Length > 0 ? offsets.Average() : null;
        double? spread = offsets.Length >= 2 ? Math.Sqrt(offsets.Select(x => Math.Pow(x - mean!.Value, 2)).Average()) : null;
        // Compare early and late thirds by target time, not number of successful hits.
        double[] early = Hits.Where(h => Notes[h.NoteIndex].TimeMs < StartMs + Settings.Seconds * 1000 / 3.0).Select(h => h.OffsetMs).ToArray();
        double[] late = Hits.Where(h => Notes[h.NoteIndex].TimeMs >= StartMs + Settings.Seconds * 2000 / 3.0).Select(h => h.OffsetMs).ToArray();
        double? drift = early.Length >= 3 && late.Length >= 3 ? late.Average() - early.Average() : null;
        return new(Guid.NewGuid(), now, Settings, Notes.Count, Hits.Count, offsets.Count(x => Math.Abs(x) <= 25), Extras,
            RepeatedKeys, mean, spread, drift);
    }

    public bool WasHit(int index) => judged[index];

    public static string NextStep(TrainerResult r)
    {
        if (r.SpinnerPractice is {} spins && r.Settings.Kind == TrainerKind.Spinner)
            return spins.HeldPercent < 90 ? "Hold a gameplay key until the spinner ends. Try short spins first and keep the movement comfortable."
                : spins.DirectionChanges > spins.Attempts ? "Keep one direction through each spinner. Try a slightly wider circle if you keep crossing the centre."
                : spins.SpeedVariationPercent is > 25 ? "Repeat at a comfortable speed. Keep the circle steady before trying to spin faster."
                : "Try a slightly smaller circle at the same speed, then compare your RPM and consistency. Keep the size that feels controlled.";
        if (r.Settings.Kind == TrainerKind.Reaction)
            return r.Reaction is { FalseAlarms: > 0 } ? "Keep the response window. Read GO or STOP before pressing; practise clean holds before adding key choices."
                : r.Reaction is { WrongKey: > 0 } ? "Keep this setup and focus on the displayed key. Choose a longer response window if the choice feels rushed."
                : r.Reaction is { Missed: > 0 } ? "Try a longer response window. Aim to respond to every GO cue before shortening it again."
                : r.Extras > 0 ? "Wait until GO appears. Keep your fingers relaxed and avoid guessing when the cue will arrive."
                : "Repeat with the same setup. Compare several runs rather than chasing one fast response.";
        if (r.Settings.Kind == TrainerKind.Reading)
            return r.Misses > r.Notes * .1 ? "Slow the tempo or shorten the sequence, then compare where the misses happen. Lower AR if the notes arrive before you can read them."
                : r.Settings.ReadingHidden ? "Repeat with Hidden, then try the same sequence with normal visibility. Compare misses and accuracy at the same tempo."
                : "Repeat this sequence, then change one thing: longer phrases, a different path, or Hidden. Keep the tempo steady for the comparison.";
        if (r.Settings.Kind is TrainerKind.Aim or TrainerKind.Reading)
            return r.Extras > r.Hits * .1 ? "Slow down enough to land on the target before clicking. Accuracy comes first."
                : r.Settings.Kind == TrainerKind.Reading ? "Read the next number while finishing the current target. Keep the cursor moving smoothly."
                : "Use one controlled movement per target. Check this control on a familiar jump map afterwards.";
        if (r.Settings.Music != "cues")
            return r.Hits < r.Notes * .8 ? "Try tapping accuracy on this song before adding faster streams. Choose a slower song if the taps still feel rushed."
                : r.SpreadMs is > 15 ? "Repeat this section. Listen for uneven gaps and keep your movement relaxed through the end."
                : "Repeat this section once, then try the same drill on another song. Aim to keep the same accuracy as the rhythm changes.";
        if (r.Hits < r.Notes * .8 || r.Extras > r.Notes * .1)
            return "Lower the tempo by 10 BPM. Listen through the count-in, then aim for one tap per target.";
        if (r.RepeatedKeys > r.Hits * .1)
            return "Keep the tempo. Practise left-right taps without pressing the same key twice in a row.";
        if (r.DriftMs is { } drift && Math.Abs(drift) >= 12)
            return drift < 0 ? "Your taps moved earlier. Keep the last few taps as evenly spaced as the first."
                : "Your taps moved later. Try 10 BPM slower and keep the motion small through the end.";
        if (r.SpreadMs is > 15)
            return "Keep this tempo. Listen for short-long gaps and try to make the spacing even.";
        if (r.MeanMs is { } mean && Math.Abs(mean) > 15)
            return "Your spacing is steady. Use the count-in to line up the first tap; check audio offset if the shift persists.";
        return "Repeat once at this tempo, then try 10 BPM faster. Check the same rhythm on a familiar osu! map afterwards.";
    }
}

public sealed record TrainerWorkspacePreferences(bool ShuffleMusic = true, bool RandomizePatterns = false, bool FreshLayout = true, bool GuidedCues = false);

public sealed class TrainerHistoryStore(string path)
{
    public TrainerGuidedPlan? LoadGuidedPlan()
    {
        try
        {
            var plan = JsonSerializer.Deserialize<TrainerGuidedPlan>(File.ReadAllText(Path.ChangeExtension(path, "guided.json")));
            if (plan is null || plan.Baseline is null || plan.Current is null || !Enum.IsDefined(plan.Focus)
                || plan.Step < 0 || plan.Focus == TrainerGuidedFocus.MovementComparison && plan.Step > 6) return null;
            plan.Baseline.Validate(); plan.Current.Validate();
            return plan;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }
    public void SaveGuidedPlan(TrainerGuidedPlan? plan)
    {
        string target = Path.ChangeExtension(path, "guided.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        File.WriteAllText(target + ".tmp", JsonSerializer.Serialize(plan));
        File.Move(target + ".tmp", target, true);
    }
    public TrainerWorkspacePreferences LoadPreferences()
    {
        try { return JsonSerializer.Deserialize<TrainerWorkspacePreferences>(File.ReadAllText(Path.ChangeExtension(path,"preferences.json"))) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void SavePreferences(TrainerWorkspacePreferences preferences)
    {
        string target = Path.ChangeExtension(path,"preferences.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        File.WriteAllText(target+".tmp", JsonSerializer.Serialize(preferences)); File.Move(target+".tmp",target,true);
    }
    public IReadOnlyList<TrainerResult> Load()
    {
        try { return JsonSerializer.Deserialize<TrainerResult[]>(File.ReadAllText(path)) ?? []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
    public void Add(TrainerResult result)
    {
        var results = Load().Where(r => r.Id != result.Id).Append(result).OrderByDescending(r => r.CompletedAt).Take(500).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(results));
        File.Move(path + ".tmp", path, true);
    }
}
