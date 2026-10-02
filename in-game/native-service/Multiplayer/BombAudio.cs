using System.Runtime.InteropServices;
using System.Text;

namespace AimMod.InGame.Multiplayer;

// CS bomb and round sounds (in-game/docs/game-modes.md 6.6.3). Every sound is AimMod's own,
// synthesised here from sine tones, noise and envelopes (no recorded, game or other game's
// audio); `--write-bomb-sounds <folder>` writes them as WAV files to listen to.
//  - The planted bomb beeps once a second at 40 s left, faster and faster down to about 7 a
//    second, then a rising tone in the last second. The beep comes from the bomb: its volume
//    falls with distance and it pans left or right with the bomb's bearing from where you look.
//  - Plant start (keypad presses) and plant complete, defuse start and defuse complete, the
//    defuse kit click, the explosion (positional), the "bomb planted" alert (2D, everyone),
//    and small ticks when your team's bomb is dropped or picked up.
// Played through Windows audio (winmm), mixed by AimMod, at the "Bomb and round sounds" volume.
// UE can't play a runtime-built sound from outside the engine without engine code AimMod
// doesn't call (USoundWaveProcedural's audio queue isn't reflected), so these stay outside
// the game's mixer: KovaaK's master volume doesn't apply, AimMod's own volume does.
static class BombSounds
{
    public const int Rate = 44100;
    public const double BombSeconds = CsRules.BombMs / 1000.0;
    // The rising tone that replaces the beeps before the bomb goes off.
    public const double FinalToneSeconds = 1.0;
    public const double FastestBeep = 0.13; // s between beeps at the end (about 7.7 a second)

    // Seconds between beeps with `remaining` seconds left: 1 s at 40 s, 0.29 s at 10 s, 0.15 s at 5 s,
    // never faster than FastestBeep.
    public static double BeepInterval(double remaining) => Math.Clamp(Math.Pow(Math.Clamp(remaining, 0, BombSeconds) / BombSeconds, 0.9), FastestBeep, 1.0);

    // The beeps fall at fixed times before the explosion (40 s left, then every BeepInterval), so every
    // machine beeps together and AimModCore's bomb light (CsGear LightOn) flashes with it: the next
    // beep at or below `remaining` seconds left.
    public static double NextBeep(double remaining)
    {
        var beep = BombSeconds;
        for (var i = 0; i < 1000 && beep > remaining; i++) beep -= BeepInterval(beep);
        return beep;
    }

    // Distance attenuation (cm): full volume within 3 m, half at about 18 m, never below 6 %
    // so the bomb stays audible across a map.
    public static double Attenuation(double distanceCm)
    {
        var d = Math.Max(0, distanceCm - 300) / 1500;
        return Math.Max(0.06, 1 / (1 + d * d));
    }

    // Equal-power stereo gains for a source at (dx, dy) from the listener looking along yaw
    // (degrees; Unreal: positive yaw turns right). Behind you is a little quieter.
    public static (double Left, double Right) Pan(double dx, double dy, double yawDegrees)
    {
        if (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6) return (Math.Sqrt(0.5), Math.Sqrt(0.5));
        var bearing = Math.Atan2(dy, dx) - yawDegrees * Math.PI / 180;
        var side = Math.Sin(bearing);          // +1 right, -1 left
        var front = Math.Cos(bearing);         // +1 ahead, -1 behind
        var angle = (side * 0.85 + 1) * Math.PI / 4; // keep a little of the far ear
        var rear = front < 0 ? 1 + 0.2 * front : 1;
        return (Math.Cos(angle) * rear, Math.Sin(angle) * rear);
    }

    // ---- synthesis ---------------------------------------------------------------------------
    internal static int N(double seconds) => (int)Math.Round(seconds * Rate);
    internal static float[] Buffer(double seconds) => new float[N(seconds)];
    // Attack, hold and exponential release (seconds).
    internal static double Env(double t, double attack, double length, double release)
    {
        if (t < 0 || t > length) return 0;
        if (t < attack) return t / attack;
        var tail = length - release;
        return t <= tail ? 1 : Math.Exp(-5 * (t - tail) / release);
    }
    internal static void Tone(float[] s, double start, double length, double freq, double gain, double attack = 0.003, double release = 0.03, double harmonic = 0.25)
    {
        var from = N(start); var count = N(length);
        for (var i = 0; i < count && from + i < s.Length; i++)
        {
            var t = i / (double)Rate;
            var phase = 2 * Math.PI * freq * t;
            s[from + i] += (float)(gain * Env(t, attack, length, release) * (Math.Sin(phase) + harmonic * Math.Sin(2 * phase)) / (1 + harmonic));
        }
    }
    // Deterministic noise, so every machine builds the same sounds.
    internal sealed class Noise(uint seed)
    {
        uint state = seed;
        public double Next() { state = state * 1664525 + 1013904223; return (state >> 8) / (double)(1 << 24) * 2 - 1; }
    }
    internal static void Burst(float[] s, double start, double length, double gain, double cutoffHz, uint seed, double decay = 30)
    {
        var noise = new Noise(seed); var from = N(start); var count = N(length);
        var a = 1 - Math.Exp(-2 * Math.PI * cutoffHz / Rate); double low = 0;
        for (var i = 0; i < count && from + i < s.Length; i++)
        {
            var t = i / (double)Rate;
            low += a * (noise.Next() - low);
            s[from + i] += (float)(gain * low * Math.Exp(-decay * t) * Math.Min(1, t / 0.002));
        }
    }
    internal static float[] Normalize(float[] s, double peak)
    {
        var max = s.Length == 0 ? 0 : s.Max(Math.Abs);
        if (max > 0) for (var i = 0; i < s.Length; i++) s[i] = (float)(s[i] / max * peak);
        return s;
    }

    // A short, soft blip around 1 kHz (CS's is a mid-pitched chirp, not a shrill one).
    public static float[] Beep() { var s = Buffer(0.13); Tone(s, 0, 0.12, 988, 1, 0.004, 0.06, 0.18); return Normalize(s, 0.5); }
    // A sweep from 700 Hz to 1.6 kHz, getting louder, over the last second.
    public static float[] FinalTone()
    {
        var s = Buffer(FinalToneSeconds + 0.05); double phase = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var t = i / (double)Rate; var u = Math.Min(1, t / FinalToneSeconds);
            var f = 700 * Math.Pow(1600 / 700.0, u);
            phase += 2 * Math.PI * f / Rate;
            s[i] = (float)((0.5 + 0.5 * u) * Env(t, 0.01, FinalToneSeconds + 0.05, 0.04) * (Math.Sin(phase) + 0.2 * Math.Sin(2 * phase)));
        }
        return Normalize(s, 0.55);
    }
    // Three keypad presses.
    public static float[] PlantStart()
    {
        var s = Buffer(0.4);
        double[] keys = [1209, 1336, 1477];
        for (var k = 0; k < 3; k++) { Tone(s, k * 0.12, 0.06, keys[k], 0.8, 0.002, 0.03, 0.1); Burst(s, k * 0.12, 0.01, 0.3, 4000, 11u + (uint)k, 300); }
        return Normalize(s, 0.6);
    }
    // Armed: two rising chirps.
    public static float[] PlantDone() { var s = Buffer(0.25); Tone(s, 0, 0.08, 880, 1); Tone(s, 0.1, 0.12, 1175, 1); return Normalize(s, 0.55); }
    // A snip and a low click.
    public static float[] DefuseStart() { var s = Buffer(0.2); Burst(s, 0, 0.07, 1, 2500, 23, 40); Tone(s, 0.06, 0.05, 660, 0.7, 0.002, 0.03, 0.1); return Normalize(s, 0.6); }
    // Disarmed: three falling notes.
    public static float[] DefuseDone()
    {
        var s = Buffer(0.5);
        double[] notes = [1568, 1175, 784];
        for (var k = 0; k < 3; k++) Tone(s, k * 0.13, 0.16, notes[k], 1, 0.004, 0.1, 0.15);
        return Normalize(s, 0.7);
    }
    // A metallic click.
    public static float[] KitPickup() { var s = Buffer(0.12); Burst(s, 0, 0.02, 1, 6000, 31, 200); Tone(s, 0.005, 0.08, 1568, 0.6, 0.001, 0.07, 0.3); return Normalize(s, 0.45); }
    // A low thump with a long rumble.
    public static float[] Explosion()
    {
        var s = Buffer(2.4); var noise = new Noise(47); double low = 0, lower = 0, phase = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var t = i / (double)Rate;
            var cutoff = 200 + 2200 * Math.Exp(-6 * t);
            var a = 1 - Math.Exp(-2 * Math.PI * cutoff / Rate);
            low += a * (noise.Next() - low); lower += 0.3 * a * (low - lower);
            phase += 2 * Math.PI * (38 + 60 * Math.Exp(-12 * t)) / Rate;
            var env = Math.Min(1, t / 0.004) * Math.Exp(-2.2 * t);
            s[i] = (float)(env * (0.9 * lower + 0.6 * low * Math.Exp(-4 * t) + 0.7 * Math.Sin(phase) * Math.Exp(-5 * t)));
        }
        return Normalize(s, 0.95);
    }
    // "The bomb has been planted": three rising notes, for everyone.
    public static float[] PlantedAlert()
    {
        var s = Buffer(0.55);
        double[] notes = [523.25, 659.25, 783.99];
        for (var k = 0; k < 3; k++) Tone(s, k * 0.15, 0.17, notes[k], 1, 0.006, 0.08, 0.35);
        return Normalize(s, 0.6);
    }
    // The knife: a swish of air (noise swept through a resonant band) for a slash or stab that meets
    // nothing, and a short dull thud with a scrape when it lands.
    internal static float[] Swish(double seconds, double from, double to, uint seed, double peak)
    {
        var s = Buffer(seconds); var noise = new Noise(seed); double low = 0, band = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var t = i / (double)Rate; var u = t / seconds;
            var f = from * Math.Pow(to / from, u);
            var a = 1 - Math.Exp(-2 * Math.PI * f / Rate);
            low += a * (noise.Next() - low); band += a * (low - band);
            s[i] = (float)((low - band) * Math.Sin(Math.PI * Math.Min(1, u * 1.15)) * (1 - 0.3 * u));
        }
        return Normalize(s, peak);
    }
    public static float[] KnifeSwish() => Swish(0.2, 900, 3800, 59, 0.5);
    public static float[] StabSwish() => Swish(0.28, 600, 2400, 61, 0.55);
    static float[] Thud(double freq, double seconds, uint seed, double peak)
    {
        var s = Buffer(seconds); double phase = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var t = i / (double)Rate;
            phase += 2 * Math.PI * freq * (1 + Math.Exp(-30 * t)) / Rate;
            s[i] = (float)(Math.Sin(phase) * Math.Exp(-18 * t) * Math.Min(1, t / 0.002));
        }
        Burst(s, 0, 0.06, 0.7, 3000, seed, 45);
        return Normalize(s, peak);
    }
    public static float[] KnifeHit() { var s = Thud(140, 0.18, 67, 0.75); return s; }
    public static float[] StabHit() => Thud(95, 0.28, 71, 0.85);

    public static float[] Tick(double freq) { var s = Buffer(0.04); Tone(s, 0, 0.03, freq, 1, 0.001, 0.02, 0.1); return Normalize(s, 0.4); }

    public static readonly IReadOnlyDictionary<string, Func<float[]>> All = new Dictionary<string, Func<float[]>>
    {
        ["beep"] = Beep, ["final"] = FinalTone, ["plant-start"] = PlantStart, ["plant-done"] = PlantDone, ["defuse-start"] = DefuseStart,
        ["defuse-done"] = DefuseDone, ["kit"] = KitPickup, ["explosion"] = Explosion, ["planted-alert"] = PlantedAlert,
        ["dropped"] = () => Tick(1400), ["picked"] = () => Tick(2600),
        ["knife-swish"] = KnifeSwish, ["knife-stab-swish"] = StabSwish, ["knife-hit"] = KnifeHit, ["knife-stab"] = StabHit,
    }.Concat(GrenadeSounds.All).ToDictionary(kv => kv.Key, kv => kv.Value);

    // 16-bit mono PCM WAV.
    public static byte[] Wav(float[] samples)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.ASCII);
        w.Write("RIFF"u8); w.Write(36 + samples.Length * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples.Length * 2);
        foreach (var v in samples) w.Write((short)Math.Round(Math.Clamp(v, -1f, 1f) * 32767));
        w.Flush();
        return stream.ToArray();
    }

    // Diagnostic: --write-bomb-sounds <folder>.
    public static int WriteFiles(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var (name, make) in All) File.WriteAllBytes(Path.Combine(folder, "aimmod-" + name + ".wav"), Wav(make()));
        Console.WriteLine(All.Count + " sounds written to " + folder);
        return 0;
    }
}

// A sound to play: the sound's name, and where it comes from (world cm), or null for 2D.
sealed record RoundCue(string Sound, double[]? From = null, double Gain = 1);

// What a CS round sounds like from one player's view: the cues for what changed since the last
// view (events, the kit), and the planted bomb's beep state. Pure; BombAudio plays it.
sealed class RoundSoundPlan
{
    string? matchKey; long lastEvent = -1; bool hadKit;

    // A new match (or the first view) only sets the baseline: nothing that already happened plays.
    public IReadOnlyList<RoundCue> Update(string matchId, CsView cs, string self)
    {
        var cues = new List<RoundCue>();
        var me = cs.Players.FirstOrDefault(p => p.Member == self);
        var latest = cs.Events.Count > 0 ? cs.Events.Max(e => e.Id) : 0;
        if (matchKey != matchId) { matchKey = matchId; lastEvent = latest; hadKit = me?.Kit ?? false; return cues; }
        var site = cs.Sites?.FirstOrDefault(s => s.Name == cs.Bomb.Site) is { } sv ? new[] { sv.X, sv.Y, sv.Z } : null;
        foreach (var e in cs.Events.Where(e => e.Id > lastEvent).OrderBy(e => e.Id))
        {
            var mine = e.Member == self;
            switch (e.Kind)
            {
                case "planting": cues.Add(new("plant-start", mine ? null : site, mine ? 1 : 0.8)); break;
                case "planted":
                    cues.Add(new("plant-done", mine ? null : cs.Bomb.Position ?? site));
                    cues.Add(new("planted-alert"));
                    break;
                case "defusing": cues.Add(new("defuse-start", mine ? null : cs.Bomb.Position)); break;
                case "defused": cues.Add(new("defuse-done")); break;
                case "exploded": cues.Add(new("explosion", cs.Bomb.Position)); break;
                case "bomb-dropped" or "bomb-picked" when me?.Side == CsRules.T: cues.Add(new(e.Kind == "bomb-dropped" ? "dropped" : "picked")); break;
            }
        }
        lastEvent = Math.Max(lastEvent, latest);
        // The kit: a click when it's yours (bought this round).
        var kit = me?.Kit ?? false;
        if (kit && !hadKit) cues.Add(new("kit"));
        hadKit = kit;
        return cues;
    }

    // The planted bomb's state for the beep: where it is and when it goes off (host clock), or null.
    public static (double[] At, long ExplodesAt)? Beeping(CsView cs) =>
        cs.Bomb is { State: "planted", Position: { Length: 3 } at, ExplodesAt: { } explodes } ? (at, explodes) : null;
}

// Plays RoundSoundPlan's cues and the bomb beep: a small mixer on a background thread (stereo
// 16-bit, 44.1 kHz) writing to winmm's default output. The device opens with the first sound
// and closes after a few quiet seconds. Never opened unless DeviceAllowed (the real service
// sets it; self-tests don't, so they never make a sound).
sealed class BombAudio : IDisposable
{
    public static bool DeviceAllowed { get; set; }
    readonly Dictionary<string, float[]> sounds = new();
    readonly object gate = new();
    sealed class Voice { public required float[] Samples; public int At; public int Delay; public float Left, Right; }
    readonly List<Voice> voices = [];
    // Beep: when the bomb goes off (local unix ms), and its current gains from the listener.
    long? explodesAtLocal; float beepLeft, beepRight; long nextBeepAt; bool finalPlayed;
    double volume = 0.7;
    const double Headroom = 0.35;
    Thread? thread; volatile bool stopping; long failedAt = long.MinValue / 2;
    public int Played { get; private set; }

    float[] Sound(string name)
    {
        if (!sounds.TryGetValue(name, out var s)) sounds[name] = s = BombSounds.All.TryGetValue(name, out var make) ? make() : [];
        return s;
    }

    // Listener: the local camera (x, y, yaw), or null when unknown (sounds play centred).
    public static (float Left, float Right) Gains(double[]? from, (double X, double Y, double Yaw)? listener, double gain)
    {
        if (from is null || listener is not { } l) return ((float)(gain * Math.Sqrt(0.5) * 1.41), (float)(gain * Math.Sqrt(0.5) * 1.41));
        var dx = from[0] - l.X; var dy = from[1] - l.Y;
        var near = BombSounds.Attenuation(Math.Sqrt(dx * dx + dy * dy));
        var (left, right) = BombSounds.Pan(dx, dy, l.Yaw);
        return ((float)(gain * near * left * 1.41), (float)(gain * near * right * 1.41));
    }

    public void Play(RoundCue cue, (double X, double Y, double Yaw)? listener)
    {
        if (volume <= 0) return;
        // The explosion is loud anywhere: never below a third of its volume.
        var (l, r) = Gains(cue.From, listener, cue.Gain);
        if (cue.Sound == "explosion") { l = Math.Max(l, 0.35f); r = Math.Max(r, 0.35f); }
        lock (gate) { voices.Add(new Voice { Samples = Sound(cue.Sound), Left = l, Right = r }); Played++; }
        Start();
    }

    // Called every service tick: the volume, the planted bomb (local ms) and the listener.
    public void Update(double newVolume, (double[] At, long ExplodesAtLocal)? bomb, (double X, double Y, double Yaw)? listener)
    {
        lock (gate)
        {
            volume = Math.Clamp(newVolume, 0, 1);
            if (bomb is not { } b) { explodesAtLocal = null; finalPlayed = false; return; }
            if (explodesAtLocal != b.ExplodesAtLocal) { explodesAtLocal = b.ExplodesAtLocal; nextBeepAt = 0; finalPlayed = false; }
            (beepLeft, beepRight) = Gains(b.At, listener, 1);
        }
        if (volume > 0) Start();
    }

    void Start()
    {
        if (!DeviceAllowed || !OperatingSystem.IsWindows()) return;
        lock (gate)
        {
            if (thread is not null || Environment.TickCount64 - failedAt < 10_000) return;
            stopping = false;
            thread = new Thread(Pump) { IsBackground = true, Name = "AimMod round sounds", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }
    }

    // Fills `frames` stereo frames starting at unix ms `now` (the beep schedule runs here, so it
    // keeps time between service ticks). Public for the self-checks.
    public void Mix(short[] output, int frames, long now)
    {
        Span<float> mix = frames * 2 <= 4096 ? stackalloc float[frames * 2] : new float[frames * 2];
        mix.Clear();
        lock (gate)
        {
            if (explodesAtLocal is { } gone && now - gone > 1000) explodesAtLocal = null; // over: nothing more to beep
            if (explodesAtLocal is { } at)
            {
                var remaining = (at - now) / 1000.0;
                if (remaining <= BombSounds.FinalToneSeconds && remaining > -0.2 && !finalPlayed)
                {
                    finalPlayed = true;
                    voices.Add(new Voice { Samples = Sound("final"), Left = beepLeft, Right = beepRight });
                }
                else if (remaining > BombSounds.FinalToneSeconds)
                {
                    if (nextBeepAt == 0) nextBeepAt = at - (long)(BombSounds.NextBeep(remaining) * 1000);
                    var end = now + frames * 1000L / BombSounds.Rate;
                    while (nextBeepAt < end)
                    {
                        var delay = (int)Math.Clamp((nextBeepAt - now) * BombSounds.Rate / 1000, 0, frames - 1);
                        voices.Add(new Voice { Samples = Sound("beep"), Left = beepLeft, Right = beepRight, Delay = delay });
                        nextBeepAt += (long)(BombSounds.BeepInterval((at - nextBeepAt) / 1000.0) * 1000);
                    }
                }
            }
            // Perceptual (the slider feels even), with headroom: these play outside KovaaK's mixer, at
            // Windows volume, so full scale would sit far above the game's own sounds.
            var gain = (float)(volume * volume * Headroom);
            foreach (var v in voices)
            {
                var start = v.Delay; v.Delay = 0;
                for (var i = start; i < frames && v.At < v.Samples.Length; i++, v.At++)
                {
                    var x = v.Samples[v.At] * gain;
                    mix[i * 2] += x * v.Left; mix[i * 2 + 1] += x * v.Right;
                }
            }
            voices.RemoveAll(v => v.At >= v.Samples.Length);
        }
        for (var i = 0; i < frames * 2; i++)
        {
            var x = mix[i];
            x = x > 1 ? 1 : x < -1 ? -1 : x - x * x * x / 3 * 0.25f; // gentle soft clip
            output[i] = (short)Math.Round(Math.Clamp(x, -1f, 1f) * 32000);
        }
    }

    bool Busy() { lock (gate) return voices.Count > 0 || explodesAtLocal is not null; }

    // winmm output: four 23 ms buffers kept queued.
    const int Frames = 1024, Buffers = 4;
    [StructLayout(LayoutKind.Sequential)] struct WaveFormat { public ushort Tag, Channels; public uint Rate, BytesPerSecond; public ushort BlockAlign, Bits, Extra; }
    [StructLayout(LayoutKind.Sequential)] struct WaveHeader { public nint Data; public uint Length, Recorded; public nint User; public uint Flags, Loops; public nint Next, Reserved; }
    [DllImport("winmm.dll")] static extern int waveOutOpen(out nint device, uint id, ref WaveFormat format, nint callback, nint instance, uint flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(nint device, nint header, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(nint device, nint header, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(nint device, nint header, int size);
    [DllImport("winmm.dll")] static extern int waveOutReset(nint device);
    [DllImport("winmm.dll")] static extern int waveOutClose(nint device);
    const uint WaveMapper = 0xFFFFFFFF, Done = 1;

    void Pump()
    {
        var format = new WaveFormat { Tag = 1, Channels = 2, Rate = BombSounds.Rate, Bits = 16, BlockAlign = 4, BytesPerSecond = BombSounds.Rate * 4 };
        if (waveOutOpen(out var device, WaveMapper, ref format, 0, 0, 0) != 0) { lock (gate) { thread = null; voices.Clear(); failedAt = Environment.TickCount64; } return; }
        var size = Marshal.SizeOf<WaveHeader>();
        var headers = new nint[Buffers]; var data = new nint[Buffers]; var pcm = new short[Frames * 2];
        try
        {
            for (var i = 0; i < Buffers; i++)
            {
                data[i] = Marshal.AllocHGlobal(Frames * 4);
                headers[i] = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(new WaveHeader { Data = data[i], Length = Frames * 4, Flags = Done }, headers[i], false);
            }
            var quietSince = Environment.TickCount64;
            while (!stopping)
            {
                var wrote = false;
                for (var i = 0; i < Buffers; i++)
                {
                    var header = Marshal.PtrToStructure<WaveHeader>(headers[i]);
                    if ((header.Flags & Done) == 0) continue;
                    waveOutUnprepareHeader(device, headers[i], size);
                    Mix(pcm, Frames, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    Marshal.Copy(pcm, 0, data[i], pcm.Length);
                    Marshal.StructureToPtr(new WaveHeader { Data = data[i], Length = Frames * 4 }, headers[i], false);
                    waveOutPrepareHeader(device, headers[i], size);
                    waveOutWrite(device, headers[i], size);
                    wrote = true;
                }
                if (Busy()) quietSince = Environment.TickCount64;
                else if (Environment.TickCount64 - quietSince > 3000) break; // quiet: give the device back
                if (!wrote) Thread.Sleep(5);
            }
        }
        finally
        {
            waveOutReset(device);
            for (var i = 0; i < Buffers; i++) { if (headers[i] != 0) { waveOutUnprepareHeader(device, headers[i], size); Marshal.FreeHGlobal(headers[i]); } if (data[i] != 0) Marshal.FreeHGlobal(data[i]); }
            waveOutClose(device);
            lock (gate) thread = null;
        }
    }

    public void Dispose() { stopping = true; Thread? t; lock (gate) t = thread; t?.Join(500); }
}
