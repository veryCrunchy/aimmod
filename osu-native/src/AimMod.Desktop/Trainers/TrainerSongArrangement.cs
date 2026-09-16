using System.Collections.Concurrent;
using System.Text.Json;

namespace AimMod.Desktop.Trainers;

/// <summary>Authored instrument onsets and held notes exported with the exact music asset.</summary>
public sealed record TrainerSongArrangement(int Version, string Song, int Bpm, string AudioSha256,
    double OffsetMs, double EndBeat, TrainerMusicSection[] Sections, TrainerMusicEvent[] Events, TrainerMusicHold[] Holds,
    string? SignatureSourceSha256 = null)
{
    private static readonly ConcurrentDictionary<string, Lazy<TrainerSongArrangement?>> cache = new();

    public static TrainerSongArrangement? For(TrainerSettings settings)
    {
        if (!TrainerMusicCatalog.IsSong(settings.Music)) return null;
        string key = $"{settings.Music}-{settings.Bpm}";
        return cache.GetOrAdd(key, k => new(() =>
        {
            using var stream = typeof(TrainerSongArrangement).Assembly.GetManifestResourceStream($"AimMod.Resources.TrainingMusic.{k}.json");
            return stream is null ? null : JsonSerializer.Deserialize<TrainerSongArrangement>(stream);
        })).Value;
    }

    public double BeatAt(double time) => (time - OffsetMs) * Bpm / 60000;
    public double TimeAt(double beat) => OffsetMs + beat * 60000 / Bpm;
    public TrainerMusicSection? SectionAt(double beat) => Sections.FirstOrDefault(s => beat >= s.StartBeat && beat < s.EndBeat);

    private static T? Near<T>(T[] sorted, double beat, Func<T,double> time, Func<T,double> strength) where T : class
    {
        int low = 0, high = sorted.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (time(sorted[middle]) < beat - .025) low = middle + 1;
            else high = middle;
        }
        T? best = null;
        for (int i = low; i < sorted.Length && time(sorted[i]) < beat + .025; i++)
            if (best is null || strength(sorted[i]) > strength(best)) best = sorted[i];
        return best;
    }

    public IReadOnlyList<TrainerNote> Arrange(TrainerSettings settings, IReadOnlyList<TrainerNote> original, double start, double end)
    {
        double beatMs = 60000.0 / Bpm;
        IEnumerable<TrainerNote> notes = original;
        // Fixed tapping, burst and stream drills retain their prescribed rhythm.
        // Free aim/reading phrases can instead follow the performed melody and bass.
        bool musicalRhythm = settings.Kind is TrainerKind.Aim or TrainerKind.Reading
            && settings.Pattern == TrainerPattern.Standard && settings.NoteSpeed == TrainerNoteSpeed.Default && !settings.RandomizePatterns;
        double step = TrainerPatterns.Step(settings);
        bool adaptive = settings.AdaptiveDifficulty && settings.Kind is not (TrainerKind.Spinner or TrainerKind.Reaction);
        if (adaptive)
            notes = TrainerMusicalPhrases.Select(this, settings, start, end);
        else if (settings.Sliders == TrainerSliderStyle.SlidersOnly)
            notes = Holds.Where(h => h.Beats >= .55).Select(h => new TrainerNote(TimeAt(h.Beat), (int)(h.Beat / 4), settings.Pattern));
        else if (musicalRhythm)
            notes = Events.Where(e => e.Strength >= .7).Select(e => new TrainerNote(TimeAt(e.Beat), (int)(e.Beat / 4), settings.Pattern));

        var result = new List<TrainerNote>();
        foreach (var note in notes.OrderBy(n => n.TimeMs))
        {
            if (note.TimeMs < start - .001 || note.TimeMs >= Math.Min(end, TimeAt(EndBeat))) continue;
            if (!adaptive && (musicalRhythm || settings.Sliders == TrainerSliderStyle.SlidersOnly) && result.Count > 0
                && note.TimeMs - result[^1].TimeMs < step * beatMs - .001) continue;
            double beat = BeatAt(note.TimeMs);
            var section = SectionAt(beat);
            var onset = Near(Events, beat, static e => e.Beat, static e => e.Strength);
            var hold = Near(Holds, beat, static h => h.Beat, static h => (h.Melody ? 100 : 0) + h.Beats);
            // Sustained background harmony must not turn every downbeat into a slider.
            if (adaptive && hold is { Melody: false, Quiet: false }) hold = null;
            result.Add(note with
            {
                MusicAccent = onset?.Strength ?? .35,
                MusicEnergy = section?.Energy ?? .7,
                MusicHoldBeats = hold is null ? 0 : Math.Min(hold.Beats, Math.Min(settings.SliderBeats, (end - note.TimeMs) / beatMs - .25)),
            });
        }
        return result;
    }
}

public sealed record TrainerMusicSection(double StartBeat, double EndBeat, double Energy, bool Quiet);
public sealed record TrainerMusicEvent(double Beat, double Strength, int Pitch);
public sealed record TrainerMusicHold(double Beat, double Beats, bool Quiet, bool Melody);
