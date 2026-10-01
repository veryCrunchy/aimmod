using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// Tracking duel (in-game/docs/game-modes.md 6.3): alternating rounds, one
// player tracks (attacker) while the other dodges. The score is time on
// target: the share of the round in which the attacker's camera ray goes
// through the dodger's avatar hull.
//
// Data, all on the host clock (unix ms):
//  - every player streams its own camera samples (AimModCore self-pose.tsv,
//    60 Hz): the dodger's give its true position, the attacker's its view;
//  - the attacker also streams "seen" rows: where its game drew the avatars
//    (self-pose.tsv target rows), which is what it actually aimed at.
// The host scores the attacker's rays against the hull it saw, favouring the
// shooter, but only where that hull matches the dodger's own track at most
// 200 ms in the past (the rewind cap). Elsewhere it uses the dodger's own
// track rewound by the measured lag, capped at 200 ms.

// One camera sample: host-clock time, eye position (cm), pitch and yaw (degrees).
sealed record TrackSample(long T, double X, double Y, double Z, double Pitch, double Yaw);
// One drawn avatar in the attacker's game: host-clock time, AimModCore target id,
// capsule centre, radius and half height.
sealed record TrackSeen(long T, int Id, double X, double Y, double Z, double Radius, double HalfHeight);
sealed record TrackBatch(string MatchId, int Round, IReadOnlyList<TrackSample> Samples, IReadOnlyList<TrackSeen> Seen)
{
    public const int MaxSamples = 64, MaxSeen = 64;

    public object Body() => new
    {
        match = MatchId, round = Round,
        s = Samples.Select(x => new double[] { x.T, R(x.X), R(x.Y), R(x.Z), R(x.Pitch), R(x.Yaw) }),
        v = Seen.Select(x => new double[] { x.T, x.Id, R(x.X), R(x.Y), R(x.Z), R(x.Radius), R(x.HalfHeight) }),
    };
    static double R(double v) => Math.Round(v, 2);

    public static TrackBatch? Read(JsonElement b)
    {
        try
        {
            var match = b.GetProperty("match").GetString();
            var round = b.GetProperty("round").GetInt32();
            if (match is not { Length: > 0 and <= 40 } || round is < 1 or > 100) return null;
            var samples = new List<TrackSample>(); var seen = new List<TrackSeen>();
            static bool Num(JsonElement e, out double v) { v = 0; return e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out v) && double.IsFinite(v) && Math.Abs(v) < 1e13; }
            static double[]? Row(JsonElement e, int n)
            {
                if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != n) return null;
                var r = new double[n]; var i = 0;
                foreach (var x in e.EnumerateArray()) { if (!Num(x, out r[i])) return null; i++; }
                return r;
            }
            foreach (var e in b.GetProperty("s").EnumerateArray())
            {
                if (samples.Count >= MaxSamples || Row(e, 6) is not { } r || Math.Abs(r[4]) > 90.5 || Math.Abs(r[5]) > 720 || Math.Abs(r[1]) > 1e7 || Math.Abs(r[2]) > 1e7 || Math.Abs(r[3]) > 1e7) return null;
                samples.Add(new TrackSample((long)r[0], r[1], r[2], r[3], r[4], r[5]));
            }
            if (b.TryGetProperty("v", out var v))
                foreach (var e in v.EnumerateArray())
                {
                    if (seen.Count >= MaxSeen || Row(e, 7) is not { } r || r[1] < 1 || r[1] > int.MaxValue || r[1] != Math.Truncate(r[1]) || r[5] is <= 0 or > 1000 || r[6] < r[5] || r[6] > 2000) return null;
                    seen.Add(new TrackSeen((long)r[0], (int)r[1], r[2], r[3], r[4], r[5], r[6]));
                }
            return new TrackBatch(match, round, samples, seen);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }
}

// The host's score for one round.
sealed record TrackResult(double OnTargetSeconds, double Percent, int Samples, int OnSamples, double Coverage,
    int SeenRows, int SeenRejected, double LagMs, double HostOnlyPercent, bool Disputed, string? Reason);

static class TrackGeometry
{
    // Ray (origin, unit direction, length) against a vertical capsule (centre, radius, half height).
    public static bool HitsCapsule(double ox, double oy, double oz, double dx, double dy, double dz, double length,
        double cx, double cy, double cz, double radius, double halfHeight)
    {
        var half = Math.Max(0, halfHeight - radius);
        // Closest points between the ray segment P(s) = O + s*D (0..length) and the axis Q(t) = C + t*Z (-half..half).
        double wx = ox - cx, wy = oy - cy, wz = oz - cz;
        var b = dz;                       // D . Z
        var d = dx * wx + dy * wy + dz * wz; // D . W
        var e = wz;                       // Z . W
        var denom = 1 - b * b;            // |D|=|Z|=1
        double s, t;
        if (denom < 1e-9) { s = 0; t = e; }
        else { s = (b * e - d) / denom; t = (e - b * d) / denom; }
        s = Math.Clamp(s, 0, length);
        t = Math.Clamp(t, -half, half);
        // Re-project after clamping: best s for the clamped t, then best t for that s.
        s = Math.Clamp(-(dx * wx + dy * wy + dz * (wz - t)), 0, length);
        t = Math.Clamp(wz + s * dz, -half, half);
        double px = wx + s * dx, py = wy + s * dy, pz = wz + s * dz - t;
        return px * px + py * py + pz * pz <= radius * radius;
    }

    public static (double X, double Y, double Z) Direction(double pitch, double yaw)
    {
        var p = pitch * Math.PI / 180; var y = yaw * Math.PI / 180;
        return (Math.Cos(p) * Math.Cos(y), Math.Cos(p) * Math.Sin(y), Math.Sin(p));
    }
}

// One round of a tracking duel on the host.
sealed class TrackingRound(string attacker, string dodger, long start, long end)
{
    public const long RewindCapMs = 200, MatchWindowMs = 400, GapMs = 50;
    public const double MatchToleranceCm = 25, RayLengthCm = 100_000;
    // Defaults when the attacker's game reported no drawn hull: the avatar body
    // profile's bounding box (AvatarProfiles: 230 cm tall, 45 cm radius) and a
    // camera 64 cm above the capsule centre (UE's default eye height).
    public const double DefaultRadius = 45, DefaultHalfHeight = 115, DefaultEyeAboveCentre = 64;
    public const int MaxSamplesPerPlayer = 8000;

    public string Attacker { get; } = attacker;
    public string Dodger { get; } = dodger;
    public long Start { get; } = start;
    public long End { get; } = end;
    readonly List<TrackSample> attack = [], dodge = [];
    readonly List<TrackSeen> seen = [];

    public void Add(string from, TrackBatch batch)
    {
        var list = from == Attacker ? attack : from == Dodger ? dodge : null;
        if (list is null) return;
        foreach (var s in batch.Samples)
        {
            if (s.T < Start - 1000 || s.T > End + 1000 || list.Count >= MaxSamplesPerPlayer) continue;
            if (list.Count > 0 && s.T <= list[^1].T) continue; // late or repeated samples are ignored
            list.Add(s);
        }
        if (from != Attacker) return;
        foreach (var v in batch.Seen)
        {
            if (v.T < Start - 1000 || v.T > End + 1000 || seen.Count >= MaxSamplesPerPlayer) continue;
            if (seen.Count > 0 && v.T < seen[^1].T) continue;
            var repeated = false;
            for (var k = seen.Count - 1; k >= 0 && seen[k].T == v.T; k--) if (seen[k].Id == v.Id) { repeated = true; break; }
            if (repeated) continue;
            seen.Add(v);
        }
    }

    static TrackSample? At(List<TrackSample> track, double t)
    {
        if (track.Count == 0 || t < track[0].T - GapMs || t > track[^1].T + GapMs) return null;
        int lo = 0, hi = track.Count - 1, i = -1; // last index with T <= t (tracks are time-ordered)
        while (lo <= hi) { var mid = (lo + hi) / 2; if (track[mid].T <= t) { i = mid; lo = mid + 1; } else hi = mid - 1; }
        if (i < 0) return track[0];
        if (i == track.Count - 1) return track[i];
        var a = track[i]; var b = track[i + 1];
        if (b.T - a.T > 250) return t - a.T <= GapMs ? a : null; // a hole in the stream
        var u = (t - a.T) / (b.T - a.T);
        return new TrackSample((long)t, a.X + (b.X - a.X) * u, a.Y + (b.Y - a.Y) * u, a.Z + (b.Z - a.Z) * u, a.Pitch, a.Yaw);
    }

    // Lag between the attacker's drawn hull and the dodger's own track: the
    // time in the past (0..MatchWindow) where the dodger was closest.
    (double Distance, long Lag, double EyeDz)? Match(TrackSeen v)
    {
        (double, long, double)? best = null;
        for (long lag = 0; lag <= MatchWindowMs; lag += 5)
        {
            if (At(dodge, v.T - lag) is not { } d) continue;
            var dist = Math.Sqrt((d.X - v.X) * (d.X - v.X) + (d.Y - v.Y) * (d.Y - v.Y));
            if (best is null || dist < best.Value.Item1) best = (dist, lag, d.Z - v.Z);
        }
        return best;
    }

    static double Median(List<double> values) { if (values.Count == 0) return double.NaN; values.Sort(); return values[values.Count / 2]; }

    public TrackResult Compute(long now, int? attackerRtt, int? dodgerRtt)
    {
        var until = Math.Min(now, End);
        var duration = Math.Max(1, End - Start);
        // Several targets may be drawn (a hidden helper bot, other bots): the
        // dodger is the one whose rows match the dodger's own track best.
        var byId = seen.Where(v => v.T <= until + RewindCapMs).GroupBy(v => v.Id)
            .Select(g => (Id: g.Key, Rows: g.ToList(), Matches: g.Select(v => (Row: v, M: Match(v))).ToList()))
            .OrderByDescending(g => g.Matches.Count(m => m.M is { } x && x.Distance <= MatchToleranceCm)).ToList();
        var rows = byId.Count > 0 ? byId[0].Matches : [];
        var matched = rows.Where(r => r.M is { } x && x.Distance <= MatchToleranceCm).ToList();
        var lags = matched.Select(r => (double)r.M!.Value.Lag).ToList();
        var measuredLag = Median(lags);
        var estimated = double.IsNaN(measuredLag) ? 100 + ((attackerRtt ?? 0) + (dodgerRtt ?? 0)) / 2.0 : measuredLag;
        var lag = Math.Clamp(estimated, 0, RewindCapMs);
        var eyeAbove = matched.Count > 0 ? Median(matched.Select(r => r.M!.Value.EyeDz).ToList()) : DefaultEyeAboveCentre;
        var radius = rows.Count > 0 ? Median(rows.Select(r => r.Row.Radius).ToList()) : DefaultRadius;
        var halfHeight = rows.Count > 0 ? Median(rows.Select(r => r.Row.HalfHeight).ToList()) : DefaultHalfHeight;
        // Valid drawn hulls: matched within tolerance and at most the rewind cap old.
        var valid = rows.Where(r => r.M is { } x && x.Distance <= MatchToleranceCm && x.Lag <= RewindCapMs).Select(r => r.Row).ToList();
        var rejected = rows.Count - valid.Count;

        double on = 0, hostOnly = 0, covered = 0; int samples = 0, onSamples = 0;
        for (var i = 0; i < attack.Count; i++)
        {
            var a = attack[i];
            if (a.T < Start || a.T >= until) continue;
            var next = i + 1 < attack.Count ? attack[i + 1].T : a.T + 17;
            var dt = Math.Min(Math.Min(next, until) - a.T, GapMs);
            if (dt <= 0) continue;
            samples++; covered += dt;
            var (dx, dy, dz) = TrackGeometry.Direction(a.Pitch, a.Yaw);
            // Host view: the dodger's own track, rewound by the (capped) lag.
            bool hostHit = false;
            if (At(dodge, a.T - lag) is { } d)
                hostHit = TrackGeometry.HitsCapsule(a.X, a.Y, a.Z, dx, dy, dz, RayLengthCm, d.X, d.Y, d.Z - eyeAbove, radius, halfHeight);
            // Shooter's view, where it is valid: the hull its game drew near this sample.
            var drawn = Drawn(valid, a.T);
            var hit = drawn is { } h ? TrackGeometry.HitsCapsule(a.X, a.Y, a.Z, dx, dy, dz, RayLengthCm, h.X, h.Y, h.Z, h.Radius, h.HalfHeight) : hostHit;
            if (hostHit) hostOnly += dt;
            if (hit) { on += dt; onSamples++; }
        }
        var coverage = Math.Min(1, covered / Math.Max(1, until - Start));
        var finished = now >= End;
        string? reason = null;
        if (finished && coverage < 0.8) reason = "coverage";
        else if (rows.Count >= 10 && rejected > rows.Count * 0.1) reason = "seen-mismatch";
        else if (finished && dodge.Count == 0) reason = "no-dodger-track";
        return new TrackResult(Math.Round(on / 1000.0, 3), Math.Round(on * 100.0 / duration, 1), samples, onSamples, Math.Round(coverage, 3),
            rows.Count, rejected, Math.Round(lag, 1), Math.Round(hostOnly * 100.0 / duration, 1), reason is not null, reason);
    }

    // The drawn hull nearest in time (within 100 ms), linearly between two valid rows when they bracket t.
    static TrackSeen? Drawn(List<TrackSeen> valid, long t)
    {
        if (valid.Count == 0) return null;
        var i = valid.FindLastIndex(v => v.T <= t);
        TrackSeen? a = i >= 0 ? valid[i] : null, b = i + 1 < valid.Count ? valid[i + 1] : null;
        if (a is not null && b is not null && b.T - a.T <= 100 && b.Id == a.Id)
        {
            var u = (double)(t - a.T) / Math.Max(1, b.T - a.T);
            return a with { T = t, X = a.X + (b.X - a.X) * u, Y = a.Y + (b.Y - a.Y) * u, Z = a.Z + (b.Z - a.Z) * u };
        }
        var near = new[] { a, b }.Where(x => x is not null && Math.Abs(x.T - t) <= 100).OrderBy(x => Math.Abs(x!.T - t)).FirstOrDefault();
        return near;
    }
}

// Client side: reads this machine's camera and drawn avatars from AimModCore's
// self-pose.tsv (pose format 1) and turns them into track batches on the host
// clock. AimModCore writes the file only while self-pose.request is fresh.
sealed class SelfPoseTracker(string outputFolder)
{
    readonly string posePath = Path.Combine(outputFolder, "self-pose.tsv");
    readonly string requestPath = Path.Combine(outputFolder, "self-pose.request");
    long lastPose = long.MinValue, lastSequence = -1, requestedAt;
    readonly List<TrackSample> samples = [];
    readonly List<TrackSeen> seenRows = [];
    readonly Dictionary<int, TrackSeen> lastSeen = new();
    // The latest drawn targets by AimModCore id (combat claims name the target they hit).
    public IReadOnlyDictionary<int, TrackSeen> LastSeen => lastSeen;

    public void Reset() { lastPose = long.MinValue; lastSequence = -1; samples.Clear(); seenRows.Clear(); lastSeen.Clear(); }

    // Keep AimModCore publishing (it stops 5 s after the last request).
    public void Request(long nowMs)
    {
        if (nowMs - requestedAt < 2000) return;
        requestedAt = nowMs;
        try { File.WriteAllText(requestPath, nowMs.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // offsetMs: host clock minus local clock.
    public void Poll(long offsetMs) => Take(LivePoseFrame.Read(posePath, TimeSpan.FromSeconds(1.5)), offsetMs);

    public void Take(LivePoseFrame? frame, long offsetMs)
    {
        if (frame is null || frame.Sequence == lastSequence) return;
        lastSequence = frame.Sequence;
        foreach (var p in frame.Poses)
        {
            if (p.UnixMs <= lastPose) continue;
            lastPose = p.UnixMs;
            samples.Add(new TrackSample(p.UnixMs + offsetMs, p.Camera[0], p.Camera[1], p.Camera[2], p.Camera[3], p.Camera[4]));
        }
        // Target rows are the latest drawn positions, so they belong to the newest pose.
        var at = frame.Poses[^1].UnixMs + offsetMs;
        lastSeen.Clear();
        foreach (var t in frame.Targets) { var row = new TrackSeen(at, (int)t[0], t[1], t[2], t[3], t[4], t[5]); seenRows.Add(row); lastSeen[row.Id] = row; }
        if (samples.Count > 4 * TrackBatch.MaxSamples) samples.RemoveRange(0, samples.Count - 4 * TrackBatch.MaxSamples);
        if (seenRows.Count > 4 * TrackBatch.MaxSeen) seenRows.RemoveRange(0, seenRows.Count - 4 * TrackBatch.MaxSeen);
    }

    // Everything gathered since the last call, split into batches the protocol accepts.
    public IEnumerable<TrackBatch> Drain(string matchId, int round)
    {
        while (samples.Count > 0 || seenRows.Count > 0)
        {
            var s = samples.Take(TrackBatch.MaxSamples).ToArray(); samples.RemoveRange(0, s.Length);
            var v = seenRows.Take(TrackBatch.MaxSeen).ToArray(); seenRows.RemoveRange(0, v.Length);
            yield return new TrackBatch(matchId, round, s, v);
        }
    }
}
