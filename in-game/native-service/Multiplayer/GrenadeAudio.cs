namespace AimMod.InGame.Multiplayer;

// CS grenade sounds (game-modes.md 6.6.5), synthesised like the bomb's (BombSounds: sine tones,
// noise and envelopes; nothing recorded or taken from a game): the pin, the throw, bounces, the HE
// blast, the flash's pop and the ringing it leaves, the smoke's hiss, fire, and a decoy's fake
// gunfire by weapon class. `--write-bomb-sounds <folder>` writes these too.
static class GrenadeSounds
{
    const int Rate = BombSounds.Rate;

    // The pin: a sharp metallic tick and the spoon's ping.
    public static float[] Pin()
    {
        var s = BombSounds.Buffer(0.3);
        BombSounds.Burst(s, 0, 0.012, 0.8, 7000, 101, 260);
        BombSounds.Tone(s, 0.004, 0.09, 2650, 0.6, 0.001, 0.08, 0.4);
        BombSounds.Burst(s, 0.15, 0.01, 0.5, 6000, 103, 300);
        BombSounds.Tone(s, 0.152, 0.12, 3400, 0.35, 0.001, 0.11, 0.3);
        return BombSounds.Normalize(s, 0.45);
    }
    public static float[] Throw() => BombSounds.Swish(0.24, 450, 1700, 107, 0.4);
    // A small metal can hitting the floor or a wall.
    public static float[] Bounce()
    {
        var s = BombSounds.Buffer(0.12);
        BombSounds.Burst(s, 0, 0.03, 1, 5000, 109, 120);
        BombSounds.Tone(s, 0.002, 0.07, 1850, 0.55, 0.001, 0.06, 0.6);
        BombSounds.Tone(s, 0.002, 0.05, 3150, 0.3, 0.001, 0.045, 0.2);
        return BombSounds.Normalize(s, 0.42);
    }
    // The HE: a hard crack, a thump and a shorter rumble than the bomb.
    public static float[] HeExplosion()
    {
        var s = BombSounds.Buffer(1.7); var noise = new BombSounds.Noise(113); double low = 0, lower = 0, phase = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var t = i / (double)Rate;
            var cutoff = 260 + 5200 * Math.Exp(-14 * t);
            var a = 1 - Math.Exp(-2 * Math.PI * cutoff / Rate);
            low += a * (noise.Next() - low); lower += 0.35 * a * (low - lower);
            phase += 2 * Math.PI * (45 + 80 * Math.Exp(-18 * t)) / Rate;
            var env = Math.Min(1, t / 0.002) * Math.Exp(-3.4 * t);
            s[i] = (float)(env * (0.8 * lower + 0.9 * low * Math.Exp(-9 * t) + 0.75 * Math.Sin(phase) * Math.Exp(-7 * t)));
        }
        return BombSounds.Normalize(s, 0.95);
    }
    // The flash: a very sharp, bright pop with a high ring.
    public static float[] FlashPop()
    {
        var s = BombSounds.Buffer(0.6);
        BombSounds.Burst(s, 0, 0.12, 1, 11000, 127, 45);
        BombSounds.Burst(s, 0, 0.3, 0.5, 1800, 131, 14);
        BombSounds.Tone(s, 0.005, 0.5, 3300, 0.22, 0.002, 0.45, 0.15);
        return BombSounds.Normalize(s, 0.9);
    }
    // Blinded: a ringing in the ears that fades.
    public static float[] FlashRing()
    {
        var s = BombSounds.Buffer(2.6);
        for (var i = 0; i < s.Length; i++)
        {
            var t = i / (double)Rate;
            s[i] = (float)(Math.Sin(2 * Math.PI * 4150 * t) * Math.Min(1, t / 0.05) * Math.Exp(-1.1 * t) * (0.85 + 0.15 * Math.Sin(2 * Math.PI * 5 * t)));
        }
        return BombSounds.Normalize(s, 0.22);
    }
    // The smoke: a long, airy hiss that swells and settles.
    public static float[] SmokeHiss() => Hiss(2.8, 137, 0.5, 2.0);
    // A fire put out by smoke: a short wet hiss.
    public static float[] FireOut() => Hiss(0.8, 139, 0.45, 5);
    static float[] Hiss(double seconds, uint seed, double peak, double decay)
    {
        var s = BombSounds.Buffer(seconds); var noise = new BombSounds.Noise(seed); double low = 0;
        var a = 1 - Math.Exp(-2 * Math.PI * 2400 / Rate);
        for (var i = 0; i < s.Length; i++)
        {
            var t = i / (double)Rate;
            var x = noise.Next(); low += a * (x - low);
            s[i] = (float)((x - low) * Math.Min(1, t / 0.08) * Math.Exp(-decay * t / seconds) * (1 - t / seconds * 0.5));
        }
        return BombSounds.Normalize(s, peak);
    }
    // Fire catching: a low whoosh that rises.
    public static float[] FireIgnite() => BombSounds.Swish(0.6, 180, 1400, 149, 0.6);
    // Fire burning: a soft roar with random crackles (played again every 1.4 s while it burns).
    public static float[] FireCrackle()
    {
        var s = BombSounds.Buffer(1.5); var noise = new BombSounds.Noise(151); double low = 0;
        var a = 1 - Math.Exp(-2 * Math.PI * 500 / Rate);
        for (var i = 0; i < s.Length; i++)
        {
            var t = i / (double)Rate;
            low += a * (noise.Next() - low);
            s[i] = (float)(0.5 * low * Math.Min(1, t / 0.1) * Math.Min(1, (1.5 - t) / 0.1));
        }
        var pops = new BombSounds.Noise(157);
        for (var k = 0; k < 14; k++) BombSounds.Burst(s, (pops.Next() + 1) / 2 * 1.4, 0.015, 0.6 + 0.3 * pops.Next(), 6000, 160u + (uint)k, 250);
        return BombSounds.Normalize(s, 0.4);
    }
    // A decoy's fake shot: a thump and a noise crack, sized by the owner's weapon class.
    public static float[] Shot(string weaponClass)
    {
        var (len, thump, crack, peak) = weaponClass switch
        {
            "sniper" => (0.5, 85.0, 4200.0, 0.85), "rifle" => (0.22, 110.0, 5200.0, 0.7), "smg" => (0.16, 140.0, 6000.0, 0.6), "heavy" => (0.3, 95.0, 3800.0, 0.75), _ => (0.18, 130.0, 5600.0, 0.6),
        };
        var s = BombSounds.Buffer(len); double phase = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var t = i / (double)Rate;
            phase += 2 * Math.PI * thump * (1 + 2 * Math.Exp(-60 * t)) / Rate;
            s[i] = (float)(0.8 * Math.Sin(phase) * Math.Exp(-25 * t) * Math.Min(1, t / 0.001));
        }
        BombSounds.Burst(s, 0, len, 1, crack, 163, 18 / len);
        return BombSounds.Normalize(s, peak);
    }
    // The decoy's last pop.
    public static float[] DecoyPop() { var s = BombSounds.Buffer(0.25); BombSounds.Burst(s, 0, 0.2, 1, 3000, 167, 25); BombSounds.Tone(s, 0, 0.1, 160, 0.6, 0.001, 0.08, 0.1); return BombSounds.Normalize(s, 0.5); }

    public static readonly IReadOnlyDictionary<string, Func<float[]>> All = new Dictionary<string, Func<float[]>>
    {
        ["nade-pin"] = Pin, ["nade-throw"] = Throw, ["nade-bounce"] = Bounce, ["he-explosion"] = HeExplosion, ["flash-pop"] = FlashPop, ["flash-ring"] = FlashRing,
        ["smoke-hiss"] = SmokeHiss, ["fire-ignite"] = FireIgnite, ["fire-crackle"] = FireCrackle, ["fire-out"] = FireOut, ["decoy-pop"] = DecoyPop,
        ["decoy-pistol"] = () => Shot("pistol"), ["decoy-smg"] = () => Shot("smg"), ["decoy-rifle"] = () => Shot("rifle"), ["decoy-sniper"] = () => Shot("sniper"), ["decoy-heavy"] = () => Shot("heavy"),
    };
}

// What the grenades sound like from one player's view: throws, bounces as the path passes them,
// blasts, smokes, fires burning, decoys firing, and the ring after a hard flash. Pure (like
// RoundSoundPlan); BombAudio plays the cues. A new match only sets the baseline.
sealed class GrenadeSoundPlan
{
    public const long CrackleMs = 1400;
    string? matchKey;
    readonly HashSet<long> seen = [];
    readonly Dictionary<long, int> bounces = new(), decoyShots = new();
    readonly Dictionary<long, long> crackleAt = new();
    long flashAt = -1;

    public IReadOnlyList<RoundCue> Update(string matchId, CsView cs, string self, long hostNow)
    {
        var cues = new List<RoundCue>();
        var grenades = cs.Grenades ?? [];
        var baseline = matchKey != matchId;
        if (baseline) { matchKey = matchId; seen.Clear(); bounces.Clear(); decoyShots.Clear(); crackleAt.Clear(); }
        foreach (var g in grenades)
        {
            var isNew = seen.Add(g.Id * 8 + StateCode(g.State));
            var at = g.Pos;
            switch (g.State)
            {
                case "flying":
                {
                    var keys = GrenadePhysics.Unflat(g.Keys);
                    if (isNew && !baseline && keys.Count > 0 && hostNow - g.At < 1000) cues.Add(new("nade-throw", g.Owner == self ? null : [keys[0].X, keys[0].Y, keys[0].Z], 0.8));
                    var played = bounces.GetValueOrDefault(g.Id);
                    for (var i = played + 1; i < keys.Count; i++)
                    {
                        if (g.At + keys[i].T > hostNow || (g.Ends is { } ends && g.At + keys[i].T > ends)) break;
                        if (!baseline && keys[i].Impact != GrenadePhysics.NoImpact && hostNow - (g.At + keys[i].T) < 500) cues.Add(new("nade-bounce", [keys[i].X, keys[i].Y, keys[i].Z], 0.9));
                        played = i;
                    }
                    bounces[g.Id] = played;
                    break;
                }
                case "blast" when isNew && !baseline && hostNow - g.At < 1000:
                    var sound = g.Kind switch { GrenadeRules.He => "he-explosion", GrenadeRules.Flash => "flash-pop", GrenadeRules.Decoy => "decoy-pop", "extinguished" => "fire-out", _ => "fire-ignite" };
                    cues.Add(new(sound, at, g.Kind == GrenadeRules.He ? 1.2 : 1));
                    break;
                case "smoke" when isNew && !baseline && hostNow - g.At < 2000:
                    cues.Add(new("smoke-hiss", at, 0.9));
                    break;
                case "fire":
                    if (isNew && !baseline && hostNow - g.At < 1000) cues.Add(new("fire-ignite", at));
                    var next = crackleAt.TryGetValue(g.Id, out var c) ? c : g.At + 300;
                    if (hostNow >= next && g.Ends is { } fireEnds && hostNow < fireEnds - 300)
                    {
                        if (!baseline) cues.Add(new("fire-crackle", at, 0.8));
                        while (next <= hostNow) next += CrackleMs;
                    }
                    crackleAt[g.Id] = next;
                    break;
                case "decoy":
                {
                    var shots = GrenadeRules.DecoyShots(g.Id, g.Weapon);
                    var done = decoyShots.TryGetValue(g.Id, out var d) ? d : -1;
                    for (var i = done + 1; i < shots.Count && g.At + shots[i] <= hostNow; i++)
                    {
                        if (!baseline && hostNow - (g.At + shots[i]) < 300) cues.Add(new("decoy-" + (g.Weapon is "rifle" or "smg" or "sniper" or "heavy" ? g.Weapon : "pistol"), at, 0.9));
                        done = i;
                    }
                    decoyShots[g.Id] = done;
                    break;
                }
            }
        }
        // A hard flash leaves your ears ringing.
        if (cs.Players.FirstOrDefault(p => p.Member == self)?.Flash is { } flash && flash.At != flashAt)
        {
            if (!baseline && flash.Amount >= 0.5 && hostNow - flash.At < 1000) cues.Add(new("flash-ring", null, flash.Amount));
            flashAt = flash.At;
        }
        return cues;
    }
    static int StateCode(string state) => state switch { "flying" => 1, "smoke" => 2, "fire" => 3, "decoy" => 4, "blast" => 5, _ => 0 };
}
