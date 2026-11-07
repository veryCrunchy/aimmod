using System.Numerics;

namespace AimMod.InGame.Analysis;

/// <summary>
/// Movement smoothness measures from the motor-control literature, applied to
/// the view speed profile (deg/s on the analysis grid).
///
/// SPARC (spectral arc length, Balasubramanian et al. 2015): the negative arc
/// length of the normalised amplitude spectrum of the speed profile up to an
/// adaptive cut-off. A single bell-shaped movement is about -1.4 to -1.6; every
/// extra bump or tremor adds spectral content and makes it more negative.
/// It is independent of movement duration and amplitude, which is why it is
/// preferred over jerk for tracking windows of arbitrary length.
///
/// LDLJ (log dimensionless jerk): -ln(duration^3 / peakSpeed^2 x integral of
/// jerk^2). More negative = less smooth. Sensitive to duration, so it is only
/// reported for segments of comparable length (single flicks).
/// </summary>
public static class Smoothness
{
    public const double DefaultCutoffHz = 10, DefaultAmplitudeThreshold = 0.05;

    /// <summary>Spectral arc length of a speed profile sampled at `rate` Hz. NaN for fewer than 8 samples or a profile that never moves.</summary>
    public static double Sparc(ReadOnlySpan<double> speed, double rate, double cutoffHz = DefaultCutoffHz, double amplitudeThreshold = DefaultAmplitudeThreshold, int padLevel = 4)
    {
        if (speed.Length < 8) return double.NaN;
        var n = 1 << (int)Math.Ceiling(Math.Log2(speed.Length) + padLevel);
        var buffer = new Complex[n];
        for (int i = 0; i < speed.Length; i++) buffer[i] = speed[i];
        Fft(buffer);
        var half = n / 2 + 1;
        var magnitude = new double[half]; double max = 0;
        for (int i = 0; i < half; i++) { magnitude[i] = buffer[i].Magnitude; max = Math.Max(max, magnitude[i]); }
        if (max <= 0) return double.NaN;
        var df = rate / n;
        // Spectrum up to the fixed cut-off, then trimmed to the last component still above the amplitude threshold.
        var limit = Math.Min(half - 1, (int)Math.Floor(cutoffHz / df));
        var last = limit;
        while (last > 1 && magnitude[last] / max < amplitudeThreshold) last--;
        if (last < 1) return double.NaN;
        var fRange = last * df;
        double arc = 0;
        for (int i = 1; i <= last; i++)
        {
            var dfNorm = df / fRange;
            var dm = (magnitude[i] - magnitude[i - 1]) / max;
            arc += Math.Sqrt(dfNorm * dfNorm + dm * dm);
        }
        return -arc;
    }

    /// <summary>Log dimensionless jerk of a speed profile (deg/s at `rate` Hz).</summary>
    public static double LogDimensionlessJerk(ReadOnlySpan<double> speed, double rate)
    {
        if (speed.Length < 4) return double.NaN;
        var dt = 1 / rate; var duration = (speed.Length - 1) * dt;
        double peak = 0, jerk2 = 0;
        for (int i = 0; i < speed.Length; i++) peak = Math.Max(peak, Math.Abs(speed[i]));
        if (peak <= 0) return double.NaN;
        for (int i = 1; i + 1 < speed.Length; i++)
        {
            var j = (speed[i + 1] - 2 * speed[i] + speed[i - 1]) / (dt * dt);
            jerk2 += j * j * dt;
        }
        return -Math.Log(Math.Pow(duration, 3) / (peak * peak) * jerk2);
    }

    /// <summary>Share of velocity energy above `hz` (tremor / micro-correction band) in the spectrum of a velocity series.</summary>
    public static double HighFrequencyShare(ReadOnlySpan<double> velocity, double rate, double hz = 6)
    {
        if (velocity.Length < 8) return double.NaN;
        var n = 1 << (int)Math.Ceiling(Math.Log2(velocity.Length));
        var buffer = new Complex[n];
        double mean = 0; for (int i = 0; i < velocity.Length; i++) mean += velocity[i]; mean /= velocity.Length;
        // Hann window: the segment's edges would otherwise leak into every band.
        for (int i = 0; i < velocity.Length; i++) buffer[i] = (velocity[i] - mean) * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (velocity.Length - 1)));
        Fft(buffer);
        var df = rate / n; double total = 0, high = 0;
        for (int i = 1; i <= n / 2; i++)
        {
            var p = buffer[i].Magnitude * buffer[i].Magnitude;
            total += p; if (i * df >= hz) high += p;
        }
        return total > 0 ? high / total : double.NaN;
    }

    /// <summary>In-place iterative radix-2 FFT; `data` length must be a power of two.</summary>
    public static void Fft(Complex[] data)
    {
        var n = data.Length;
        if (n <= 1 || (n & (n - 1)) != 0) throw new ArgumentException("FFT length must be a power of two.");
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            var angle = -2 * Math.PI / len;
            var wn = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = data[i + k]; var v = data[i + k + len / 2] * w;
                    data[i + k] = u + v; data[i + k + len / 2] = u - v;
                    w *= wn;
                }
            }
        }
    }
}
