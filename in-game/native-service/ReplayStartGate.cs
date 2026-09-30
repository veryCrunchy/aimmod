using System.Text.Json;

namespace AimMod.InGame;

/// <summary>What KovaaK's shows now (core-scene.json, published by AimModCore).</summary>
sealed record GameScene(bool Available, string Scenario, string MapName, double? MapScale, bool InChallenge, bool Running, bool Loading, bool Paused)
{
    public static GameScene? Read(string output, DateTime? utcNow = null)
    {
        try
        {
            var file = new FileInfo(Path.Combine(output, "core-scene.json"));
            var now = utcNow ?? DateTime.UtcNow;
            if (!file.Exists || file.Length > 8192 || now - file.LastWriteTimeUtc > TimeSpan.FromSeconds(3)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file.FullName));
            var r = doc.RootElement;
            if (r.GetProperty("version").GetInt32() != 1) return null;
            bool Flag(string key) => r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
            return new(Flag("available"), r.GetProperty("scenario").GetString() ?? "", r.GetProperty("mapName").GetString() ?? "",
                r.TryGetProperty("mapScale", out var scale) && scale.TryGetDouble(out var s) ? s : null,
                Flag("inChallenge"), Flag("running"), Flag("loading"), Flag("paused"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }
}

/// <summary>Why an in-game replay cannot start right now, in words for the player.</summary>
sealed record ReplayStartBlock(string Reason, string Message);

/// <summary>
/// Replay start gate. A replay plays in the world it was recorded in, from the
/// pause menu. A start request that cannot be honoured yet stays pending (with
/// a reason the workspace shows) and starts by itself once the game is ready.
/// </summary>
sealed class ReplayStartGate
{
    static readonly TimeSpan PendingLimit = TimeSpan.FromMinutes(10);
    readonly object gate = new();
    string? pendingId;
    string? pendingScenario;
    DateTime pendingSince;
    ReplayStartBlock? block;

    internal static ReplayStartBlock? Evaluate(NativeReplay replay, GameScene? scene, bool rendererReady, string rendererReason)
    {
        if (scene is null)
            return rendererReady ? null : new("game-unavailable", "Start KovaaK's with AimMod to watch replays in the game.");
        if (!scene.Available || scene.Loading) return new("scenario-loading", "Waiting for KovaaK's to finish loading the scenario.");
        if (!string.Equals(scene.Scenario, replay.Scenario, StringComparison.Ordinal))
            return new("scenario-mismatch", $"This replay was recorded in \"{replay.Scenario}\". Load that scenario in KovaaK's; the replay starts when it is ready.");
        if (replay.MapName is { Length: > 0 } map && scene.MapName.Length > 0 && !string.Equals(map, scene.MapName, StringComparison.Ordinal))
            return new("map-mismatch", "The loaded map differs from the one this replay was recorded on. Restart the scenario, then try again.");
        if ((scene.InChallenge || scene.Running) && !scene.Paused)
            return new(scene.InChallenge ? "challenge-active" : "scenario-active", "Open the pause menu (Esc) to watch the replay; it starts there.");
        if (!rendererReady)
            return rendererReason switch
            {
                "challenge-active" or "scenario-active" => new(rendererReason, "Open the pause menu (Esc) to watch the replay; it starts there."),
                "scenario-mismatch" => new("scenario-mismatch", $"Load \"{replay.Scenario}\" in KovaaK's; the replay starts when it is ready."),
                "map-mismatch" => new("map-mismatch", "The loaded map differs from the one this replay was recorded on. Restart the scenario, then try again."),
                _ => new("renderer-unavailable", "Open the pause menu (Esc) in KovaaK's; the replay starts when the in-game viewer is ready."),
            };
        return null;
    }

    public ReplayStartBlock? Block { get { lock (gate) return block; } }
    public string? PendingId { get { lock (gate) return pendingId; } }
    public object Status
    {
        get
        {
            lock (gate)
                return new { pending = pendingId, scenario = pendingScenario, reason = block?.Reason, message = block?.Message,
                    waitingSeconds = pendingId is null ? 0 : (int)(DateTime.UtcNow - pendingSince).TotalSeconds };
        }
    }

    public void Wait(string id, string scenario, ReplayStartBlock reason)
    {
        lock (gate)
        {
            if (pendingId != id) pendingSince = DateTime.UtcNow;
            pendingId = id; pendingScenario = scenario; block = reason;
        }
    }
    public void Clear(ReplayStartBlock? reason = null) { lock (gate) { pendingId = null; pendingScenario = null; block = reason; } }

    /// <summary>Re-evaluates a pending start; returns the replay to load when it may start now.</summary>
    public NativeReplay? Poll(Func<string, NativeReplay?> read, Func<GameScene?> scene, Func<(bool Ready, string Reason)> renderer)
    {
        string? id;
        lock (gate)
        {
            id = pendingId;
            if (id is null) return null;
            if (DateTime.UtcNow - pendingSince > PendingLimit)
            {
                pendingId = null;
                block = new("timed-out", "The replay did not start within 10 minutes. Press play to try again.");
                return null;
            }
        }
        var replay = read(id);
        if (replay is null) { Clear(new("replay-unavailable", "This replay can no longer be read.")); return null; }
        var state = renderer();
        var reason = Evaluate(replay, scene(), state.Ready, state.Reason);
        lock (gate)
        {
            if (pendingId != id) return null;
            if (reason is not null) { block = reason; return null; }
            pendingId = null; block = null;
        }
        return replay;
    }
}
