namespace AimMod.InGame.Multiplayer;

// Bot grenades (game-modes.md 6.6.5): what host-run bots buy and throw, kept apart from BotBrain so
// the bot logic can take it over. The service runs one IBotGrenadePolicy after BotBrain each tick
// (MultiplayerService.StepBotGrenades); its buys go through the lobby like a bot's other buys, its
// throws through CsMatch.BotThrow (a grenade the bot carries, at most a full throw).
//  - Replace the policy: set MultiplayerService.BotGrenadePolicy to your own IBotGrenadePolicy.
//  - Or use the parts: BotGrenades.Buys (what to buy with the money left), GrenadeAim.Lob/Timed (the
//    velocity that lands a grenade on a point, or has it there when it goes off) and
//    BotGrenades.Plan (this policy's throw for one bot now, or null).
interface IBotGrenadePolicy
{
    BotGrenadeStep Step(BotWorld world);
}
// A throw: the bot, the grenade, its eye and the velocity.
sealed record BotGrenadeThrow(string Bot, string Kind, double[] From, double[] Velocity, double[] Target);
sealed record BotGrenadeStep(IReadOnlyList<(string Bot, string Item)> Buys, IReadOnlyList<BotGrenadeThrow> Throws);

// The simple policy: bots buy utility with the money left after their guns and armour (Easy a
// flash, Normal a smoke, a flash and an HE, Hard the full set with a fire grenade). Terrorists smoke
// the site they push towards from the Counter-Terrorists' side and flash over it; a Terrorist with
// a molotov burns a defuse; Counter-Terrorists smoke the planted bomb to defuse under it and burn a
// plant; anyone with an HE throws it at an enemy they see 7-22 m away. Once per kind a round, one
// throw every 2 s at most.
sealed class BotGrenades : IBotGrenadePolicy
{
    public const long BuyDelayMs = 3000, ThrowGapMs = 2000;
    sealed class State { public int Round = -1; public bool Bought; public HashSet<string> Thrown = []; public long NextThrow; }
    readonly Dictionary<string, State> states = new();

    public static IReadOnlyList<string> Wishlist(string skill, string side) => skill switch
    {
        BotSkills.Easy => [GrenadeRules.Flash],
        BotSkills.Hard => [GrenadeRules.Smoke, GrenadeRules.Flash, side == CsRules.T ? GrenadeRules.Molotov : GrenadeRules.Incendiary, GrenadeRules.He, GrenadeRules.Flash],
        _ => [GrenadeRules.Smoke, GrenadeRules.Flash, GrenadeRules.He],
    };

    // What to buy with `money` on top of what's carried: the wishlist in order, while it's affordable
    // and the carry limits allow; only with a gun bought (or money to spare).
    public static IReadOnlyList<string> Buys(CsPlayerView me, string skill)
    {
        var list = new List<string>(); var money = me.Money; var have = (me.Grenades ?? []).ToList();
        if (me.Primary is null && money < 2500) return list;
        foreach (var kind in Wishlist(skill, me.Side))
        {
            var g = GrenadeRules.Find(kind)!;
            if (g.Price > money || GrenadeRules.CarryProblem(have, kind) is not null) continue;
            list.Add(kind); have.Add(kind); money -= g.Price;
        }
        return list;
    }

    public BotGrenadeStep Step(BotWorld w)
    {
        var buys = new List<(string, string)>(); var throws = new List<BotGrenadeThrow>();
        if (w.Cs is not { } cs) return new(buys, throws);
        foreach (var (member, skill) in w.Bots)
        {
            if (cs.Players.FirstOrDefault(p => p.Member == member) is not { Alive: true } me) continue;
            var st = states.TryGetValue(member, out var s) ? s : states[member] = new State();
            if (st.Round != cs.Round) { st.Round = cs.Round; st.Bought = false; st.Thrown.Clear(); st.NextThrow = 0; }
            // Buy in freeze time, a moment in, after the brain's own buys.
            if (cs.Phase == "freeze" && !st.Bought && cs.PhaseEndsAt - w.Now < CsRules.FreezeMs - BuyDelayMs)
            {
                st.Bought = true;
                foreach (var item in Buys(me, skill)) buys.Add((member, item));
            }
            if (cs.Phase is not ("live" or "planted") || w.Now < st.NextThrow || me.Grenades is not { Count: > 0 }) continue;
            if (Plan(w, cs, me, member, st.Thrown) is { } t)
            {
                throws.Add(t);
                st.Thrown.Add(t.Kind); st.NextThrow = w.Now + ThrowGapMs;
            }
        }
        return new(buys, throws);
    }

    // This policy's throw for one bot now, or null. `thrown`: the kinds it already threw this round.
    public static BotGrenadeThrow? Plan(BotWorld w, CsView cs, CsPlayerView me, string member, IReadOnlySet<string> thrown)
    {
        var self = w.Players.FirstOrDefault(p => p.Member == member);
        var sight = w.Sight.TryGetValue(member, out var seen) && w.Now - seen.At <= BotBrain.SightFreshMs ? seen : null;
        // The eye as the bot brain has it: the player row (its avatar's floor plus eye height), else above the avatar.
        var eye = self is not null ? new[] { self.X, self.Y, self.Z } : sight is not null ? new[] { sight.X, sight.Y, sight.Z + BotBrain.EyeAboveCentre } : null;
        if (eye is null) return null;
        bool Has(string kind) => me.Grenades!.Contains(kind) && !thrown.Contains(kind);
        double Dist(double[] p) => Math.Sqrt((p[0] - eye[0]) * (p[0] - eye[0]) + (p[1] - eye[1]) * (p[1] - eye[1]));
        var floor = eye[2] - GrenadeRules.EyeHeightCm;
        BotGrenadeThrow Lob(string kind, double[] target) => new(member, kind, eye, GrenadeAim.Lob(eye, target), target);
        BotGrenadeThrow Timed(string kind, double[] target, double seconds) => new(member, kind, eye, GrenadeAim.Timed(eye, target, seconds), target);
        var bomb = cs.Bomb;
        var fire = me.Side == CsRules.T ? GrenadeRules.Molotov : GrenadeRules.Incendiary;
        // The bomb: a Terrorist burns a defuse, a Counter-Terrorist burns a plant or smokes the planted bomb.
        if (bomb.State == "planted" && bomb.Position is { Length: 3 } at)
        {
            var spot = new[] { at[0], at[1], at[2] };
            if (me.Side == CsRules.T && bomb.Defuser is not null && Has(fire) && Dist(spot) is > 300 and < 2500) return Lob(fire, spot);
            if (me.Side == CsRules.CT && Has(GrenadeRules.Smoke) && Dist(spot) is > 300 and < 2000) return Lob(GrenadeRules.Smoke, spot);
        }
        if (me.Side == CsRules.CT && bomb.Planter is { } planter && Has(fire) && w.Players.FirstOrDefault(p => p.Member == planter) is { } pl)
        {
            var spot = new[] { pl.X, pl.Y, pl.Z - GrenadeRules.EyeHeightCm };
            if (Dist(spot) is > 400 and < 2500) return Lob(fire, spot);
        }
        // An enemy in sight at mid range: the HE (there when it goes off).
        if (Has(GrenadeRules.He) && sight is not null)
            foreach (var tag in sight.Visible)
                if (w.Players.FirstOrDefault(p => p.Tag == tag) is { Alive: true } target && cs.Players.FirstOrDefault(p => p.Member == target.Member)?.Side is { } side && side != me.Side
                    && Dist([target.X, target.Y]) is > 700 and < 2200)
                    return Timed(GrenadeRules.He, [target.X, target.Y, target.Z - 120], GrenadeRules.HeFuseMs / 1000.0);
        // Terrorists on the way to a site, before the plant: smoke it off from the CT side, then flash over it.
        if (me.Side == CsRules.T && bomb.State != "planted" && cs.Sites is { Count: > 0 } sites)
        {
            var site = sites.OrderBy(x => Dist([x.X, x.Y])).First();
            var d = Dist([site.X, site.Y]);
            var ctSpawns = cs.Spawns?.Where(kv => cs.Players.FirstOrDefault(p => p.Member == kv.Key)?.Side == CsRules.CT).Select(kv => kv.Value).ToList() ?? [];
            if (Has(GrenadeRules.Smoke) && d is > 600 and < 1800)
            {
                // Between the site and the defenders' spawn, so the site can't be watched from there.
                var to = ctSpawns.Count > 0 ? new[] { ctSpawns.Average(p => p[0]), ctSpawns.Average(p => p[1]) } : null;
                var off = to is null ? 0 : Math.Sqrt((to[0] - site.X) * (to[0] - site.X) + (to[1] - site.Y) * (to[1] - site.Y));
                var spot = to is null || off < 1 ? new[] { site.X, site.Y, floor } : new[] { site.X + (to[0] - site.X) / off * Math.Min(700, off * 0.4), site.Y + (to[1] - site.Y) / off * Math.Min(700, off * 0.4), floor };
                return Lob(GrenadeRules.Smoke, spot);
            }
            if (Has(GrenadeRules.Flash) && d is > 500 and < 1500 && (thrown.Contains(GrenadeRules.Smoke) || !me.Grenades!.Contains(GrenadeRules.Smoke)))
                return Timed(GrenadeRules.Flash, [site.X, site.Y, floor + 350], GrenadeRules.FlashFuseMs / 1000.0);
        }
        return null;
    }
}
