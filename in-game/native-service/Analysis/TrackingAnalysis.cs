namespace AimMod.InGame.Analysis;

/// <summary>
/// Continuous pursuit against the nearest target: error decomposition
/// (horizontal / vertical, bias, along-motion lead or lag), the time shift
/// between target and view motion, time on target, reversal reaction and
/// smoothness (SPARC over 2 s windows, high-frequency velocity share).
/// Positive yaw error means the view is to the right of the target; positive
/// pitch error above it. Positive lag means the view trails the target.
/// </summary>
public sealed record TrackingReport(
    double TrackedSeconds, double TrackedShare,
    double RmsYawErrorDeg, double RmsPitchErrorDeg, double MeanAbsErrorDeg, double YawBiasDeg, double PitchBiasDeg,
    /// Signed error along the target's direction of motion while it moves: negative = behind.
    double AlongMotionErrorDeg, double BehindShare,
    /// Shift (ms) that best aligns view velocity with target velocity (cross-correlation) and that minimises yaw error.
    double LagMs, double LagMsByError, double VelocityCorrelation,
    double OnTargetShare, double OnTargetShareWhileFiring, double FiringShare,
    /// Target direction reversals; the mean delay until the view reversed too (negative = anticipated) and the error peak that followed.
    int Reversals, double MeanReversalReactionMs, double MeanReversalOvershootDeg,
    int TargetSwitches, double MeanSwitchAcquireMs,
    double MeanSparc, double MeanHighFrequencyShare, double MeanSpeed, double MeanTargetSpeed);

public static class TrackingAnalysis
{
    public const double MaxLagSeconds = 0.3;
    public const double WindowSeconds = 2;
    public const double ReversalSpeed = 15; // deg/s the target must reach on both sides of a direction change
    public const int ReversalHoldSamples = 10; // 40 ms the view's velocity must keep the new sign to count as having followed
    public const int ReversalAnticipationSamples = 37; // 150 ms before the target reversal in which a view reversal counts as anticipation (negative reaction)

    public static TrackingReport Analyze(AnalysisInput input) => Analyze(input, ViewPath.Resample(input.View, input.Duration));

    public static TrackingReport Analyze(AnalysisInput input, ViewPath path, Action? checkpoint = null)
    {
        var n = Math.Min(path.Count, input.Targets.Count);
        // Nearest target per sample, absolute direction; NaN where no target is visible.
        var id = new double[n]; var tYaw = new double[n]; var tPitch = new double[n]; var on = new bool[n];
        for (int i = 0; i < n; i++)
        {
            var targets = input.Targets[i];
            if (targets.Length == 0) { id[i] = 0; tYaw[i] = tPitch[i] = double.NaN; continue; }
            var t = targets[0]; id[i] = t.Id; tYaw[i] = t.Yaw; tPitch[i] = t.Pitch; on[i] = t.OnTarget;
        }
        // Errors (view minus target) and target velocity on the same grid.
        var eYaw = new double[n]; var ePitch = new double[n]; var tvYaw = new double[n]; var tvPitch = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (double.IsNaN(tYaw[i])) { eYaw[i] = ePitch[i] = tvYaw[i] = tvPitch[i] = double.NaN; continue; }
            eYaw[i] = Angles.Wrap(Angles.Wrap(path.Yaw[i]) - tYaw[i]); ePitch[i] = path.Pitch[i] - tPitch[i];
            int a = i, b = i;
            if (i > 0 && id[i - 1] == id[i]) a = i - 1;
            if (i + 1 < n && id[i + 1] == id[i]) b = i + 1;
            if (a == b) { tvYaw[i] = tvPitch[i] = 0; continue; }
            var dt = (b - a) * ViewPath.Step;
            tvYaw[i] = Angles.Wrap(tYaw[b] - tYaw[a]) / dt; tvPitch[i] = (tPitch[b] - tPitch[a]) / dt;
        }
        Smooth(tvYaw); Smooth(tvPitch);
        var tracked = Enumerable.Range(0, n).Where(i => !double.IsNaN(eYaw[i])).ToArray();
        var firing = FiringMask(input, path, n);
        var rmsYaw = Stats.Rms(tracked.Select(i => eYaw[i]).ToArray()); var rmsPitch = Stats.Rms(tracked.Select(i => ePitch[i]).ToArray());
        var meanAbs = tracked.Length == 0 ? double.NaN : tracked.Average(i => Angles.Distance(eYaw[i], ePitch[i]));
        var yawBias = Stats.Mean(tracked.Select(i => eYaw[i]).ToArray()); var pitchBias = Stats.Mean(tracked.Select(i => ePitch[i]).ToArray());
        // Along-motion error: project the error onto the target's velocity direction while it moves.
        var moving = tracked.Where(i => Angles.Distance(tvYaw[i], tvPitch[i]) >= 10).ToArray();
        var along = moving.Select(i => { var s = Angles.Distance(tvYaw[i], tvPitch[i]); return (eYaw[i] * tvYaw[i] + ePitch[i] * tvPitch[i]) / s; }).ToArray();
        var alongMean = Stats.Mean(along); var behind = along.Length == 0 ? double.NaN : along.Average(v => v < 0 ? 1.0 : 0);

        // Lag is only meaningful against a target that moves; static targets give boundary solutions.
        var targetMoves = moving.Length >= 0.2 * Math.Max(1, tracked.Length);
        var (lagMs, correlation) = targetMoves ? LagByCorrelation(path.YawVelocity, tvYaw, id, n, checkpoint) : (double.NaN, double.NaN);
        var lagByError = targetMoves ? LagByError(path.Yaw, tYaw, id, n, checkpoint) : double.NaN;
        var onShare = tracked.Length == 0 ? double.NaN : tracked.Average(i => on[i] ? 1.0 : 0);
        var firingTracked = tracked.Where(i => firing[i]).ToArray();
        var onFiring = firingTracked.Length == 0 ? double.NaN : firingTracked.Average(i => on[i] ? 1.0 : 0);
        var firingShare = n == 0 ? double.NaN : firing.Count(f => f) / (double)n;

        var (reversals, reaction, reversalOvershoot) = Reversals(path, tvYaw, eYaw, id, n);
        var (switches, acquire) = Switches(id, on, n);

        // Smoothness over 2 s windows that are fully tracked.
        var window = (int)Math.Round(WindowSeconds * ViewPath.Rate);
        var sparcs = new List<double>(); var highs = new List<double>();
        for (int start = 0; start + window <= n; start += window / 2)
        {
            checkpoint?.Invoke();
            bool complete = true;
            for (int i = start; i < start + window; i++) if (double.IsNaN(eYaw[i])) { complete = false; break; }
            if (!complete) continue;
            var s = Smoothness.Sparc(path.Speed.AsSpan(start, window), ViewPath.Rate);
            var h = Smoothness.HighFrequencyShare(path.YawVelocity.AsSpan(start, window), ViewPath.Rate);
            if (double.IsFinite(s)) sparcs.Add(s);
            if (double.IsFinite(h)) highs.Add(h);
        }
        return new TrackingReport(tracked.Length * ViewPath.Step, n == 0 ? 0 : tracked.Length / (double)n,
            rmsYaw, rmsPitch, meanAbs, yawBias, pitchBias, alongMean, behind,
            lagMs, lagByError, correlation,
            onShare, onFiring, firingShare,
            reversals, reaction, reversalOvershoot, switches, acquire,
            Stats.Mean(sparcs), Stats.Mean(highs),
            tracked.Length == 0 ? double.NaN : tracked.Average(i => path.Speed[i]),
            tracked.Length == 0 ? double.NaN : tracked.Average(i => Angles.Distance(tvYaw[i], tvPitch[i])));
    }

    static void Smooth(double[] v)
    {
        var copy = (double[])v.Clone(); double[] w = [1, 2, 3, 2, 1];
        for (int i = 0; i < v.Length; i++)
        {
            if (double.IsNaN(copy[i])) continue;
            double s = 0, sw = 0;
            for (int k = -2; k <= 2; k++) { var j = i + k; if (j < 0 || j >= v.Length || double.IsNaN(copy[j])) continue; s += copy[j] * w[k + 2]; sw += w[k + 2]; }
            v[i] = s / sw;
        }
    }

    static bool[] FiringMask(AnalysisInput input, ViewPath path, int n)
    {
        var mask = new bool[n];
        var presses = input.FirePressed.Order().ToArray(); var releases = input.FireReleased.Order().ToArray();
        int r = 0;
        foreach (var press in presses)
        {
            while (r < releases.Length && releases[r] <= press) r++;
            var end = r < releases.Length ? releases[r] : input.Duration;
            for (int i = Math.Max(0, path.IndexAt(press)); i < n && path.T[i] <= end; i++) mask[i] = true;
        }
        return mask;
    }

    /// <summary>Shift of the target velocity (in samples, converted to ms) that maximises its correlation with the view velocity.</summary>
    internal static (double LagMs, double Correlation) LagByCorrelation(double[] viewVelocity, double[] targetVelocity, double[] id, int n, Action? checkpoint = null)
    {
        var maxShift = (int)Math.Round(MaxLagSeconds * ViewPath.Rate);
        double best = double.NegativeInfinity; int bestShift = 0;
        for (int shift = -maxShift; shift <= maxShift; shift++)
        {
            checkpoint?.Invoke();
            double sxy = 0, sxx = 0, syy = 0; int count = 0;
            for (int i = Math.Max(0, shift); i < n && i - shift < n; i++)
            {
                var j = i - shift; // view at i follows target at j = i - shift
                if (double.IsNaN(targetVelocity[j]) || id[j] == 0 || id[j] != id[i]) continue;
                double x = viewVelocity[i], y = targetVelocity[j];
                sxy += x * y; sxx += x * x; syy += y * y; count++;
            }
            if (count < 50 || sxx <= 0 || syy <= 0) continue;
            var c = sxy / Math.Sqrt(sxx * syy);
            if (c > best) { best = c; bestShift = shift; }
        }
        return double.IsFinite(best) && Math.Abs(bestShift) < maxShift ? (bestShift * ViewPath.Step * 1000, best) : (double.NaN, double.NaN);
    }

    /// <summary>Shift of the target direction that minimises the RMS yaw error against the view.</summary>
    internal static double LagByError(double[] viewYaw, double[] targetYaw, double[] id, int n, Action? checkpoint = null)
    {
        var maxShift = (int)Math.Round(MaxLagSeconds * ViewPath.Rate);
        double best = double.PositiveInfinity; int bestShift = 0;
        for (int shift = -maxShift; shift <= maxShift; shift++)
        {
            checkpoint?.Invoke();
            double sum = 0; int count = 0;
            for (int i = Math.Max(0, shift); i < n && i - shift < n; i++)
            {
                var j = i - shift;
                if (double.IsNaN(targetYaw[j]) || id[j] == 0 || id[j] != id[i]) continue;
                var e = Angles.Wrap(Angles.Wrap(viewYaw[i]) - targetYaw[j]); sum += e * e; count++;
            }
            if (count < 50) continue;
            var rms = Math.Sqrt(sum / count);
            if (rms < best) { best = rms; bestShift = shift; }
        }
        return double.IsFinite(best) && Math.Abs(bestShift) < maxShift ? bestShift * ViewPath.Step * 1000 : double.NaN;
    }

    /// <summary>Target yaw reversals and how long the view took to reverse too, plus the error peak that followed.</summary>
    internal static (int Count, double MeanReactionMs, double MeanOvershootDeg) Reversals(ViewPath path, double[] tvYaw, double[] eYaw, double[] id, int n)
    {
        var reactions = new List<double>(); var overshoots = new List<double>(); int count = 0;
        int window = (int)Math.Round(0.6 * ViewPath.Rate), confirm = (int)Math.Round(0.3 * ViewPath.Rate);
        for (int i = 1; i < n; i++)
        {
            if (double.IsNaN(tvYaw[i]) || double.IsNaN(tvYaw[i - 1]) || id[i] != id[i - 1]) continue;
            if (Math.Sign(tvYaw[i]) == Math.Sign(tvYaw[i - 1]) || tvYaw[i] == 0) continue;
            // A genuine reversal: the same target reached the speed floor within 300 ms on both sides (bots accelerate gradually).
            double before = 0, after = 0; bool same = true;
            for (int j = Math.Max(0, i - confirm); j < i; j++) { if (id[j] != id[i]) { same = false; break; } before = Math.Max(before, Math.Abs(tvYaw[j])); }
            for (int j = i; same && j < n && j <= i + confirm; j++) { if (id[j] != id[i]) { same = false; break; } after = Math.Max(after, Math.Abs(tvYaw[j])); }
            if (!same || before < ReversalSpeed || after < ReversalSpeed) continue;
            count++;
            var newSign = Math.Sign(tvYaw[i]);
            int react = int.MinValue; double peak = 0;
            // The view has reversed when its velocity crosses zero into the new direction and stays there for 40 ms.
            // The search starts 150 ms early: pursuit of a predictable target often reverses before the target does.
            for (int j = Math.Max(0, i - ReversalAnticipationSamples); j < n && j <= i + window && id[j] == id[i]; j++)
            {
                if (react == int.MinValue && Math.Sign(path.YawVelocity[j]) == newSign)
                {
                    bool holds = true;
                    for (int k = j; k < Math.Min(n, j + ReversalHoldSamples); k++) if (Math.Sign(path.YawVelocity[k]) != newSign) { holds = false; break; }
                    if (holds) react = j;
                }
                if (j >= i && !double.IsNaN(eYaw[j])) peak = Math.Max(peak, Math.Abs(eYaw[j]));
            }
            if (react != int.MinValue) reactions.Add((react - i) * ViewPath.Step * 1000);
            overshoots.Add(peak);
            i += confirm;
        }
        return (count, Stats.Mean(reactions), Stats.Mean(overshoots));
    }

    /// <summary>Nearest-target changes and the time until the view was on the new target.</summary>
    internal static (int Count, double MeanAcquireMs) Switches(double[] id, bool[] on, int n)
    {
        var acquire = new List<double>(); int count = 0;
        var limit = (int)Math.Round(1.0 * ViewPath.Rate);
        for (int i = 1; i < n; i++)
        {
            if (id[i] == 0 || id[i - 1] == 0 || id[i] == id[i - 1]) continue;
            count++;
            for (int j = i; j < n && j <= i + limit && id[j] == id[i]; j++) if (on[j]) { acquire.Add((j - i) * ViewPath.Step * 1000); break; }
        }
        return (count, Stats.Mean(acquire));
    }
}
