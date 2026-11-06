namespace AimMod.InGame.Multiplayer;

static partial class MultiplayerChecks
{
    static void WeaponDropChecks()
    {
        const long start = 30_000_000;
        var match = new CsMatch(["a", "b"], start, 6, false, null);
        long time = start + 100;
        void Place(string id, double x, double z = 164)
        {
            time += 100;
            match.Combat.Track(id, new TrackBatch("m", 1, [new TrackSample(time, x, 0, z, 0, 0)], []));
        }
        CsPlayerView Player(string id) => match.View().Players.Single(p => p.Member == id);
        Place("a", 0); Place("b", 81);
        var originalCarrier = match.View().Bomb.Carrier;
        match.Hold("a", CsRules.PistolSlot);
        Check(match.Drop("a", time) is null && Player("a").Secondary is null, "G drops the held pistol even while carrying the bomb");
        Check(match.View().Drops is { Count: 1 } && match.View().Bomb.Carrier == originalCarrier, "The host publishes a weapon drop without dropping a carried bomb");
        Check(match.Use("b", true, time) is not null && Player("b").Secondary == "usp", "E cannot swap beyond the pickup radius");
        Place("a", 1000);
        Place("b", 80);
        match.Tick(time);
        Check(Player("b").Secondary == "usp", "Walking over an occupied slot preserves its weapon");
        Check(match.Use("b", true, time) is null && Player("b").Secondary == "glock", "E swaps a weapon at the pickup boundary across sides");
        Check(match.View().Drops!.Single().Weapon == "usp", "Swapping leaves the replaced weapon for teammates");
        match.Tick(time);
        Check(Player("b").Secondary == "glock", "The swapper cannot immediately take back their own drop");
        Place("a", 80, 500);
        match.Tick(time);
        Check(Player("a").Secondary is null, "Weapons on another floor cannot be picked up");
        Place("a", 80);
        match.Tick(time);
        Check(Player("a").Secondary == "usp" && match.View().Drops!.Count == 0, "An empty slot picks up a nearby weapon automatically");
        match.Hold("a", CsRules.KnifeSlot);
        Check(match.DropWeapon("a", time) == "no-weapon", "The knife cannot be dropped");
        Check(Player("a").Adr == 0 && Player("a").HsPercent == 0 && Player("a").Mvps == 0, "Initial scoreboard statistics are finite zeros");

        match = new CsMatch(["a", "b", "c", "d"], start, 6, false, null,
            new Dictionary<string, int> { ["a"] = 1, ["b"] = 2, ["c"] = 1, ["d"] = 2 });
        Place("a", 1000); Place("b", 0); Place("c", 2000); Place("d", 3000);
        Check(match.Buy("b", "defuse-kit", time) is null, "A defender can buy a kit before a death drop");
        time = start + CsRules.FreezeMs + 100;
        match.Tick(time);
        Check(match.Combat.AreaHit("a", "b", 200, GrenadeRules.Weapons.First().Combat, time, null) is null, "Enemy damage can eliminate a defender");
        Check(match.View().Drops!.Select(d => d.Weapon).Order().SequenceEqual(new[] { "defuse-kit", "usp" }), "Death drops the pistol and the kit");
        match.Tick(time + 1);
        Check(Player("a").Adr == 100, "ADR counts enemy health damage without overkill");
        match.Combat.AreaHit("a", "d", 200, GrenadeRules.Weapons.First().Combat, time + 10, null);
        match.Tick(time + 11);
        Check(match.View().Phase == "end" && match.View().Drops!.Count == 0, "Round end removes every weapon and kit drop");
        Check(Player("a").Mvps == 1 && Player("a").Adr == 200, "The winning side's top killer receives the round MVP");

        match = new CsMatch(["a", "b"], start, 6, false, null);
        time = start + 100;
        Place("a", 0); Place("b", 0);
        match.Bots.Add("b");
        Check(match.Buy("a", "deagle", time) is null, "A teammate can buy a weapon to drop");
        match.Hold("a", CsRules.PistolSlot);
        match.DropWeapon("a", time);
        Place("a", 1000);
        match.Tick(time);
        Check(Player("b").Secondary == "deagle", "A bot swaps its pistol for a better nearby weapon");
        match.Tick(time + CsMatch.DropBlockMs + 1);
        Check(Player("b").Secondary == "deagle", "A bot keeps the better weapon after its pickup cooldown");
    }
}
