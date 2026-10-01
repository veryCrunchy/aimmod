using System.Globalization;

namespace AimMod.InGame;

/// <summary>
/// Diagnostic: --compare-replays &lt;rootA&gt; &lt;rootB&gt; &lt;id&gt; decodes the same
/// attempt from two output folders (for example format 1 and its format 2
/// conversion) and reports how far playback samples differ.
/// </summary>
static class ReplayCompare
{
    /// <summary>Target appearances: a new target, a target reappearing after an absence, or one that jumped more than 150 cm between frames (respawn).</summary>
    internal static List<(double T, double[] Position)> Spawns(NativeReplay replay)
    {
        var events = new List<(double, double[])>();
        var last = new Dictionary<double, (double T, double[] P)>();
        foreach (var frame in replay.Frames)
            foreach (var actor in frame.Actors)
            {
                var p = new[] { actor[1], actor[2], actor[3] };
                if (!last.TryGetValue(actor[0], out var previous)) events.Add((frame.T, p));
                else
                {
                    var jump = Math.Sqrt((p[0] - previous.P[0]) * (p[0] - previous.P[0]) + (p[1] - previous.P[1]) * (p[1] - previous.P[1]) + (p[2] - previous.P[2]) * (p[2] - previous.P[2]));
                    // A reappearance after an absence (dead, hidden), or a jump within one frame, is a spawn.
                    if (frame.T - previous.T > 0.1 || jump > 150) events.Add((frame.T, p));
                }
                last[actor[0]] = (frame.T, p);
            }
        return events;
    }

    /// <summary>--compare-spawns rootA idA rootB idB: determinism report for two runs of one scenario.</summary>
    public static int Spawns(string rootA, string idA, string rootB, string idB)
    {
        var a = new ReplayCatalog(rootA).Read(idA);
        var b = new ReplayCatalog(rootB).Read(idB);
        if (a is null || b is null) { Console.Error.WriteLine("Replay unreadable."); return 1; }
        var sa = Spawns(a); var sb = Spawns(b);
        int matched = 0, compared = Math.Min(sa.Count, sb.Count), firstMismatch = -1;
        string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        for (int i = 0; i < compared; i++)
        {
            var d = Math.Sqrt(Enumerable.Range(0, 3).Sum(k => Math.Pow(sa[i].Position[k] - sb[i].Position[k], 2)));
            if (d <= 5) matched++;
            else if (firstMismatch < 0) firstMismatch = i;
            if (i < 20 || d > 5 && i < firstMismatch + 5)
                Console.WriteLine($"spawn {i + 1}: A t={N(sa[i].T)} ({N(sa[i].Position[0])}, {N(sa[i].Position[1])}, {N(sa[i].Position[2])})  B t={N(sb[i].T)} ({N(sb[i].Position[0])}, {N(sb[i].Position[1])}, {N(sb[i].Position[2])})  delta {N(d)} cm");
        }
        Console.WriteLine($"scenario A \"{a.Scenario}\" B \"{b.Scenario}\"; spawns A {sa.Count} B {sb.Count}; identical (<=5 cm) {matched}/{compared}" +
            (firstMismatch >= 0 ? $"; first difference at spawn {firstMismatch + 1}" : "; no difference"));
        return 0;
    }

    public static int Run(string rootA, string rootB, string id)
    {
        var a = new ReplayCatalog(rootA).Read(id);
        var b = new ReplayCatalog(rootB).Read(id);
        if (a is null || b is null) { Console.Error.WriteLine("Replay unreadable."); return 1; }
        double maxAngle = 0, sumAngle = 0, maxPosition = 0, maxTarget = 0; int samples = 0, targetMismatch = 0;
        foreach (var frame in a.Frames)
        {
            var sa = NativeReplayPlayback.Sample(a, frame.T);
            var sb = NativeReplayPlayback.Sample(b, frame.T);
            double Angle(double x, double y) => Math.Abs(((y - x + 540) % 360 + 360) % 360 - 180);
            var angle = Math.Max(Math.Abs(sa.Camera[3] - sb.Camera[3]), Angle(sa.Camera[4], sb.Camera[4]));
            maxAngle = Math.Max(maxAngle, angle); sumAngle += angle * angle; samples++;
            maxPosition = Math.Max(maxPosition, Math.Sqrt(Enumerable.Range(0, 3).Sum(i => Math.Pow(sa.Camera[i] - sb.Camera[i], 2))));
            if (sa.Actors.Length != sb.Actors.Length) { targetMismatch++; continue; }
            for (int i = 0; i < sa.Actors.Length; i++)
                maxTarget = Math.Max(maxTarget, Math.Sqrt(Enumerable.Range(1, 3).Sum(k => Math.Pow(sa.Actors[i][k] - sb.Actors[i][k], 2))));
        }
        string N(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
        Console.WriteLine($"A: format {a.Version}, {a.Frames.Count} frames, {a.Inputs.Count} inputs, {N(a.Duration)} s");
        Console.WriteLine($"B: format {b.Version}, {b.Frames.Count} frames, {b.Inputs.Count} inputs, {N(b.Duration)} s, motion {(b.Motion is null ? "no" : b.Motion.EngineFrames + " engine frames")}");
        Console.WriteLine($"view rotation difference at {samples} samples: max {N(maxAngle)} deg, rms {N(Math.Sqrt(sumAngle / Math.Max(1, samples)))} deg");
        Console.WriteLine($"camera position max {N(maxPosition)} cm; target position max {N(maxTarget)} cm; target count mismatches {targetMismatch}");
        Console.WriteLine($"final score A {a.Frames[^1].Stats?.Score} B {b.Frames[^1].Stats?.Score}; hits A {a.Frames[^1].Stats?.Hits} B {b.Frames[^1].Stats?.Hits}; shots A {a.Frames[^1].Stats?.Shots} B {b.Frames[^1].Stats?.Shots}");
        return 0;
    }
}
