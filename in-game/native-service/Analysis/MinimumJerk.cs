namespace AimMod.InGame.Analysis;

/// <summary>
/// Minimum-jerk point-to-point movement (Flash and Hogan 1985): the straight
/// path whose position follows 10u^3 - 15u^4 + 6u^5 over normalised time u.
/// Its bell-shaped speed profile peaks at 1.875 x distance / duration, at
/// mid-movement. Used as the "ideal" a recorded flick is scored against.
/// </summary>
public static class MinimumJerk
{
    public const double PeakFactor = 1.875;

    /// <summary>Fraction of the distance covered at normalised time u in [0, 1].</summary>
    public static double Position(double u) { u = Math.Clamp(u, 0, 1); return u * u * u * (10 - 15 * u + 6 * u * u); }

    /// <summary>Speed (distance units per second) at normalised time u for a move of `distance` over `duration` seconds.</summary>
    public static double Speed(double u, double distance, double duration)
    {
        u = Math.Clamp(u, 0, 1);
        return distance / duration * 30 * u * u * (1 - u) * (1 - u);
    }

    /// <summary>Ideal path samples from (yaw0, pitch0) to (yaw1, pitch1) over `duration`, at the analysis rate.</summary>
    public static ViewSample[] Path(double t0, double yaw0, double pitch0, double yaw1, double pitch1, double duration)
    {
        var n = Math.Max(2, (int)Math.Round(duration * ViewPath.Rate) + 1);
        var result = new ViewSample[n];
        for (int i = 0; i < n; i++)
        {
            var u = i / (double)(n - 1);
            var p = Position(u);
            result[i] = new(t0 + u * duration, yaw0 + (yaw1 - yaw0) * p, pitch0 + (pitch1 - pitch0) * p);
        }
        return result;
    }

    /// <summary>
    /// How far a recorded speed profile departs from the minimum-jerk bell of the
    /// same distance and duration: RMS difference normalised by the ideal peak
    /// (0 = identical shape), plus where the recorded peak sits (0.5 = ideal).
    /// </summary>
    /// <param name="restThreshold">Speed below which the segment was cut off at both ends (deg/s); the bell is fitted to the full movement those cuts imply.</param>
    public static (double ShapeError, double PeakRatio, double TimeToPeak) Compare(ReadOnlySpan<double> speed, double distance, double duration, double restThreshold = 0)
    {
        if (speed.Length < 3 || distance <= 0 || duration <= 0) return (double.NaN, double.NaN, double.NaN);
        double peak = 0; int peakAt = 0;
        for (int i = 0; i < speed.Length; i++) if (speed[i] > peak) { peak = speed[i]; peakAt = i; }
        if (peak <= 0) return (double.NaN, double.NaN, double.NaN);
        // Where on the normalised bell the segment was truncated: s(u0) = threshold / peak.
        var u0 = restThreshold > 0 && restThreshold < peak ? Truncation(restThreshold / peak) : 0;
        var fullDuration = duration / (1 - 2 * u0);
        var fullDistance = distance / (Position(1 - u0) - Position(u0));
        var ideal = PeakFactor * fullDistance / fullDuration;
        double sum = 0;
        for (int i = 0; i < speed.Length; i++)
        {
            var u = u0 + (1 - 2 * u0) * i / (speed.Length - 1);
            var d = speed[i] - Speed(u, fullDistance, fullDuration);
            sum += d * d;
        }
        return (Math.Sqrt(sum / speed.Length) / ideal, peak / ideal, (u0 + (1 - 2 * u0) * peakAt / (speed.Length - 1)));
    }

    /// <summary>Normalised time u in [0, 0.5) at which the bell's speed is `fraction` of its peak.</summary>
    internal static double Truncation(double fraction)
    {
        if (fraction <= 0) return 0;
        double lo = 0, hi = 0.5;
        for (int i = 0; i < 40; i++) { var mid = (lo + hi) / 2; if (30 * mid * mid * (1 - mid) * (1 - mid) / PeakFactor < fraction) lo = mid; else hi = mid; }
        return (lo + hi) / 2;
    }
}
