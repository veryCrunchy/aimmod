using AimMod.InGame.Analysis;

namespace AimMod.InGame;

// Run analysis for a run the in-game recorder saved, built from its replay alone: no
// desktop database is needed. Read-only; the last few runs are kept in memory.
static class NativeInspection
{
    internal const double Window = 5;
    const int Kept = 6;
    internal sealed record Built(InspectedRun Run, RunDetails Details, AnalysisWindow[] Windows, InspectedShot[] Shots, ResponseSummary? Response, ResponseEpisode[] Episodes);
    static readonly object gate = new();
    static readonly List<(string Id, long Size, long Stamp, Built Value)> cache = new();

    public static InspectionResult? Read(ReplayCatalog catalog, string id, int shotPage = 0)
    {
        if (!id.StartsWith("native:", StringComparison.Ordinal) || id.Length <= 7) return null;
        var replayId = id[7..];
        var source = catalog.Resolve(replayId);
        if (source is null) return null;
        var info = new FileInfo(source);
        Built? built = null;
        lock (gate)
            foreach (var entry in cache)
                if (entry.Id == id && entry.Size == info.Length && entry.Stamp == info.LastWriteTimeUtc.Ticks) built = entry.Value;
        if (built is null)
        {
            var replay = catalog.Read(replayId);
            if (replay is null || replay.Frames.Count < 2) return null;
            built = Build(id, replay);
            lock (gate)
            {
                cache.RemoveAll(e => e.Id == id);
                cache.Add((id, info.Length, info.LastWriteTimeUtc.Ticks, built));
                if (cache.Count > Kept) cache.RemoveAt(0);
            }
        }
        var pages = Math.Max(1, (int)Math.Ceiling(built.Shots.Length / 100.0));
        shotPage = Math.Clamp(shotPage, 0, pages - 1);
        return new(built.Run, built.Details, built.Response, built.Episodes, (int)(built.Response?.Episodes ?? 0), built.Windows, built.Windows.Length, built.Shots.Skip(shotPage * 100).Take(100).ToArray(),
            built.Shots.Length, shotPage, pages, built.Shots.Length > 0 ? built.Shots[0].TimestampMs : null, []) { ReplayId = replayId };
    }

    // Cumulative counters carried forward between the frames that recorded them.
    sealed class Counter
    {
        readonly List<double> times = new(), values = new();
        public bool Known => times.Count > 0;
        public void Add(double t, double? value)
        {
            if (value is not double v || !double.IsFinite(v)) return;
            if (values.Count > 0 && values[^1] == v) return;
            times.Add(t); values.Add(v);
        }
        // Zero before the first record: every counter starts a run at zero.
        public double At(double t)
        {
            int lo = 0, hi = times.Count;
            while (lo < hi) { var mid = (lo + hi) / 2; if (times[mid] <= t) lo = mid + 1; else hi = mid; }
            return lo == 0 ? 0 : values[lo - 1];
        }
    }

    internal static Built Build(string id, NativeReplay replay)
    {
        Counter score = new(), shots = new(), hits = new(), kills = new();
        foreach (var frame in replay.Frames)
        {
            if (frame.Stats is not { } s) continue;
            score.Add(frame.T, s.Score); shots.Add(frame.T, s.Shots); hits.Add(frame.T, s.Hits); kills.Add(frame.T, s.Kills);
        }
        var duration = replay.Duration > 0 ? replay.Duration : replay.Frames[^1].T;
        double? Total(Counter c) => c.Known ? c.At(duration) : null;
        double? Share(double? hit, double? fired) => hit is double h && fired is double f && f > 0 && h >= 0 ? Math.Clamp(h / f * 100, 0, 100) : null;

        // One point a second; pace, kills and accuracy over the 5 seconds before it.
        var timeline = new List<RunTimelinePoint>();
        var seconds = (int)Math.Min(14400, Math.Ceiling(duration));
        for (int i = 1; i <= seconds; i++)
        {
            var t = Math.Min(i, duration); var from = Math.Max(0, t - Window); var span = t - from;
            if (span <= 0) continue;
            double? pace = score.Known ? (score.At(t) - score.At(from)) / span * 60 : null;
            double? kps = kills.Known ? (kills.At(t) - kills.At(from)) / span : null;
            double? accuracy = shots.Known && hits.Known ? Share(hits.At(t) - hits.At(from), shots.At(t) - shots.At(from)) : null;
            timeline.Add(new(t, pace, kps, accuracy, null, score.Known ? score.At(t) : null, kills.Known ? kills.At(t) : null,
                shots.Known ? shots.At(t) : null, hits.Known ? hits.At(t) : null));
        }
        // Peaks only from full windows: the first seconds divide by a short span.
        var full = timeline.Where(p => p.Time >= Window).ToArray();
        var finalScore = Total(score); var fired = Total(shots); var landed = Total(hits); var killed = Total(kills);
        var accuracyTotal = Share(landed, fired);
        var summary = new RunSummary(duration, finalScore, finalScore is double fs && duration > 0 ? fs / duration * 60 : null, fired, landed,
            killed is double k && duration > 0 ? k / duration : null, null, accuracyTotal,
            full.Length > 0 && score.Known ? full.Max(p => p.ScorePerMinute) : null, full.Length > 0 && kills.Known ? full.Max(p => p.KillsPerSecond) : null,
            null, null, fired is double f2 && landed is double l2 && l2 > 0 ? f2 / l2 : null, null);

        // Key moments: the strongest and the slowest 5 seconds, when they really differ.
        var windows = new List<AnalysisWindow>();
        if (score.Known && duration >= 20)
        {
            var candidates = full.Where(p => p.ScorePerMinute is not null && p.Time <= duration - 1).ToArray();
            if (candidates.Length >= 10)
            {
                var best = candidates.MaxBy(p => p.ScorePerMinute)!; var worst = candidates.MinBy(p => p.ScorePerMinute)!;
                AnalysisWindow Moment(string kind, string label, RunTimelinePoint p)
                {
                    var from = p.Time - Window;
                    double? windowFired = shots.Known ? shots.At(p.Time) - shots.At(from) : null, windowHits = hits.Known ? hits.At(p.Time) - hits.At(from) : null;
                    return new(kind, label, "", from * 1000, p.Time * 1000, windowFired, windowHits, Share(windowHits, windowFired), "", null, null, null, null, p.ScorePerMinute, p.KillsPerSecond);
                }
                if (best.ScorePerMinute > 0 && worst.ScorePerMinute <= best.ScorePerMinute * .85 && Math.Abs(best.Time - worst.Time) >= Window)
                {
                    windows.Add(Moment("peak_pace", "Strongest 5 seconds", best));
                    windows.Add(Moment("pace_drop", "Slowest 5 seconds", worst));
                    windows.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
                }
            }
        }

        var response = NativeTargetResponses.Build(replay);
        summary = summary with { AverageFireToHitMs = response.FireToHitMs, P90FireToHitMs = response.FireToHitP90Ms,
            CorrectiveShotRatio = response.CorrectiveRatio };
        windows.AddRange(response.Windows);
        windows.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

        return new(new(id, replay.Scenario, finalScore, accuracyTotal, duration, replay.RecordedAt), new(id, summary, timeline), windows.ToArray(), response.Shots, response.Summary, response.Episodes);
    }
}
