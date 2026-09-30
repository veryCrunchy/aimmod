using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AimMod.InGame;

sealed record ReplayStats(double? Score, double? Shots, double? Hits, double? Kills, double? Damage, double? Seconds, double? HitTime = null, double? HitDelta = null, double? HitTarget = null);
sealed record ReplayHealth(double Id, double Percent);
sealed record ReplayAppearance(double Id, string Profile, double[] Rotation);
sealed record ReplayFrame(double T, double[] Camera, double[][] Actors, ReplayStats? Stats = null, ReplayHealth[]? Health = null, ReplayAppearance[]? Appearance = null);
sealed record ReplayInput(double T, string Action, double Value);
sealed record ReplaySummary(string Id, string Scenario, string RecordedAt, string Reason, int Frames, int InputEvents);
sealed record NativeReplay(int Version, string Id, string Scenario, string RecordedAt, string Reason,
    double Duration, IReadOnlyList<ReplayFrame> Frames, IReadOnlyList<ReplayInput> Inputs, string? MapName = null, double? MapScale = null);

/// <summary>Private state replays only. No score database writes or game commands.</summary>
sealed class ReplayCatalog
{
    const long MaxBytes = 64 * 1024 * 1024;
    const int MaxFrames = 36000, MaxActors = 128, MaxInputs = 500000, MaxLine = 131072, MaxActorSamples = 2_000_000;
    readonly string directory;
    readonly string completedJournal;
    // \z, not $: "$" also matches before a trailing newline.
    static readonly Regex IdPattern = new(@"^[A-Za-z0-9_-]{1,100}\z", RegexOptions.CultureInvariant);
    static readonly HashSet<string> Actions = new(StringComparer.Ordinal) {
        "AxisTurn", "AxisLookUp", "AxisMoveForward", "AxisMoveRight", "FirePressed", "FireReleased",
        "AltFirePressed", "AltFireReleased", "JumpPressed", "JumpReleased", "CrouchPressed", "CrouchReleased",
        "ReloadPressed", "ReloadReleased", "ADSPressed", "ADSReleased", "AbilityPressed", "AbilityReleased",
        "WeaponPressed", "WeaponReleased"
    };
    public ReplayCatalog(string output)
    {
        directory = Path.GetFullPath(Path.Combine(output, "replays"));
        completedJournal = Path.Combine(output, "completed.tsv");
        Directory.CreateDirectory(directory);
    }
    internal string? Resolve(string id)
    {
        if (!IdPattern.IsMatch(id)) return null;
        var path = Path.Combine(directory, id + ".amreplay");
        var info = new FileInfo(path);
        return info.Exists && info.Length is > 0 and <= MaxBytes &&
            !info.Attributes.HasFlag(FileAttributes.ReparsePoint) ? path : null;
    }
    public IReadOnlyList<ReplaySummary> List()
    {
        var result = new List<ReplaySummary>();
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.amreplay")
            .OrderByDescending(f => f.LastWriteTimeUtc).Take(250))
        {
            try
            {
                var id = Path.GetFileNameWithoutExtension(file.Name);
                var path = Resolve(id);
                if (path is null) continue;
                using var stream = File.OpenRead(path);
                using var reader = new StreamReader(stream);
                using var header = JsonDocument.Parse(ReadLine(reader) ?? "{}");
                var h = header.RootElement;
                ValidateHeader(h, id);
                // A tiny tail read avoids parsing every frame just to list runs.
                stream.Seek(Math.Max(0, stream.Length - 2048), SeekOrigin.Begin);
                reader.DiscardBufferedData();
                var tail = reader.ReadToEnd();
                if (!tail.EndsWith('\n')) continue;
                var last = tail.TrimEnd('\r', '\n').Split('\n')[^1];
                using var end = JsonDocument.Parse(last);
                var e = end.RootElement;
                if (Text(e, "kind") != "end" || Text(e, "reason") != "completed") continue;
                var count = e.GetProperty("frames").GetInt32();
                var inputCount = e.GetProperty("inputEvents").GetInt32();
                if (count is < 2 or > MaxFrames || inputCount is < 0 or > MaxInputs) continue;
                result.Add(new(id, Text(h, "scenario"), Text(h, "recordedAt"), Text(e, "reason"), count,
                    inputCount));
            }
            catch (Exception e) when (DataError(e)) { /* Incomplete/unsupported records stay unavailable. */ }
        }
        return result;
    }
    public NativeReplay? Read(string id)
    {
        var path = Resolve(id);
        if (path is null) return null;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxBytes) return null;
            using var reader = new StreamReader(stream);
            using var header = JsonDocument.Parse(ReadLine(reader) ?? "{}");
            var h = header.RootElement;
            ValidateHeader(h, id);
            var frames = new List<ReplayFrame>();
            var inputs = new List<ReplayInput>();
            string? reason = null;
            double lastFrame = -1, lastInput = -1;
            long actorSamples = 0;
            while (ReadLine(reader) is { } line)
            {
                if (reason is not null) throw new InvalidDataException("Data after end marker");
                using var doc = JsonDocument.Parse(line);
                var row = doc.RootElement;
                switch (Text(row, "kind"))
                {
                    case "frame":
                        var t = Number(row.GetProperty("t"));
                        if (t <= lastFrame || t < 0 || t > 86400 || frames.Count >= MaxFrames) throw new InvalidDataException();
                        var camera = Vector(row.GetProperty("camera"), 7);
                        if (camera[6] is <= 1 or >= 179) throw new InvalidDataException("Invalid FOV");
                        var entities = row.GetProperty("actors");
                        if (entities.GetArrayLength() > MaxActors) throw new InvalidDataException();
                        // Total budget across frames: the 64 MB file cap alone still admits
                        // several million compact actor rows (hundreds of MB once parsed).
                        actorSamples += entities.GetArrayLength();
                        if (actorSamples > MaxActorSamples) throw new InvalidDataException("Replay exceeds actor sample budget");
                        var actors = entities.EnumerateArray().Select(a => Vector(a, 6)).ToArray();
                        var ids = new HashSet<double>();
                        foreach (var a in actors)
                            if (a[0] < 1 || a[0] != Math.Truncate(a[0]) || !ids.Add(a[0]) || a[4] <= 0 || a[5] < a[4])
                                throw new InvalidDataException("Invalid actor capsule");
                        ReplayStats? stats = null;
                        if (row.TryGetProperty("stats", out var st)) {
                            double? Field(string name) => st.TryGetProperty(name, out var value) ? Number(value) : null;
                            stats = new(Field("score"), Field("shots"), Field("hits"), Field("kills"), Field("damage"), Field("seconds"), Field("hitTime"), Field("hitDelta"), Field("hitTarget"));
                            if (stats.HitTarget is double target && (target < 1 || target != Math.Truncate(target))) throw new InvalidDataException("Invalid replay hit target");
                            if (stats.Shots < 0 || stats.Hits < 0 || stats.Kills < 0 || stats.Seconds < 0 || stats.HitTime < 0 || stats.HitTime > t || stats.HitDelta <= 0 || (stats.HitTime.HasValue != stats.HitDelta.HasValue)) throw new InvalidDataException("Invalid replay statistics");
                        }
                        ReplayHealth[]? health = null;
                        if (row.TryGetProperty("health", out var healthRows)) {
                            if (healthRows.GetArrayLength() > MaxActors) throw new InvalidDataException("Invalid health count");
                            var healthIds = new HashSet<double>();
                            health = healthRows.EnumerateArray().Select(value => {
                                var id = Number(value.GetProperty("id")); var percent = Number(value.GetProperty("percent"));
                                if (!ids.Contains(id) || !healthIds.Add(id) || percent < 0 || percent > 1) throw new InvalidDataException("Invalid replay health");
                                return new ReplayHealth(id, percent);
                            }).ToArray();
                        }
                        ReplayAppearance[]? appearance = null;
                        if (row.TryGetProperty("appearance", out var appearanceRows)) {
                            if (appearanceRows.GetArrayLength() > MaxActors) throw new InvalidDataException("Invalid appearance count");
                            var appearanceIds = new HashSet<double>();
                            appearance = appearanceRows.EnumerateArray().Select(value => {
                                var id = Number(value.GetProperty("id")); var profile = Text(value,"profile");
                                if (!ids.Contains(id) || !appearanceIds.Add(id) || profile.Length is <1 or >256 || profile.Any(char.IsControl)) throw new InvalidDataException("Invalid replay appearance");
                                return new ReplayAppearance(id, profile, Vector(value.GetProperty("rotation"),3));
                            }).ToArray();
                        }
                        frames.Add(new(t, camera, actors, stats, health, appearance)); lastFrame = t;
                        break;
                    case "input":
                        var it = Number(row.GetProperty("t"));
                        var action = Text(row, "action");
                        if (it < 0 || it < lastInput || it > 86400 || inputs.Count >= MaxInputs || !Actions.Contains(action))
                            throw new InvalidDataException();
                        inputs.Add(new(it, action, Number(row.GetProperty("value")))); lastInput = it;
                        break;
                    case "end":
                        if (row.GetProperty("frames").GetInt32() != frames.Count || row.GetProperty("inputEvents").GetInt32() != inputs.Count)
                            throw new InvalidDataException("Incomplete replay");
                        reason = Text(row, "reason");
                        if (row.TryGetProperty("score", out var finalScore) && frames.Count > 0) {
                            var terminal = frames[^1];
                            var measured = terminal.Stats ?? new ReplayStats(null,null,null,null,null,null);
                            frames[^1] = terminal with { Stats = measured with { Score = Number(finalScore) } };
                        }
                        break;
                    default: throw new InvalidDataException("Unknown replay record");
                }
            }
            if (reason is null || frames.Count < 2) return null;
            // Exact native attempt identity only: never infer scores from nearby
            // timestamps or scenario names, and never synthesize a score curve.
            if (reason == "completed" && frames[^1].Stats?.Score is null && File.Exists(completedJournal)
                && new FileInfo(completedJournal).Length <= 16 * 1024 * 1024) {
                var match = NativeRuns.Read(completedJournal).FirstOrDefault(run => run.Id == "native:" + id && run.Scenario == Text(h, "scenario"));
                if (match is not null) {
                    var terminal = frames[^1];
                    frames[^1] = terminal with { Stats = (terminal.Stats ?? new ReplayStats(null,null,null,null,null,null)) with { Score = match.Score } };
                }
            }
            string? mapName = null; double? mapScale = null;
            if (h.TryGetProperty("mapName", out var map) && h.TryGetProperty("mapScale", out var scale)) {
                mapName = map.GetString(); mapScale = Number(scale);
                if (string.IsNullOrWhiteSpace(mapName) || mapName.Length > 1024 || mapName.Any(char.IsControl) || mapScale <= 0) throw new InvalidDataException("Invalid replay map identity");
            }
            return new(1, id, Text(h, "scenario"), Text(h, "recordedAt"), reason, frames[^1].T, frames, inputs, mapName, mapScale);
        }
        catch (Exception e) when (DataError(e)) { return null; }
    }
    static void ValidateHeader(JsonElement h, string id)
    {
        if (Text(h, "kind") != "header" || h.GetProperty("version").GetInt32() != 1 || Text(h, "id") != id ||
            Text(h, "coordinates") != "unreal-centimeters" || Text(h, "scenario").Length > 1024 ||
            !DateTimeOffset.TryParse(Text(h, "recordedAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
            throw new InvalidDataException("Unsupported replay header");
    }
    static string Text(JsonElement row, string key) => row.GetProperty(key).GetString() ?? throw new InvalidDataException();
    static double Number(JsonElement value)
    {
        if (!value.TryGetDouble(out var n) || !double.IsFinite(n) || Math.Abs(n) >= 1e12) throw new InvalidDataException();
        return n;
    }
    static double[] Vector(JsonElement value, int length)
    {
        if (value.GetArrayLength() != length) throw new InvalidDataException();
        return value.EnumerateArray().Select(Number).ToArray();
    }
    static string? ReadLine(StreamReader reader)
    {
        // Bound allocation before parsing; StreamReader.ReadLine alone can allocate
        // an unbounded line in a damaged or manually replaced local file.
        var line = new System.Text.StringBuilder();
        while (reader.Read() is var c && c != -1)
        {
            if (c == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= MaxLine) throw new InvalidDataException("Replay line too long");
            line.Append((char)c);
        }
        if (line.Length > 0) throw new InvalidDataException("Truncated replay line");
        return null;
    }
    static bool DataError(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
        InvalidOperationException or KeyNotFoundException or FormatException or OverflowException;
}
