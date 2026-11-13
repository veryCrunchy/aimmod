namespace AimMod.InGame.Analysis;

/// <summary>Synthetic-data checks for the practice analysis core (run by --self-test).</summary>
static class AnalysisChecks
{
    static int passed;
    static void Check(bool ok, string name) { if (!ok) throw new Exception("Analysis check failed: " + name); passed++; }
    static bool Near(double a, double b, double tolerance) => double.IsFinite(a) && Math.Abs(a - b) <= tolerance;

    /// <summary>A static target in front of the player at (yaw, pitch) with the given angular radius, visible for the whole grid.</summary>
    static TargetSample[][] StaticTarget(ViewPath path, double id, double yaw, double pitch, double radius) =>
        path.T.Select((t, i) => new[] { new TargetSample(t, id, yaw, pitch, radius, Angles.Distance(Angles.Wrap(path.Yaw[i] - yaw), path.Pitch[i] - pitch) <= radius) }).ToArray();

    /// <summary>View path that rests, moves with the given submovements (each a minimum-jerk displacement over a duration) and rests again.</summary>
    static List<ViewSample> Compose(double start, params (double DYaw, double DPitch, double Duration, double PauseAfter)[] moves)
    {
        var samples = new List<ViewSample>(); double t = 0, yaw = 0, pitch = 0;
        for (; t < start; t += 1 / 400.0) samples.Add(new(t, yaw, pitch));
        foreach (var (dYaw, dPitch, duration, pause) in moves)
        {
            var n = (int)Math.Round(duration * 400);
            for (int i = 1; i <= n; i++) { var p = MinimumJerk.Position(i / (double)n); samples.Add(new(t + i / 400.0, yaw + dYaw * p, pitch + dPitch * p)); }
            t += n / 400.0; yaw += dYaw; pitch += dPitch;
            var rest = (int)Math.Round(pause * 400);
            for (int i = 1; i <= rest; i++) samples.Add(new(t + i / 400.0, yaw, pitch));
            t += rest / 400.0;
        }
        for (int i = 1; i <= 200; i++) samples.Add(new(t + i / 400.0, yaw, pitch));
        return samples;
    }

    public static void Run()
    {
        passed = 0;
        MinimumJerkChecks(); SmoothnessChecks(); FlickChecks(); TrackingChecks(); GeometryChecks();
        Console.WriteLine($"{passed} practice analysis checks passed.");
    }

    static void MinimumJerkChecks()
    {
        Check(MinimumJerk.Position(0) == 0 && MinimumJerk.Position(1) == 1 && Near(MinimumJerk.Position(0.5), 0.5, 1e-9), "min-jerk position symmetric");
        Check(Near(MinimumJerk.Speed(0.5, 30, 0.2), 1.875 * 30 / 0.2, 1e-9), "min-jerk peak speed 1.875 D/T");
        Check(Near(MinimumJerk.Truncation(0.5), 0.2294, 0.001) && MinimumJerk.Truncation(0.08) is > 0.05 and < 0.12 && MinimumJerk.Truncation(0) == 0, "truncation point of the bell");
        var cut = Enumerable.Range(8, 85).Select(i => MinimumJerk.Speed(i / 100.0, 30, 0.2)).ToArray();
        var fitted = MinimumJerk.Compare(cut, 30 * (MinimumJerk.Position(0.92) - MinimumJerk.Position(0.08)), 0.84 * 0.2, MinimumJerk.Speed(0.08, 30, 0.2));
        Check(Near(fitted.PeakRatio, 1, 0.02) && fitted.ShapeError < 0.02 && Near(fitted.TimeToPeak, 0.5, 0.02), "a bell cut at the rest threshold is still recognised as one");
        var path = MinimumJerk.Path(0, 0, 0, 30, 0, 0.2);
        Check(path.Length == 51 && Near(path[^1].Yaw, 30, 1e-9) && Near(path[25].Yaw, 15, 1e-9), "min-jerk path samples");
        var speed = Enumerable.Range(0, 101).Select(i => MinimumJerk.Speed(i / 100.0, 30, 0.2)).ToArray();
        var (shape, peak, toPeak) = MinimumJerk.Compare(speed, 30, 0.2);
        Check(Near(shape, 0, 1e-9) && Near(peak, 1, 1e-9) && Near(toPeak, 0.5, 1e-9), "min-jerk self comparison is exact");
        var early = Enumerable.Range(0, 101).Select(i => MinimumJerk.Speed(Math.Min(1, i / 60.0), 30, 0.12)).ToArray();
        Check(MinimumJerk.Compare(early, 30, 0.2).ShapeError > 0.2 && MinimumJerk.Compare(early, 30, 0.2).TimeToPeak < 0.4, "front-loaded profile departs from min-jerk");
    }

    static void SmoothnessChecks()
    {
        const double rate = 250;
        var bell = Enumerable.Range(0, 250).Select(i => MinimumJerk.Speed(i / 249.0, 30, 1)).ToArray();
        var bumpy = bell.Select((v, i) => v + 8 * Math.Sin(2 * Math.PI * 8 * i / rate)).ToArray();
        var sparcBell = Smoothness.Sparc(bell, rate); var sparcBumpy = Smoothness.Sparc(bumpy, rate);
        Check(sparcBell is < -1 and > -2, "SPARC of one bell is about -1.5");
        Check(sparcBumpy < sparcBell - 0.3, "8 Hz tremor lowers SPARC");
        Check(Smoothness.LogDimensionlessJerk(bumpy, rate) < Smoothness.LogDimensionlessJerk(bell, rate), "tremor lowers LDLJ");
        Check(Near(Smoothness.Sparc(bell.Select(v => v * 3).ToArray(), rate), sparcBell, 1e-9), "SPARC is amplitude invariant");
        var slow = Enumerable.Range(0, 500).Select(i => MinimumJerk.Speed(i / 499.0, 30, 2)).ToArray();
        Check(Near(Smoothness.Sparc(slow, rate), sparcBell, 0.15), "SPARC is roughly duration invariant");
        var hf = Smoothness.HighFrequencyShare(bumpy, rate); var lf = Smoothness.HighFrequencyShare(bell, rate);
        Check(hf > 0.05 && lf < 0.01 && hf > 5 * lf, "high-frequency share separates tremor from a clean move");
        Check(double.IsNaN(Smoothness.Sparc(new double[20], rate)), "SPARC of no movement is undefined");
    }

    static AnalysisInput Input(List<ViewSample> view, double targetYaw, double targetPitch, double radius, double[] clicks, double[]? spawns = null, bool hit = true)
    {
        var path = ViewPath.Resample(view);
        var targets = StaticTarget(path, 7, targetYaw, targetPitch, radius);
        return new("Synthetic", view[^1].T, view, targets, clicks, clicks.Select(c => c + 0.01).ToArray(),
            hit ? clicks.Select(c => new HitEvent(c + 0.002, 7)).ToArray() : [], (spawns ?? []).Select(s => new SpawnEvent(s, 7)).ToArray());
    }

    static void FlickChecks()
    {
        // One clean 30 degree flick right: target spawns at 0.2 s, movement 0.45-0.65 s, click at 0.75 s.
        var clean = Input(Compose(0.45, (30, 0, 0.2, 0.3)), 30, 0, 1.5, [0.75], [0.2]);
        var report = FlickAnalysis.Analyze(clean);
        Check(report.Count == 1 && report.Skipped == 0, "one flick segmented");
        var f = report.Flicks[0];
        Check(Near(f.OnsetT, 0.45, 0.02), "onset found at movement start");
        Check(f.ReactionMs is double r && Near(r, 250, 25), "reaction = spawn to onset");
        Check(Near(f.BallisticMs, 200, 40), "ballistic phase covers the primary submovement");
        Check(f.Corrections == 0 && Near(f.CorrectionMs, 0, 1), "clean flick has no corrective submovement");
        Check(Near(f.VerificationMs, 100, 40), "verification = rest before the click");
        Check(Near(f.Distance, 30, 0.5) && f.Efficiency > 0.98, "straight path is fully efficient");
        Check(Near(f.PrimaryEndRatio, 1, 0.05) && f.OvershootRatio < 0.02, "primary lands on target");
        Check(f.Sector == 0 && f.Hit && f.ClickErrorDeg < 0.5, "sector right, hit, on target at click");
        Check(Near(f.PeakRatio, 1, 0.12) && Near(f.TimeToPeak, 0.5, 0.1) && f.ShapeError < 0.15, "primary matches minimum jerk");
        Check(report.Throughput > 0 && Near(f.IndexOfDifficulty, Math.Log2(30 / 3.0 + 1), 0.1), "Fitts index of difficulty");

        // Overshoot: 36 degrees out, 6 back, then click.
        var over = Input(Compose(0.4, (36, 0, 0.2, 0.05), (-6, 0, 0.1, 0.1)), 30, 0, 1.5, [0.9]);
        var fo = FlickAnalysis.Analyze(over).Flicks.Single();
        Check(fo.Corrections == 1, "overshoot produces one corrective submovement");
        Check(Near(fo.PrimaryEndRatio, 1.2, 0.08) && Near(fo.OvershootRatio, 0.2, 0.05), "overshoot measured as 20% past the target");
        Check(Near(fo.Efficiency, 30.0 / 42, 0.03), "path efficiency counts the way back");
        Check(fo.CorrectionMs > 60 && fo.ReactionMs is null, "correction phase timed; no spawn means no reaction");

        // Undershoot: 24 out, 6 more, then click; movement up-left.
        var under = Input(Compose(0.4, (-17, 17, 0.2, 0.05), (-4.2, 4.2, 0.1, 0.1)), -21.2, 21.2, 1.5, [0.9]);
        var fu = FlickAnalysis.Analyze(under).Flicks.Single();
        Check(fu.PrimaryEndRatio is > 0.75 and < 0.85 && fu.OvershootRatio == 0 && fu.Corrections == 1, "undershoot ends at 80% then corrects");
        Check(fu.Sector == 3, "up-left sector");
        var summary = FlickAnalysis.Analyze(under);
        Check(summary.UndershootShare == 1 && summary.OvershootShare == 0 && summary.CostliestPhase == "ballistic", "summary shares and costliest phase");

        // Two targets in a row: clicks bound the flicks; the micro-adjustment second click on the same target is skipped.
        var view = Compose(0.4, (20, 0, 0.15, 0.1), (0.3, 0, 0.05, 0.1), (-40, 0, 0.25, 0.1));
        var path = ViewPath.Resample(view);
        var targets = path.T.Select((t, i) => new[] { new TargetSample(t, 1, 20, 0, 1.5, false), new TargetSample(t, 2, -20, 0, 1.5, false) }
            .OrderBy(s => Math.Abs(Angles.Wrap(path.Yaw[i] - s.Yaw))).ToArray()).ToArray();
        var two = new AnalysisInput("Synthetic", view[^1].T, view, targets, [0.62, 0.78, 1.15], [0.63, 0.79, 1.16], [new(0.621, 1), new(1.151, 2)], [new(0, 1), new(0.75, 2)]);
        var pair = FlickAnalysis.Analyze(two);
        Check(pair.Count == 2 && pair.Skipped == 1, "micro click skipped, two flicks kept");
        Check(pair.Flicks[0].Id == 1 && pair.Flicks[1].Id == 2 && pair.Flicks[1].Sector == 4, "targets resolved from hits; second flick goes left");
        Check(Near(pair.Flicks[1].Distance, 40, 1) && pair.Flicks[1].ReactionMs is double r2 && Near(r2, 50, 25), "second flick measured from the first target with its spawn reaction");
        Check(FlickAnalysis.Analyze(two with { Spawns = [new(0, 1), new(0.63, 2)] }).Flicks[1].ReactionMs is null, "a target that spawned before the previous click was not reacted to");
        Check(pair.Sectors.Count == 2 && pair.Amplitudes.Count == 2, "sector and amplitude breakdowns");
        Check(Near(FlickAnalysis.Analyze(two with { Hits = [] }).HitRate, 0, 1e-9), "no hits means no hit rate");
        // Hit counter without target identity, plus the target vanishing after the click: identity comes from the despawn.
        var vanishing = path.T.Select((t, i) => (t < 0.625 ? new[] { new TargetSample(t, 1, 20, 0, 1.5, false), new TargetSample(t, 2, -20, 0, 1.5, false) } : new[] { new TargetSample(t, 2, -20, 0, 1.5, false) })
            .OrderBy(s => Math.Abs(Angles.Wrap(path.Yaw[i] - s.Yaw))).ToArray()).ToArray();
        var counter = two with { Targets = vanishing, Hits = [new(0.63, 0), new(1.16, 0)], Despawns = [new(0.624, 1)] };
        var resolved = FlickAnalysis.Analyze(counter);
        Check(resolved.Count == 2 && resolved.Flicks[0].Id == 1 && resolved.Flicks[0].Hit && resolved.Flicks[0].ClickErrorDeg < 1, "vanished target resolved from the despawn and the hit counter");
        // Continuous flow: no rest between the previous click and the next movement means no reaction time.
        var flow = Compose(0.4, (20, 0, 0.15, 0), (-40, 0, 0.25, 0.1));
        var flowPath = ViewPath.Resample(flow);
        var flowTargets = flowPath.T.Select((t, i) => new[] { new TargetSample(t, 1, 20, 0, 1.5, false), new TargetSample(t, 2, -20, 0, 1.5, false) }.OrderBy(s => Math.Abs(Angles.Wrap(flowPath.Yaw[i] - s.Yaw))).ToArray()).ToArray();
        var flowing = FlickAnalysis.Analyze(new AnalysisInput("Synthetic", flow[^1].T, flow, flowTargets, [0.55, 0.9], [0.56, 0.91], [new(0.551, 1), new(0.901, 2)], [new(0, 1), new(0.5, 2)]));
        Check(flowing.Count == 2 && flowing.Flicks[1].ReactionMs is null && flowing.Flicks[1].GapMs < 60, "no reaction without a rest before the movement");
        Check(FlickAnalysis.Analyze(two with { FirePressed = [] }).Count == 0, "no clicks means no flicks");
    }

    static void TrackingChecks()
    {
        // Target sweeping sinusoidally in yaw; view follows with an 80 ms delay and a small constant lead in pitch.
        const double rate = 400, amplitude = 20, hz = 0.5, delay = 0.08;
        var n = (int)(20 * rate);
        double Target(double t) => amplitude * Math.Sin(2 * Math.PI * hz * t);
        var view = Enumerable.Range(0, n).Select(i => { var t = i / rate; return new ViewSample(t, Target(t - delay), 0.5); }).ToList();
        var path = ViewPath.Resample(view);
        var targets = path.T.Select((t, i) => new[] { new TargetSample(t, 3, Target(t), 0, 1.0, Math.Abs(Angles.Wrap(path.Yaw[i] - Target(t))) <= 1 && Math.Abs(path.Pitch[i]) <= 1) }).ToArray();
        var input = new AnalysisInput("Synthetic", 20, view, targets, [0], [20], [], [new(0, 3)]);
        var report = TrackingAnalysis.Analyze(input, path);
        Check(Near(report.LagMs, 80, 10) && Near(report.LagMsByError, 80, 10), "lag recovered by correlation and by error");
        Check(report.VelocityCorrelation > 0.95, "velocity correlation high when following");
        Check(Near(report.PitchBiasDeg, 0.5, 0.01) && Math.Abs(report.YawBiasDeg) < 0.2, "pitch bias and no yaw bias");
        Check(report.AlongMotionErrorDeg < -1 && report.BehindShare > 0.9, "a delayed follower is behind the target");
        Check(report.RmsYawErrorDeg > 2 && report.RmsYawErrorDeg < 6, "yaw error scales with delay x speed");
        Check(report.FiringShare > 0.99 && report.OnTargetShare is > 0.1 and < 0.9, "firing all run; on target part of the time");
        Check(report.Reversals is >= 18 and <= 20 && Near(report.MeanReversalReactionMs, 80, 24), "reversals counted with the follower's delay");
        Check(report.MeanSparc is < -1 and > -4 && report.MeanHighFrequencyShare < 0.05, "smooth pursuit has a clean spectrum");
        Check(report.TargetSwitches == 0 && Near(report.TrackedShare, 1, 0.01), "single target tracked throughout");
        var exactTargets = path.T.Select(t => new[] { new TargetSample(t, 3, Target(t), 0, 1.0, true) }).ToArray();
        var exact = new AnalysisInput("Synthetic", 20, path.T.Select(t => new ViewSample(t, Target(t), 0)).ToList(), exactTargets, [0], [20], [], []);
        var perfect = TrackingAnalysis.Analyze(exact);
        Check(Near(perfect.LagMs, 0, 4) && perfect.RmsYawErrorDeg < 0.05 && Near(perfect.OnTargetShare, 1, 0.01), "exact pursuit has no lag or error");
        var jittery = new AnalysisInput("Synthetic", 20, path.T.Select(t => new ViewSample(t, Target(t) + 0.6 * Math.Sin(2 * Math.PI * 9 * t), 0)).ToList(), exactTargets, [0], [20], [], []);
        var rough = TrackingAnalysis.Analyze(jittery);
        Check(rough.MeanSparc < perfect.MeanSparc && rough.MeanHighFrequencyShare > perfect.MeanHighFrequencyShare + 0.1, "9 Hz jitter is less smooth");
        Check(ReplayAnalysis.FireHoldShare(input) == 1 && ReplayAnalysis.FireHoldShare(input with { FirePressed = [1, 5], FireReleased = [1.5, 5.5] }) == 0.05, "fire hold share");
    }

    static void GeometryChecks()
    {
        double[] camera = [0, 0, 100];
        Check(ReplayAnalysis.RayHitsCapsule(camera, 0, 0, 500, 0, 100, 20, 60), "ray through the capsule centre hits");
        Check(ReplayAnalysis.RayHitsCapsule(camera, 0, 0, 500, 15, 100, 20, 60), "ray within the radius hits");
        Check(!ReplayAnalysis.RayHitsCapsule(camera, 0, 0, 500, 25, 100, 20, 60), "ray outside the radius misses");
        Check(ReplayAnalysis.RayHitsCapsule(camera, 0, 0, 500, 0, 150, 20, 60), "the capsule's upper half is hit");
        Check(!ReplayAnalysis.RayHitsCapsule(camera, 0, 0, 500, 0, 175, 20, 60), "above the capsule misses");
        Check(!ReplayAnalysis.RayHitsCapsule(camera, 180, 0, 500, 0, 100, 20, 60), "a target behind the player is not hit");
        Check(ReplayAnalysis.RayHitsCapsule(camera, 90, 0, 0, 300, 100, 20, 60), "yaw 90 looks along +y");
        Check(Angles.Sector(1, 0) == 0 && Angles.Sector(0, 1) == 2 && Angles.Sector(-1, 0) == 4 && Angles.Sector(0, -1) == 6 && Angles.Sector(1, 1) == 1, "compass sectors");
        var yaw = new[] { 170.0, 179, -179, -170, -100, 100 }; Angles.Unwrap(yaw);
        Check(Near(yaw[2], 181, 1e-9) && Near(yaw[3], 190, 1e-9) && Near(yaw[4], 260, 1e-9) && Near(yaw[5], 100, 1e-9), "yaw unwrap continuous across +-180");
        var samples = new List<ViewSample> { new(0, 0, 0), new(0.01, 1, 0), new(0.02, 2, 0) };
        var path = ViewPath.Resample(samples);
        Check(path.Count == 6 && Near(path.Yaw[3], 1.2, 1e-9) && Near(path.YawVelocity[2], 100, 1e-6), "resampling interpolates and differentiates");
    }
}
