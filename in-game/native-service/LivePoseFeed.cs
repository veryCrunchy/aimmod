using System.Globalization;

namespace AimMod.InGame;

/// <summary>One camera pose of a live view (spectating) or of the local player.</summary>
sealed record LivePose(long UnixMs, double[] Camera);

/// <summary>
/// Live pose stream file (pose format 1), shared by AimModCore (self-pose.tsv,
/// the local player's view) and the multiplayer bridge (spectate-pose.tsv, the
/// view being watched). See in-game/native-mod/DESIGN.md "Spectating".
/// </summary>
sealed record LivePoseFrame(long Sequence, string Stream, string Scenario, string MapName, double? MapScale, IReadOnlyList<LivePose> Poses, IReadOnlyList<double[]> Targets)
{
    /// <summary>Optional: target id -> stream id of the player that target is (avatars).</summary>
    public IReadOnlyDictionary<int, string> Tags { get; init; } = new Dictionary<int, string>();
    /// <summary>Optional: the sender's own body (unix ms, x, y, z, radius, half height, crouched 0/1).</summary>
    public double[]? Self { get; init; }
    /// <summary>Optional: the sender's weapon (unix ms, shots fired total, fired since the previous publication 0/1).</summary>
    public double[]? Fire { get; init; }
    public static LivePoseFrame? Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2 || text.Length > 65536) return null;
        var head = lines[0].Split('\t');
        // Optional third cell: the stream (watched player) identity.
        if (head.Length is not (2 or 3) || head[0] != "AIMMOD_POSE_1" || !long.TryParse(head[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)) return null;
        var stream = head.Length == 3 ? head[2] : "";
        if (!IsStreamId(stream)) return null;
        string scenario = "", map = ""; double? scale = null;
        var poses = new List<LivePose>(); var targets = new List<double[]>(); var tags = new Dictionary<int, string>();
        double[]? self = null, fire = null;
        static bool Num(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v) && Math.Abs(v) < 1e12;
        foreach (var line in lines.Skip(1))
        {
            var c = line.Split('\t');
            switch (c[0])
            {
                case "meta" when c.Length == 4:
                    scenario = Uri.UnescapeDataString(c[1]); map = Uri.UnescapeDataString(c[2]);
                    if (Num(c[3], out var s) && s > 0) scale = s;
                    if (scenario.Length > 512 || map.Length > 1024 || scenario.Any(char.IsControl) || map.Any(char.IsControl)) return null;
                    break;
                case "pose" when c.Length == 9:
                    if (!long.TryParse(c[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ms) || poses.Count >= 64 || (poses.Count > 0 && ms <= poses[^1].UnixMs)) return null;
                    var camera = new double[7];
                    for (int i = 0; i < 7; i++) if (!Num(c[i + 2], out camera[i])) return null;
                    if (camera[6] is <= 1 or >= 179) return null;
                    poses.Add(new(ms, camera));
                    break;
                case "target" when c.Length == 7:
                    var t = new double[6];
                    for (int i = 0; i < 6; i++) if (!Num(c[i + 1], out t[i])) return null;
                    if (t[0] < 1 || t[0] != Math.Truncate(t[0]) || t[4] <= 0 || t[5] < t[4] || targets.Count >= 128 || targets.Any(x => x[0] == t[0])) return null;
                    targets.Add(t);
                    break;
                case "tag" when c.Length == 3:
                    if (!int.TryParse(c[1], NumberStyles.None, CultureInfo.InvariantCulture, out var tagged) || tagged < 1 || !IsStreamId(c[2]) || c[2].Length == 0 || tags.Count >= 128) return null;
                    tags[tagged] = c[2];
                    break;
                case "self" when c.Length == 8:
                    // The first cell is a unix ms time (about 1.8e12): an integer, outside Num's range.
                    self = new double[7];
                    if (!long.TryParse(c[1], NumberStyles.None, CultureInfo.InvariantCulture, out var selfMs)) return null;
                    self[0] = selfMs;
                    for (int i = 1; i < 7; i++) if (!Num(c[i + 1], out self[i])) return null;
                    if (self[4] <= 0 || self[5] < self[4] || self[6] is not (0 or 1)) return null;
                    break;
                case "fire" when c.Length == 4:
                    fire = new double[3];
                    if (!long.TryParse(c[1], NumberStyles.None, CultureInfo.InvariantCulture, out var fireMs)) return null;
                    fire[0] = fireMs;
                    for (int i = 1; i < 3; i++) if (!Num(c[i + 1], out fire[i])) return null;
                    if (fire[1] < 0 || fire[2] is not (0 or 1)) return null;
                    break;
                case "meta" or "pose" or "target" or "tag" or "self" or "fire": return null; // known row, wrong shape
                default: break; // rows added later are ignored, never fatal
            }
        }
        return poses.Count == 0 ? null : new(sequence, stream, scenario, map, scale, poses, targets) { Tags = tags, Self = self, Fire = fire };
    }

    /// <summary>Empty (unnamed stream) or [A-Za-z0-9_-]{1,64}.</summary>
    public static bool IsStreamId(string? id) => id is not null && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static LivePoseFrame? Read(string path, TimeSpan maxAge)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 65536 || DateTime.UtcNow - file.LastWriteTimeUtc > maxAge) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// Buffers the watched player's poses (spectate-pose.tsv) for playback: the
/// presenter shows them about 120 ms behind the newest pose so network jitter
/// never empties the motion window.
/// </summary>
sealed class LivePoseFeed(string path, string? stream = null)
{
    string? stream = stream;
    /// <summary>Follow another stream in place (no stop/start, no new 2 s window).</summary>
    public void Follow(string? next) => stream = next;
    public const double Delay = 0.12;
    readonly List<LivePose> buffer = [];
    long startMs = -1;
    string? current;
    /// <summary>Stream switches seen (the view jumps, it never blends across streams).</summary>
    public int Switches { get; private set; }
    public IReadOnlyList<double[]> Targets { get; private set; } = [];
    public DateTime LastUpdate { get; private set; }

    /// <summary>Reads new poses; returns false when the stream is stale (2 s).</summary>
    public bool Update()
    {
        var frame = LivePoseFrame.Read(path, TimeSpan.FromSeconds(2));
        // A stream other than the one asked for (still the previous player) is
        // not shown, and does not end the view before the 2 s quiet limit.
        if (frame is null || (stream is { Length: > 0 } && frame.Stream != stream)) return DateTime.UtcNow - LastUpdate < TimeSpan.FromSeconds(2);
        // A new stream (or a clock that jumped back: another machine) starts
        // a fresh buffer, so the view cuts to it.
        var jumped = buffer.Count > 0 && frame.Poses[^1].UnixMs < buffer[^1].UnixMs - 1000;
        if (current is not null && (frame.Stream != current || jumped)) { buffer.Clear(); startMs = -1; Switches++; }
        current = frame.Stream;
        foreach (var pose in frame.Poses)
            if (buffer.Count == 0 || pose.UnixMs > buffer[^1].UnixMs) buffer.Add(pose);
        if (startMs < 0 && buffer.Count > 0) startMs = buffer[0].UnixMs;
        while (buffer.Count > 128) buffer.RemoveAt(0);
        Targets = frame.Targets;
        LastUpdate = DateTime.UtcNow;
        return true;
    }

    double Seconds(LivePose p) => (p.UnixMs - startMs) / 1000.0;
    /// <summary>Display time (stream seconds) and the window of poses from it.</summary>
    public (double Time, IReadOnlyList<(double T, double[] Camera)> Window) Window()
    {
        if (buffer.Count == 0) return (0, []);
        var latest = Seconds(buffer[^1]);
        var time = Math.Max(Seconds(buffer[0]), latest - Delay);
        var window = new List<(double, double[])>();
        for (int i = 0; i < buffer.Count; i++)
        {
            var t = Seconds(buffer[i]);
            if (t < time)
            {
                // The pose pair straddling the display time starts the window, interpolated.
                if (i + 1 < buffer.Count && Seconds(buffer[i + 1]) > time)
                {
                    var b = buffer[i + 1]; var u = (time - t) / (Seconds(b) - t);
                    var camera = buffer[i].Camera.Select((v, k) => k is 4 or 5 ? v + (((b.Camera[k] - v + 540) % 360 + 360) % 360 - 180) * u : v + (b.Camera[k] - v) * u).ToArray();
                    window.Add((time, camera));
                }
                continue;
            }
            window.Add((t, buffer[i].Camera));
        }
        return (time, window.Count > 48 ? window.TakeLast(48).ToList() : window);
    }
}
