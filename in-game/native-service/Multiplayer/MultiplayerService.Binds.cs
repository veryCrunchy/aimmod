namespace AimMod.InGame.Multiplayer;

// KovaaK's binds the lobby's match relies on (GameBinds): a Keybinds row in the lobby, a line
// on the load screen and countdown, and one short in-game notice per match when a used action
// has no key or not its usual one. Information only: nothing is rebound and nothing waits on it.
sealed partial class MultiplayerService
{
    // The player's binds (GameBinds.Current in the running service); unset reads as KovaaK's defaults.
    public Func<GameBinds.Bound>? Binds { get; set; }
    (string Key, IReadOnlyList<GameBinds.Use> Uses)? usesCache;
    (string Match, long At)? bindsShown;
    string? bindsDismissed;
    const long BindsNoticeMs = 8000;

    // The scenario the match plays: the generated match scenario once written, else the base.
    IReadOnlyList<GameBinds.Use> MatchUses(LobbySettings s)
    {
        if (s.Scenario is null) return [];
        string? path = null;
        if (MatchScenario.Needed(s) && library.ScenarioFolder is { } folder && Path.Combine(folder, MatchScenario.Name(s) + ".sce") is var generated && File.Exists(generated)) path = generated;
        path ??= library.PathOf("scenario", s.Scenario.Name);
        if (path is null) return [];
        try
        {
            var key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks + "|" + s.Mode;
            if (usesCache is { } c && c.Key == key) return c.Uses;
            var text = string.Join('\n', File.ReadLines(path).Take(20_000).TakeWhile(l => l != "[Map Data]"));
            var uses = GameBinds.Uses(text, s.Mode);
            usesCache = (key, uses);
            return uses;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    IReadOnlyList<GameBinds.Row> BindRows(LobbySettings s)
    {
        var uses = MatchUses(s);
        if (uses.Count == 0) return [];
        var own = new List<(string, string)>();
        if (s.Mode == LobbyModes.Cs) { own.Add(("Plant / defuse", CsUseKey)); own.Add(("Buy", CsBuyKey)); }
        own.Add(("Scoreboard", prefs.ScoreboardKey));
        return GameBinds.Rows(uses, Binds?.Invoke() ?? GameBinds.Bound.Standard, own);
    }

    IReadOnlyList<string> BindIssues(LobbySettings s) => GameBinds.Issues(BindRows(s));
    string? BindNote(LobbySettings s) => BindIssues(s) is { Count: > 0 } issues ? string.Join(" · ", issues.Take(3)) : null;

    object? BindsView(LobbySettings s)
    {
        var rows = BindRows(s);
        return rows.Count == 0 ? null : new { rows = rows.Select(r => new { label = r.Label, keys = r.Keys, usual = r.Usual, standard = r.Standard, move = r.Move, aimmod = r.AimMod }), issues = GameBinds.Issues(rows) };
    }

    // Shown once per match, from its first countdown (CS) or live moment, for a few seconds; the lobby key hides it.
    GameNotice? BindsNotice(LobbySnapshot lobby, long now)
    {
        if (lobby.Match is not { Phase: MatchPhases.Countdown or MatchPhases.Live } m || !m.Players.Contains(SelfId) || bindsDismissed == m.Id) return null;
        if (bindsShown is { } shown && shown.Match == m.Id && now - shown.At >= BindsNoticeMs) return null;
        if (BindIssues(lobby.Settings) is not { Count: > 0 } issues) return null;
        if (bindsShown?.Match != m.Id) bindsShown = (m.Id, now);
        return new GameNotice("keys-" + m.Id, "info", issues[0], string.Join(" · ", issues.Skip(1).Take(2)), HotkeyName, null, "none") { Eyebrow = "AimMod · Keybinds" };
    }
}
