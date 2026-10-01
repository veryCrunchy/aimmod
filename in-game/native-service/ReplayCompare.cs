using System.Globalization;

namespace AimMod.InGame;

/// <summary>
/// Diagnostic: --compare-replays &lt;rootA&gt; &lt;rootB&gt; &lt;id&gt; decodes the same
/// attempt from two output folders (for example format 1 and its format 2
/// conversion) and reports how far playback samples differ.
/// </summary>
static class ReplayCompare
{
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
