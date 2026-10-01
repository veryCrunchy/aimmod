using System.Globalization;

namespace AimMod.InGame;

/// <summary>One camera pose of a live view (spectating) or of the local player.</summary>
sealed record LivePose(long UnixMs, double[] Camera);

/// <summary>
/// Live pose stream file (pose format 1), shared by AimModCore (self-pose.tsv,
/// the local player's view) and the multiplayer bridge (spectate-pose.tsv, the
/// view being watched). See in-game/native-mod/DESIGN.md "Spectating".
/// </summary>
sealed record LivePoseFrame(long Sequence, string Scenario, string MapName, double? MapScale, IReadOnlyList<LivePose> Poses, IReadOnlyList<double[]> Targets)
{
    public static LivePoseFrame? Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2 || text.Length > 65536) return null;
        var head = lines[0].Split('\t');
        if (head.Length != 2 || head[0] != "AIMMOD_POSE_1" || !long.TryParse(head[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)) return null;
        string scenario = "", map = ""; double? scale = null;
        var poses = new List<LivePose>(); var targets = new List<double[]>();
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
                default: return null;
            }
        }
        return poses.Count == 0 ? null : new(sequence, scenario, map, scale, poses, targets);
    }

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
sealed class LivePoseFeed(string path)
{
    public const double Delay = 0.12;
    readonly List<LivePose> buffer = [];
    long startMs = -1;
    public IReadOnlyList<double[]> Targets { get; private set; } = [];
    public DateTime LastUpdate { get; private set; }

    /// <summary>Reads new poses; returns false when the stream is stale (2 s).</summary>
    public bool Update()
    {
        var frame = LivePoseFrame.Read(path, TimeSpan.FromSeconds(2));
        if (frame is null) return DateTime.UtcNow - LastUpdate < TimeSpan.FromSeconds(2);
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
