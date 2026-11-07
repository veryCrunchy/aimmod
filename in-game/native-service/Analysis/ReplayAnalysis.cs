namespace AimMod.InGame.Analysis;

public enum ScenarioKind { Clicking, Tracking }

sealed record ReplayAnalysisResult(string Id, string Scenario, double Duration, ScenarioKind Kind, double FireHoldShare, int ViewSamples, FlickReport Flicks, TrackingReport Tracking);

/// <summary>
/// Turns a decoded replay into the analysis input and runs both analyses.
/// Format 2 gives the view at every engine frame (the input stream replayed
/// through the recorded sensitivity constants) and exact target keyframes;
/// format 1 falls back to the 60 Hz frame list. Read-only: never touches
/// scoring, the game or the Hub.
/// </summary>
static class ReplayAnalysis
{
    /// <summary>Scenarios where fire is held for more than this share of the run are treated as tracking.</summary>
    public const double TrackingHoldShare = 0.3;
    /// <summary>Targets that jump further than this between two keyframes less than 50 ms apart respawned in place.</summary>
    const double RespawnJumpCm = 150;

    public static ReplayAnalysisResult Analyze(NativeReplay replay)
    {
        var input = Build(replay);
        var path = ViewPath.Resample(input.View, input.Duration);
        var hold = FireHoldShare(input);
        return new(replay.Id, replay.Scenario, replay.Duration, hold >= TrackingHoldShare ? ScenarioKind.Tracking : ScenarioKind.Clicking, hold, input.View.Count,
            FlickAnalysis.Analyze(input, path), TrackingAnalysis.Analyze(input, path));
    }

    public static double FireHoldShare(AnalysisInput input)
    {
        if (input.Duration <= 0) return 0;
        var presses = input.FirePressed.Order().ToArray(); var releases = input.FireReleased.Order().ToArray();
        double held = 0; int r = 0;
        foreach (var press in presses)
        {
            while (r < releases.Length && releases[r] <= press) r++;
            held += (r < releases.Length ? releases[r] : input.Duration) - press;
        }
        return Math.Clamp(held / input.Duration, 0, 1);
    }

    public static AnalysisInput Build(NativeReplay replay, Action? checkpoint = null)
    {
        // View path at the finest available rate.
        var view = new List<ViewSample>();
        if (replay.Motion is { } motion)
        {
            var yaw = new double[motion.Times.Length]; var pitch = new double[motion.Times.Length];
            for (int i = 0; i < motion.Times.Length; i++) { var (p, y, _) = motion.Rotation(motion.Times[i]); yaw[i] = y; pitch[i] = p; }
            Angles.Unwrap(yaw);
            for (int i = 0; i < yaw.Length; i++) view.Add(new(motion.Times[i], yaw[i], pitch[i]));
        }
        else
        {
            var yaw = replay.Frames.Select(f => f.Camera[4]).ToArray();
            Angles.Unwrap(yaw);
            for (int i = 0; i < yaw.Length; i++) view.Add(new(replay.Frames[i].T, yaw[i], replay.Frames[i].Camera[3]));
        }
        var duration = replay.Duration > 0 ? replay.Duration : view[^1].T;
        var path = ViewPath.Resample(view, duration);

        // Targets at every grid sample, in the player's angular frame, nearest to the view ray first.
        var targets = new TargetSample[path.Count][];
        for (int i = 0; i < path.Count; i++)
        {
            if (i % 250 == 0) checkpoint?.Invoke();
            var t = path.T[i];
            var frame = NativeReplayPlayback.Sample(replay, t);
            var c = frame.Camera;
            var list = new List<TargetSample>(frame.Actors.Length);
            foreach (var actor in frame.Actors)
            {
                double dx = actor[1] - c[0], dy = actor[2] - c[1], dz = actor[3] - c[2];
                var horizontal = Math.Sqrt(dx * dx + dy * dy); var distance = Math.Sqrt(horizontal * horizontal + dz * dz);
                if (distance < 1) continue;
                var yaw = Math.Atan2(dy, dx) * 180 / Math.PI; var pitch = Math.Atan2(dz, horizontal) * 180 / Math.PI;
                var radius = actor[4]; var half = actor[5];
                var angular = Math.Asin(Math.Clamp(radius / distance, 0, 1)) * 180 / Math.PI;
                var on = RayHitsCapsule(c, Angles.Wrap(path.Yaw[i]), path.Pitch[i], actor[1], actor[2], actor[3], radius, half);
                list.Add(new(t, actor[0], yaw, pitch, angular, on));
            }
            var viewYaw = Angles.Wrap(path.Yaw[i]); var viewPitch = path.Pitch[i];
            targets[i] = list.OrderBy(s => Angles.Distance(Angles.Wrap(s.Yaw - viewYaw), s.Pitch - viewPitch) - s.AngularRadius).ToArray();
        }

        // Clicks, holds, hits, spawns.
        var pressed = replay.Inputs.Where(x => x.Action == "FirePressed").Select(x => x.T).ToArray();
        var released = replay.Inputs.Where(x => x.Action == "FireReleased").Select(x => x.T).ToArray();
        var hits = new List<HitEvent>(); var spawns = new List<SpawnEvent>(); var despawns = new List<SpawnEvent>();
        if (replay.Motion is { } m)
        {
            foreach (var (t, target) in m.Hits) hits.Add(new(t, target));
            foreach (var track in m.Tracks)
                for (int k = 0; k < track.T.Length; k++)
                {
                    var jump = k > 0 && track.T[k] - track.T[k - 1] <= 0.05 && Enumerable.Range(0, 3).Sum(a => (track.V[k][a] - track.V[k - 1][a]) * (track.V[k][a] - track.V[k - 1][a])) > RespawnJumpCm * RespawnJumpCm;
                    if (k == 0 || track.Start[k] || jump) spawns.Add(new(track.T[k], track.Id));
                    if (k + 1 == track.T.Length || track.Start[k + 1] || (k + 1 < track.T.Length && track.T[k + 1] - track.T[k] <= 0.05 && Enumerable.Range(0, 3).Sum(a => (track.V[k + 1][a] - track.V[k][a]) * (track.V[k + 1][a] - track.V[k][a])) > RespawnJumpCm * RespawnJumpCm))
                        despawns.Add(new(track.T[k], track.Id));
                }
        }
        else
        {
            foreach (var (t, position) in ReplayCompare.Spawns(replay)) spawns.Add(new(t, 0));
            for (int k = 0; k + 1 < replay.Frames.Count; k++)
            {
                var next = replay.Frames[k + 1].Actors.Select(a => a[0]).ToHashSet();
                foreach (var actor in replay.Frames[k].Actors) if (!next.Contains(actor[0])) despawns.Add(new(replay.Frames[k].T, actor[0]));
            }
        }
        // Without per-target hit records (hook coverage varies by game build) the
        // scenario's hit counter still says when a shot landed; the target stays unknown.
        if (hits.Count == 0)
        {
            double? last = null;
            foreach (var frame in replay.Frames)
            {
                if (frame.Stats?.Hits is not double count) continue;
                if (last is double previous && count > previous) hits.Add(new(frame.T, 0));
                last = count;
            }
        }
        return new(replay.Scenario, duration, view, targets, pressed, released, hits, spawns) { Despawns = despawns };
    }

    /// <summary>True when the ray from the camera along (yaw, pitch) passes within `radius` of the capsule's axis segment.</summary>
    internal static bool RayHitsCapsule(double[] camera, double yaw, double pitch, double x, double y, double z, double radius, double halfHeight)
    {
        double cy = Math.Cos(yaw * Math.PI / 180), sy = Math.Sin(yaw * Math.PI / 180), cp = Math.Cos(pitch * Math.PI / 180), sp = Math.Sin(pitch * Math.PI / 180);
        double dx = cp * cy, dy = cp * sy, dz = sp; // unit ray direction
        var axis = Math.Max(0, halfHeight - radius);
        // Closest points between ray (o + s d, s >= 0) and the vertical segment (a + u (b - a), u in [0, 1]).
        double ax = x, ay = y, az = z - axis, bz = z + axis;
        double ex = 0, ey = 0, ez = bz - az; // segment direction (vertical)
        double rx = camera[0] - ax, ry = camera[1] - ay, rz = camera[2] - az;
        double a = 1, b = dx * ex + dy * ey + dz * ez, c = ez * ez, d = dx * rx + dy * ry + dz * rz, e = ex * rx + ey * ry + ez * rz;
        double s, u;
        var det = a * c - b * b;
        if (c <= 1e-9) { u = 0; s = Math.Max(0, -d); }
        else
        {
            if (det > 1e-9) { s = Math.Clamp((b * e - c * d) / det, 0, double.MaxValue); u = (b * s + e) / c; } else { s = 0; u = e / c; }
            if (u < 0) { u = 0; s = Math.Max(0, -d); } else if (u > 1) { u = 1; s = Math.Max(0, -(d - b)); }
        }
        double px = camera[0] + dx * s - (ax + ex * u), py = camera[1] + dy * s - (ay + ey * u), pz = camera[2] + dz * s - (az + ez * u);
        return px * px + py * py + pz * pz <= radius * radius;
    }
}
