namespace AimMod.InGame.Multiplayer;

// Keeps aimmod-session.txt (SessionMarker) in step with this player's AimMod session:
// lobby while in a lobby, match while playing a generated match scenario, spectate
// while watching one. Nothing is written for normal scenarios. The file is rewritten
// every 30 s (expires in 90 s) and at once when the mode or scenario changes, and
// deleted on leaving, when the lobby closes, when the Steam link to the host is
// gone, at shutdown and at startup.
sealed partial class MultiplayerService
{
    string? markerKey;
    long markerWrittenAt = long.MinValue / 2;

    string? MarkerPath => outputFolder is null ? null : Path.Combine(outputFolder, SessionMarker.FileName);

    (string Mode, string? Scenario)? WantedMarker()
    {
        // Spectating a friend without a lobby: only when they play an AimMod match scenario.
        if (Current is null && watch is { State: "watching" or "loading" or "manual" } w)
            return SessionMarker.ValidScenario(w.Scenario) ? ("spectate", w.Scenario) : null;
        if (Current is not { } lobby) return null;
        // The Steam link to a remote host is gone: no session to vouch for.
        if (hostPeer is not null && !transport.Available) return null;
        var s = lobby.Settings;
        var name = s.Scenario is not null && MatchScenario.Needed(s) ? MatchScenario.Name(s) : null;
        if (lobby.Match is { Phase: not MatchPhases.Final } match)
        {
            if (match.Players.Contains(SelfId)) return name is null ? null : ("match", name);
            if (spectating is not null) return name is null ? null : ("spectate", name);
        }
        return ("lobby", null);
    }

    void UpdateSessionMarker(bool force = false)
    {
        if (MarkerPath is not { } path) return;
        var want = WantedMarker();
        var key = want is { } k ? k.Mode + "|" + k.Scenario : null;
        var now = clock();
        if (key is null) { if (markerKey is not null || force) DeleteSessionMarker(); return; }
        if (!force && key == markerKey && now - markerWrittenAt < SessionMarker.RefreshSeconds * 1000L) return;
        var text = SessionMarker.Format(want!.Value.Mode, want.Value.Scenario, now / 1000 + SessionMarker.LifetimeSeconds);
        if (text is null) { DeleteSessionMarker(); return; }
        try { AtomicFile.WriteText(path, text); markerKey = key; markerWrittenAt = now; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    void DeleteSessionMarker()
    {
        markerKey = null;
        DeleteLooks();
        if (MarkerPath is not { } path) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
