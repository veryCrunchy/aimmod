using System.Runtime.InteropServices;

namespace AimMod.InGame;

record RunSummary(double? Duration, double? Score, double? ScorePerMinute, double? ShotsFired,
    double? ShotsHit, double? KillsPerSecond, double? DamageEfficiency, double? Accuracy,
    double? PeakScorePerMinute, double? PeakKillsPerSecond, double? AverageFireToHitMs,
    double? P90FireToHitMs, double? AverageShotsToHit, double? CorrectiveShotRatio);
record RunTimelinePoint(double Time, double? ScorePerMinute, double? KillsPerSecond,
    double? Accuracy, double? DamageEfficiency, double? Score, double? Kills,
    double? ShotsFired, double? ShotsHit);
record RunDetails(string RunId, RunSummary? Summary, IReadOnlyList<RunTimelinePoint> Timeline);

// Selected-run lookup only: never reads replay blobs, changes schema, or writes the desktop database.
static class RunMetrics
{
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint db, int flags, nint vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_close_v2(nint db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_busy_timeout(nint db, int milliseconds);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out nint stmt, nint tail);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_bind_text(nint stmt, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int length, nint destructor);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_step(nint stmt);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_finalize(nint stmt);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_column_type(nint stmt, int column);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern double sqlite3_column_double(nint stmt, int column);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern nint sqlite3_errmsg(nint db);
    static double? Number(nint stmt, int column)
    {
        if (sqlite3_column_type(stmt, column) is not (1 or 2)) return null;
        double value = sqlite3_column_double(stmt, column);
        return double.IsFinite(value) ? value : null;
    }
    internal static double? Accuracy(double? direct, double? hit, double? fired)
    {
        if (hit is >= 0 && fired is > 0 && double.IsFinite(hit.Value) && double.IsFinite(fired.Value) && hit <= fired + .0001)
            return Math.Clamp(hit.Value / fired.Value * 100, 0, 100);
        return direct is >= 0 and <= 100 && double.IsFinite(direct.Value) ? (direct <= 1 ? direct * 100 : direct) : null;
    }
    public static RunDetails Read(string path, string runId)
    {
        var empty = new RunDetails(runId, null, []);
        if (!File.Exists(path) || string.IsNullOrWhiteSpace(runId)) return empty;
        nint db = 0;
        try
        {
            if (sqlite3_open_v2(path, out db, 1, 0) != 0) throw new IOException("Cannot open run metrics read-only.");
            sqlite3_busy_timeout(db, 750);
            RunSummary? summary = null;
            var timeline = new List<RunTimelinePoint>();
            // Older databases can lack either table; retain whichever data is available.
            Query("SELECT duration_secs, score_total, score_per_minute, shots_fired, shots_hit, kills_per_second, damage_efficiency, accuracy_pct, peak_score_per_minute, peak_kills_per_second, avg_fire_to_hit_ms, p90_fire_to_hit_ms, avg_shots_to_hit, corrective_shot_ratio FROM session_run_summaries WHERE session_id = ?1", s => {
                double? N(int col) => Number(s, col);
                summary = new(N(0), N(1), N(2), N(3), N(4), N(5), N(6), Accuracy(N(7), N(4), N(3)), N(8), N(9), N(10), N(11), N(12), N(13));
            });
            Query("SELECT t_sec, score_per_minute, kills_per_second, accuracy_pct, damage_efficiency, score_total, kills, shots_fired, shots_hit FROM session_run_timelines WHERE session_id = ?1 ORDER BY t_sec LIMIT 14401", s => {
                double? N(int col) => Number(s, col);
                if (N(0) is not double time || time < 0) return;
                timeline.Add(new(time, N(1), N(2), Accuracy(N(3), N(8), N(7)), N(4), N(5), N(6), N(7), N(8)));
            });
            return new(runId, summary, timeline);
            void Query(string sql, Action<nint> row)
            {
                nint stmt = 0;
                try
                {
                    var prepared = sqlite3_prepare_v2(db, sql, -1, out stmt, 0);
                    var error = prepared == 0 ? "" : Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) ?? "";
                    if (prepared == 1 && (error.StartsWith("no such table:", StringComparison.Ordinal) || error.StartsWith("no such column:", StringComparison.Ordinal))) return;
                    if (prepared != 0) throw new IOException("Run metrics query could not be prepared.");
                    if (sqlite3_bind_text(stmt, 1, runId, -1, -1) != 0) throw new IOException("Run selection could not be bound.");
                    int result;
                    while ((result = sqlite3_step(stmt)) == 100) row(stmt);
                    if (result != 101) throw new IOException("Run metrics query did not complete.");
                }
                finally { if (stmt != 0) sqlite3_finalize(stmt); }
            }
        }
        finally { if (db != 0) sqlite3_close_v2(db); }
    }
}
