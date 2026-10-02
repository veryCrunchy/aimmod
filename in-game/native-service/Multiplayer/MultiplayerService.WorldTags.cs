namespace AimMod.InGame.Multiplayer;

// world-tags.tsv for AimModCore's name tags (native-mod/DESIGN.md "World tags"): who each
// avatar stream is, friend or enemy, the team colour and alive. AimModCore draws teammates'
// names over their heads (through walls, like CS) and an enemy's name only under the
// crosshair in line of sight. Rewritten on change and every second (stale after 5 s).
//   AIMMOD_TAGS_1\t<seq>
//   tag\t<stream id>\t<friend|enemy>\t<T|CT|1|2|0>\t<alive 0/1>\t<name, percent-escaped>[\t<gear, percent-escaped>]
//   gear, CS teammates only: w=<weapon in hand>;hp=<health>;ar=1 armour;hm=1 helmet;kit=1;c4=1 has the bomb.
sealed partial class MultiplayerService
{
    string? lastWorldTags; long worldTagsAt, worldTagsSequence;

    internal static string WorldTagsBody(IEnumerable<(string Stream, bool Friend, string Team, bool Alive, string Name)> rows) =>
        WorldTagsBody(rows.Select(r => (r.Stream, r.Friend, r.Team, r.Alive, r.Name, (string?)null)));
    internal static string WorldTagsBody(IEnumerable<(string Stream, bool Friend, string Team, bool Alive, string Name, string? Gear)> rows) =>
        string.Concat(rows.Take(32).Select(r => "tag\t" + r.Stream + "\t" + (r.Friend ? "friend" : "enemy") + "\t" + r.Team + "\t" + (r.Alive ? 1 : 0) + "\t" + Uri.EscapeDataString(r.Name)
            + (string.IsNullOrEmpty(r.Gear) ? "" : "\t" + Uri.EscapeDataString(r.Gear)) + "\n"));

    // What a CS teammate's tag shows under the name: the weapon in hand, health, armour, helmet,
    // defuse kit and the bomb. Null for enemies (as in CS) and outside CS.
    internal static string? CsGear(CsView? cs, string member, bool friend)
    {
        if (!friend || cs?.Players.FirstOrDefault(x => x.Member == member) is not { Alive: true } p) return null;
        var parts = new List<string>();
        if (CsRules.FindAny(p.Holding) is { } held) parts.Add("w=" + held.Label);
        parts.Add("hp=" + Math.Max(0, (int)Math.Round(p.Health)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (p.Armor > 0) parts.Add("ar=1");
        if (p.Helmet) parts.Add("hm=1");
        if (p.Kit) parts.Add("kit=1");
        if (cs.Bomb is { State: "carried" } bomb && bomb.Carrier == member) parts.Add("c4=1");
        return string.Join(';', parts);
    }

    void WriteWorldTags(MatchSnapshot match, CombatView view, CombatPlayerView self)
    {
        if (outputFolder is null || Current is not { } lobby) return;
        string Name(string id) => LobbyRules.CleanName(lobby.Members.FirstOrDefault(m => m.Id == id)?.Name, "Player");
        var cs = match.Cs;
        string TeamOf(CombatPlayerView p) => cs?.Players.FirstOrDefault(x => x.Member == p.Member)?.Side ?? (p.Team is 1 or 2 ? p.Team.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0");
        var body = WorldTagsBody(view.Players.Where(p => p.Member != SelfId).Select(p =>
        {
            var friend = self.Team != 0 && p.Team == self.Team;
            return (StreamIds.For(AvatarPeer(p.Member)), friend, TeamOf(p), p.Alive, Name(p.Member), CsGear(cs, p.Member, friend));
        }));
        var now = clock();
        if (body == lastWorldTags && now - worldTagsAt < 1000) return;
        lastWorldTags = body; worldTagsAt = now;
        try { AtomicFile.WriteText(Path.Combine(outputFolder, "world-tags.tsv"), "AIMMOD_TAGS_1\t" + ++worldTagsSequence + "\n" + body); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
