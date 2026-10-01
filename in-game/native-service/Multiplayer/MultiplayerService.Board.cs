namespace AimMod.InGame.Multiplayer;

// Live standings for the corner panel, the hold-to-show scoreboard and OBS.
sealed partial class MultiplayerService
{
    public static readonly string[] ScoreboardKeys = ["Tab", "CapsLock", "Tilde"];
    volatile bool boardHeld;

    // The scoreboard key is held down (MultiplayerHotkey, only while KovaaK's has focus).
    public void ScoreboardHeld(bool held) => boardHeld = held;
    public bool BoardArmed { get { lock (gate) return Current?.Match is not null; } }

    // The current match's standings, or null outside a match.
    public Board? BoardView()
    {
        lock (gate)
        {
            if (Current is not { Match: { } m } lobby) return null;
            var hostNow = clock() + (core is null && hostPeer is not null ? clocks.GetValueOrDefault(hostPeer)?.Offset ?? 0 : 0);
            return Standings.Build(lobby, m, SelfId, hostNow, LiveCombat(m));
        }
    }

    // For the notice layer: the compact corner panel (when wanted) and the full board while the key is held.
    (Board? Compact, Board? Full) NoticeBoards()
    {
        if (BoardView() is not { } board) return (null, null);
        var corner = prefs.ShowBoard && board.Phase is MatchPhases.Countdown or MatchPhases.Live or MatchPhases.Round ? Standings.Compact(board) : null;
        return (corner, boardHeld ? board : null);
    }
}
