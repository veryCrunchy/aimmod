namespace AimMod.InGame.Multiplayer;

// world-tags.tsv for AimModCore's name tags (native-mod/DESIGN.md "World tags"): who each
// avatar stream is, friend or enemy, the team colour and alive. AimModCore draws teammates'
// names over their heads (through walls, like CS) and an enemy's name only under the
// crosshair in line of sight. Rewritten on change and every second (stale after 5 s).
//   AIMMOD_TAGS_1\t<seq>
//   tag\t<stream id>\t<friend|enemy>\t<T|CT|1|2|0>\t<alive 0/1>\t<name, percent-escaped>
sealed partial class MultiplayerService
{
    string? lastWorldTags; long worldTagsAt, worldTagsSequence;

    internal static string WorldTagsBody(IEnumerable<(string Stream, bool Friend, string Team, bool Alive, string Name)> rows) =>
        string.Concat(rows.Take(32).Select(r => "tag\t" + r.Stream + "\t" + (r.Friend ? "friend" : "enemy") + "\t" + r.Team + "\t" + (r.Alive ? 1 : 0) + "\t" + Uri.EscapeDataString(r.Name) + "\n"));

    void WriteWorldTags(MatchSnapshot match, CombatView view, CombatPlayerView self)
    {
        if (outputFolder is null || Current is not { } lobby) return;
        string Name(string id) => LobbyRules.CleanName(lobby.Members.FirstOrDefault(m => m.Id == id)?.Name, "Player");
        var cs = match.Cs;
        string TeamOf(CombatPlayerView p) => cs?.Players.FirstOrDefault(x => x.Member == p.Member)?.Side ?? (p.Team is 1 or 2 ? p.Team.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0");
        var body = WorldTagsBody(view.Players.Where(p => p.Member != SelfId).Select(p =>
            (StreamIds.For(AvatarPeer(p.Member)), self.Team != 0 && p.Team == self.Team, TeamOf(p), p.Alive, Name(p.Member))));
        var now = clock();
        if (body == lastWorldTags && now - worldTagsAt < 1000) return;
        lastWorldTags = body; worldTagsAt = now;
        try { AtomicFile.WriteText(Path.Combine(outputFolder, "world-tags.tsv"), "AIMMOD_TAGS_1\t" + ++worldTagsSequence + "\n" + body); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
