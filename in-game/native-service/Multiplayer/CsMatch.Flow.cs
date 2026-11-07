namespace AimMod.InGame.Multiplayer;

sealed partial class CsMatch
{
    public bool Paused { get; private set; }
    long pauseAt;
    readonly bool[] timeoutUsed = new bool[2], timeoutQueued = new bool[2];
    public long? TimeoutUntil { get; private set; }
    readonly HashSet<string> warmupReady = [];
    readonly Dictionary<string, long> warmupRespawns = [];

    public void Surrender(int team, long now)
    {
        if (team is 1 or 2 && !Over) { drops.Clear(); Grenades.Clear(); Finish(now, 3 - team); }
    }

    public string? QueueTimeout(int team, long now)
    {
        if (team is not (1 or 2) || Over || Phase == "warmup") return "not-now";
        if (timeoutUsed[team - 1]) return "timeout-used";
        timeoutUsed[team - 1] = timeoutQueued[team - 1] = true;
        Event("timeout-queued", now, null, null, team);
        return null;
    }

    void ApplyTimeouts(long now)
    {
        var count = timeoutQueued.Count(x => x);
        TimeoutUntil = count > 0 ? now + count * 30_000 : null;
        PhaseEndsAt += count * 30_000;
        Array.Clear(timeoutQueued);
    }

    public void SetPaused(bool value, long now)
    {
        if (Over || Paused == value) return;
        if (Paused) ShiftPause(now);
        if (value) Combat.CancelPending(now);
        Paused = value; pauseAt = now;
        Event(value ? "paused" : "unpaused", now, null, null);
    }

    void ShiftPause(long now)
    {
        var delta = Math.Max(0, now - pauseAt); pauseAt = now;
        PhaseEndsAt += delta; LiveAt += delta; explodesAt += delta;
        plantDoneAt += delta; defuseDoneAt += delta; TimeoutUntil += delta;
        dropperBlockedUntil += delta;
        for (var i = 0; i < drops.Count; i++) drops[i] = drops[i] with { AvailableAt = drops[i].AvailableAt + delta };
        foreach (var id in warmupRespawns.Keys.ToArray()) warmupRespawns[id] += delta;
        foreach (var p in players.Values) if (p.Flash is { } flash) p.Flash = flash with { At = flash.At + delta };
        Grenades.ShiftTime(delta);
    }

    public void BeginWarmup(long now)
    {
        Combat.CountScores = false;
        Phase = "warmup"; Round = 0; PhaseEndsAt = now + 60_000; LiveAt = now;
        carrier = null; bombState = "none"; warmupReady.Clear();
        foreach (var p in players.Values) p.Money = CsRules.MaxMoney;
        Event("warmup", now, null, null);
    }

    public void ReadyWarmup(string id, bool ready)
    {
        if (Phase != "warmup" || !players.ContainsKey(id)) return;
        if (ready) warmupReady.Add(id); else warmupReady.Remove(id);
    }

    bool TickWarmup(long now)
    {
        if (Phase != "warmup") return false;
        CollectDamage(); Grenades.Tick(now);
        foreach (var p in players.Values) TryPickup(p.Id, now, false);
        PickUpGrenades();
        foreach (var (id, at) in warmupRespawns.ToArray())
            if (now >= at && players.TryGetValue(id, out var p) && !p.Spectating)
            {
                Combat.Revive(id, now, CsRules.MaxHealth); p.SpawnSerial++;
                p.Armor = 0; p.Helmet = p.Kit = false; p.Flash = null;
                warmupRespawns.Remove(id);
            }
        if (now < PhaseEndsAt && players.Values.Any(p => !p.Spectating && !Bots.Contains(p.Id) && !warmupReady.Contains(p.Id))) return true;
        CollectDamage(); warmupRespawns.Clear(); warmupReady.Clear();
        foreach (var p in players.Values)
        {
            p.Money = CsRules.StartMoney; p.Primary = p.Secondary = null;
            p.Armor = 0; p.Helmet = p.Kit = false; p.Grenades.Clear(); p.Held = -1;
        }
        Combat.CountScores = true;
        Round = 0; StartRound(now);
        return true;
    }

    public bool Controlled(string id) => players.TryGetValue(id, out var p) && p.Afk;
    public void Control(string id, bool value, long now)
    {
        if (!players.TryGetValue(id, out var p) || p.Spectating || p.Afk == value) return;
        p.Afk = value;
        if (Combat.Position(id) is { } at)
        {
            roundSpawns[id] = [at.X, at.Y, Centre(id, at), at.Yaw]; p.SpawnSerial++;
        }
        Event(value ? "afk" : "returned", now, id, null);
    }

    public void Spectate(string id, long now)
    {
        if (!players.TryGetValue(id, out var p)) return;
        p.Spectating = true; p.Afk = false; Leave(id, now);
        Event("spectating", now, id, null);
    }
}
