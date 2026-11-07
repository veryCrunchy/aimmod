using System.Text.Json;
using System.Text.Json.Serialization;
using AimMod.InGame.Analysis;

namespace AimMod.InGame;

sealed record PracticePoint(double Time, double? Horizontal, double? Vertical, double? Along);
sealed record PracticeRun(int Version, string Id, string Scenario, DateTimeOffset RecordedAt, long SourceSize, long SourceStamp,
    FlickReport Flicks, TrackingReport Tracking, PracticePoint[] Timeline);
sealed record PracticeCell(string Direction, string Amplitude, int Count, int Runs, double ErrorPercent);

// One idle-time worker owns analysis and disk writes. HTTP handlers only read snapshots.
sealed class PracticePipeline : IDisposable
{
    internal const int Version = 1, MaxRuns = 128;
    internal const long MaxCacheBytes = 32 * 1024 * 1024, MaxEntryBytes = 2 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new PracticeNumberConverter() } };
    readonly ReplayCatalog catalog;
    readonly string folder;
    readonly Func<bool> live;
    readonly Thread worker;
    readonly ManualResetEventSlim stop = new();
    readonly object gate = new();
    readonly Dictionary<string, PracticeRun> cache = new();
    readonly Dictionary<string, DateTimeOffset> failures = new();
    string? requested;
    DateTimeOffset next;
    internal PracticePipeline(string output, ReplayCatalog catalog, Func<bool> live, bool start = true)
    {
        this.catalog = catalog; this.live = live;
        folder = Path.Combine(output, "practice-cache"); Directory.CreateDirectory(folder);
        worker = new Thread(Loop) { IsBackground = true, Name = "Practice analysis", Priority = ThreadPriority.Lowest };
        if (start) worker.Start();
    }
    void CheckIdle() { if (stop.IsSet || live()) throw new OperationCanceledException(); }
    void Loop()
    {
        while (!stop.IsSet)
        {
            try { Tick(DateTimeOffset.UtcNow); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException or InvalidDataException) { }
            stop.Wait(1000);
        }
    }
    internal bool Tick(DateTimeOffset now)
    {
        if (live() || now < next) return false;
        next = now.AddSeconds(10);
        CheckIdle();
        string? demand; lock(gate) demand=requested;
        var candidates = catalog.List(CheckIdle).ToList();
        if(demand is not null && candidates.All(r => r.Id != demand))
        {
            CheckIdle(); var older = catalog.Read(demand); CheckIdle();
            if(older is { Reason: "completed" }) candidates.Add(new(older.Id, older.Scenario, older.RecordedAt, older.Reason, older.Frames.Count, older.Inputs.Count));
        }
        var newest = candidates.Where(r => r.Reason == "completed").OrderByDescending(r => r.Id == demand).ThenByDescending(r => r.RecordedAt, StringComparer.Ordinal).Take(MaxRuns).ToArray();
        var keep = newest.Select(r => r.Id).ToHashSet();
        lock (gate) { foreach (var id in cache.Keys.Where(id => !keep.Contains(id)).ToArray()) cache.Remove(id); foreach(var id in failures.Keys.Where(id => !keep.Contains(id)).ToArray()) failures.Remove(id); }
        Trim();
        foreach (var summary in newest)
        {
            CheckIdle();
            var source = catalog.Resolve(summary.Id); if (source is null) continue;
            var info = new FileInfo(source);
            PracticeRun? saved; lock (gate) cache.TryGetValue(summary.Id, out saved);
            if (saved is not null && Valid(saved, info)) continue;
            var path = Path.Combine(folder, summary.Id + ".json");
            if (File.Exists(path))
            {
                try { if (new FileInfo(path).Length <= MaxEntryBytes) saved = JsonSerializer.Deserialize<PracticeRun>(File.ReadAllText(path), Json); }
                catch (Exception e) when (e is JsonException or IOException) { saved = null; }
                if (saved is not null && saved.Id == summary.Id && Valid(saved, info)) { lock(gate) cache[summary.Id] = saved; continue; }
            }
            lock(gate) { cache.Remove(summary.Id); if (failures.TryGetValue(summary.Id, out var retry) && now < retry) continue; }
            try
            {
                var replay = catalog.Read(summary.Id); CheckIdle();
                if (replay is null || replay.Duration > 600) throw new InvalidDataException();
                var input = ReplayAnalysis.Build(replay, CheckIdle); CheckIdle();
                var view = ViewPath.Resample(input.View, input.Duration);
                var flicks = FlickAnalysis.Analyze(input, view, CheckIdle); CheckIdle();
                var tracking = TrackingAnalysis.Analyze(input, view, CheckIdle); CheckIdle();
                var points = new List<PracticePoint>();
                for (int i = 0; i < view.Count; i += Math.Max(1, view.Count / 240))
                {
                    var target = input.Targets[i].FirstOrDefault();
                    if (input.Targets[i].Length == 0) { points.Add(new(view.T[i], null, null, null)); continue; }
                    var yaw = Angles.Wrap(view.Yaw[i] - target.Yaw); var pitch = view.Pitch[i] - target.Pitch;
                    double? along = null;
                    if (i > 0 && input.Targets[i-1].Length > 0 && input.Targets[i-1][0].Id == target.Id)
                    {
                        var previous = input.Targets[i-1][0]; var dx = Angles.Wrap(target.Yaw - previous.Yaw); var dy = target.Pitch - previous.Pitch; var speed = Math.Sqrt(dx*dx+dy*dy);
                        if(speed > .04) along = (yaw*dx+pitch*dy)/speed;
                    }
                    points.Add(new(view.T[i], yaw, pitch, along));
                }
                saved = new(Version, summary.Id, summary.Scenario, DateTimeOffset.Parse(summary.RecordedAt), info.Length, info.LastWriteTimeUtc.Ticks, flicks, tracking, points.ToArray());
                var bytes = JsonSerializer.SerializeToUtf8Bytes(saved, Json);
                if (bytes.Length > MaxEntryBytes) throw new InvalidDataException();
                CheckIdle(); File.WriteAllBytes(path + ".tmp", bytes); File.Move(path + ".tmp", path, true);
                lock(gate) { cache[summary.Id] = saved; failures.Remove(summary.Id); }
                Trim(); return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is IOException or JsonException or ArgumentException or InvalidDataException)
            { lock(gate) failures[summary.Id] = now.AddMinutes(5); return false; }
        }
        return false;
    }
    static bool Valid(PracticeRun r, FileInfo source) => r.Version == Version && r.Flicks is not null && r.Tracking is not null && r.Timeline is not null && r.SourceSize == source.Length && r.SourceStamp == source.LastWriteTimeUtc.Ticks;
    internal void Trim()
    {
        long bytes = 0; int count = 0;
        foreach(var file in new DirectoryInfo(folder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc))
        { if (file.Length > MaxEntryBytes || count >= MaxRuns || bytes + file.Length > MaxCacheBytes) { file.Delete(); lock(gate) cache.Remove(Path.GetFileNameWithoutExtension(file.Name)); } else { count++; bytes += file.Length; } }
    }
    internal object Read(string runId)
    {
        var id = runId.StartsWith("native:", StringComparison.Ordinal) ? runId[7..] : runId;
        var source = catalog.Resolve(id);
        if (source is null) return new { status = "unavailable" };
        var info = new FileInfo(source);
        lock(gate) { if(cache.TryGetValue(id, out var value) && Valid(value, info)) return new { status = "ready", analysis = value }; requested=id; return new { status = failures.ContainsKey(id) ? "error" : "pending" }; }
    }
    internal PracticeRun[] Snapshot() { lock(gate) return cache.Values.OrderByDescending(r => r.RecordedAt).ToArray(); }
    internal object Summary() => PracticeGuidance.Build(Snapshot(), DateTimeOffset.UtcNow);
    public void Dispose() { stop.Set(); if(worker.IsAlive) worker.Join(); stop.Dispose(); }
}

static class PracticeGuidance
{
    internal static string Bin(double d) => d < 5 ? "1-5" : d < 15 ? "5-15" : d < 30 ? "15-30" : d < 60 ? "30-60" : "60+";
    internal static PracticeCell[] Map(IEnumerable<PracticeRun> runs) => runs.SelectMany(r => r.Flicks.Flicks.Select(f => (r.Id, Flick: f)))
        .GroupBy(x => (x.Flick.Sector, Bin(x.Flick.Distance))).Select(g => new PracticeCell(Angles.SectorNames[g.Key.Sector], g.Key.Item2, g.Count(), g.Select(x => x.Id).Distinct().Count(), (g.Average(x => x.Flick.PrimaryEndRatio)-1)*100)).ToArray();
    internal static object Build(PracticeRun[] runs, DateTimeOffset now)
    {
        var map = Map(runs.Take(30));
        var readiness = "Play several repeats of one scenario to measure your warm-up.";
        var fatigue = "No sustained mechanics drift measured yet.";
        var sensitivity = "Keep your sensitivity while you gather consistent repeats.";
        var session = new List<PracticeRun>(); var cursor = now;
        foreach(var r in runs.OrderByDescending(r => r.RecordedAt)) { if(cursor-r.RecordedAt > TimeSpan.FromMinutes(30)) break; session.Add(r); cursor=r.RecordedAt; }
        var same = session.GroupBy(r => r.Scenario).OrderByDescending(g => g.Count()).FirstOrDefault()?.Where(r => r.Flicks.Count >= 10).OrderBy(r => r.RecordedAt).ToArray() ?? [];
        if(same.Length >= 6)
        {
            var early = same.Take(3).ToArray(); var recent = same.TakeLast(3).ToArray();
            double Drift(Func<PracticeRun,double> metric, double floor) => (recent.Average(metric)-early.Average(metric))/Math.Max(floor,early.Average(metric));
            var corrections = Drift(r => r.Flicks.MeanCorrections, .25); var verification = Drift(r => r.Flicks.MeanVerificationMs, 20); var efficiency = Drift(r => r.Flicks.MeanEfficiency, .1);
            if(recent.All(r => r.Flicks.MeanCorrections > early.Average(x => x.Flicks.MeanCorrections)*1.2) && (verification > .2 || efficiency < -.1)) fatigue = "Corrections and settling have worsened across your last three repeats. Take a short break and compare again.";
            readiness = corrections < -.1 || verification < -.1 ? "Your mechanics are settling as you warm up. Keep the same pace for another repeat." : Math.Abs(corrections) <= .1 && Math.Abs(verification) <= .1 && efficiency >= -.05 ? "Your last three repeats are stable against your first three. Try a focused scoring block." : "Your mechanics are still changing. Use an easier warm-up repeat.";
        }
        if(same.Length < 6)
        {
            var trackingRuns = session.Where(r => r.Tracking.TrackedSeconds >= 10 && double.IsFinite(r.Tracking.RmsYawErrorDeg)).GroupBy(r => r.Scenario)
                .OrderByDescending(g => g.Count()).FirstOrDefault()?.OrderBy(r => r.RecordedAt).ToArray() ?? [];
            if(trackingRuns.Length >= 6)
            {
                var early = trackingRuns.Take(3).ToArray(); var recent = trackingRuns.TakeLast(3).ToArray();
                var error = recent.Average(r => r.Tracking.MeanAbsErrorDeg) / Math.Max(.05, early.Average(r => r.Tracking.MeanAbsErrorDeg));
                var smoothness = recent.Average(r => r.Tracking.MeanSparc) - early.Average(r => r.Tracking.MeanSparc);
                readiness = error < .9 ? "Tracking error is falling as you warm up. Repeat at the same pace." : error <= 1.1 ? "Tracking error is stable across your recent repeats. Try a focused block." : "Tracking error is rising. Ease the pace before scoring.";
                if(error > 1.2 && smoothness < -.15) fatigue = "Tracking error and movement roughness rose together across three repeats. Take a short break and compare again.";
            }
        }
        var evidence = Map(runs.Where(r => r.Scenario == runs.FirstOrDefault()?.Scenario).Take(30)).Where(c => c.Count >= 30 && c.Runs >= 3).ToArray();
        if(evidence.Length > 0)
        {
            var high = evidence.Where(c => c.ErrorPercent > 5).ToArray(); var low = evidence.Where(c => c.ErrorPercent < -10).ToArray();
            if(high.Length >= 2 && low.Length == 0) sensitivity = "Repeated overshoot appears across multiple ranges or directions. Try a slightly lower sensitivity in practice, then compare the same scenarios.";
            else if(low.Length >= 2 && high.Length == 0) sensitivity = "Repeated undershoot appears across multiple ranges or directions. Try a slightly higher sensitivity in practice, then compare the same scenarios.";
            else sensitivity = "Direction and distance show mixed results. Keep sensitivity and practise the weaker direction first.";
        }
        return new { status = runs.Length == 0 ? "pending" : "ready", runs = runs.Length, map, readiness, fatigue, sensitivity };
    }
}

sealed class PracticeNumberConverter : JsonConverter<double>
{
    public override bool HandleNull => true;
    public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.TokenType == JsonTokenType.Null ? double.NaN : reader.GetDouble();
    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) { if(double.IsFinite(value)) writer.WriteNumberValue(value); else writer.WriteNullValue(); }
}
