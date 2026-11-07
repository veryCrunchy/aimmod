using System.Globalization;

namespace AimMod.InGame.Analysis;

/// <summary>
/// Developer diagnostic: --analyze-replays [--output &lt;AimMod folder&gt;] [--id &lt;replay id&gt;] [--flicks]
/// runs the practice analysis over the local replay library and prints
/// per-replay and aggregate results to the console only. Nothing is written.
/// </summary>
static class ReplayAnalysisCli
{
    static string N(double v, string format = "0.0") => double.IsFinite(v) ? v.ToString(format, CultureInfo.InvariantCulture) : "--";
    static string P(double v) => double.IsFinite(v) ? (v * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "--";

    public static int Run(string[] args)
    {
        var output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AimMod", "KovaaksNative");
        string? only = null; var flicks = args.Contains("--flicks");
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--output" && i + 1 < args.Length) output = Path.GetFullPath(args[++i]);
            else if (args[i] == "--id" && i + 1 < args.Length) only = args[++i];
        }
        var catalog = new ReplayCatalog(output);
        var ids = only is not null ? [only] : catalog.List().Select(r => r.Id).ToArray();
        if (ids.Length == 0) { Console.Error.WriteLine("No replays found."); return 1; }
        var results = new List<ReplayAnalysisResult>();
        foreach (var id in ids)
        {
            NativeReplay? replay;
            try { replay = catalog.Read(id); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { Console.WriteLine($"{id}: unreadable ({ex.Message})"); continue; }
            if (replay is null) { Console.WriteLine($"{id}: not found"); continue; }
            var started = System.Diagnostics.Stopwatch.StartNew();
            var r = ReplayAnalysis.Analyze(replay);
            results.Add(r);
            var f = r.Flicks; var t = r.Tracking;
            var counts = ReplayAnalysis.Build(replay);
            Console.WriteLine($"{r.Id}  \"{r.Scenario}\"  {N(r.Duration, "0")} s  format {replay.Version}  {r.ViewSamples} view samples  {counts.FirePressed.Count} clicks  {counts.Hits.Count} hits  {counts.Spawns.Count} spawns  {r.Kind} (fire held {P(r.FireHoldShare)})  analysed in {started.ElapsedMilliseconds} ms");
            Console.WriteLine($"  flicks {f.Count} (skipped {f.Skipped}) hit {P(f.HitRate)}  reaction {N(f.MeanReactionMs, "0")} ms ({f.ReactionSamples})  gap {N(f.MeanGapMs, "0")}  ballistic {N(f.MeanBallisticMs, "0")}  correction {N(f.MeanCorrectionMs, "0")}  verify {N(f.MeanVerificationMs, "0")}  total {N(f.MeanTotalMs, "0")} ms  costliest {f.CostliestPhase}");
            Console.WriteLine($"  efficiency {P(f.MeanEfficiency)}  primary end {N(f.MeanPrimaryEndRatio, "0.00")} (under {P(f.UndershootShare)} / over {P(f.OvershootShare)})  overshoot {P(f.MeanOvershootRatio)}  lateral {P(f.MeanLateralRatio)}  corrections {N(f.MeanCorrections, "0.00")} (clean {P(f.CleanShare)})  peak {N(f.MeanPeakSpeed, "0")} deg/s  shape err {N(f.MeanShapeError, "0.00")}  peak ratio {N(f.MeanPeakRatio, "0.00")}  t-peak {N(f.MeanTimeToPeak, "0.00")}  click err {N(f.MeanClickErrorDeg, "0.00")} deg  TP {N(f.Throughput, "0.0")} bit/s (ID {N(f.MeanIndexOfDifficulty, "0.0")})");
            Console.WriteLine($"  tracking {N(t.TrackedSeconds, "0")} s ({P(t.TrackedShare)})  rms yaw {N(t.RmsYawErrorDeg, "0.00")} pitch {N(t.RmsPitchErrorDeg, "0.00")} deg  bias yaw {N(t.YawBiasDeg, "0.00")} pitch {N(t.PitchBiasDeg, "0.00")}  along {N(t.AlongMotionErrorDeg, "0.00")} (behind {P(t.BehindShare)})  lag {N(t.LagMs, "0")} ms (by error {N(t.LagMsByError, "0")}, r {N(t.VelocityCorrelation, "0.00")})  on target {P(t.OnTargetShare)} (firing {P(t.OnTargetShareWhileFiring)})  reversals {t.Reversals} react {N(t.MeanReversalReactionMs, "0")} ms peak err {N(t.MeanReversalOvershootDeg, "0.0")}  switches {t.TargetSwitches} acquire {N(t.MeanSwitchAcquireMs, "0")} ms  SPARC {N(t.MeanSparc, "0.00")}  HF {P(t.MeanHighFrequencyShare)}  speed {N(t.MeanSpeed, "0")} / target {N(t.MeanTargetSpeed, "0")} deg/s");
            foreach (var s in f.Sectors) Console.WriteLine($"    {s.Name,-10} n {s.Count,3}  hit {P(s.HitRate),4}  total {N(s.MeanTotalMs, "0"),4} ms  primary end {N(s.MeanPrimaryEndRatio, "0.00")}  eff {P(s.MeanEfficiency)}  corr {N(s.MeanCorrections, "0.00")}");
            foreach (var a in f.Amplitudes) Console.WriteLine($"    {a.Range,-10} n {a.Count,3}  hit {P(a.HitRate),4}  total {N(a.MeanTotalMs, "0"),4} ms  primary end {N(a.MeanPrimaryEndRatio, "0.00")}  eff {P(a.MeanEfficiency)}  corr {N(a.MeanCorrections, "0.00")}");
            if (flicks)
                foreach (var x in f.Flicks)
                    Console.WriteLine($"    #{x.Index,3} t {N(x.ClickT, "0.00")}  {Angles.SectorNames[x.Sector],-10} {N(x.Distance, "0.0"),5} deg  react {N(x.ReactionMs ?? double.NaN, "0"),4}  gap {N(x.GapMs, "0"),4}  ball {N(x.BallisticMs, "0"),4}  corr {N(x.CorrectionMs, "0"),4} ({x.Corrections})  verify {N(x.VerificationMs, "0"),4}  eff {P(x.Efficiency),4}  end {N(x.PrimaryEndRatio, "0.00")}  over {P(x.OvershootRatio),4}  peak {N(x.PeakSpeed, "0"),5}  shape {N(x.ShapeError, "0.00")}  err {N(x.ClickErrorDeg, "0.00")} {(x.Hit ? "hit" : "miss")}");
        }
        if (results.Count > 1)
        {
            Console.WriteLine();
            Console.WriteLine($"aggregate over {results.Count} replays: {results.Count(r => r.Kind == ScenarioKind.Clicking)} clicking, {results.Count(r => r.Kind == ScenarioKind.Tracking)} tracking");
            var clicking = results.Where(r => r.Kind == ScenarioKind.Clicking).ToArray();
            var all = clicking.SelectMany(r => r.Flicks.Flicks).ToList();
            if (all.Count > 0)
            {
                var f = FlickAnalysis.Summarize(all, clicking.Sum(r => r.Flicks.Skipped));
                Console.WriteLine($"  {f.Count} flicks in clicking runs: hit {P(f.HitRate)}  reaction {N(f.MeanReactionMs, "0")} ms (median {N(f.MedianReactionMs, "0")}, n {f.ReactionSamples})  ballistic {N(f.MeanBallisticMs, "0")}  correction {N(f.MeanCorrectionMs, "0")}  verify {N(f.MeanVerificationMs, "0")}  total {N(f.MeanTotalMs, "0")} (median {N(f.MedianTotalMs, "0")}) ms  costliest {f.CostliestPhase}");
                Console.WriteLine($"  efficiency {P(f.MeanEfficiency)} (median {P(f.MedianEfficiency)})  primary end {N(f.MeanPrimaryEndRatio, "0.00")} under {P(f.UndershootShare)} over {P(f.OvershootShare)}  corrections {N(f.MeanCorrections, "0.00")} clean {P(f.CleanShare)}  peak {N(f.MeanPeakSpeed, "0")} deg/s  peak ratio {N(f.MeanPeakRatio, "0.00")}  t-peak {N(f.MeanTimeToPeak, "0.00")}  LDLJ {N(f.MeanLdlj, "0.0")}  TP {N(f.Throughput, "0.0")} bit/s");
                foreach (var s in f.Sectors) Console.WriteLine($"    {s.Name,-10} n {s.Count,3}  hit {P(s.HitRate),4}  total {N(s.MeanTotalMs, "0"),4} ms  primary end {N(s.MeanPrimaryEndRatio, "0.00")}  eff {P(s.MeanEfficiency)}  corr {N(s.MeanCorrections, "0.00")}");
                foreach (var a in f.Amplitudes) Console.WriteLine($"    {a.Range,-10} n {a.Count,3}  hit {P(a.HitRate),4}  total {N(a.MeanTotalMs, "0"),4} ms  primary end {N(a.MeanPrimaryEndRatio, "0.00")}  eff {P(a.MeanEfficiency)}  corr {N(a.MeanCorrections, "0.00")}");
            }
            var tracking = results.Where(r => r.Kind == ScenarioKind.Tracking).Select(r => r.Tracking).ToArray();
            if (tracking.Length > 0)
                Console.WriteLine($"  tracking runs: lag {N(tracking.Average(t => t.LagMs), "0")} ms  rms yaw {N(tracking.Average(t => t.RmsYawErrorDeg), "0.00")} pitch {N(tracking.Average(t => t.RmsPitchErrorDeg), "0.00")}  behind {P(tracking.Average(t => t.BehindShare))}  on target {P(tracking.Average(t => t.OnTargetShare))}  SPARC {N(tracking.Average(t => t.MeanSparc), "0.00")}  HF {P(tracking.Average(t => t.MeanHighFrequencyShare))}  reversal react {N(tracking.Where(t => double.IsFinite(t.MeanReversalReactionMs)).Select(t => t.MeanReversalReactionMs).DefaultIfEmpty(double.NaN).Average(), "0")} ms");
        }
        return 0;
    }
}
