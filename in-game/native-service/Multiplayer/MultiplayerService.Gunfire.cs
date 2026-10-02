using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// Other players' gunfire and footsteps (GunAudio.cs) in the shooting modes. Each shot's time comes
// from the shooter's own stream: a player's game lists every shot it fires (self-shots.tsv, hits and
// misses), which their service sends to the host ("fired"); a bot's shots are the host's own
// decisions (BotBrain's BotFire). The host checks each against the shooter's weapon and passes it on
// to everyone at once ("shots"); every machine plays the others' shots at their own times, from
// where they were fired, and the footsteps of the players its game draws.
sealed partial class MultiplayerService
{
    readonly ShotLog shotLog = new();
    readonly GunSoundPlan gunSounds = new();
    readonly FootstepPlan footsteps = new();
    long shotsPushed; string? shotsPushedKey;
    internal ShotLog ShotsForTest => shotLog;
    // Test hooks (checks only): the host's bot fires once; this machine's game fired once (local clock).
    internal void BotFireForTest(string bot) { lock (gate) { if (Current?.Match is { } m) { RecordShot(m, bot, clock(), 0, [300, 400, 160]); PushShots(); } } }
    internal void FiredForTest(long localMs) { lock (gate) { if (Current?.Match is { } m) SendFired(m, [new ShotFeed.Shot(localMs, 1, 10, 20, 160, 0, 0, 0, 0, false)], HostOffset()); } }
    static string ShotKey(MatchSnapshot match) => match.Id + "#" + match.Round;

    // The weapon a shot came from (its sound class) and its fire interval, or null for no sound: a slot
    // that never fires (bomb, grenades), a player down, or a CS round not under way.
    internal static (string Weapon, double Interval)? ShotWeapon(MatchSnapshot match, string member, int slot)
    {
        if (match.Combat?.Players.FirstOrDefault(p => p.Member == member) is not { Alive: true }) return null;
        if (match.Cs is not { } cs) return match.Mode == LobbyModes.Instagib ? ("railgun", CombatRules.Railgun.TimeBetweenShots) : ("rifle", CombatRules.Weapon(match.Mode).TimeBetweenShots);
        if (cs.Phase is not ("live" or "planted" or "end") || cs.Players.FirstOrDefault(p => p.Member == member) is not { Alive: true } p) return null;
        if (slot is CsRules.BombSlot or CsRules.GrenadeSlot) return null;
        var weapon = slot == CsRules.StabSlot ? CsRules.Knife : CsRules.InSlot(slot, p.Primary, p.Secondary, cs.Bomb.Carrier == member) ?? CsRules.Find(p.Secondary);
        return weapon is null ? null : (weapon.Id, slot == CsRules.StabSlot ? CsRules.Stab.TimeBetweenShots : weapon.Combat.TimeBetweenShots);
    }

    // Host: a shot to pass on (host clock).
    void RecordShot(MatchSnapshot match, string member, long t, int slot, double[] from, IReadOnlyList<string>? hidden = null)
    {
        if (core is null || !match.Players.Contains(member) || ShotWeapon(match, member, slot) is not { } w) return;
        shotLog.Reset(ShotKey(match));
        shotLog.Record(member, t, w.Weapon, w.Interval, from, hidden, clock());
    }

    // Host: everyone's new shots to every other machine, as soon as they are known.
    void PushShots()
    {
        if (core is null || (shotsPushedKey == shotLog.Key && shotLog.LatestId <= shotsPushed)) return; // nothing new
        if (core.Snapshot() is not { Match: { } m } lobby || shotLog.Key != ShotKey(m)) return;
        if (shotsPushedKey != shotLog.Key) { shotsPushedKey = shotLog.Key; shotsPushed = 0; }
        var fresh = shotLog.Since(shotsPushed).ToArray();
        if (fresh.Length == 0) return;
        shotsPushed = fresh[^1].Id;
        foreach (var chunk in fresh.Chunk(ShotLog.MaxPerMessage))
            foreach (var peer in RemotePeers(lobby)) Send(peer, "shots", new { match = m.Id, round = m.Round, s = chunk });
    }

    // Host: a player's own shots ({match, round, s:[[t, slot, x, y, z]]}, host clock).
    void ReceiveFired(string peer, JsonElement body)
    {
        try
        {
            if (core?.Snapshot().Match is not { } match || body.GetProperty("match").GetString() != match.Id || body.GetProperty("round").GetInt32() != match.Round) return;
            foreach (var row in body.GetProperty("s").EnumerateArray().Take(ShotLog.MaxPerMessage))
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 5 || row.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Number)) continue;
                var slot = row[1].GetInt32();
                if (slot is < 0 or > 7) continue;
                RecordShot(match, peer, row[0].GetInt64(), slot, [row[2].GetDouble(), row[3].GetDouble(), row[4].GetDouble()]);
            }
            PushShots();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException) { }
    }

    // Client: the host's shots.
    void ReceiveShots(JsonElement body)
    {
        try
        {
            if (Current?.Match is not { } match || body.GetProperty("match").GetString() != match.Id || body.GetProperty("round").GetInt32() != match.Round) return;
            var shots = body.GetProperty("s").Deserialize<GunShot[]>(Protocol.Json) ?? [];
            shotLog.Reset(ShotKey(match));
            shotLog.Receive(shots.Where(s => s is { Member: not null, Weapon: not null, From: not null }));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or JsonException) { }
    }

    // This machine's own shots (every shot its game fired since the last poll), to the host.
    void SendFired(MatchSnapshot match, IEnumerable<ShotFeed.Shot> shots, long offset)
    {
        var rows = shots.Where(s => match.Cs is null || s.Weapon is not (CsRules.BombSlot or CsRules.GrenadeSlot))
            .Select(s => new double[] { s.UnixMs + offset, s.Weapon, Math.Round(s.X, 1), Math.Round(s.Y, 1), Math.Round(s.Z, 1) }).ToArray();
        if (rows.Length == 0) return;
        if (core is not null) { foreach (var r in rows) RecordShot(match, SelfId, (long)r[0], (int)r[1], [r[2], r[3], r[4]]); return; }
        if (hostPeer is null) return;
        foreach (var chunk in rows.Chunk(ShotLog.MaxPerMessage)) Send(hostPeer, "fired", new { match = match.Id, round = match.Round, s = chunk });
    }

    // Host: its bots' trigger pulls. A wall is known to hide a bot from the players its game traced
    // this step and didn't see (its line traces); those hear the shot muffled.
    void RecordBotFire(MatchSnapshot match, BotStep step, long now)
    {
        foreach (var f in step.Fired ?? [])
        {
            var order = step.Orders.FirstOrDefault(o => o.Member == f.Bot);
            List<string>? hidden = null;
            if (order is not null && botSight.TryGetValue(f.Bot, out var sight) && now - sight.At <= BotBrain.SightFreshMs)
                hidden = order.Sight.Where(s => !sight.Visible.Contains(s.Tag) && s.Tag >= 0 && s.Tag < match.Players.Count).Select(s => match.Players[s.Tag]).Where(m => m != f.Target).ToList();
            RecordShot(match, f.Bot, f.T, f.Slot, f.From, hidden);
        }
    }

    // Every machine, each tick of a shooting match: the others' shots and footsteps.
    void GunfireSounds(MatchSnapshot match)
    {
        var key = ShotKey(match);
        shotLog.Reset(key);
        roundAudio.SetGunVolume(prefs.GunVolume);
        (double X, double Y, double Yaw)? listener = ownRecent.Count > 0 ? (ownRecent[^1].X, ownRecent[^1].Y, ownRecent[^1].Yaw) : null;
        var now = clock(); var offset = HostOffset();
        foreach (var cue in gunSounds.Update(key, shotLog.Recent, SelfId, now, offset)) roundAudio.PlayAt(cue, listener);
        if (poseTracker is null || LiveCombat(match) is not { } view) return;
        var selfTeam = view.Players.FirstOrDefault(p => p.Member == SelfId)?.Team ?? 0;
        var avatars = poseTracker.LastSeen.Values.Where(v => v.Member is { } m && m != SelfId).GroupBy(v => v.Member!).Select(g => g.First())
            .Select(v => view.Players.FirstOrDefault(p => p.Member == v.Member) is { } p ? (v.Member!, v.T, v.X, v.Y, v.Z, v.HalfHeight, p.Alive, selfTeam != 0 && p.Team == selfTeam) : default)
            .Where(a => a.Item1 is not null).ToList();
        foreach (var cue in footsteps.Update(key, avatars, listener, now + offset, offset)) roundAudio.PlayAt(cue, listener);
    }
}
