using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AimMod.InGame;

record Run(string Id, string Scenario, double Score, double? Accuracy, double Duration,
    double Kills, double Damage, string Timestamp, double? Smoothness, double? Jitter,
    double? Efficiency, double? Correction, bool Replay);

// Windows' SQLite library, opened strictly read-only. Never migrate the existing
// desktop database or load its replay blobs merely to display a history page.
static class History
{
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint db, int flags, nint vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_close_v2(nint db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_busy_timeout(nint db, int milliseconds);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_prepare_v2(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out nint stmt, nint tail);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_step(nint stmt);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_finalize(nint stmt);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern nint sqlite3_column_text(nint stmt, int column);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern double sqlite3_column_double(nint stmt, int column);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern nint sqlite3_errmsg(nint db);
    static string Text(nint stmt, int col) => Marshal.PtrToStringUTF8(sqlite3_column_text(stmt, col)) ?? "";
    static void Check(int code, nint db) { if (code != 0) throw new IOException(Marshal.PtrToStringUTF8(sqlite3_errmsg(db))); }

    public static IReadOnlyList<Run> Read(string path)
    {
        if (!File.Exists(path)) return [];
        nint db = 0, stmt = 0;
        try
        {
            Check(sqlite3_open_v2(path, out db, 1, 0), db); // SQLITE_OPEN_READONLY
            Check(sqlite3_busy_timeout(db, 1500), db);
            Check(sqlite3_prepare_v2(db, "SELECT id, scenario, score, accuracy, duration_secs, kills, damage_done, timestamp, smoothness_json, has_replay FROM sessions ORDER BY timestamp DESC, id DESC", -1, out stmt, 0), db);
            var runs = new List<Run>();
            int result;
            while ((result = sqlite3_step(stmt)) == 100)
            {
                double? smooth = null, jitter = null, efficiency = null, correction = null;
                var json = Text(stmt, 8);
                if (json.Length > 0)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        double? Metric(string key) => HubHistory.Number(doc.RootElement, key);
                        smooth = Metric("composite"); jitter = Metric("jitter");
                        efficiency = Metric("path_efficiency"); correction = Metric("correction_ratio");
                    }
                    catch (JsonException) { /* Missing mechanics must remain unknown. */ }
                }
                var score = sqlite3_column_double(stmt, 2);
                var duration = sqlite3_column_double(stmt, 4);
                if (!double.IsFinite(score) || !double.IsFinite(duration) || duration <= 0) continue;
                var accuracy = sqlite3_column_double(stmt, 3);
                // SessionRecord.accuracy is stored as a percentage, unlike bridge ratios.
                runs.Add(new(Text(stmt, 0), Text(stmt, 1), score,
                    double.IsFinite(accuracy) && accuracy is >= 0 and <= 100 ? accuracy : null,
                    duration, sqlite3_column_double(stmt, 5), sqlite3_column_double(stmt, 6),
                    Text(stmt, 7), smooth, jitter, efficiency, correction, sqlite3_column_double(stmt, 9) != 0));
            }
            if (result != 101) Check(result, db);
            return runs;
        }
        finally { if (stmt != 0) sqlite3_finalize(stmt); if (db != 0) sqlite3_close_v2(db); }
    }
}
