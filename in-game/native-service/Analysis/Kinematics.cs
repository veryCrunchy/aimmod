namespace AimMod.InGame.Analysis;

/// <summary>View direction at one instant: yaw and pitch in degrees (yaw unwrapped, continuous).</summary>
public readonly record struct ViewSample(double T, double Yaw, double Pitch);

/// <summary>Where one target was at one instant, in the player's angular frame: direction (deg), angular radius (deg) and whether the view ray is inside its capsule.</summary>
public readonly record struct TargetSample(double T, double Id, double Yaw, double Pitch, double AngularRadius, bool OnTarget);

/// <summary>A target that appeared (new, reappearing, or jumped): candidates for the reaction phase of a flick.</summary>
public readonly record struct SpawnEvent(double T, double Id);

/// <summary>A registered hit of the player's shot at time `T` on target `Id` (0 when only the hit counter advanced and the target is unknown).</summary>
public readonly record struct HitEvent(double T, double Id);

/// <summary>
/// Everything the analysis needs from one run, independent of replay files:
/// the view path, where targets were (sampled on the same grid), and the
/// click, hold and hit timeline. Built from a NativeReplay by ReplayAnalysisInput
/// or directly from synthetic data by the checks.
/// </summary>
/// <param name="Targets">Targets per grid sample: Targets[i] are the targets visible at View[i].T, nearest to the view ray first.</param>
public sealed record AnalysisInput(
    string Scenario,
    double Duration,
    IReadOnlyList<ViewSample> View,
    IReadOnlyList<TargetSample[]> Targets,
    IReadOnlyList<double> FirePressed,
    IReadOnlyList<double> FireReleased,
    IReadOnlyList<HitEvent> Hits,
    IReadOnlyList<SpawnEvent> Spawns)
{
    /// <summary>Targets that vanished (killed, hidden): with no per-target hit record, the one that vanished right after a click is the one that was shot.</summary>
    public IReadOnlyList<SpawnEvent> Despawns { get; init; } = [];
}

/// <summary>Uniformly resampled view path with smoothed angular velocity, the shared basis of both analyses.</summary>
public sealed class ViewPath
{
    /// <summary>Analysis grid: 250 Hz keeps mouse quantisation (about 0.1 deg per count) usable after a 20 ms smoothing window.</summary>
    public const double Rate = 250;
    public const double Step = 1 / Rate;
    public double[] T { get; }
    public double[] Yaw { get; }
    public double[] Pitch { get; }
    /// <summary>Smoothed yaw / pitch velocity (deg/s) and speed (their magnitude).</summary>
    public double[] YawVelocity { get; }
    public double[] PitchVelocity { get; }
    public double[] Speed { get; }
    public int Count => T.Length;

    public ViewPath(double[] t, double[] yaw, double[] pitch)
    {
        T = t; Yaw = yaw; Pitch = pitch;
        var n = t.Length;
        YawVelocity = new double[n]; PitchVelocity = new double[n]; Speed = new double[n];
        if (n < 3) return;
        var vy = new double[n]; var vp = new double[n];
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - 1), b = Math.Min(n - 1, i + 1);
            var dt = t[b] - t[a];
            if (dt <= 0) continue;
            vy[i] = (yaw[b] - yaw[a]) / dt; vp[i] = (pitch[b] - pitch[a]) / dt;
        }
        // 5-sample (20 ms) triangular window: removes the count-per-frame staircase without smearing a 100 ms ballistic phase.
        double[] w = [1, 2, 3, 2, 1];
        for (int i = 0; i < n; i++)
        {
            double sy = 0, sp = 0, sw = 0;
            for (int k = -2; k <= 2; k++)
            {
                var j = i + k; if (j < 0 || j >= n) continue;
                sy += vy[j] * w[k + 2]; sp += vp[j] * w[k + 2]; sw += w[k + 2];
            }
            YawVelocity[i] = sy / sw; PitchVelocity[i] = sp / sw;
            Speed[i] = Math.Sqrt(YawVelocity[i] * YawVelocity[i] + PitchVelocity[i] * PitchVelocity[i]);
        }
    }

    /// <summary>Resamples irregular (engine-frame) view samples onto the uniform grid by linear interpolation. Yaw must already be unwrapped.</summary>
    public static ViewPath Resample(IReadOnlyList<ViewSample> samples, double? duration = null)
    {
        if (samples.Count == 0) return new([], [], []);
        var end = duration is double d && d > 0 ? Math.Min(d, samples[^1].T) : samples[^1].T;
        var n = Math.Max(1, (int)Math.Floor((end - samples[0].T) / Step + 1e-9) + 1);
        var t = new double[n]; var yaw = new double[n]; var pitch = new double[n];
        int j = 0;
        for (int i = 0; i < n; i++)
        {
            t[i] = samples[0].T + i * Step;
            while (j + 1 < samples.Count && samples[j + 1].T <= t[i]) j++;
            if (j + 1 >= samples.Count || samples[j + 1].T <= samples[j].T) { yaw[i] = samples[j].Yaw; pitch[i] = samples[j].Pitch; continue; }
            var u = (t[i] - samples[j].T) / (samples[j + 1].T - samples[j].T);
            yaw[i] = samples[j].Yaw + (samples[j + 1].Yaw - samples[j].Yaw) * u;
            pitch[i] = samples[j].Pitch + (samples[j + 1].Pitch - samples[j].Pitch) * u;
        }
        return new(t, yaw, pitch);
    }

    /// <summary>Index of the grid sample at or before `time` (clamped).</summary>
    public int IndexAt(double time) => T.Length == 0 ? 0 : Math.Clamp((int)Math.Floor((time - T[0]) / Step + 1e-9), 0, T.Length - 1);

    /// <summary>Angular path length (deg) between two grid indices, summing straight steps on the (yaw, pitch) plane.</summary>
    public double PathLength(int from, int to)
    {
        double sum = 0;
        for (int i = Math.Max(1, from + 1); i <= to && i < T.Length; i++)
            sum += Angles.Distance(Yaw[i] - Yaw[i - 1], Pitch[i] - Pitch[i - 1]);
        return sum;
    }
}

public static class Angles
{
    public static double Wrap(double degrees) { degrees = (degrees + 180) % 360; if (degrees < 0) degrees += 360; return degrees - 180; }
    /// <summary>Length of a (yaw, pitch) displacement on the small-angle plane. Flicks are short enough that the plane is a fair model.</summary>
    public static double Distance(double dYaw, double dPitch) => Math.Sqrt(dYaw * dYaw + dPitch * dPitch);
    /// <summary>Unwraps a yaw series in place (continuous across +-180).</summary>
    public static void Unwrap(double[] yaw)
    {
        double offset = 0;
        for (int i = 1; i < yaw.Length; i++)
        {
            var raw = yaw[i];
            var d = Wrap(raw - (yaw[i - 1] - offset));
            yaw[i] = yaw[i - 1] + d;
            offset = yaw[i] - raw;
        }
    }
    /// <summary>Compass sector of a (yaw, pitch) displacement: 0 = right, counter-clockwise in 45 degree sectors (1 = up-right, 2 = up, ...). Yaw grows to the right on screen.</summary>
    public static int Sector(double dYaw, double dPitch)
    {
        var angle = Math.Atan2(dPitch, dYaw) * 180 / Math.PI;
        return ((int)Math.Floor((angle + 22.5 + 360) / 45)) % 8;
    }
    public static readonly string[] SectorNames = ["right", "up-right", "up", "up-left", "left", "down-left", "down", "down-right"];
}

public static class Stats
{
    public static double Mean(IReadOnlyList<double> v) => v.Count == 0 ? double.NaN : v.Average();
    public static double Median(IReadOnlyList<double> v)
    {
        if (v.Count == 0) return double.NaN;
        var s = v.Order().ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }
    public static double Percentile(IReadOnlyList<double> v, double p)
    {
        if (v.Count == 0) return double.NaN;
        var s = v.Order().ToArray();
        var x = Math.Clamp(p / 100 * (s.Length - 1), 0, s.Length - 1);
        var i = (int)Math.Floor(x); var u = x - i;
        return i + 1 < s.Length ? s[i] + (s[i + 1] - s[i]) * u : s[i];
    }
    public static double StdDev(IReadOnlyList<double> v)
    {
        if (v.Count < 2) return double.NaN;
        var m = v.Average();
        return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Count - 1));
    }
    public static double Rms(IReadOnlyList<double> v) => v.Count == 0 ? double.NaN : Math.Sqrt(v.Sum(x => x * x) / v.Count);
}
