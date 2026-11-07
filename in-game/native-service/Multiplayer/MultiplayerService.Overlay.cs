namespace AimMod.InGame.Multiplayer;

sealed record OverlayPlayer(string Id, string Name, int Team, string Role, bool Ready, bool Away, string Connection, string? Avatar);
sealed record OverlayLobby(string Id, string Mode, long? ReadyCheck, long? AutoStartAt, OverlayPlayer[] Players);
sealed record OverlayMultiplayer(long Now, OverlayPlayer Self, OverlayLobby? Lobby, MatchSnapshot? Match, CsView? Cs,
    IReadOnlyList<MultiplayerService.CsFeedLine> KillFeed, double? BombSeconds);

sealed partial class MultiplayerService
{
    string? OverlayPicture(string id) => AvatarUrl(id) is { } url ? "overlay-avatar/" + url.Split('/').Last() : null;
    public byte[]? OverlayAvatar(string name) { lock (gate) return steamAvatars.File(name)?.Png; }
    public OverlayMultiplayer OverlayView()
    {
        lock (gate)
        {
            var lobby = Current; var match = lobby?.Match; var cs = match?.Cs; var now = clock() + HostOffset();
            OverlayPlayer Player(LobbyMember m) => new(m.Id, m.Name, m.Team, m.Role, m.Ready, m.Away, m.Connection, OverlayPicture(m.Id));
            var self = lobby?.Members.FirstOrDefault(m => m.Id == SelfId);
            var own = self is null ? new OverlayPlayer(SelfId, LocalName(), 0, "player", false, false, "local", OverlayPicture(SelfId)) : Player(self);
            string Name(string? id) => lobby?.Members.FirstOrDefault(m => m.Id == id)?.Name ?? "Player";
            var kills = cs?.Events.Where(e => e.Kind == "kill").TakeLast(20).Select(e => {
                var parts = (e.Text ?? "").Split('\t'); var killer = parts.ElementAtOrDefault(0);
                return new CsFeedLine(e.Id, Name(killer), Name(e.Member), CsRules.FindAny(parts.ElementAtOrDefault(1) ?? "")?.Label ?? "", parts.ElementAtOrDefault(2) == "1", killer == SelfId ? "killer" : e.Member == SelfId ? "victim" : null,
                    cs.Players.FirstOrDefault(p => p.Member == killer)?.Team ?? 0, parts.ElementAtOrDefault(3) == "TK");
            }).ToArray() ?? [];
            return new(now, own, lobby is null ? null : new(lobby.Id, lobby.Settings.Mode, lobby.ReadyCheck, lobby.AutoStartAt, lobby.Members.Select(Player).ToArray()), match, cs, kills,
                cs?.Bomb is { State: "planted", ExplodesAt: { } at } ? Math.Max(0, (at - now) / 1000.0) : null);
        }
    }
}
