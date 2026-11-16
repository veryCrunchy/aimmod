using AimMod.InGame.Analysis;

namespace AimMod.InGame;

partial record ResponseEpisode
{
    public double? FirstOnTargetMs { get; init; }
    public double? PreClickSpeedRatio { get; init; }
    public double? ArrivalErrorDegrees { get; init; }
    public double? OvershootDegrees { get; init; }
    public int? Corrections { get; init; }
    public double? KillToMovementMs { get; init; }
    public double? TimeToKillMs { get; init; }
    public string? Stimulus { get; init; }
}
partial record ResponseSummary
{
    public double? FirstOnTargetMs { get; init; }
    public double? PreClickSpeedRatio { get; init; }
    public double? TimeToKillMs { get; init; }
}
partial record InspectedShot
{
    public double? TargetAgeMs { get; init; }
    public double? ClickSpeedDegreesPerSecond { get; init; }
    public bool? MovingFast { get; init; }
    public string? MissDirection { get; init; }
    public double? MissDegrees { get; init; }
}
partial record AnalysisWindow
{
    public string? Detail { get; init; }
}

// All timings are relative to the replay clock. Unknown outcomes stay unknown.
static class NativeTargetResponses
{
    internal sealed record Result(ResponseSummary? Summary, ResponseEpisode[] Episodes, InspectedShot[] Shots,
        AnalysisWindow[] Windows, double? FireToHitMs, double? FireToHitP90Ms, double? CorrectiveRatio);
    sealed record Segment(int Start, int End, double Id, double Appeared, string Stimulus);
    internal static double? Mean(IEnumerable<double?> values)
    { var known = values.OfType<double>().ToArray(); return known.Length == 0 ? null : known.Average(); }
    static double? P90(IEnumerable<double?> values)
    { var known = values.OfType<double>().ToArray(); return known.Length == 0 ? null : Stats.Percentile(known, 90); }
    static string Label(double id) => "Target " + id.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    internal static Result Build(NativeReplay replay)
    {
        var input = ReplayAnalysis.Build(replay);
        return Analyze(input, ViewPath.Resample(input.View, input.Duration),
            replay.Motion?.Hits.Count > 0 || replay.Frames.Any(f => f.Stats?.Hits is not null));
    }
    internal static Result Analyze(AnalysisInput input, ViewPath path, bool outcomesKnown)
    {
        var n = Math.Min(path.Count, input.Targets.Count);
        if (n < 2) return new(null, [], [], [], null, null, null);
        var tracking = ReplayAnalysis.FireHoldShare(input) >= ReplayAnalysis.TrackingHoldShare;
        var segments = new List<Segment>();
        var ages = new Dictionary<double, double>();
        var previous = new HashSet<double>();
        double? selected = null, candidate = null;
        int start = 0, candidateStart = 0;
        double appeared = 0;
        string stimulus = "spawn";
        var shotAges = new double?[n];
        var respawns = input.Spawns.Where(s => s.Id != 0).GroupBy(s => path.IndexAt(s.T)).ToDictionary(g => g.Key, g => g.ToArray());
        // A 40 ms dwell and a 0.75 degree advantage stop nearest-target boundary chatter.
        for (int i = 0; i < n; i++)
        {
            var ts = input.Targets[i];
            if (respawns.TryGetValue(i, out var events))
                foreach (var spawn in events)
                {
                    ages[spawn.Id] = spawn.T;
                    if (selected == spawn.Id)
                    {
                        if (i > start) segments.Add(new(start, i - 1, spawn.Id, appeared, stimulus));
                        start = i; appeared = spawn.T; stimulus = "spawn";
                    }
                }
            var present = new HashSet<double>();
            foreach (var t in ts)
            {
                present.Add(t.Id);
                if (!previous.Contains(t.Id)) ages[t.Id] = path.T[i];
            }
            if (selected is double old && !present.Contains(old))
            { segments.Add(new(start, i - 1, old, appeared, stimulus)); selected = null; candidate = null; }
            if (ts.Length > 0)
            {
                var nearest = ts[0];
                shotAges[i] = (path.T[i] - ages[nearest.Id]) * 1000;
                var current = ts.FirstOrDefault(t => t.Id == selected);
                double Error(TargetSample t) => Angles.Distance(Angles.Wrap(t.Yaw - path.Yaw[i]), t.Pitch - path.Pitch[i]) - t.AngularRadius;
                if (selected is null || (nearest.Id != selected && Error(nearest) + .75 < Error(current)))
                {
                    if (candidate != nearest.Id) { candidate = nearest.Id; candidateStart = i; }
                    if (i - candidateStart >= 10)
                    {
                        if (selected is double prior) segments.Add(new(start, candidateStart - 1, prior, appeared, stimulus));
                        selected = nearest.Id; start = candidateStart; appeared = ages[nearest.Id];
                        stimulus = path.T[start] - appeared <= .08 ? "spawn" : "selection";
                        candidate = null;
                    }
                }
                else candidate = null;
                // A moving target's sharp turn is a stimulus even while fire is held.
                if (tracking && selected is double moving && i - start >= 100 && i >= 30)
                {
                    TargetSample? At(int j) { foreach (var t in input.Targets[j]) if (t.Id == moving) return t; return null; }
                    if (At(i - 30) is { } a && At(i - 15) is { } b && At(i) is { } c)
                    {
                        var x = Angles.Wrap(b.Yaw - a.Yaw); var y = b.Pitch - a.Pitch;
                        var u = Angles.Wrap(c.Yaw - b.Yaw); var v = c.Pitch - b.Pitch;
                        var lengths = Angles.Distance(x, y) * Angles.Distance(u, v);
                        if (lengths > .36 && (x * u + y * v) / lengths < .5)
                        {
                            segments.Add(new(start, i - 15 - 1, moving, appeared, stimulus));
                            start = i - 15; stimulus = "path_change";
                        }
                    }
                }
            }
            previous = present;
        }
        if (selected is double last) segments.Add(new(start, n - 1, last, appeared, stimulus));
        var presses = input.FirePressed.Where(t => t >= path.T[0] && t <= path.T[n - 1]).Distinct().Order().Take(5000).ToArray();
        var hits = input.Hits.OrderBy(h => h.T).ToArray();
        var shots = new List<InspectedShot>(); var latencies = new List<double?>();
        int hitIndex = 0;
        foreach (var press in presses)
        {
            var i = path.IndexAt(press); var targets = input.Targets[i];
            // Counter-only hit timestamps have frame granularity; exact hooks use a tighter window.
            while (hitIndex < hits.Length && hits[hitIndex].T < press - .005) hitIndex++;
            HitEvent? landed = hitIndex < hits.Length && hits[hitIndex].T <= press + (hits[hitIndex].Id == 0 ? .15 : .04) ? hits[hitIndex] : null;
            if (landed is not null) { hitIndex++; latencies.Add(Math.Max(0, landed.Value.T - press) * 1000); }
            var context = new List<ShotTarget>();
            double? yaw = null, pitch = null;
            if (targets.Length > 0)
            {
                yaw = Angles.Wrap(path.Yaw[i] - targets[0].Yaw); pitch = path.Pitch[i] - targets[0].Pitch;
                context.Add(new(Label(targets[0].Id), null, yaw, pitch, true));
            }
            var kind = landed is not null ? "hit" : outcomesKnown && !tracking ? "miss" : "unknown";
            var peak = 0.0;
            for (int j = Math.Max(0, i - 75); j <= i; j++) peak = Math.Max(peak, path.Speed[j]);
            shots.Add(new(shots.Count, kind, press * 1000, null, null, context)
            {
                TargetAgeMs = shotAges[i], ClickSpeedDegreesPerSecond = path.Speed[i], MovingFast = peak >= 20 ? path.Speed[i] >= Math.Max(20, peak * .35) : null,
                MissDirection = kind == "miss" && yaw is double ey && pitch is double ep ? Math.Abs(ey) >= Math.Abs(ep) ? ey > 0 ? "right" : "left" : ep > 0 ? "over" : "under" : null,
                MissDegrees = kind == "miss" && yaw is double dy && pitch is double dp ? Angles.Distance(dy, dp) : null
            });
        }
        var episodes = new List<ResponseEpisode>();
        double? previousKill = null;
        int eligible = 0, corrective = 0;
        foreach (var s in segments.Take(2000))
        {
            if (s.End - s.Start < 15) continue;
            var t0 = path.T[s.Start]; var end = path.T[s.End];
            if (t0 <= .04 && s.Stimulus == "spawn") continue;
            var samples = new List<(int I, TargetSample Target)>();
            for (int i = s.Start; i <= s.End; i++)
                foreach (var target in input.Targets[i]) if (target.Id == s.Id) { samples.Add((i, target)); break; }
            if (samples.Count < 15) continue;
            var first = samples[0].Target;
            var ey = Angles.Wrap(first.Yaw - path.Yaw[s.Start]); var ep = first.Pitch - path.Pitch[s.Start];
            var distance = Angles.Distance(ey, ep);
            // Initial already-on-target holds and tiny transitions have no acquisition to measure.
            if (distance < Math.Max(.75, first.AngularRadius) && s.Stimulus != "path_change") continue;
            var relevant = shots.Where(x => x.TimestampMs >= t0 * 1000 && x.TimestampMs <= end * 1000 && x.Targets.FirstOrDefault()?.Label == Label(s.Id)).ToArray();
            var hit = hits.FirstOrDefault(h => h.T >= t0 && h.T <= end + .08 && (h.Id == s.Id || h.Id == 0 && relevant.Any(x => x.Kind == "hit" && Math.Abs(x.TimestampMs / 1000 - h.T) <= .15)));
            double? hitTime = hit.T > 0 ? hit.T : null;
            var stop = tracking ? end : hitTime ?? end;
            var baselineStart = Math.Max(0, s.Start - 35); var baselineEnd = Math.Max(baselineStart, s.Start - 6);
            var baselineCount = Math.Max(1, baselineEnd - baselineStart + 1);
            var baselineYaw = Enumerable.Range(baselineStart, baselineCount).Average(i => path.YawVelocity[i]);
            var baselinePitch = Enumerable.Range(baselineStart, baselineCount).Average(i => path.PitchVelocity[i]);
            var baselineSpeed = Angles.Distance(baselineYaw, baselinePitch);
            double? slowdown = null;
            int? onset = null, arrival = null, settled = null, ballisticEnd = null;
            int onStreak = 0;
            double peak = 0, overshoot = 0; int corrections = 0, sign = 0, lastTurn = s.Start;
            double? arrivalError = null;
            foreach (var (i, target) in samples)
            {
                if (path.T[i] > stop) break;
                peak = Math.Max(peak, path.Speed[i]);
                var dx = Angles.Wrap(target.Yaw - path.Yaw[i]); var dy = target.Pitch - path.Pitch[i];
                var error = Angles.Distance(dx, dy);
                var toward = error > .01 ? ((path.YawVelocity[i] - baselineYaw) * dx + (path.PitchVelocity[i] - baselinePitch) * dy) / error : 0;
                // Require 12 ms of directed change from the pre-stimulus movement.
                var threshold = Math.Max(8, baselineSpeed * .25);
                bool Directed(int j) => error > .01 && ((path.YawVelocity[j] - baselineYaw) * dx + (path.PitchVelocity[j] - baselinePitch) * dy) / error >= threshold;
                if (onset is null && i + 3 <= s.End && toward >= threshold && Directed(i + 1) && Directed(i + 2) && Directed(i + 3)) onset = i;
                if (slowdown is null && baselineSpeed > 10 && path.T[i] - t0 <= .5 && path.Speed[i] <= baselineSpeed * .82) slowdown = (path.T[i] - t0) * 1000;
                if (arrival is null && target.OnTarget) arrival = i;
                onStreak = target.OnTarget ? onStreak + 1 : 0;
                if (settled is null && onStreak >= 13) settled = i - 12;
                var projection = distance > .01 ? -(dx * ey + dy * ep) / distance : 0;
                overshoot = Math.Max(overshoot, projection - target.AngularRadius);
                var along = distance > .01 ? (path.YawVelocity[i] * ey + path.PitchVelocity[i] * ep) / distance : 0;
                var nextSign = along > 8 ? 1 : along < -8 ? -1 : 0;
                if (nextSign != 0 && nextSign != sign)
                {
                    if (sign != 0 && i - lastTurn >= 10) corrections++;
                    sign = nextSign; lastTurn = i;
                }
                if (onset is int o && i > o + 5 && ballisticEnd is null && peak >= 20 && path.Speed[i] < peak * .25)
                { ballisticEnd = i; arrivalError = projection; }
            }
            double? reaction = onset is int move && s.Stimulus != "selection" && t0 > .04 ? (path.T[move] - t0) * 1000 : null;
            // Anticipation and unrelated movement long after a stimulus are not reactions.
            if (reaction is < 80 or > 1000) reaction = null;
            var firstClick = relevant.FirstOrDefault();
            double? ratio = null;
            if (firstClick is not null && peak >= 20)
            {
                var a = Math.Max(s.Start, path.IndexAt(firstClick.TimestampMs / 1000 - .08)); var b = path.IndexAt(firstClick.TimestampMs / 1000);
                ratio = Enumerable.Range(a, Math.Max(1, b - a + 1)).Average(i => path.Speed[i]) / peak;
            }
            // A disappearance alone is not a kill; require a matching hit immediately before it.
            double? kill = null;
            foreach (var h in hits)
                if (h.T >= t0 && h.T <= end + .08 && (h.Id == s.Id || h.Id == 0 && relevant.Any(x => x.Kind == "hit" && Math.Abs(x.TimestampMs / 1000 - h.T) <= .15)) &&
                    input.Despawns.Any(d => d.Id == s.Id && d.T >= h.T - .02 && d.T <= h.T + .08 && d.T < input.Duration - .04)) kill = h.T;
            var epResult = new ResponseEpisode(s.Stimulus == "path_change" ? "path_change" : "target_switch", t0 * 1000, stop * 1000,
                Label(s.Id), distance, samples.Where(x => path.T[x.I] <= stop).Max(x => Math.Abs(Angles.Wrap(path.Yaw[x.I] - x.Target.Yaw))), reaction, slowdown,
                settled is int settle ? (path.T[settle] - t0) * 1000 : null, settled is not null && overshoot <= .1 && corrections <= 1)
            {
                Stimulus = s.Stimulus, FirstOnTargetMs = arrival is int at ? (path.T[at] - t0) * 1000 : null,
                PreClickSpeedRatio = ratio, ArrivalErrorDegrees = arrivalError, OvershootDegrees = overshoot,
                Corrections = hitTime is not null ? corrections : null,
                KillToMovementMs = previousKill is double pk && onset is int om && path.T[om] >= pk ? (path.T[om] - pk) * 1000 : null,
                TimeToKillMs = kill is double kt ? (kt - t0) * 1000 : null
            };
            episodes.Add(epResult);
            previousKill = kill;
            if (!tracking && hitTime is not null && relevant.Length > 0) { var count = relevant.Count(x => x.TimestampMs <= hitTime * 1000); eligible++; if (count > 1) corrective++; }
        }
        var moments = new List<AnalysisWindow>();
        AnalysisWindow Moment(string kind, string label, string detail, double a, double b) =>
            new(kind, label, "", a, b, null, null, null, "", null, null, null, null, null, null) { Detail = detail };
        var measured = episodes.Where(e => e.ReactionMs is not null).ToArray();
        if (measured.Length >= 3)
        {
            var slow = measured.MaxBy(e => e.ReactionMs)!;
            if (slow.ReactionMs >= 250) moments.Add(Moment("slow_response", "Slowest target response", "Watch the target transition and start moving as soon as the next target becomes clear.", slow.StartMs, slow.EndMs));
        }
        int streak = 0, bestStreak = 0, streakStart = 0, bestStart = 0, bestEnd = 0;
        for (int i = 0; i < shots.Count; i++)
        {
            if (shots[i].Kind == "miss") { if (streak == 0) streakStart = i; streak++; if (streak > bestStreak) { bestStreak = streak; bestStart = streakStart; bestEnd = i; } }
            else streak = 0;
        }
        if (bestStreak >= 3) moments.Add(Moment("miss_streak", "Repeated missed clicks", "Let the crosshair reach the target before clicking again.", shots[bestStart].TimestampMs, shots[bestEnd].TimestampMs));
        var clean = episodes.Where(e => e.Stable && e.TimeToKillMs is not null).OrderBy(e => e.TimeToKillMs).FirstOrDefault();
        if (clean is not null && episodes.Count >= 5) moments.Add(Moment("clean_response", "Clean target acquisition", "Use this controlled arrival and click timing as a reference for the next targets.", clean.StartMs, clean.EndMs));
        var summary = episodes.Count == 0 ? null : new ResponseSummary(episodes.Count, episodes.Count(e => e.Kind == "path_change"), episodes.Count(e => e.Kind == "target_switch"),
            100.0 * measured.Length / episodes.Count, Mean(episodes.Select(e => e.ReactionMs)), P90(episodes.Select(e => e.ReactionMs)), Mean(episodes.Select(e => e.PreSlowdownReactionMs)),
            Mean(episodes.Select(e => e.RecoveryMs)), P90(episodes.Select(e => e.RecoveryMs)), Mean(episodes.Where(e => e.Kind == "path_change").Select(e => e.ReactionMs)),
            Mean(episodes.Where(e => e.Kind == "target_switch").Select(e => e.ReactionMs)), Mean(episodes.Select(e => e.TriggerDegrees)), Mean(episodes.Select(e => e.PeakYawDegrees)),
            (double)episodes.Count(e => e.Stable) / episodes.Count)
        { FirstOnTargetMs = Mean(episodes.Select(e => e.FirstOnTargetMs)), PreClickSpeedRatio = Mean(episodes.Select(e => e.PreClickSpeedRatio)), TimeToKillMs = Mean(episodes.Select(e => e.TimeToKillMs)) };
        return new(summary, episodes.Take(1000).ToArray(), shots.ToArray(), moments.ToArray(), tracking ? null : Mean(latencies), tracking ? null : P90(latencies), eligible > 0 ? (double)corrective / eligible : null);
    }
}
