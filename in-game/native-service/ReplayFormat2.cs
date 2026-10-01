using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AimMod.InGame;

/// <summary>
/// Format 2 motion, queried at any playback instant: view rotation at every
/// recorded engine frame (from the input stream) and piecewise-linear camera
/// location, FOV and target tracks. Playback samples this directly instead of
/// re-interpolating 60 Hz frames, so motion is reproduced at render rate.
/// </summary>
sealed class Motion
{
    internal sealed record Track(double Id, string Profile, double[] T, bool[] Start, double[][] V, double[] HealthT, double[] Health,
        double[] RotationT, bool[] RotationStart, double[][] Rotation);
    readonly double[] times, pitch, yaw, roll, cameraT;
    readonly double[][] camera;
    readonly Track[] tracks;
    internal Motion(double[] times, double[] pitch, double[] yaw, double[] roll, double[] cameraT, double[][] camera, Track[] tracks)
    { this.times = times; this.pitch = pitch; this.yaw = yaw; this.roll = roll; this.cameraT = cameraT; this.camera = camera; this.tracks = tracks; }
    public int EngineFrames => times.Length;
    public double[] Times => times;

    static double Angle(double a, double b, double u) => a + (((b - a + 540) % 360 + 360) % 360 - 180) * u;
    // Index of the first element >= t.
    static int Lower(double[] values, double t) { int i = Array.BinarySearch(values, t); return i >= 0 ? i : ~i; }
    static double[]? At(double[] t, bool[]? start, double[][] v, double time, bool clampEnds)
    {
        if (t.Length == 0) return null;
        var i = Lower(t, time);
        if (i < t.Length && t[i] == time) return v[i];
        if (i == 0) return clampEnds ? v[0] : null;
        if (i >= t.Length) return clampEnds ? v[^1] : null;
        if (start is not null && start[i]) return null; // gap between segments
        var u = (time - t[i - 1]) / (t[i] - t[i - 1]);
        var a = v[i - 1]; var b = v[i]; var r = new double[a.Length];
        for (int k = 0; k < r.Length; k++) r[k] = a[k] + (b[k] - a[k]) * u;
        return r;
    }

    public (double Pitch, double Yaw, double Roll) Rotation(double time)
    {
        var hi = Lower(times, time);
        if (hi < times.Length && times[hi] == time) return (pitch[hi], yaw[hi], roll[hi]);
        if (hi <= 0) return (pitch[0], yaw[0], roll[0]);
        if (hi >= times.Length) return (pitch[^1], yaw[^1], roll[^1]);
        var lo = hi - 1; var u = (time - times[lo]) / (times[hi] - times[lo]);
        return (pitch[lo] + (pitch[hi] - pitch[lo]) * u, Angle(yaw[lo], yaw[hi], u), Angle(roll[lo], roll[hi], u));
    }

    /// <summary>Camera row (x y z pitch yaw roll fov), targets, health and appearance at `time`.</summary>
    public (double[] Camera, double[][] Actors, ReplayHealth[]? Health, ReplayAppearance[]? Appearance) Pose(double time)
    {
        var c = At(cameraT, null, camera, time, true)!;
        var (p, y, r) = Rotation(time);
        var actors = new List<double[]>(tracks.Length); var health = new List<ReplayHealth>(); var appearance = new List<ReplayAppearance>();
        foreach (var track in tracks)
        {
            var v = At(track.T, track.Start, track.V, time, false);
            if (v is null || actors.Count >= 128) continue;
            actors.Add([track.Id, v[0], v[1], v[2], v[3], v[4]]);
            var h = Lower(track.HealthT, time);
            if (h < track.HealthT.Length && track.HealthT[h] == time) h++;
            if (h > 0 && track.Health[h - 1] >= 0) health.Add(new(track.Id, track.Health[h - 1]));
            if (track.Profile.Length == 0) continue;
            var rot = At(track.RotationT, track.RotationStart, track.Rotation, time, false);
            if (rot is not null) appearance.Add(new(track.Id, track.Profile, [rot[0], ((rot[1] + 180) % 360 + 360) % 360 - 180, rot[2]]));
        }
        return ([c[0], c[1], c[2], p, y, r, c[3]], actors.ToArray(), health.Count > 0 ? health.ToArray() : null, appearance.Count > 0 ? appearance.ToArray() : null);
    }
}

/// <summary>
/// Compact replay format 2 (see in-game/native-mod/DESIGN.md): the input stream
/// plus sparse keyframes, decoded into the same NativeReplay model as format 1.
/// </summary>
static class ReplayFormat2
{
    static readonly byte[] Magic = "AMRPLAY2"u8.ToArray();
    static readonly string[] Actions = [
        "AxisTurn", "AxisLookUp", "AxisMoveForward", "AxisMoveRight", "FirePressed", "FireReleased", "AltFirePressed",
        "AltFireReleased", "JumpPressed", "JumpReleased", "CrouchPressed", "CrouchReleased", "ReloadPressed", "ReloadReleased",
        "ADSPressed", "ADSReleased", "AbilityPressed", "AbilityReleased", "WeaponPressed", "WeaponReleased"];
    const double TimeUnits = 20000, PositionScale = 100, KeyAngleScale = 10000, FovScale = 10000, AngleScale = 100, HealthScale = 10000;
    static readonly double[] StatScales = [1000, 1, 1, 1, 1e6, 1000];
    const int MaxFrames = 20_000_000, MaxGridFrames = 72000, MaxActors = 128;

    public static bool IsFormat2(ReadOnlySpan<byte> start) => start.Length >= 8 && start[..8].SequenceEqual(Magic);

    /// <summary>Header JSON only (no body decode); null if not format 2.</summary>
    public static JsonDocument? ReadHeader(Stream stream)
    {
        Span<byte> head = stackalloc byte[16];
        if (stream.Read(head) != 16 || !IsFormat2(head) || BinaryPrimitives.ReadUInt32LittleEndian(head[8..]) != 2) return null;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
        if (length is 0 or > 65536) throw new InvalidDataException("Invalid replay header");
        var json = new byte[length];
        stream.ReadExactly(json);
        return JsonDocument.Parse(json);
    }

    sealed class Reader(byte[] data)
    {
        int p;
        public bool End => p == data.Length;
        public byte U8() => p < data.Length ? data[p++] : throw new InvalidDataException("Truncated replay");
        public ulong Var()
        {
            ulong v = 0;
            for (int shift = 0; shift < 64; shift += 7) { var b = U8(); v |= (ulong)(b & 0x7f) << shift; if ((b & 0x80) == 0) return v; }
            throw new InvalidDataException("Invalid varint");
        }
        public long SVar() { var v = Var(); return (long)(v >> 1) ^ -(long)(v & 1); }
        public double F64() { if (data.Length - p < 8) throw new InvalidDataException(); var v = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(p)); p += 8; return v; }
        public float F32() { if (data.Length - p < 4) throw new InvalidDataException(); var v = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(p)); p += 4; return v; }
        public int Count(int limit) { var n = Var(); return n <= (ulong)limit ? (int)n : throw new InvalidDataException("Count out of range"); }
        public string Str() { var n = Count(4096); if (data.Length - p < n) throw new InvalidDataException(); var s = Encoding.UTF8.GetString(data, p, n); p += n; return s; }
    }

    record struct Point(int Frame, bool Start, double[] V);

    public static NativeReplay Decode(byte[] file, string id)
    {
        if (!IsFormat2(file) || file.Length < 24) throw new InvalidDataException("Not a format 2 replay");
        var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12));
        if (headerLength is <= 0 or > 65536 || 16 + headerLength + 8 > file.Length) throw new InvalidDataException();
        using var header = JsonDocument.Parse(file.AsMemory(16, headerLength));
        var h = header.RootElement;
        string Text(string key) => h.GetProperty(key).GetString() ?? throw new InvalidDataException();
        if (Text("kind") != "header" || h.GetProperty("version").GetInt32() != 2 || Text("id") != id || Text("coordinates") != "unreal-centimeters")
            throw new InvalidDataException("Unsupported replay header");
        if (!DateTimeOffset.TryParse(Text("recordedAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)) throw new InvalidDataException();
        var at = 16 + headerLength;
        var algorithm = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at));
        var rawSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4));
        if (rawSize is < 0 or > 256 * 1024 * 1024) throw new InvalidDataException();
        var body = Decompress(algorithm, file.AsSpan(at + 8), rawSize);
        var r = new Reader(body);

        var frameCount = r.Count(MaxFrames);
        var times = new double[frameCount]; ulong units = 0;
        for (int i = 0; i < frameCount; i++) { units += r.Var(); times[i] = units / TimeUnits; }
        if (frameCount < 2) throw new InvalidDataException("Replay has no frames");
        var quantum = r.F64();
        var inputCount = r.Count(MaxFrames);
        var inputFrames = new int[inputCount]; var inputActions = new byte[inputCount]; var inputValues = new double[inputCount];
        int frame = 0;
        for (int i = 0; i < inputCount; i++) { frame += (int)r.Var(); if (frame >= frameCount) throw new InvalidDataException(); inputFrames[i] = frame; }
        for (int i = 0; i < inputCount; i++) { inputActions[i] = r.U8(); if (inputActions[i] >= Actions.Length) throw new InvalidDataException(); }
        for (int i = 0; i < inputCount; i++)
            inputValues[i] = inputActions[i] >= 4 ? 1 : quantum > 0 ? (float)(r.SVar() * quantum) : r.F32();
        var yawPerUnit = r.F64(); var pitchPerUnit = r.F64();
        var keyCount = r.Count(MaxFrames);
        var keyFrames = new int[keyCount]; var keyRot = new double[keyCount, 3];
        frame = 0;
        for (int i = 0; i < keyCount; i++)
        {
            frame += (int)r.Var(); keyFrames[i] = frame;
            for (int k = 0; k < 3; k++) keyRot[i, k] = r.SVar() / KeyAngleScale;
        }
        if (keyCount == 0) throw new InvalidDataException("Replay has no orientation");
        List<Point> Points(int dims, double[] scales)
        {
            var n = r.Count(MaxFrames); var list = new List<Point>(n); int f = 0; var last = new long[dims];
            for (int i = 0; i < n; i++)
            {
                var head = r.Var(); f += (int)(head >> 1);
                if (f >= frameCount) throw new InvalidDataException();
                var v = new double[dims];
                for (int k = 0; k < dims; k++) { last[k] += r.SVar(); v[k] = last[k] / scales[k]; }
                list.Add(new(f, (head & 1) != 0, v));
            }
            return list;
        }
        var camera = Points(4, [PositionScale, PositionScale, PositionScale, FovScale]);
        if (camera.Count == 0) throw new InvalidDataException();
        var actorCount = r.Count(4096);
        var tracks = new List<(int Id, string Profile, List<Point> Points, List<(int Frame, double Value)> Health, List<Point> Rotation)>();
        for (int a = 0; a < actorCount; a++)
        {
            var actorId = (int)r.Var(); var profile = r.Str();
            if (actorId < 1 || profile.Length > 256 || profile.Any(char.IsControl)) throw new InvalidDataException("Invalid replay target");
            var points = Points(5, [PositionScale, PositionScale, PositionScale, PositionScale, PositionScale]);
            foreach (var p in points) if (p.V[3] <= 0 || p.V[4] < p.V[3]) throw new InvalidDataException("Invalid actor capsule");
            var healthCount = r.Count(MaxFrames); var health = new List<(int, double)>(healthCount); int f = 0;
            for (int i = 0; i < healthCount; i++) { f += (int)r.Var(); var v = r.Var(); health.Add((f, v == 0 ? -1 : Math.Clamp((v - 1) / HealthScale, 0, 1))); }
            var rotation = Points(3, [AngleScale, AngleScale, AngleScale]);
            tracks.Add((actorId, profile, points, health, rotation));
        }
        var statsCount = r.Count(MaxFrames);
        var stats = new List<(int Frame, int Mask, double[] Values)>(statsCount);
        var current = new long[6]; frame = 0;
        for (int i = 0; i < statsCount; i++)
        {
            frame += (int)r.Var(); int mask = r.U8(); var values = new double[6];
            for (int k = 0; k < 6; k++) if ((mask & (1 << k)) != 0) { current[k] += r.SVar(); values[k] = current[k] / StatScales[k]; }
            stats.Add((frame, mask, values));
        }
        var hitCount = r.Count(MaxFrames);
        var hits = new List<(int Frame, int Target)>(hitCount); frame = 0;
        for (int i = 0; i < hitCount; i++) { frame += (int)r.Var(); hits.Add((frame, (int)r.Var())); }
        if (!r.End) throw new InvalidDataException("Trailing replay data");

        // Rotation at every engine frame: nearest keyframe plus the look
        // inputs consumed since, through the recorded per-unit constants.
        var pitch = new double[frameCount]; var yaw = new double[frameCount]; var roll = new double[frameCount];
        {
            // Inputs of frame f are applied before the state after frame f;
            // those at or before a keyframe are already part of it.
            int key = 0, input = 0; double turn = 0, look = 0;
            while (input < inputCount && inputFrames[input] <= keyFrames[0]) input++;
            for (int f = 0; f < frameCount; f++)
            {
                if (key + 1 < keyCount && keyFrames[key + 1] <= f)
                {
                    while (key + 1 < keyCount && keyFrames[key + 1] <= f) key++;
                    turn = look = 0;
                    while (input < inputCount && inputFrames[input] <= keyFrames[key]) input++;
                }
                while (input < inputCount && inputFrames[input] <= f)
                {
                    if (inputActions[input] == 0) turn += inputValues[input]; else if (inputActions[input] == 1) look += inputValues[input];
                    input++;
                }
                if (f <= keyFrames[0]) { pitch[f] = keyRot[0, 0]; yaw[f] = keyRot[0, 1]; roll[f] = keyRot[0, 2]; continue; }
                pitch[f] = Math.Clamp(keyRot[key, 0] + pitchPerUnit * look, -90, 90);
                yaw[f] = Wrap(keyRot[key, 1] + yawPerUnit * turn);
                roll[f] = keyRot[key, 2];
            }
        }

        // 60 Hz frames (format 1 model) for the renderer, browser and HUD.
        var duration = times[^1];
        var step = Math.Max(1.0 / 60, duration / (MaxGridFrames - 1));
        var grid = new List<int>();
        double next = times[0];
        for (int f = 0; f < frameCount; f++) if (times[f] >= next) { grid.Add(f); next = next + step > times[f] ? next + step : times[f] + step; }
        if (grid[^1] != frameCount - 1) grid.Add(frameCount - 1);

        double[] T(List<Point> points) => points.Select(q => times[q.Frame]).ToArray();
        var motion = new Motion(times, pitch, yaw, roll, T(camera), camera.Select(q => q.V).ToArray(), tracks.Select(track => new Motion.Track(
            track.Id, track.Profile, T(track.Points), track.Points.Select(q => q.Start).ToArray(), track.Points.Select(q => q.V).ToArray(),
            track.Health.Select(x => times[x.Frame]).ToArray(), track.Health.Select(x => x.Value).ToArray(),
            T(track.Rotation), track.Rotation.Select(q => q.Start).ToArray(), track.Rotation.Select(q => q.V).ToArray())).ToArray());
        var frames = new List<ReplayFrame>(grid.Count);
        int statIndex = -1; var statValues = new double?[6]; double secondsAt = 0;
        double? lastHits = null, hitTime = null, hitDelta = null;
        int hitIndex = -1;
        foreach (var f in grid)
        {
            var t = times[f];
            var (cameraRow, actors, health, appearance) = motion.Pose(t);
            if (cameraRow[6] is <= 1 or >= 179) throw new InvalidDataException("Invalid FOV");
            while (statIndex + 1 < stats.Count && stats[statIndex + 1].Frame <= f)
            {
                statIndex++;
                var s = stats[statIndex];
                for (int k = 0; k < 6; k++) if ((s.Mask & (1 << k)) != 0) { statValues[k] = s.Values[k]; if (k == 5) secondsAt = times[s.Frame]; }
            }
            ReplayStats? frameStats = null;
            if (statIndex >= 0)
            {
                var hitsNow = statValues[2];
                if (hitsNow is double hn && lastHits is double lh && hn > lh) { hitTime = t; hitDelta = hn - lh; }
                if (hitsNow < lastHits) { hitTime = null; hitDelta = null; }
                lastHits = hitsNow;
                double? target = null;
                while (hitIndex + 1 < hits.Count && hits[hitIndex + 1].Frame <= f) hitIndex++;
                if (hitIndex >= 0 && t - times[hits[hitIndex].Frame] <= 0.1 && hits[hitIndex].Target > 0) { target = hits[hitIndex].Target; hitTime = times[hits[hitIndex].Frame]; hitDelta = 1; }
                frameStats = new(statValues[0], statValues[1], statValues[2], statValues[3], statValues[4],
                    statValues[5] is double sec ? sec + (t - secondsAt) : null, hitTime, hitDelta, target);
            }
            frames.Add(new(t, cameraRow, actors, frameStats, health, appearance));
        }
        var reason = Text("reason");
        if (reason == "completed" && h.TryGetProperty("score", out var finalScore) && finalScore.TryGetDouble(out var score) && double.IsFinite(score))
            frames[^1] = frames[^1] with { Stats = (frames[^1].Stats ?? new ReplayStats(null, null, null, null, null, null)) with { Score = score } };
        var inputs = new List<ReplayInput>(inputCount);
        for (int i = 0; i < inputCount; i++) inputs.Add(new(times[inputFrames[i]], Actions[inputActions[i]], inputValues[i]));
        string? mapName = null; double? mapScale = null;
        if (h.TryGetProperty("mapName", out var map) && h.TryGetProperty("mapScale", out var scaleValue))
        {
            mapName = map.GetString(); mapScale = scaleValue.GetDouble();
            if (string.IsNullOrWhiteSpace(mapName) || mapName.Length > 1024 || mapName.Any(char.IsControl) || !(mapScale > 0)) throw new InvalidDataException("Invalid replay map identity");
        }
        return new(2, id, Text("scenario"), Text("recordedAt"), reason, duration, frames, inputs, mapName, mapScale, motion);
    }

    static double Wrap(double degrees) { degrees = (degrees + 180) % 360; if (degrees < 0) degrees += 360; return degrees - 180; }

    static byte[] Decompress(uint algorithm, ReadOnlySpan<byte> input, int rawSize)
    {
        if (algorithm == 0) { if (input.Length != rawSize) throw new InvalidDataException(); return input.ToArray(); }
        if (!OperatingSystem.IsWindows()) throw new InvalidDataException("Compressed replays need Windows");
        if (!CreateDecompressor(algorithm, IntPtr.Zero, out var handle)) throw new InvalidDataException("Unsupported replay compression");
        try
        {
            var output = new byte[rawSize];
            unsafe
            {
                fixed (byte* src = input) fixed (byte* dst = output)
                    if (!NativeDecompress(handle, src, (nuint)input.Length, dst, (nuint)output.Length, out var written) || (int)written != rawSize)
                        throw new InvalidDataException("Replay body damaged");
            }
            return output;
        }
        finally { CloseDecompressor(handle); }
    }

    [DllImport("cabinet.dll", SetLastError = true)] static extern bool CreateDecompressor(uint algorithm, IntPtr allocationRoutines, out IntPtr handle);
    [DllImport("cabinet.dll", EntryPoint = "Decompress", SetLastError = true)] static extern unsafe bool NativeDecompress(IntPtr handle, byte* data, nuint size, byte* buffer, nuint bufferSize, out nuint written);
    [DllImport("cabinet.dll")] static extern bool CloseDecompressor(IntPtr handle);
}
