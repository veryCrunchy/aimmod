using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics.Textures;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Skinning;
using osu.Game.Audio;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Types;
using osuTK;

namespace AimMod.Desktop.Trainers;

/// <summary>Short, unranked exercises played by the standard osu! ruleset.</summary>
public sealed class TrainerBeatmap : WorkingBeatmap
{
    private readonly Beatmap<OsuHitObject> map;
    private readonly ITrackStore tracks;
    private readonly string audioName;
    private readonly double musicVolume;
    private TrainerBackground? background;
    internal void SetBackground(byte[]? bytes, osu.Framework.Graphics.Rendering.IRenderer renderer)
    {
        if (bytes is not null) background = new TrainerBackground(bytes, renderer);
    }
    public double SeekTime { get; private set; }
    public TrainerSession Timeline { get; }

    public TrainerBeatmap(TrainerSettings settings, AudioManager audio, double volume)
        : this(Create(settings), new TrainerSession(settings), audio, volume, settings.Music == "cues" ? null : TrainerAudio.Asset(TrainerMusicCatalog.Asset(settings.Music, settings.Bpm)), settings.Music == "cues" ? ".wav" : ".ogg") { }

    private TrainerBeatmap(Beatmap<OsuHitObject> map, TrainerSession timeline, AudioManager audio, double volume, byte[]? sourceAudio = null, string extension = ".wav")
        : base(map.BeatmapInfo, audio)
    {
        this.map = map;
        Timeline = timeline;
        audioName = $"trainer-{Guid.NewGuid():N}{extension}";
        byte[] bytes = sourceAudio ?? TrainerAudio.Render(timeline, map.HitObjects.Select(o => o.StartTime));
        TrainerAudioIdentity.Attach(map.BeatmapInfo, bytes);
        tracks = audio.GetTrackStore(new TrainerAudio { Wave = bytes });
        tracks.Volume.Value = volume;
        musicVolume = volume;
    }

    public static Beatmap<OsuHitObject> Create(TrainerSettings settings) => Create(settings, null);

    public static Beatmap<OsuHitObject> Create(TrainerSettings settings, IReadOnlyList<TrainerNote>? notes, ControlPointInfo? timing = null)
    {
        settings = TrainerSkillProfile.Apply(settings,settings.SkillLimits??new());
        settings.Validate();
        if (settings.Kind == TrainerKind.Reaction) throw new ArgumentException("Reaction uses a separate cue exercise.");
        var timeline = new TrainerSession(settings);
        notes = TrainerReadingPatterns.ConstrainTiming(settings, TrainerSkillProfile.ConstrainNotes(settings, notes ?? timeline.Notes));
        var map = new Beatmap<OsuHitObject> { StackLeniency = 0 };
        map.BeatmapInfo.Ruleset = new OsuRuleset().RulesetInfo;
        map.BeatmapInfo.DifficultyName = NativeTrainersWorkspace.DisplayName(settings.Kind);
        map.Metadata.Title = map.BeatmapInfo.DifficultyName;
        map.Metadata.Artist = "AimMod";
        map.Metadata.Author.Username = "AimMod";
        map.Metadata.AudioFile = "trainer.wav";
        map.Difficulty = new BeatmapDifficulty { CircleSize = settings.CircleSize, ApproachRate = settings.ApproachRate, OverallDifficulty = 5, DrainRate = 0, SliderMultiplier = 1.4f };
        if (timing is not null) map.ControlPointInfo = timing;
        else map.ControlPointInfo.Add(0, new TimingControlPoint { BeatLength = timeline.BeatMs });
        var random = new Random(settings.PatternSeed);
        Vector2[] reading = settings.Kind == TrainerKind.Reading ? TrainerReadingPatterns.Create(settings, notes) : [];
        Vector2[] jumps = settings.Kind is TrainerKind.Aim or TrainerKind.Reading || settings.PathStyle == TrainerPathStyle.Random ? TrainerAimPatterns.Create(settings, notes.Count) : [];
        double travel = 0;
        double previousObjectEnd = 0;
        for (int i = 0; i < notes.Count; i++)
        {
            bool phraseStart = i == 0 || notes[i].Phrase != notes[i - 1].Phrase;
            if (i > 0)
            {
                double gap = notes[i].TimeMs - notes[i - 1].TimeMs;
                travel += settings.Kind switch
                {
                    TrainerKind.Steady => 48,
                    TrainerKind.Bursts when phraseStart => 115,
                    TrainerKind.Rhythm when gap > timeline.BeatMs * .3 => 44,
                    _ => 30,
                };
            }
            Vector2 position = settings.Kind switch
            {
                TrainerKind.Aim => jumps[i],
                TrainerKind.Reading => reading[i],
                _ => settings.PathStyle switch
                {
                    TrainerPathStyle.Arc => new Vector2(256 + 155*(float)Math.Cos(travel*settings.AimSpacing/16000),192+105*(float)Math.Sin(travel*settings.AimSpacing/16000)),
                    TrainerPathStyle.Zigzag => zigzagPosition(travel*settings.AimSpacing/100.0),
                    TrainerPathStyle.Random => jumps[i],
                    _ => streamPosition(travel * settings.AimSpacing / 100.0 + (settings.PatternSeed == 0 ? 0 : Math.Abs((long)settings.PatternSeed)%700)),
                },
            };
            // Connecting taps sit between jump anchors instead of adding equally wide jumps.
            if (settings.Kind == TrainerKind.Aim && notes[i].Pattern is TrainerPattern.JumpFill or TrainerPattern.JumpTriples)
            {
                int group = notes[i].Pattern == TrainerPattern.JumpFill ? 2 : 3;
                int anchor = i / group * group, next = Math.Min(anchor+group, jumps.Length-1);
                position = Vector2.Lerp(jumps[anchor], jumps[next], (i%group)/(float)group);
            }
            bool newCombo = settings.Kind switch
            {
                TrainerKind.Bursts => phraseStart,
                TrainerKind.Alternating => i % 16 == 0,
                TrainerKind.Rhythm => i == 0 || notes[i].Phrase / 2 != notes[i - 1].Phrase / 2,
                TrainerKind.Reading => i % settings.ReadingGroupSize == 0,
                _ => i % 4 == 0,
            };
            if (settings.RandomizePatterns && settings.SkillLimits is {} skill && map.HitObjects.LastOrDefault() is {} previous)
            {
                double availableTime = Math.Max(0,notes[i].TimeMs-previousObjectEnd)/1000;
                double distance = Vector2.Distance(previous.EndPosition,position);
                double maximum = Math.Min(skill.MaxJumpDistance,skill.MaxAimVelocity*availableTime);
                if (distance>maximum && distance>0) position=Vector2.Lerp(previous.EndPosition,position,(float)(maximum/distance));
            }
            bool slider = settings.Sliders != TrainerSliderStyle.None && (settings.Sliders == TrainerSliderStyle.SlidersOnly || (settings.RandomizePatterns || settings.Kind == TrainerKind.Reading ? random.Next(5)==0 : i%8==0));
            // Compact comparisons preserve every target time and the original path geometry.
            position = new Vector2(256, 192) + (position - new Vector2(256, 192)) * (float)settings.MovementScale;
            double beatLength = map.ControlPointInfo.TimingPointAt(notes[i].TimeMs).BeatLength;
            double available = notes[^1].TimeMs - notes[i].TimeMs;
            if (slider && available >= beatLength * settings.SliderBeats + beatLength*.25)
            {
                int repeats = settings.Sliders == TrainerSliderStyle.BackAndForth ? 3 : 0;
                double duration = beatLength * settings.SliderBeats;
                double length = 100 * map.Difficulty.SliderMultiplier * settings.SliderBeats / (repeats+1);
                // Keep even four-beat paths inside the playfield through a curved path.
                Vector2 direction = new(position.X < 256 ? 1 : -1, position.Y < 192 ? .35f : -.35f);
                direction.Normalize();
                double spanLength = Math.Min(length, 220);
                if(settings.RandomizePatterns && settings.SkillLimits is {} sliderSkill)
                    spanLength=Math.Min(spanLength,Math.Min(sliderSkill.MaxJumpDistance,sliderSkill.MaxAimVelocity*duration/1000/(repeats+1)));
                spanLength *= settings.MovementScale;
                var path = new SliderPath([new PathControlPoint(Vector2.Zero, PathType.LINEAR), new PathControlPoint(direction*(float)spanLength)], spanLength);
                var obj = new Slider { StartTime = notes[i].TimeMs, Position = position, NewCombo = newCombo, Path = path, RepeatCount = repeats,
                    SliderVelocityMultiplier = spanLength / length, Samples = [new HitSampleInfo(HitSampleInfo.HIT_NORMAL)] };
                map.HitObjects.Add(obj);
                previousObjectEnd = obj.StartTime+duration;
                // Leave a quarter-beat release gap; circles never demand a tap during a hold.
                double resume = obj.StartTime + duration + beatLength*.25;
                while (i+1 < notes.Count && notes[i+1].TimeMs < resume-.001) i++;
            }
            else
            {
                map.HitObjects.Add(new HitCircle { StartTime = notes[i].TimeMs, Position = position, NewCombo = newCombo, Samples = [new HitSampleInfo(HitSampleInfo.HIT_NORMAL)] });
                previousObjectEnd = notes[i].TimeMs;
            }
        }
        TrainerSpinners.Apply(settings, map);
        return map;
    }

    public static TrainerBeatmap FromSong(TrainerSettings settings, IBeatmap source, byte[] audioBytes, string extension, AudioManager audio, double volume)
    {
        var notes = SongNotes(settings, source);
        if (notes.Count < 4) throw new InvalidDataException("Choose an earlier section or a longer song.");
        var map = Create(settings, notes, source.ControlPointInfo);
        map.Metadata.Title = source.Metadata.Title;
        map.Metadata.Artist = source.Metadata.Artist;
        map.Metadata.BackgroundFile = source.Metadata.BackgroundFile;
        double leadIn = 4 * source.ControlPointInfo.TimingPointAt(notes[0].TimeMs).BeatLength;
        return new TrainerBeatmap(map, new TrainerSession(settings), audio, volume, audioBytes, extension)
        { SeekTime = Math.Max(0, notes[0].TimeMs - leadIn) };
    }

    public static IReadOnlyList<TrainerNote> SongNotes(TrainerSettings settings, IBeatmap source)
    {
        settings = TrainerSkillProfile.Apply(settings,settings.SkillLimits??new());
        settings.Validate();
        if (source.HitObjects.Count == 0 || source.ControlPointInfo.TimingPoints.Count == 0)
            throw new InvalidDataException("This map has no playable timing.");
        double start = source.HitObjects[0].StartTime + settings.SongStartSeconds * 1000;
        double end = Math.Min(start + settings.Seconds * 1000, source.HitObjects[^1].StartTime + 1);
        var points = source.ControlPointInfo.TimingPoints;
        var notes = new List<TrainerNote>();
        int phraseBase = 0;
        for (int p = 0; p < points.Count; p++)
        {
            var point = points[p];
            double beat = point.BeatLength;
            if (!double.IsFinite(beat) || beat < 100 || beat > 3000) throw new InvalidDataException("This song's timing is outside the trainer range.");
            double lower = Math.Max(start, point.Time);
            double upper = Math.Min(end, p + 1 < points.Count ? points[p + 1].Time : end);
            if (lower >= upper) continue;
            int firstBar = Math.Max(0, (int)Math.Floor((lower-point.Time)/(beat*4)));
            int lastBar = (int)Math.Ceiling((upper-point.Time)/(beat*4));
            var segmentSettings=settings with { Bpm=(int)Math.Round(60000/beat) };
            for (int bar = firstBar; bar < lastBar; bar++)
                foreach (var (offset, phrase) in TrainerPatterns.Bar(segmentSettings, bar))
                {
                    double time = point.Time + (bar*4+offset)*beat;
                    if (time >= lower-.00001 && time < upper-.00001) notes.Add(new(time, phraseBase+phrase, TrainerPatterns.PatternAt(settings,bar)));
                }
            phraseBase += lastBar*16+2;
        }
        return TrainerReadingPatterns.ConstrainTiming(settings, TrainerSkillProfile.ConstrainNotes(settings,notes));
    }

    // Sample by distance, not angle, so bends do not produce sudden spacing changes.
    // The closed figure-eight gives continuous streams in both directions without
    // teleporting to the beginning when a long session wraps around.
    private static readonly (Vector2[] Points, double[] Distances) streamPath = createStreamPath();

    private static (Vector2[], double[]) createStreamPath()
    {
        const int samples = 512;
        var points = new Vector2[samples + 1];
        var distances = new double[samples + 1];
        for (int i = 0; i <= samples; i++)
        {
            double phase = i * Math.PI * 2 / samples;
            points[i] = new Vector2(256 + 156 * (float)Math.Sin(phase), 192 + 98 * (float)Math.Sin(phase * 2));
            if (i > 0) distances[i] = distances[i - 1] + Vector2.Distance(points[i - 1], points[i]);
        }
        return (points, distances);
    }

    private static Vector2 streamPosition(double distance)
    {
        distance %= streamPath.Distances[^1];
        int index = Array.BinarySearch(streamPath.Distances, distance);
        if (index >= 0) return streamPath.Points[index];
        index = ~index;
        float progress = (float)((distance - streamPath.Distances[index - 1]) / (streamPath.Distances[index] - streamPath.Distances[index - 1]));
        return Vector2.Lerp(streamPath.Points[index - 1], streamPath.Points[index], progress);
    }
    private static Vector2 zigzagPosition(double travel) => new(100 + (float)(travel%600 < 300 ? travel%300 : 300-travel%300), 192+95*(float)Math.Sin(travel/70));

    protected override IBeatmap GetBeatmap() => map;
    public override Texture GetBackground() => background?.Texture!;
    protected override Track GetBeatmapTrack() => tracks.Get(audioName);
    public override bool TryTransferTrack(WorkingBeatmap target) => false;
    protected override ISkin GetSkin() => null!; // The player's current SkinManager supplies the complete skin.
    public override Stream GetStream(string storagePath) => Stream.Null;
    public void ReleaseAudio() { tracks.Dispose(); background?.Dispose(); }
    public void FadeOutro(double progress) => tracks.Volume.Value = musicVolume * (1-Math.Clamp(progress,0,1));
}
