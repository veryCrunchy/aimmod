namespace AimMod.InGame.Multiplayer;

// Other players' gunfire and footsteps (GunAudio.cs), and the avatars' weapons in hand: the sounds
// by weapon class, the host's shot log, playback at each shot's own time, the footstep plan, the
// mixer's gunfire volume, and avatar-state.tsv never going stale. Synthetic data only.
static partial class MultiplayerChecks
{
    static void GunfireChecks()
    {
        // Sounds: one per weapon class, a muffled one for each, and footsteps.
        foreach (var c in GunSounds.Classes)
            Check(BombSounds.All.ContainsKey("gun-" + c) && BombSounds.All.ContainsKey("gun-" + c + "-muffled"), "A " + c + " shot sound, and its muffled twin");
        Check(Enumerable.Range(0, GunSounds.StepVariants).All(v => BombSounds.All.ContainsKey("step-" + v)), "Footstep sounds");
        static double Brightness(float[] s) => s.Zip(s.Skip(1)).Sum(p => Math.Abs(p.Second - p.First)) / Math.Max(1e-9, s.Sum(x => Math.Abs(x)));
        static double Energy(float[] s) => s.Sum(x => (double)x * x);
        var rifle = GunSounds.Shot("rifle"); var muffled = BombSounds.All["gun-rifle-muffled"]();
        Check(Brightness(muffled) < Brightness(rifle) * 0.5 && muffled.Max(Math.Abs) < rifle.Max(Math.Abs), "Through a wall a shot loses its highs and is quieter");
        Check(GunSounds.Shot("sniper").Length > rifle.Length && rifle.Length > GunSounds.Shot("smg").Length && Energy(GunSounds.Shot("sniper")) > Energy(GunSounds.Shot("pistol")),
            "The AWP booms longest; an SMG is short and light");
        Check(GunSounds.ClassOf("glock") == "pistol" && GunSounds.ClassOf("deagle") == "pistol" && GunSounds.ClassOf("mac10") == "smg" && GunSounds.ClassOf("ak47") == "rifle" && GunSounds.ClassOf("awp") == "sniper"
            && GunSounds.ClassOf("knife") == "knife" && GunSounds.ClassOf("c4") is null && GunSounds.ClassOf("he") is null && GunSounds.ClassOf("rifle") == "rifle" && GunSounds.ClassOf("railgun") == "sniper",
            "Each weapon sounds like its class (the bomb and grenades make no gunshot)");

        // The host's shot log: the shooter's own times, no faster than the weapon fires, recent only.
        const long t0 = 5_000_000;
        var log = new ShotLog();
        log.Reset("m#1");
        var ak = CsRules.Find("ak47")!.Combat.TimeBetweenShots;
        var a = log.Record("p1", t0, "ak47", ak, [1, 2, 3], null, t0 + 50);
        var tooFast = log.Record("p1", t0 + 40, "ak47", ak, [1, 2, 3], null, t0 + 60);
        var next = log.Record("p1", t0 + 100, "ak47", ak, [1, 2, 3], null, t0 + 120);
        Check(a is { Id: 1 } && tooFast is null && next is { Id: 2 } && log.Record("p2", t0 - 5000, "ak47", ak, [0, 0, 0], null, t0) is null && log.Record("p2", t0 + 2000, "ak47", ak, [0, 0, 0], null, t0) is null
            && log.Record("p2", t0, "ak47", ak, [double.NaN, 0, 0], null, t0) is null, "The host takes a shot at the shooter's time, never faster than the weapon, and only recent ones");
        Check(log.Since(1).Single().Id == 2, "New shots since the last push");
        var mirror = new ShotLog(); mirror.Reset("m#1");
        mirror.Receive([a!, next!, a!]);
        Check(mirror.Recent.Count == 2, "A client keeps the host's shots once each");
        log.Reset("m#2");
        Check(log.Recent.Count == 0 && log.Record("p1", t0 + 110, "ak47", ak, [1, 2, 3], null, t0 + 120) is not null, "A new match or round starts the log afresh");

        // Playback: each shot once, at its own time (a burst keeps its fire rate however it arrives),
        // never your own, muffled for whoever a wall is known to hide the shooter from.
        var plan = new GunSoundPlan();
        GunShot S(long id, string who, long t, IReadOnlyList<string>? hidden = null) => new(id, who, t, "ak47", [1000, 0, 160], hidden);
        var burst = new[] { S(1, "p1", t0), S(2, "p1", t0 + 100), S(3, "p1", t0 + 200), S(4, "me", t0 + 200) };
        var cues = plan.Update("k", burst, "me", t0 + 250, 0);
        Check(cues.Count == 3 && cues.All(c => c.Cue.Sound == "gun-rifle" && c.Cue.From is [1000, 0, 160]) && cues[1].At - cues[0].At == 100 && cues[2].At - cues[1].At == 100 && cues[0].At >= t0 + 250,
            "A burst that arrives at once still plays at its fire rate, after the shooter's delay; your own shots aren't replayed");
        Check(plan.Update("k", burst, "me", t0 + 300, 0).Count == 0, "Each shot plays once");
        Check(plan.Update("k", [S(5, "p1", t0 + 300)], "me", t0 + 320, 0).Single().At - (t0 + 300) == (long)plan.DelayFor("p1"), "Later shots of that shooter keep its delay (their spacing stays even)");
        var stale = plan.Update("k", [S(6, "p2", t0 - 1000)], "me", t0 + 350, 0);
        Check(stale.Count == 0, "A shot far too late is dropped rather than played out of time");
        var wall = plan.Update("k", [S(7, "bot", t0 + 400, ["me"]), S(8, "bot", t0 + 500, ["someone"])], "me", t0 + 500, 0);
        Check(wall[0].Cue.Sound == "gun-rifle-muffled" && wall[1].Cue.Sound == "gun-rifle" && wall[0].Cue.Gain < wall[1].Cue.Gain, "Hidden behind a wall (the bot's traces say so): muffled");
        var offsetPlan = new GunSoundPlan();
        Check(offsetPlan.Update("k", [S(1, "p1", t0 + 1000)], "me", t0 + 10, 1000).Single().At == t0 + 10, "Shot times are host time, played on this machine's clock");

        // Footsteps: a run steps at its stride, a walk or crouch is quieter, standing still is silent.
        IReadOnlyList<TimedCue> Walk(double speed, double half = 115, bool mate = false, double away = 500, double climb = 0)
        {
            var steps = new FootstepPlan(); var all = new List<TimedCue>();
            for (long t = 0; t <= 3000; t += 100)
                all.AddRange(steps.Update("k", [("p1", t0 + t, away - 500 + speed * (t / 1000.0 - 1.5), 0, 115 + climb * t / 1000.0, t == 0 ? 115 : half, true, mate)], (0, 0, 0), t0 + t, 0));
            return all;
        }
        var run = Walk(1000);
        var runGaps = run.Zip(run.Skip(1)).Select(p => p.Second.At - p.First.At).ToList();
        Check(run.Count is >= 7 and <= 11 && runGaps.All(g => g is >= 290 and <= 420) && run.All(c => c.Cue.Sound.StartsWith("step-", StringComparison.Ordinal) && c.Cue.Gain > 0.5),
            "A running player steps about three times a second (" + run.Count + " steps in 3 s)");
        var walk = Walk(450); var crouch = Walk(300, half: 75);
        Check(walk.Count > 0 && walk.All(c => c.Cue.Gain <= FootstepPlan.WalkGain + 1e-9) && crouch.Count > 0 && crouch.All(c => c.Cue.Gain <= FootstepPlan.CrouchGain + 1e-9), "Walking and crouching are quieter");
        Check(Walk(0).Count == 0 && Walk(1000, away: 5000).Count == 0 && Walk(1000, climb: 600).Count == 0, "No steps standing still, from across the map, or in the air");
        Check(Walk(1000, mate: true).All(c => c.Cue.Gain <= FootstepPlan.MateGain + 1e-9), "A teammate's steps are quieter than an enemy's");

        // The mixer: a timed shot isn't heard before its time; the gunfire volume is its own.
        var audio = new BombAudio();
        audio.Update(0.7, null, (0, 0, 0));
        audio.PlayAt(new TimedCue(new RoundCue("gun-rifle", [500, 0, 160]), t0 + 200), (0, 0, 0));
        var pcm = new short[1024 * 2];
        audio.Mix(pcm, 1024, t0);
        var before = pcm.Max(v => Math.Abs((int)v));
        long t1 = t0;
        var heard = 0;
        for (var i = 0; i < 20; i++) { t1 += 1024 * 1000 / BombSounds.Rate; audio.Mix(pcm, 1024, t1); heard = Math.Max(heard, pcm.Max(v => Math.Abs((int)v))); }
        Check(before == 0 && heard > 1000 && audio.Pending == 0, "A shot plays at its time, not before");
        var muted = new BombAudio(); muted.SetGunVolume(0); muted.Update(0.7, null, (0, 0, 0));
        muted.PlayAt(new TimedCue(new RoundCue("gun-rifle", [500, 0, 160]), 0), (0, 0, 0));
        muted.Play(new RoundCue("planted-alert"), null);
        muted.Mix(pcm, 1024, t0);
        Check(muted.Played == 1 && pcm.Any(v => v != 0), "Gunfire at 0 % is silent while the round sounds still play");
        var crowd = new BombAudio();
        for (var i = 0; i < BombAudio.MaxVoices * 2; i++) crowd.PlayAt(new TimedCue(new RoundCue("gun-smg", [500, 0, 160]), t0 + i), (0, 0, 0));
        Check(crowd.Pending <= BombAudio.MaxVoices, "A firefight never piles up more than the mixer's voices");
        Check(MultiplayerPrefs.Apply(new(), J(new { gunVolume = 4 }))!.GunVolume == 1 && MultiplayerPrefs.Apply(new(), J(new { gunVolume = -2 }))!.GunVolume == 0 && new MultiplayerPrefs().GunVolume == 0.7,
            "The gunfire and footsteps volume is a preference (0-100 %, default 70 %)");
        var near = BombAudio.Gains([1500, 0, 0], (0, 0, 0), 1, GunSounds.ShotDistanceScale); var bomb = BombAudio.Gains([1500, 0, 0], (0, 0, 0), 1);
        Check(near.Left > bomb.Left && Math.Abs(near.Left - near.Right) < 1e-6, "Gunfire carries farther than the bomb's beep, panned by bearing");

        // avatar-state.tsv: AimModSteam drops a file older than 10 s (everyone then unarmed): rewritten
        // every second even when nothing changes (freeze time), and at once on a change.
        Check(MultiplayerService.AvatarStateDue("a", null, 0, 10) && !MultiplayerService.AvatarStateDue("a", "a", 1000, 1500) && MultiplayerService.AvatarStateDue("a", "a", 1000, 2000)
            && MultiplayerService.AvatarStateDue("b", "a", 1000, 1001), "avatar-state.tsv is rewritten on change and every second, so it never goes stale");
        RoundStartWeapons();
    }

    // The match's weapons in hand on avatars: at every round start (freeze time) each player holds a gun.
    static void RoundStartWeapons()
    {
        var cs = new CsMatch(["t1", "ct1"], 7_000_000, 12, true, null, new Dictionary<string, int> { ["t1"] = 1, ["ct1"] = 2 });
        Check(cs.View().Phase == "freeze" && MultiplayerService.AvatarWeapon(cs.View(), "t1") == "Pistol" && MultiplayerService.AvatarWeapon(cs.View(), "ct1") == "Pistol",
            "In freeze time everyone holds their pistol");
        cs.Hold("t1", CsRules.BombSlot);
        Check(MultiplayerService.AvatarWeapon(cs.View(), "t1") == "Pistol", "With the bomb in hand (no third-person model) the body still shows a gun");
    }
}
