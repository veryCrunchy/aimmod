namespace AimMod.InGame.Analysis;

/// <summary>
/// One click-terminated aiming movement, decomposed the way the two-component
/// model of rapid aiming does (Woodworth 1899; Meyer et al. 1988; Elliott et al.
/// 2010): a reaction phase, one large ballistic primary submovement, zero or
/// more corrective submovements, and a verification pause before the click.
/// Geometry is measured on the (yaw, pitch) plane against the straight line
/// from the view at onset to the target at the click.
/// </summary>
public sealed record Flick(
    int Index, double Id,
    double ClickT, double OnsetT, double PrimaryEndT, double CorrectionEndT, double? SpawnT,
    /// Spawn to movement onset, when the target appeared after the previous click.
    double? ReactionMs,
    /// Previous click (or run start) to movement onset, whatever happened in between.
    double GapMs,
    double BallisticMs, double CorrectionMs, double VerificationMs, double TotalMs,
    /// Straight angular distance (deg) from the view at onset to the target direction at the click.
    double Distance,
    double PathLength, double Efficiency,
    /// Where the primary submovement ended along the start-target axis, as a fraction of the distance (1 = on target, below = undershoot, above = overshoot).
    double PrimaryEndRatio,
    /// Furthest excursion beyond the target along the axis, as a fraction of the distance (0 = never passed it).
    double OvershootRatio,
    /// Largest sideways departure from the straight line, as a fraction of the distance.
    double LateralRatio,
    int Corrections, int Sector,
    double TargetRadius, double ClickErrorDeg, bool Hit,
    double PeakSpeed, double ShapeError, double PeakRatio, double TimeToPeak, double Ldlj)
{
    public double IndexOfDifficulty => Math.Log2(Distance / Math.Max(0.05, 2 * TargetRadius) + 1);
}

public sealed record SectorSummary(string Name, int Count, double HitRate, double MeanTotalMs, double MeanPrimaryEndRatio, double MeanEfficiency, double MeanCorrections);
public sealed record AmplitudeSummary(string Range, int Count, double HitRate, double MeanTotalMs, double MeanCorrections, double MeanEfficiency, double MeanPrimaryEndRatio);

public sealed record FlickReport(
    int Count, int Skipped, double HitRate,
    int ReactionSamples, double MeanReactionMs, double MedianReactionMs,
    double MeanGapMs, double MeanBallisticMs, double MeanCorrectionMs, double MeanVerificationMs, double MeanTotalMs, double MedianTotalMs,
    /// Phase with the largest mean duration among reaction (when known), ballistic, correction, verification.
    string CostliestPhase,
    double MeanEfficiency, double MedianEfficiency,
    double MeanPrimaryEndRatio, double UndershootShare, double OvershootShare, double MeanOvershootRatio, double MeanLateralRatio,
    double MeanCorrections, double CleanShare,
    double MeanPeakSpeed, double MeanShapeError, double MeanPeakRatio, double MeanTimeToPeak, double MeanLdlj,
    double MeanClickErrorDeg,
    /// Fitts' law throughput in bits/s: index of difficulty over movement time, nominal target width.
    double Throughput, double MeanIndexOfDifficulty,
    IReadOnlyList<SectorSummary> Sectors, IReadOnlyList<AmplitudeSummary> Amplitudes, IReadOnlyList<Flick> Flicks);

public static class FlickAnalysis
{
    /// <summary>Movements shorter than this are micro-adjustments between shots on one target, not flicks.</summary>
    public const double MinDistance = 1.0;
    /// <summary>Longest movement considered one flick; longer windows are planning or idling.</summary>
    public const double MaxTotalSeconds = 2.0;
    /// <summary>Speed below which the view counts as resting (deg/s); the floor of the window-relative threshold.</summary>
    public const double RestSpeed = 20;
    public const double UndershootBelow = 0.9, OvershootAbove = 1.05;
    static readonly (double From, double To, string Label)[] AmplitudeBins = [(0, 5, "1-5 deg"), (5, 15, "5-15 deg"), (15, 30, "15-30 deg"), (30, 60, "30-60 deg"), (60, double.PositiveInfinity, "60+ deg")];

    public static FlickReport Analyze(AnalysisInput input) => Analyze(input, ViewPath.Resample(input.View, input.Duration));

    public static FlickReport Analyze(AnalysisInput input, ViewPath path, Action? checkpoint = null)
    {
        var flicks = new List<Flick>(); int skipped = 0;
        var clicks = input.FirePressed.Where(t => t >= path.T[0] && t <= path.T[^1]).Order().ToArray();
        var hits = input.Hits.OrderBy(h => h.T).ToArray();
        var spawns = input.Spawns.OrderBy(s => s.T).ToArray();
        double previousClick = path.T[0];
        for (int k = 0; k < clicks.Length; k++)
        {
            checkpoint?.Invoke();
            var flick = Segment(input, path, clicks[k], previousClick, k, hits, spawns);
            previousClick = clicks[k];
            if (flick is null) { skipped++; continue; }
            flicks.Add(flick);
        }
        return Summarize(flicks, skipped);
    }

    internal static Flick? Segment(AnalysisInput input, ViewPath path, double click, double previousClick, int index, HitEvent[] hits, SpawnEvent[] spawns)
    {
        int ic = path.IndexAt(click), ip = path.IndexAt(previousClick);
        if (ic <= ip + 2 || ic >= input.Targets.Count) return null;
        // The target: the one registered as hit by this shot, else the nearest at the click. A killed
        // target's track ends at the last 60 Hz sample before the shot, so look back up to 100 ms.
        var hitIndex = Array.FindIndex(hits, h => h.T >= click - 0.02 && h.T <= click + 0.2);
        var hit = hitIndex >= 0 ? hits[hitIndex] : default;
        var wasHit = hitIndex >= 0;
        // Target identity: the recorded hit target, else the target that vanished right after the click, else the nearest.
        double wanted = hit.Id;
        if (wanted == 0) foreach (var d in despawnsOf(input)) if (d.T >= click - 0.03 && d.T <= click + 0.15) { wanted = d.Id; break; }
        TargetSample target = default; int goalIndex = ic;
        for (int i = ic; i >= Math.Max(ip, ic - 25) && target.Id == 0; i--)
        {
            var visible = input.Targets[i];
            if (visible.Length == 0) continue;
            if (wanted > 0) { var match = visible.FirstOrDefault(t => t.Id == wanted); if (match.Id != 0) { target = match; goalIndex = i; } }
            else { target = visible[0]; goalIndex = i; }
        }
        if (target.Id == 0)
            for (int i = ic; i >= Math.Max(ip, ic - 25) && target.Id == 0; i--) if (input.Targets[i].Length > 0) { target = input.Targets[i][0]; goalIndex = i; }
        if (target.Id == 0) return null;
        static IReadOnlyList<SpawnEvent> despawnsOf(AnalysisInput input) => input.Despawns;

        // Window-relative rest threshold, then the submovement peaks of the speed profile.
        double maxSpeed = 0;
        for (int i = ip + 1; i <= ic; i++) maxSpeed = Math.Max(maxSpeed, path.Speed[i]);
        if (maxSpeed < 2 * RestSpeed) return null;
        var rest = Math.Max(RestSpeed, 0.08 * maxSpeed);
        var peaks = Peaks(path.Speed, ip + 1, ic, Math.Max(1.5 * rest, 0.1 * maxSpeed));
        if (peaks.Count == 0) return null;
        var primary = peaks.First(p => path.Speed[p] >= 0.5 * maxSpeed);
        int onset = primary;
        while (onset > ip && path.Speed[onset - 1] >= rest) onset--;
        // A reaction can only be timed when the movement started from rest; in a continuous
        // flow from the previous click the onset is that click and no spawn was reacted to.
        var fromRest = onset > ip;
        var later = peaks.Where(p => p > primary).ToList();
        int primaryEnd = later.Count > 0 ? ArgMin(path.Speed, primary, later[0]) : FirstBelow(path.Speed, primary, ic, rest);
        int correctionEnd = later.Count > 0 ? FirstBelow(path.Speed, later[^1], ic, rest) : primaryEnd;
        if (correctionEnd < primaryEnd) correctionEnd = primaryEnd;
        var totalMs = (ic - onset) * ViewPath.Step * 1000;
        if (totalMs > MaxTotalSeconds * 1000 || totalMs <= 0) return null;

        // Geometry against the straight line from the view at onset to the target at the click.
        var goalYaw = path.Yaw[goalIndex] + Angles.Wrap(target.Yaw - Angles.Wrap(path.Yaw[goalIndex]));
        var goalPitch = target.Pitch;
        double dYaw = goalYaw - path.Yaw[onset], dPitch = goalPitch - path.Pitch[onset];
        var distance = Angles.Distance(dYaw, dPitch);
        if (distance < MinDistance) return null;
        double ux = dYaw / distance, uy = dPitch / distance;
        double maxProjection = double.NegativeInfinity, maxLateral = 0, primaryProjection = 0;
        for (int i = onset; i <= ic; i++)
        {
            double rx = path.Yaw[i] - path.Yaw[onset], ry = path.Pitch[i] - path.Pitch[onset];
            var projection = rx * ux + ry * uy;
            var lateral = Math.Abs(rx * uy - ry * ux);
            maxProjection = Math.Max(maxProjection, projection); maxLateral = Math.Max(maxLateral, lateral);
            if (i == primaryEnd) primaryProjection = projection;
        }
        var pathLength = path.PathLength(onset, ic);
        var efficiency = pathLength > 0 ? Math.Min(1, distance / pathLength) : 1;
        var clickError = Angles.Distance(Angles.Wrap(path.Yaw[ic] - goalYaw), path.Pitch[ic] - goalPitch);

        // Shape of the primary submovement against the minimum-jerk bell of the same displacement and duration.
        var primaryDistance = Angles.Distance(path.Yaw[primaryEnd] - path.Yaw[onset], path.Pitch[primaryEnd] - path.Pitch[onset]);
        var primarySpeed = path.Speed.AsSpan(onset, Math.Max(3, primaryEnd - onset + 1));
        var (shape, peakRatio, timeToPeak) = MinimumJerk.Compare(primarySpeed, primaryDistance, (primarySpeed.Length - 1) * ViewPath.Step, rest);
        var ldlj = Smoothness.LogDimensionlessJerk(path.Speed.AsSpan(onset, ic - onset + 1), ViewPath.Rate);

        var onsetT = path.T[onset];
        double? spawnT = null;
        if (fromRest) foreach (var s in spawns) if (s.Id == target.Id && s.T > previousClick - 0.05 && s.T <= onsetT) spawnT = s.T;
        return new Flick(index, target.Id, click, onsetT, path.T[primaryEnd], path.T[correctionEnd], spawnT,
            spawnT is double st ? (onsetT - st) * 1000 : null,
            (onsetT - previousClick) * 1000,
            (primaryEnd - onset) * ViewPath.Step * 1000, (correctionEnd - primaryEnd) * ViewPath.Step * 1000, (ic - correctionEnd) * ViewPath.Step * 1000, totalMs,
            distance, pathLength, efficiency,
            primaryProjection / distance, Math.Max(0, maxProjection - distance) / distance, maxLateral / distance,
            later.Count, Angles.Sector(dYaw, dPitch), target.AngularRadius, clickError, wasHit,
            path.Speed[primary], shape, peakRatio, timeToPeak, ldlj);
    }

    /// <summary>Local maxima of `v` in [from, to] above `threshold`; neighbouring maxima whose valley stays above 80% of the lower peak are one submovement.</summary>
    internal static List<int> Peaks(double[] v, int from, int to, double threshold)
    {
        var peaks = new List<int>();
        for (int i = Math.Max(1, from); i <= to && i + 1 < v.Length; i++)
            if (v[i] >= threshold && v[i] >= v[i - 1] && v[i] > v[i + 1]) peaks.Add(i);
        if (peaks.Count == 0 && to < v.Length && to >= from && v[to] >= threshold) peaks.Add(to);
        for (int i = 0; i + 1 < peaks.Count;)
        {
            var valley = v[ArgMin(v, peaks[i], peaks[i + 1])];
            if (valley > 0.8 * Math.Min(v[peaks[i]], v[peaks[i + 1]]))
            {
                if (v[peaks[i]] >= v[peaks[i + 1]]) peaks.RemoveAt(i + 1); else peaks.RemoveAt(i);
            }
            else i++;
        }
        return peaks;
    }

    static int ArgMin(double[] v, int from, int to) { int best = from; for (int i = from; i <= to; i++) if (v[i] < v[best]) best = i; return best; }
    static int FirstBelow(double[] v, int from, int to, double threshold) { for (int i = from; i <= to; i++) if (v[i] < threshold) return i; return to; }

    internal static FlickReport Summarize(List<Flick> flicks, int skipped)
    {
        double M(Func<Flick, double> f) => flicks.Count == 0 ? double.NaN : flicks.Select(f).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Average();
        double Med(Func<Flick, double> f) => Stats.Median(flicks.Select(f).Where(double.IsFinite).ToArray());
        var reactions = flicks.Where(f => f.ReactionMs is double r && r >= 0).Select(f => f.ReactionMs!.Value).ToArray();
        var phases = new (string Name, double Ms)[] { ("reaction", reactions.Length >= 3 ? reactions.Average() : double.NaN), ("ballistic", M(f => f.BallisticMs)), ("correction", M(f => f.CorrectionMs)), ("verification", M(f => f.VerificationMs)) };
        var costliest = flicks.Count == 0 ? "none" : phases.Where(p => double.IsFinite(p.Ms)).MaxBy(p => p.Ms).Name;
        var sectors = Enumerable.Range(0, 8).Select(s => { var g = flicks.Where(f => f.Sector == s).ToArray(); return g.Length == 0 ? null : new SectorSummary(Angles.SectorNames[s], g.Length,
            g.Average(f => f.Hit ? 1.0 : 0), g.Average(f => f.TotalMs), g.Average(f => f.PrimaryEndRatio), g.Average(f => f.Efficiency), g.Average(f => f.Corrections)); }).Where(s => s is not null).Select(s => s!).ToArray();
        var amplitudes = AmplitudeBins.Select(b => { var g = flicks.Where(f => f.Distance >= b.From && f.Distance < b.To).ToArray(); return g.Length == 0 ? null : new AmplitudeSummary(b.Label, g.Length,
            g.Average(f => f.Hit ? 1.0 : 0), g.Average(f => f.TotalMs), g.Average(f => f.Corrections), g.Average(f => f.Efficiency), g.Average(f => f.PrimaryEndRatio)); }).Where(a => a is not null).Select(a => a!).ToArray();
        return new FlickReport(flicks.Count, skipped, M(f => f.Hit ? 1 : 0),
            reactions.Length, reactions.Length > 0 ? reactions.Average() : double.NaN, Stats.Median(reactions),
            M(f => f.GapMs), M(f => f.BallisticMs), M(f => f.CorrectionMs), M(f => f.VerificationMs), M(f => f.TotalMs), Med(f => f.TotalMs),
            costliest,
            M(f => f.Efficiency), Med(f => f.Efficiency),
            M(f => f.PrimaryEndRatio), M(f => f.PrimaryEndRatio < UndershootBelow ? 1 : 0), M(f => f.PrimaryEndRatio > OvershootAbove ? 1 : 0), M(f => f.OvershootRatio), M(f => f.LateralRatio),
            M(f => f.Corrections), M(f => f.Corrections == 0 ? 1 : 0),
            M(f => f.PeakSpeed), M(f => f.ShapeError), M(f => f.PeakRatio), M(f => f.TimeToPeak), M(f => f.Ldlj),
            M(f => f.ClickErrorDeg),
            M(f => f.IndexOfDifficulty / (f.TotalMs / 1000)), M(f => f.IndexOfDifficulty),
            sectors, amplitudes, flicks);
    }
}
