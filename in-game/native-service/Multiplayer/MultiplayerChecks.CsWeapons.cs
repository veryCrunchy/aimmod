namespace AimMod.InGame.Multiplayer;

// CS weapons, slots and the bomb's sights and sounds (game-modes.md 6.6.2, 6.6.3): the generated
// profiles, the loadout and bomb lines AimModCore applies, the knife's reach, what others see in a
// player's hands, and the synthesised round sounds.
static partial class MultiplayerChecks
{
    static void CsWeapons()
    {
        // Every CS item has a KovaaK's viewmodel (an unknown name, like the old "Rifle", shows nothing),
        // a third-person model others see (or none), and the class's look.
        Check(CsRules.Profiles.All(w => MatchScenario.ViewModels.Contains(w.Look.Model)) && CsRules.Profiles.All(w => w.Look.ThirdPerson == "-" || MatchScenario.ThirdPersonModels.Contains(w.Look.ThirdPerson)),
            "Every CS item uses one of KovaaK's own viewmodels and third-person models");
        Check(CsRules.Weapons.Where(w => w.Class == "pistol").All(w => w.Slot == CsRules.PistolSlot) && CsRules.Weapons.Where(w => w.Class != "pistol").All(w => w.Slot == CsRules.PrimarySlot)
            && CsRules.Knife.Slot == CsRules.KnifeSlot && CsRules.Grenade.Slot == CsRules.GrenadeSlot && CsRules.Bomb.Slot == CsRules.BombSlot && CsRules.GrenadeSlot == 3 && CsRules.BombSlot == 4,
            "Slots the CS way: 1 primary, 2 pistol, 3 knife, 4 grenades, 5 bomb");
        Check(CsRules.Find("ak47")!.Look.Model == "KovaaKs Rifle" && CsRules.Find("m4a1s")!.Look.Model == "Heavy Surge Rifle" && CsRules.Find("awp")!.Look is { Model: "Spider", Scope: true, Magazine: 5 }
            && CsRules.Find("mac10")!.Look.Model == "Machine Pistol" && CsRules.Find("deagle")!.Look.Model == "Law Bringer" && CsRules.Knife.Look.Model == CsRules.BlankModel && CsRules.Bomb.Look.Model == CsRules.BlankModel,
            "Rifles, SMGs, pistols and the sniper get their class's viewmodel; the knife and bomb are drawn by AimModCore");
        Check(CsRules.Weapons.All(w => w.Look.Magazine > 0 && w.Look.Reload > 1.5) && CsRules.Find("glock")!.Look.Magazine == 20 && CsRules.Find("ak47")!.Look.Magazine == 30 && CsRules.Find("m4a1s")!.Look.Magazine == 20,
            "CS2 magazines and reload times");
        Check(CsRules.Weapons.Where(w => w.Class == "rifle").All(r => CsRules.Weapons.Where(w => w.Class == "smg").All(s => r.Look.KickUp > s.Look.KickUp)) && CsRules.Find("awp")!.Look.KickUp > CsRules.Find("ak47")!.Look.KickUp,
            "Rifles kick more than SMGs, the AWP most");
        Check(CsRules.FindAny("knife") == CsRules.Knife && CsRules.Find("knife") is null && CsRules.ByProfile(CsRules.Bomb.Combat.Name) == CsRules.Bomb, "The knife and bomb aren't for sale but are known items");
        Check(CsRules.InSlot(0, "ak47", "glock", false)?.Id == "ak47" && CsRules.InSlot(1, null, "usp", false)?.Id == "usp" && CsRules.InSlot(2, null, null, false) == CsRules.Knife
            && CsRules.InSlot(4, null, null, true) == CsRules.Bomb && CsRules.InSlot(4, null, null, false) is null && CsRules.InSlot(3, null, null, false, true) == CsRules.Grenade && CsRules.InSlot(3, null, null, true) is null && CsRules.InSlot(0, null, "usp", false) is null, "What each slot holds");

        // The arena: every CS profile with its look, the four slots filled, the weapon shown.
        var content = new FakeContent();
        var cs = LobbyRules.Apply(new LobbySettings(Scenario: content.Scenario("Synthetic A")), J(new { mode = "cs" }), 2, content).Settings!;
        var arena = MatchScenario.Generate(new(BaseScenario, cs with { Scenario = new ScenarioChoice("Synthetic A", ContentLibrary.TextHash(BaseScenario), "synthetic_map", ContentLibrary.TextHash("m"), 60) }));
        string? Profile(string name)
        {
            var at = arena.IndexOf("[Weapon Profile]\nName=" + name + "\n", StringComparison.Ordinal);
            if (at < 0) return null;
            var end = arena.IndexOf("\n\n", at, StringComparison.Ordinal);
            return arena[at..(end < 0 ? arena.Length : end + 1)];
        }
        Check(CsRules.Profiles.All(w => Profile(w.Combat.Name) is { } p && p.Contains("\nWeaponModel=" + w.Look.Model + "\n", StringComparison.Ordinal)) && !arena.Contains("WeaponModel=Rifle\n", StringComparison.Ordinal),
            "The CS arena carries every CS profile with its viewmodel");
        Check(arena.Contains("WeaponProfileNames=AimMod CS USP-S;AimMod CS Glock-18;AimMod CS Knife;AimMod CS Grenade;AimMod CS C4;;;\n", StringComparison.Ordinal) && arena.Contains("\nHideWeapon=false\n", StringComparison.Ordinal)
            && !MatchScenario.Validate(BaseScenario, arena).Except(MatchScenario.Validate(BaseScenario, BaseScenario)).Any(), "The player's slots: two pistols until the loadout, the knife, grenades, the bomb; the weapon is shown; the arena validates");
        var ak = Profile("AimMod CS AK-47")!; var awp = Profile("AimMod CS AWP")!; var knife = Profile("AimMod CS Knife")!; var bomb = Profile("AimMod CS C4")!;
        Check(ak.Contains("\nMagazineMax=30\n") && ak.Contains("\nReloadTimeFromEmpty=2.43\n") && ak.Contains("\nMaxRecoilUp=0.42\n") && ak.Contains("\nSpreadSSA=0.0,0.0,0.0,0.0\n") && ak.Contains("\nCanAimDownSight=false\n")
            && ak.Contains("\n3rdPersonWeaponModel=AK47\n") && ak.Contains("\nFullyAutomatic=true\n"), "The AK: 30 rounds, CS2 reload, a view kick, no spread, KovaaK's AK in third person");
        Check(awp.Contains("\nCanAimDownSight=true\n") && awp.Contains("\nMagazineMax=5\n") && awp.Contains("\nFullyAutomatic=false\n"), "The AWP scopes on the right mouse button");
        Check(knife.Contains("\nMaxHitscanRange=210.0\n") && knife.Contains("\nDamagePerShot=40.0\n") && knife.Contains("\nMagazineMax=0\n") && bomb.Contains("\nDamagePerShot=0.0\n") && bomb.Contains("\nMaxHitscanRange=1.0\n"),
            "The knife reaches 2.1 m; the bomb can't hurt anyone");
        Check(knife.Contains("\nShootSound=None\n") && bomb.Contains("\nShootSound=None\n") && !ak.Contains("ShootSound=None"), "The knife and bomb make no gunshot");
        // ADS zoom: CS-style (the AWP scopes), off, or all weapons; the zoomed sensitivity ratio.
        string Arena(object patch)
        {
            var settings = LobbyRules.Apply(cs, J(patch), 2, content).Settings!;
            return MatchScenario.Generate(new(BaseScenario, settings with { Scenario = new ScenarioChoice("Synthetic A", ContentLibrary.TextHash(BaseScenario), "synthetic_map", ContentLibrary.TextHash("m"), 60) }));
        }
        string Section(string text, string name) { var at = text.IndexOf("[Weapon Profile]\nName=" + name + "\n", StringComparison.Ordinal); var end = text.IndexOf("\n\n", at, StringComparison.Ordinal); return text[at..(end < 0 ? text.Length : end + 1)]; }
        Check(cs.AdsZoom == AdsZooms.Cs && cs.AdsSensitivity == 1 && awp.Contains("\nADSFOVOverride=40.0\n") && awp.Contains("\nADSZoomSensFactor=1.0\n"), "By default only the AWP zooms, at the hip-fire sensitivity");
        var all = Arena(new { adsZoom = "all", adsSensitivity = 0.8 });
        Check(Section(all, "AimMod CS AK-47").Contains("\nCanAimDownSight=true\n") && Section(all, "AimMod CS AK-47").Contains("\nADSFOVOverride=70.0\n") && Section(all, "AimMod CS Glock-18").Contains("\nADSFOVOverride=80.0\n")
            && Section(all, "AimMod CS AK-47").Contains("\nADSZoomSensFactor=0.8\n") && Section(all, "AimMod CS Knife").Contains("\nCanAimDownSight=false\n"), "All weapons: a mild zoom on guns, never on the knife");
        var off = Arena(new { adsZoom = "off" });
        Check(Section(off, "AimMod CS AWP").Contains("\nCanAimDownSight=false\n") && MatchScenario.Name(LobbyRules.Apply(cs, J(new { adsZoom = "off" }), 2, content).Settings!) != MatchScenario.Name(cs), "ADS off: nothing zooms; a different setting builds a different arena");
        Check(!LobbyRules.Apply(cs, J(new { adsZoom = "max" }), 2, content).Result.Ok && LobbyRules.Apply(cs, J(new { adsSensitivity = 9 }), 2, content).Settings!.AdsSensitivity == 2
            && LobbyRules.Plausible(cs) && !LobbyRules.Plausible(cs with { AdsZoom = "x" }), "ADS settings are checked and clamped");
        var dm = LobbyRules.Apply(new LobbySettings(Scenario: content.Scenario("Synthetic A")), J(new { mode = "deathmatch" }), 2, content).Settings!;
        var dmArena = MatchScenario.Generate(new(BaseScenario, dm with { Scenario = new ScenarioChoice("Synthetic A", ContentLibrary.TextHash(BaseScenario), "synthetic_map", ContentLibrary.TextHash("m"), 60) }));
        Check(dmArena.Contains("\nWeaponModel=" + MatchScenario.CombatRifleModel + "\n", StringComparison.Ordinal) && MatchScenario.ViewModels.Contains(MatchScenario.CombatRifleModel) && MatchScenario.ViewModels.Contains(MatchScenario.RailgunModel),
            "The combat modes' rifle shows KovaaK's rifle in the hand");

        // round-state.tsv: the loadout names slots 0-3 (the bomb only for its carrier, nothing while down).
        var me = new CsPlayerView("me", 1, CsRules.T, 800, true, 100, 0, false, false, "ak47", "glock", 0, 0);
        Check(MultiplayerService.CsLoadoutLine(me, true) == "loadout\tAimMod CS AK-47\tAimMod CS Glock-18\t0\t0\t0\tAimMod CS Knife\tAimMod CS C4\t-"
            && MultiplayerService.CsLoadoutLine(me with { Primary = null }, false) == "loadout\t-\tAimMod CS Glock-18\t0\t0\t0\tAimMod CS Knife\t-\t-"
            && MultiplayerService.CsLoadoutLine(me with { Grenades = ["flash"] }, false).EndsWith("\t-\tAimMod CS Grenade", StringComparison.Ordinal)
            && MultiplayerService.CsLoadoutLine(me with { Alive = false, Grenades = ["he"] }, true).EndsWith("\t-\t-\t-", StringComparison.Ordinal),
            "The loadout line fills the knife, the grenade slot while you carry any and, for its carrier, the bomb slot");
        Check(MultiplayerService.CsBombLine(new CsBombView("planted", null, "A", [10, 20.04, -30], 50_000, null, null, "b", 49_000), 1000) == "bomb\tplanted\t10\t20\t-30\t49000\t1"
            && MultiplayerService.CsBombLine(new CsBombView("dropped", null, null, [1, 2, 3], null, null, null, null, null), 0) == "bomb\tdropped\t1\t2\t3\t0\t0"
            && MultiplayerService.CsBombLine(new CsBombView("carried", "a", null, null, null, null, null, null, null), 0) is null, "The bomb line says where the bomb lies and when it goes off, in local time");
        var state = PlayState.Round(3, "AimMod Match - X", null, [MultiplayerService.CsLoadoutLine(me, true), "bomb\tdropped\t1\t2\t3\t0\t0"]);
        Check(state.Contains("\nloadout\t") && state.EndsWith("bomb\tdropped\t1\t2\t3\t0\t0\n", StringComparison.Ordinal), "round-state.tsv carries the loadout and bomb lines");

        // The knife: a hit within reach, refused beyond it; the bomb never hits.
        const long t0 = 20_000_000;
        var match = new CsMatch(["a", "b"], t0, 6, true, null);
        void Place(string id, double x, double yaw) { var list = new List<TrackSample>(); for (var t = t0 + 12_000; t < t0 + 20_000; t += 17) list.Add(new TrackSample(t, x, 0, 164, 0, yaw)); for (var i = 0; i < list.Count; i += 60) match.Combat.Track(id, new TrackBatch("m", 1, list.Skip(i).Take(60).ToList(), [])); }
        Place("a", 0, 0); Place("b", 150, 180);
        match.Tick(t0 + CsRules.FreezeMs);
        long seq = 0;
        HitClaim Stab(long t, int slot, double targetX = 150) => new("m", 1, ++seq, t, 0, 0, 164, 0, 0, false, targetX, 0, 100, 45, 115, slot);
        var live = t0 + CsRules.FreezeMs + 1000;
        var stab = match.Combat.Claim("a", Stab(live, CsRules.KnifeSlot), live + 50, 40);
        Check(stab is null && match.View().Players.First(p => p.Member == "b").Health == 60, "A knife hit within reach does 40 (" + (stab ?? "accepted") + ", " + match.View().Players.First(p => p.Member == "b").Health + ")");
        Check(match.Combat.Claim("a", Stab(live + 600, CsRules.BombSlot), live + 650, 40) == "weapon" && match.Combat.Claim("a", Stab(live + 900, CsRules.GrenadeSlot), live + 950, 40) == "weapon"
            && match.Combat.Claim("a", Stab(live + 1200, CsRules.PrimarySlot), live + 1250, 40) == "weapon", "The bomb and grenade slots never hit, and an empty primary slot has no weapon");
        var far = new CsMatch(["a", "b"], t0, 6, true, null);
        match = far; Place("a", 0, 0); Place("b", 600, 180); far.Tick(t0 + CsRules.FreezeMs);
        Check(far.Combat.Claim("a", Stab(live, CsRules.KnifeSlot, 600), live + 50, 40) == "range" && far.Combat.Claim("a", Stab(live + 500, CsRules.PistolSlot, 600), live + 550, 40) is null,
            "Beyond the knife's reach the stab is refused; the pistol still hits");

        // Slashes in quick succession do 25; the right-mouse stab (claimed as slot 5) does 65 within its shorter reach, once a second.
        var quick = new CsMatch(["a", "b"], t0, 6, true, null);
        match = quick; Place("a", 0, 0); Place("b", 150, 180); quick.Tick(t0 + CsRules.FreezeMs);
        Check(quick.Combat.Claim("a", Stab(live, CsRules.KnifeSlot), live + 10, 40) is null && quick.Combat.Claim("a", Stab(live + 450, CsRules.KnifeSlot), live + 460, 40) is null
            && quick.View().Players.First(p => p.Member == "b").Health == 35, "A first slash does 40, a quick follow-up 25");
        var stabbed = new CsMatch(["a", "b"], t0, 6, true, null);
        match = stabbed; Place("a", 0, 0); Place("b", 120, 180); stabbed.Tick(t0 + CsRules.FreezeMs);
        Check(stabbed.Combat.Claim("a", Stab(live, CsRules.StabSlot, 120), live + 10, 40) is null && stabbed.View().Players.First(p => p.Member == "b").Health == 35
            && stabbed.Combat.Claim("a", Stab(live + 500, CsRules.StabSlot, 120), live + 510, 40) == "fire-rate", "The stab does 65, once a second");
        var shot = new ShotFeed.Shot(0, 1, 0, 0, 164, 0, 0, CsRules.KnifeSlot, 3, false);
        Check(MultiplayerService.KnifeSound(shot, false, new TrackSeen(0, 3, 150, 0, 100, 45, 115)) == "knife-hit" && MultiplayerService.KnifeSound(shot, false, new TrackSeen(0, 3, 900, 0, 100, 45, 115)) == "knife-swish"
            && MultiplayerService.KnifeSound(shot, true, null) == "knife-stab-swish" && MultiplayerService.KnifeSound(shot, true, new TrackSeen(0, 3, 120, 0, 100, 45, 115)) == "knife-stab",
            "The knife sounds: a thud within reach, a swish otherwise");

        // What others see in a player's hands: the slot they hold, if they have something there.
        var view = (Func<string, CsPlayerView>)(id => far.View().Players.First(p => p.Member == id));
        Check(view("a").Holding == "glock" && far.Hold("a", CsRules.KnifeSlot) is null && view("a").Holding == "knife" && far.Hold("a", CsRules.PrimarySlot) is null && view("a").Holding == "glock"
            && far.Hold("a", 9) == "invalid" && far.Hold("nobody", 0) == "not-playing", "Holding: the slot a player switched to, else their best weapon");
        far.Hold("a", CsRules.PistolSlot);
        Check(MultiplayerService.AvatarWeapon(far.View(), "a") == "Pistol" && (far.Hold("a", CsRules.KnifeSlot) is null && MultiplayerService.AvatarWeapon(far.View(), "a") == "-")
            && MultiplayerService.AvatarWeapon(far.View(), "nobody") == "-", "Avatars show the held weapon's third-person model; the knife shows none");
        // The held slot comes from AimModCore's self-pose weapon row.
        var frame = LivePoseFrame.Parse("AIMMOD_POSE_1\t5\npose\t1790871546958\t1\t2\t3\t0\t90\t0\t90\nweapon\t1790871546958\t2\n");
        Check(frame?.Weapon == 2 && LivePoseFrame.Parse("AIMMOD_POSE_1\t5\npose\t1\t1\t2\t3\t0\t90\t0\t90\nweapon\t1\t9\n") is null && LivePoseFrame.Parse("AIMMOD_POSE_1\t5\npose\t1\t1\t2\t3\t0\t90\t0\t90\n")?.Weapon is null,
            "The self-pose weapon row names the held slot (0-7)");
        Check(GameBinds.Uses(arena, LobbyModes.Cs).Any(u => u is { Action: "Weapon3", Label: "Knife" }) && GameBinds.Uses(arena, LobbyModes.Cs).Any(u => u is { Action: "Weapon4", Label: "Grenades" })
            && GameBinds.Uses(arena, LobbyModes.Cs).Any(u => u is { Action: "Weapon5", Label: "Bomb" }), "The binds check covers the knife, grenade and bomb keys");
        Check(MultiplayerPrefs.Apply(new(), J(new { roundVolume = 3 }))!.RoundVolume == 1 && MultiplayerPrefs.Apply(new(), J(new { roundVolume = -1 }))!.RoundVolume == 0 && new MultiplayerPrefs().RoundVolume == 0.7,
            "The bomb and round sounds volume is a preference (0-100 %, default 70 %)");
        RoundSounds();
    }

    static void RoundSounds()
    {
        // The beep: once a second at 40 s left, faster as it runs out, about 7 a second at the end.
        Check(Math.Abs(BombSounds.BeepInterval(40) - 1) < 1e-9 && BombSounds.BeepInterval(10) is > 0.25 and < 0.35 && BombSounds.BeepInterval(2) == BombSounds.FastestBeep && 1 / BombSounds.BeepInterval(0) is > 4 and < 8,
            "Beeps speed up from 1 a second to about 7 a second");
        var intervals = Enumerable.Range(0, 41).Select(s => BombSounds.BeepInterval(40 - s)).ToArray();
        Check(intervals.Zip(intervals.Skip(1)).All(p => p.Second <= p.First), "The beep never slows down");
        Check(BombSounds.NextBeep(40) == 40 && BombSounds.NextBeep(39.5) == 39 && BombSounds.NextBeep(10) <= 10 && BombSounds.NextBeep(10) > 10 - BombSounds.BeepInterval(10) - 0.05,
            "Beeps fall at fixed times before the explosion (every machine and the bomb light in step)");
        Check(BombSounds.Attenuation(100) == 1 && BombSounds.Attenuation(2000) < BombSounds.Attenuation(1000) && BombSounds.Attenuation(1e6) >= 0.06, "Farther is quieter, never silent");
        var ahead = BombSounds.Pan(0, 100, 90); var (l, r) = BombSounds.Pan(0, 100, 0); var (l2, r2) = BombSounds.Pan(0, -100, 0); var (l3, r3) = BombSounds.Pan(100, 0, 0); var (lb, rb) = BombSounds.Pan(-100, 0, 0);
        Check(r > l && l2 > r2 && Math.Abs(l3 - r3) < 1e-9 && lb < l3 && Math.Abs(ahead.Left - ahead.Right) < 1e-9, "Panning follows the bomb's bearing from where you look (Unreal yaw)");
        foreach (var (name, make) in BombSounds.All)
        {
            var s = make();
            Check(s.Length is > 100 and < BombSounds.Rate * 3 && s.All(float.IsFinite) && s.Max(Math.Abs) is > 0.1f and <= 1f && make().SequenceEqual(s), "Sound " + name + " is synthesised, bounded and the same every time");
        }
        var wav = BombSounds.Wav(BombSounds.Beep());
        Check(wav.Length == 44 + BombSounds.Beep().Length * 2 && System.Text.Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(wav, 8, 4) == "WAVE" && BitConverter.ToInt32(wav, 24) == BombSounds.Rate,
            "WAV export: 16-bit mono at 44.1 kHz");

        // Cues: nothing that already happened plays; then each event's sound, 2D for your own actions.
        var plan = new RoundSoundPlan();
        CsView View(CsBombView bomb, IReadOnlyList<CsEvent> events, bool kit = false) => new(1, "live", 0, 0, [0, 0], CsRules.T, 6, true,
            [new("me", 1, CsRules.T, 800, true, 100, 0, false, false, null, "glock", 0, 0), new("ct", 2, CsRules.CT, 800, true, 100, 0, false, kit, null, "usp", 0, 0)], bomb, null, null, null, events, null, [new CsSiteView("A", 500, 0, 0)]);
        var carried = new CsBombView("carried", "me", null, null, null, null, null, null, null);
        var planted = new CsBombView("planted", null, "A", [500, 10, 0], 90_000, null, null, null, null);
        Check(plan.Update("m1", View(carried, [new(1, "planting", 1, "me", "A")]), "me").Count == 0, "A first view only sets the baseline");
        var cues = plan.Update("m1", View(planted, [new(1, "planting", 1, "me", "A"), new(2, "planted", 2, "me", "A")]), "me");
        Check(cues.Count == 2 && cues[0] is { Sound: "plant-done", From: null } && cues[1] is { Sound: "planted-alert", From: null }, "Your plant: the armed chirp in your ears, and the planted alert for everyone");
        cues = plan.Update("m1", View(planted with { Defuser = "ct" }, [new(3, "defusing", 3, "ct", null), new(4, "defused", 4, "ct", "A")]), "me");
        Check(cues.Count == 2 && cues[0] is { Sound: "defuse-start", From: [500, 10, 0] } && cues[1].Sound == "defuse-done", "Someone else's defuse sounds from the bomb; the result is for everyone");
        var ctPlan = new RoundSoundPlan();
        ctPlan.Update("m1", View(carried, []), "ct");
        Check(ctPlan.Update("m1", View(carried, [], kit: true), "ct").Single().Sound == "kit" && ctPlan.Update("m1", View(planted, [new(5, "exploded", 5, null, "A")], kit: true), "ct").Single() is { Sound: "explosion", From: [500, 10, 0] },
            "The kit clicks when it's yours; the explosion comes from the bomb");
        Check(ctPlan.Update("m1", View(carried, [new(6, "bomb-dropped", 6, "me", null)], kit: true), "ct").Count == 0 && plan.Update("m1", View(carried, [new(6, "bomb-dropped", 6, "me", null)]), "me").Single().Sound == "dropped",
            "Bomb drops tick only for the Terrorists");
        Check(RoundSoundPlan.Beeping(View(planted, [])) is { ExplodesAt: 90_000 } && RoundSoundPlan.Beeping(View(carried, [])) is null, "Only a planted bomb beeps");

        // The mixer keeps the beep's time: count beeps in 2 s of output at 40 s and at 4 s left.
        int Beeps(long explodesIn)
        {
            var audio = new BombAudio();
            audio.Update(1, ([500.0, 0, 0], 1_000_000 + explodesIn), (0, 0, 0));
            // 5 ms windows: a beep is a run of loud windows after a quiet one.
            var buffer = new short[1024 * 2]; var samples = new List<int>();
            for (long t = 1_000_000; t < 1_002_000; t += 1024 * 1000 / BombSounds.Rate)
            {
                audio.Mix(buffer, 1024, t);
                for (var i = 0; i < buffer.Length; i += 2) samples.Add(Math.Abs(buffer[i]) + Math.Abs(buffer[i + 1]));
            }
            var count = 0; var wasLoud = false;
            for (var w = 0; w + 220 <= samples.Count; w += 220)
            {
                var loud = samples.Skip(w).Take(220).Max() > 2000;
                if (loud && !wasLoud) count++;
                wasLoud = loud;
            }
            return count;
        }
        var slow = Beeps(40_000); var fast = Beeps(4_000);
        Check(slow is >= 2 and <= 3 && fast >= 8 && fast > slow * 3, "The mixed beep runs about 1/s at 40 s and many times faster at 4 s (" + slow + " vs " + fast + " in 2 s)");
        var silent = new BombAudio(); silent.Update(0, ([0.0, 0, 0], 1_000_000 + 40_000), null);
        var quiet = new short[2048]; silent.Mix(quiet, 1024, 1_000_000);
        Check(quiet.All(v => v == 0) && !BombAudio.DeviceAllowed, "Volume 0 is silent; checks never open an audio device");
    }
}
