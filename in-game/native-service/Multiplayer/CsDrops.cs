namespace AimMod.InGame.Multiplayer;

sealed record CsDropView(long Id, string Owner, string Weapon, double X, double Y, double Z, long AvailableAt);

sealed partial class CsMatch
{
    readonly List<CsDropView> drops = [];
    public ISet<string> Bots { get; } = new HashSet<string>();
    long dropId, damageCursor;
    public const double WeaponPickupCm = 80;
    public const int MaxDroppedItems = 32;

    void CollectDamage()
    {
        foreach (var e in Combat.EventsSince(damageCursor))
        {
            damageCursor = Math.Max(damageCursor, e.Id);
            if (Phase != "warmup" && e.Kind == "damage" && e.Attacker is { } attacker && players.TryGetValue(attacker, out var p)
                && players.TryGetValue(e.Member, out var victim) && victim.Team != p.Team) p.EnemyDamage += e.Amount;
        }
    }

    void AddDrop(P p, string weapon, long now)
    {
        if (Combat.Position(p.Id) is not { } at) return;
        if (drops.Count >= MaxDroppedItems) drops.RemoveAt(0);
        drops.Add(new(++dropId, p.Id, weapon, at.X, at.Y, Centre(p.Id, at), now + DropBlockMs));
        Event("weapon-dropped", now, p.Id, weapon);
    }

    void DropOnDeath(P p, long now)
    {
        if (Phase is not ("live" or "planted")) return;
        if ((p.Primary ?? p.Secondary) is { } weapon) AddDrop(p, weapon.Id, now);
        if (p.Kit) AddDrop(p, "defuse-kit", now);
    }

    public string? DropWeapon(string id, long now)
    {
        if (!players.TryGetValue(id, out var p) || !Combat.Alive(id)) return "dead";
        if (Phase is not ("warmup" or "freeze" or "live" or "planted")) return "not-now";
        if (Combat.Position(id) is null) return "no-track";
        var weapon = Holding(p);
        if (weapon.Slot is not (CsRules.PrimarySlot or CsRules.PistolSlot)) return "no-weapon";
        AddDrop(p, weapon.Id, now);
        if (weapon.Slot == CsRules.PrimarySlot) p.Primary = null; else p.Secondary = null;
        p.Held = CsRules.KnifeSlot;
        return null;
    }

    bool TryPickup(string id, long now, bool swap)
    {
        if (Phase is not ("warmup" or "freeze" or "live" or "planted") || !players.TryGetValue(id, out var p)
            || !Combat.Alive(id) || Combat.Position(id) is not { } at) return false;
        foreach (var d in drops.OrderBy(d => Math.Pow(d.X - at.X, 2) + Math.Pow(d.Y - at.Y, 2)).ToArray())
        {
            if (d.Owner == id && now < d.AvailableAt || Math.Pow(d.X - at.X, 2) + Math.Pow(d.Y - at.Y, 2) > WeaponPickupCm * WeaponPickupCm
                || Math.Abs(Centre(id, at) - d.Z) > 120) continue;
            if (d.Weapon == "defuse-kit")
            {
                if (p.Kit || SideOf(id) != CsRules.CT) continue;
                p.Kit = true;
            }
            else
            {
                if (CsRules.Find(d.Weapon) is not { } weapon) continue;
                var owned = weapon.Slot == CsRules.PrimarySlot ? p.Primary : p.Secondary;
                if (owned is not null && !swap && ((!Bots.Contains(id) && !Controlled(id)) || weapon.Price <= owned.Price)) continue;
                if (owned is not null) AddDrop(p, owned.Id, now);
                if (weapon.Slot == CsRules.PrimarySlot) p.Primary = weapon; else p.Secondary = weapon;
            }
            drops.Remove(d);
            Event("weapon-picked", now, id, d.Weapon);
            return true;
        }
        return false;
    }
}
