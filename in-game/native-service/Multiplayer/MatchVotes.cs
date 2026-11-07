namespace AimMod.InGame.Multiplayer;

sealed record MatchVoteView(string Kind, string Label, int Team, string? Target, long EndsAt,
    int Required, IReadOnlyList<string> Eligible, IReadOnlyList<string> Yes, IReadOnlyList<string> No);

sealed class MatchVote
{
    public string Kind { get; }
    public string Label { get; }
    public int Team { get; }
    public string? Target { get; }
    public long EndsAt { get; }
    readonly HashSet<string> eligible, yes = [], no = [];
    public int Required { get; }
    public MatchVote(string kind, string label, int team, string? target, IEnumerable<string> humans, long now)
    {
        Kind = kind; Label = label; Team = team; Target = target; EndsAt = now + 20_000;
        eligible = humans.ToHashSet(StringComparer.Ordinal);
        Required = kind == "surrender" ? Math.Max(1, eligible.Count - (eligible.Count >= 4 ? 1 : 0)) : eligible.Count / 2 + 1;
    }
    public bool Cast(string id, bool approve)
    {
        if (!eligible.Contains(id) || yes.Contains(id) || no.Contains(id)) return false;
        (approve ? yes : no).Add(id); return true;
    }
    public bool Passed => yes.Count >= Required;
    public bool Finished(long now) => Passed || now >= EndsAt || eligible.Count - no.Count < Required;
    public MatchVoteView View() => new(Kind, Label, Team, Target, EndsAt, Required, eligible.ToArray(), yes.ToArray(), no.ToArray());
}
