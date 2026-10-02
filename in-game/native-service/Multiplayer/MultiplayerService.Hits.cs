using System.Globalization;
using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// Hit registration plumbing (game-modes.md 6.2.2). A shooter's claims go to the host as "hit"
// messages and stay in the outbox until the host's "hit-ack" answers them (resent otherwise; the
// host answers a repeat with its first decision). The host decides every claim (CombatMatch),
// sends the decision back and logs each refusal with what the check measured, so a live test
// shows exactly why a hit that showed a hitmarker didn't count.
sealed partial class MultiplayerService
{
    readonly ClaimOutbox outbox = new();
    // For the checks: what the shot feed saw, and (host) every shooter's claim outcomes.
    internal ShotFeed.Counters? ShotStats => shotFeed?.Stats;
    internal IReadOnlyDictionary<string, (int Claims, int Accepted, int Pending, int Duplicates, IReadOnlyDictionary<string, int> Reasons)> HostClaimStats => core?.ClaimStats() ?? new Dictionary<string, (int, int, int, int, IReadOnlyDictionary<string, int>)>();
    long hitStatsLoggedAt = long.MinValue / 2;
    // Host: what this machine's own shots got (it decides them itself).
    int ownAccepted; readonly Dictionary<string, int> ownRejected = new();

    // A claim of this machine's player: decided here (host) or sent and kept until confirmed.
    void SendClaim(HitClaim claim)
    {
        if (core is not null) { core.Claim(SelfId, claim); ConfirmClaims(); return; }
        if (hostPeer is null) return;
        outbox.Add(claim, clock());
        Send(hostPeer, "hit", claim.Body());
    }

    // Client: claims the host hasn't answered, sent again.
    void ResendClaims()
    {
        if (core is not null || hostPeer is null) return;
        foreach (var claim in outbox.Due(clock())) Send(hostPeer, "hit", claim.Body());
    }

    // Host: every decision since the last call goes back to its shooter and into the log.
    // True when one of them was a hit (the combat events then go out at once).
    bool ConfirmClaims()
    {
        if (core is null) return false;
        var decided = core.TakeClaimVerdicts();
        if (decided.Count == 0) return false;
        var landed = false;
        var match = core.Snapshot().Match;
        foreach (var v in decided)
        {
            landed |= v.Reason is null && !v.Duplicate;
            if (!v.Duplicate)
            {
                if (v.Reason is not null)
                    Console.Error.WriteLine("Hit refused: " + Who(v.Shooter) + " claim #" + v.Seq + " (shot #" + v.Shot + ") " + v.Reason + (v.Detail is null ? "" : ": " + v.Detail)
                        + (v.WaitedMs > 0 ? " (waited " + v.WaitedMs + " ms for the shooter's track)" : ""));
                if (v.Shooter == SelfId)
                {
                    if (v.Reason is null) ownAccepted++;
                    else ownRejected[v.Reason] = ownRejected.GetValueOrDefault(v.Reason) + 1;
                }
            }
            if (v.Shooter == SelfId || match is null || core.Members.All(m => m.Id != v.Shooter || Offline(m))) continue;
            Send(v.Shooter, "hit-ack", new
            {
                match = match.Id, round = match.Round, seq = v.Seq, shot = v.Shot, ok = v.Reason is null, reason = v.Reason, detail = v.Detail,
                victim = v.Victim, damage = v.Damage, head = v.Head, kill = v.Kill, dup = v.Duplicate,
            });
        }
        return landed;
    }

    string Who(string member) => (member == SelfId ? selfName : core?.Members.FirstOrDefault(m => m.Id == member)?.Name ?? mirror?.Members.FirstOrDefault(m => m.Id == member)?.Name) ?? "player";

    // Client: the host's answer to one of our claims.
    void ReceiveHitAck(JsonElement body)
    {
        try
        {
            if (Current?.Match is not { } match || body.GetProperty("match").GetString() != match.Id) return;
            var seq = body.GetProperty("seq").GetInt64();
            var ok = body.GetProperty("ok").ValueKind == JsonValueKind.True;
            var reason = ok ? null : body.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : "refused";
            if (!outbox.Confirm(seq, reason) || ok) return;
            var detail = body.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            var shot = body.TryGetProperty("shot", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
            Console.Error.WriteLine("Hit refused by the host: claim #" + seq + " (shot #" + shot + ") " + reason + (detail is { Length: > 0 and <= 300 } ? ": " + detail : ""));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException) { }
    }

    // Every 10 s of a live combat match: shots read, game hits, claims and their outcomes.
    void LogHitStats(MatchSnapshot match)
    {
        var now = clock();
        if (now - hitStatsLoggedAt < 10_000 || shotFeed is null) return;
        hitStatsLoggedAt = now;
        var f = shotFeed.Stats;
        if (f.Shots == 0 && outbox.Sent == 0) return;
        static string Reasons(IReadOnlyDictionary<string, int> r) => r.Count == 0 ? "none" : string.Join(", ", r.OrderByDescending(x => x.Value).Select(x => x.Key + " " + x.Value.ToString(CultureInfo.InvariantCulture)));
        var line = "Hits: " + f.Shots + " shots read (" + f.GameHits + " game hits, " + f.GameHitNoTarget + " of them without a drawn target, " + f.RayOnly + " ray-only not claimed, "
            + f.Lost + " lost before reading, " + f.Stale + " stale), " + f.Claims + " claims";
        if (core is not null)
        {
            line += "; decided here: " + ownAccepted + " accepted, refused: " + Reasons(ownRejected);
            if (core.ClaimStats() is { Count: > 0 } all)
                line += "; all shooters: " + string.Join("; ", all.Select(kv => Who(kv.Key) + " " + kv.Value.Accepted + "/" + kv.Value.Claims + " accepted" + (kv.Value.Pending > 0 ? ", " + kv.Value.Pending + " waiting" : "")
                    + (kv.Value.Duplicates > 0 ? ", " + kv.Value.Duplicates + " repeats" : "") + (kv.Value.Reasons.Count > 0 ? " (" + Reasons(kv.Value.Reasons) + ")" : "")));
        }
        else line += "; sent " + outbox.Sent + ", resent " + outbox.Resent + ", confirmed " + outbox.Confirmed + " (accepted " + outbox.Accepted + ", refused: " + Reasons(outbox.Rejected) + "), waiting " + outbox.Open + ", unanswered " + outbox.Unanswered;
        Console.Error.WriteLine(line);
    }
}
