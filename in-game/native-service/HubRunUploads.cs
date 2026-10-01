using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AimMod.InGame;

// Uploads runs completed in KovaaK's (completed.tsv) to AimMod Hub as the
// linked account, through the same IngestSession call the companion app uses.
//
// Identity: when KovaaK's own stats file for the run is found, the run is sent
// with the companion app's session id (scenario + stats timestamp), so a run
// the companion app also uploads lands on the same Hub run instead of a
// duplicate. Runs the companion app has recorded locally are left to it, so its
// richer upload (timeline, mouse path) is never overwritten. Without a stats
// file the run is sent with its own "aimmod-<attempt>" id.
//
// Only runs completed after the account was linked on this version are sent
// (no silent backfill of old history), never AimMod's generated multiplayer
// arenas, and never more than one request every few seconds.
sealed class HubRunUploads
{
    internal const string StateFile = "hub-uploads.json";
    // KovaaK's writes its stats file as the run ends; give it (and the companion app) time.
    internal static readonly TimeSpan Settle = TimeSpan.FromSeconds(20), GiveUpOnStats = TimeSpan.FromMinutes(3), MaxAge = TimeSpan.FromDays(14), Spacing = TimeSpan.FromSeconds(3);
    const int MaxAttempts = 6, MaxDone = 5000;
    const int SchemaVersion = 11;
    readonly Hub hub;
    readonly HubSharingSettings settings;
    readonly string statePath;
    readonly Func<string?> statsFolder;
    readonly Func<DateTimeOffset> clock;
    State state;
    DateTimeOffset nextAttempt;
    int failures;
    string rejected = "";
    volatile string status = "starting";
    int uploaded, skipped;

    sealed record State(string Handle, DateTimeOffset Since, List<string> Done, Dictionary<string, int> Attempts);

    public HubRunUploads(Hub hub, HubSharingSettings settings, string folder, Func<string?>? statsFolder = null, Func<DateTimeOffset>? clock = null)
    {
        this.hub = hub; this.settings = settings; this.statsFolder = statsFolder ?? (() => null);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        statePath = Path.Combine(folder, StateFile);
        state = Load() ?? new("", default, [], []);
    }
    /// <summary>off, not-linked, idle, uploading, unavailable, rejected or starting.</summary>
    public string Status => status;
    public object StatusInfo => new { state = status, uploaded, skipped };

    State? Load()
    {
        try
        {
            if (!File.Exists(statePath) || new FileInfo(statePath).Length > 1024 * 1024) return null;
            var loaded = JsonSerializer.Deserialize<State>(File.ReadAllText(statePath));
            return loaded is { Handle: not null, Done: not null, Attempts: not null } ? loaded : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }
    void Save()
    {
        if (state.Done.Count > MaxDone) state.Done.RemoveRange(0, state.Done.Count - MaxDone);
        try { AtomicFile.WriteBytes(statePath, JsonSerializer.SerializeToUtf8Bytes(state)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("Hub upload state could not be saved (" + ex.GetType().Name + ")."); }
    }
    void Finish(string id, bool sent)
    {
        state.Done.Add(id); state.Attempts.Remove(id);
        if (sent) uploaded++; else skipped++;
        Save();
    }

    internal static bool Eligible(Run run) =>
        run.Id.StartsWith("native:", StringComparison.Ordinal) && run.Id.Length is > 7 and <= 160
        && !string.IsNullOrWhiteSpace(run.Scenario) && run.Scenario.Length <= 512
        // AimMod's own arenas and probes are generated scenarios, not KovaaK's.
        && !run.Scenario.StartsWith("AimMod ", StringComparison.OrdinalIgnoreCase)
        && double.IsFinite(run.Score) && Math.Abs(run.Score) < 1e12 && double.IsFinite(run.Duration) && run.Duration is > 0 and <= 86400;

    /// <summary>The companion app's session id for a stats file: lower-cased scenario, spaces as underscores, then the stamp.</summary>
    internal static string CompanionSessionId(string scenario, string stamp) => scenario.ToLowerInvariant().Replace(' ', '_') + "-" + stamp;

    internal sealed record StatsMatch(string Stamp, Run Csv);

    /// <summary>KovaaK's stats file for this run: same scenario and score, written as it ended.</summary>
    internal static StatsMatch? FindStats(string? folder, Run run, DateTimeOffset completed)
    {
        if (folder is null || !CsvHistory.AcceptableDirectory(folder) || !Directory.Exists(folder) || run.Scenario.IndexOfAny(['*', '?', '/', '\\', ':', '<', '>', '|', '"']) >= 0) return null;
        StatsMatch? best = null; var bestGap = double.MaxValue;
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, run.Scenario + " - *.csv", SearchOption.TopDirectoryOnly).Take(4096))
            {
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length is 0 or > 1024 * 1024) continue;
                // Cheap filter before reading: written around the time the run ended.
                if (Math.Abs((info.LastWriteTimeUtc - completed.UtcDateTime).TotalMinutes) > 10) continue;
                var stamp = Regex.Match(info.Name, @" - (\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2})(?: Stats)?\.csv$", RegexOptions.CultureInvariant);
                if (!stamp.Success) continue;
                var ended = HubHistory.Date(stamp.Groups[1].Value);
                var gap = (ended - completed).TotalSeconds;
                if (gap is < -30 or > 120) continue;
                Run csv;
                try { csv = CsvHistory.Parse(info.Name, File.ReadAllText(path)); }
                catch (Exception ex) when (ex is FormatException or Microsoft.VisualBasic.FileIO.MalformedLineException) { continue; }
                if (!csv.Scenario.Equals(run.Scenario, StringComparison.Ordinal) || Math.Abs(csv.Score - run.Score) > Math.Max(0.05, Math.Abs(run.Score) * 1e-4)) continue;
                if (Math.Abs(gap) < bestGap) { bestGap = Math.Abs(gap); best = new(stamp.Groups[1].Value, csv); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        return best;
    }

    static JsonObject Number(double value) => new() { ["numberValue"] = value };
    static JsonObject Text(string value) => new() { ["stringValue"] = value };

    internal static JsonObject Payload(Run run, string sessionId, StatsMatch? stats, string externalId, string version)
    {
        var playedAt = stats is null ? HubHistory.Date(run.Timestamp) : HubHistory.Date(stats.Stamp);
        var accuracy = run.Accuracy ?? stats?.Csv.Accuracy;
        var summary = new JsonObject
        {
            ["appVersion"] = Text(version),
            ["client"] = Text(HubLivePayload.ClientName),
            ["platformOs"] = Text("windows"),
            ["kills"] = Number(run.Kills),
            ["damageDone"] = Number(run.Damage),
            ["hasStatsFile"] = new JsonObject { ["boolValue"] = stats is not null },
        };
        if (stats is not null)
        {
            summary["csvScore"] = Number(stats.Csv.Score);
            if (stats.Csv.Accuracy is double csvAccuracy) summary["csvAccuracy"] = Number(csvAccuracy);
            summary["csvKills"] = Number(stats.Csv.Kills);
            summary["csvDurationSecs"] = Number(stats.Csv.Duration);
            summary["csvDamageDone"] = Number(stats.Csv.Damage);
        }
        var payload = new JsonObject
        {
            ["appVersion"] = "in-game/" + version,
            ["schemaVersion"] = SchemaVersion,
            ["sessionId"] = sessionId,
            ["scenarioName"] = run.Scenario,
            ["score"] = run.Score,
            ["durationMs"] = (long)Math.Round(run.Duration * 1000),
            ["playedAtIso"] = playedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["summary"] = summary,
        };
        if (accuracy is double a) payload["accuracy"] = Math.Round(a, 4);
        if (externalId.Length > 0) payload["userExternalId"] = externalId;
        return payload;
    }

    /// <summary>
    /// One step: uploads at most one run. nativeRuns: the journal; companionIds:
    /// session ids the companion app recorded locally; hubRunIds: runs already on Hub.
    /// </summary>
    public async Task Tick(IReadOnlyList<Run> nativeRuns, IReadOnlySet<string> companionIds, IReadOnlySet<string> hubRunIds, CancellationToken token)
    {
        var now = clock();
        var handle = hub.LinkedHandle;
        if (handle is null) { status = "not-linked"; return; }
        if (!settings.Current.RunUploads) { status = "off"; return; }
        var account = hub.AccountFingerprint;
        if (account == rejected) { status = "rejected"; return; }
        // A newly linked account starts from now: past runs are not uploaded silently.
        if (!state.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase)) { state = new(handle, now, [], []); Save(); }
        if (now < nextAttempt) return;
        var done = state.Done.ToHashSet(StringComparer.Ordinal);
        Run? next = null; DateTimeOffset nextAt = default;
        foreach (var run in nativeRuns)
        {
            if (done.Contains(run.Id) || !Eligible(run)) continue;
            var at = HubHistory.Date(run.Timestamp);
            if (at == DateTimeOffset.MinValue || at < state.Since || now - at > MaxAge || now - at < Settle) continue;
            if (next is null || at < nextAt) { next = run; nextAt = at; }
        }
        if (next is null) { status = "idle"; return; }
        var stats = FindStats(statsFolder(), next, nextAt);
        if (stats is null && now - nextAt < GiveUpOnStats && statsFolder() is not null) { status = "idle"; return; } // Wait for the stats file.
        var sessionId = stats is null ? "aimmod-" + next.Id[7..] : CompanionSessionId(next.Scenario, stats.Stamp);
        var externalId = hub.ExternalId;
        if (companionIds.Contains(sessionId) || externalId.Length > 0 && hubRunIds.Contains(HubHistory.PublicId(externalId, sessionId)))
        { Finish(next.Id, sent: false); status = "idle"; return; }
        status = "uploading";
        var result = await hub.SendAsAccount(HttpMethod.Post, "/aimmod.hub.v1.HubService/IngestSession", Payload(next, sessionId, stats, externalId, HubLiveActivityBuilder.Version).ToJsonString(), token, connect: true);
        if (result is not { } r) { status = "not-linked"; return; }
        nextAttempt = now + Spacing;
        if (r.Ok) { failures = 0; Finish(next.Id, sent: true); status = "idle"; return; }
        if (r.Status == 401) { rejected = account; status = "rejected"; Console.Error.WriteLine("Hub run upload: the linked account was refused. Link the account again."); return; }
        // A refused run (invalid, identity conflict, too large) is not retried forever.
        var attempts = state.Attempts.GetValueOrDefault(next.Id) + 1;
        if (r.Status is 400 or 403 or 404 or 409 or 413 or 422 || attempts >= MaxAttempts) { Finish(next.Id, sent: false); Console.Error.WriteLine($"Hub run upload refused ({r.Status})."); status = "idle"; return; }
        state.Attempts[next.Id] = attempts; Save();
        failures = Math.Min(failures + 1, 16);
        nextAttempt = now + (r.Status is 429 or 503 && r.RetryAfter is TimeSpan wait ? TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 5, 3600)) : HubLivePublisher.Backoff(failures) * 2);
        status = "unavailable";
    }
}
