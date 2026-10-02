using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// CS grenades on this machine (game-modes.md 6.6.5):
//  - the host trades with its own game (AimModSteam): grenade-sim.tsv asks for each throw's path and
//    for line-of-sight traces from blasts, grenade-paths.tsv answers (GrenadeFiles);
//  - every player: key 4 picks the grenade in hand (again: the next one), fire pulls the pin and
//    throws on release, right mouse throws underhand, both buttons a medium throw ("throw" to the
//    host); grenades.tsv tells AimModCore what to draw (the grenade in hand, grenades in flight,
//    smokes, fires, decoys, blasts); the grenades' sounds.
sealed partial class MultiplayerService
{
    public const string GrenadeSimFile = "grenade-sim.tsv", GrenadePathsFile = "grenade-paths.tsv", GrenadesFile = "grenades.tsv";
    public const string CsGrenadeKey = "4";

    // ---- host: paths and traces from this machine's game ---------------------------------------
    long grenadeSimSequence, grenadeSimWrittenAt, grenadePathsStamp = -1; string? lastGrenadeSim;
    void StepGrenades()
    {
        if (outputFolder is null || core is null) return;
        core.WithCsGrenades(field =>
        {
            ReadGrenadePaths(field);
            var paths = field.PathRequests; var los = field.LosRequests;
            var body = paths.Count + los.Count == 0 ? "" : GrenadeFiles.Sim(0, paths, los);
            var now = clock();
            if (body == lastGrenadeSim && (body.Length == 0 || now - grenadeSimWrittenAt < 1000)) return;
            lastGrenadeSim = body; grenadeSimWrittenAt = now;
            try { AtomicFile.WriteText(Path.Combine(outputFolder, GrenadeSimFile), body.Length == 0 ? "AIMMOD_GRENADESIM_1\t" + ++grenadeSimSequence + "\n" : GrenadeFiles.Sim(++grenadeSimSequence, paths, los)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
    }
    void ReadGrenadePaths(CsGrenadeField field)
    {
        var path = Path.Combine(outputFolder!, GrenadePathsFile);
        try
        {
            if (!File.Exists(path)) return;
            var info = new FileInfo(path);
            if (info.Length > 256 * 1024 || info.LastWriteTimeUtc.Ticks == grenadePathsStamp) return;
            grenadePathsStamp = info.LastWriteTimeUtc.Ticks;
            if (GrenadeFiles.Paths(File.ReadAllText(path), clock()) is not { } answer) return;
            foreach (var (id, keys) in answer.Paths) field.SetPath(id, keys);
            foreach (var (tag, clear) in answer.Los) field.AnswerLos(tag, clear);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- every player: the grenade in hand and throwing -----------------------------------------
    string? grenadeHand; bool pinPulled, throwLeft, throwRight; long grenadeSlotSince = long.MaxValue, grenadeThrownAt;
    string? grenadeRoundKey;
    internal string? GrenadeHand => grenadeHand;

    void CsGrenadeInput(MatchSnapshot match, CsView cs, CsPlayerView me, bool foreground)
    {
        var key = match.Id + "#" + cs.Round;
        if (key != grenadeRoundKey) { grenadeRoundKey = key; pinPulled = false; grenadeThrownAt = 0; }
        var carried = me.Grenades ?? [];
        if (grenadeHand is null || !carried.Contains(grenadeHand)) grenadeHand = GrenadeRules.Sorted(carried).FirstOrDefault();
        var inHand = me.Alive && grenadeHand is not null && poseTracker?.Weapon == CsRules.GrenadeSlot;
        var now = clock();
        if (!inHand) { grenadeSlotSince = long.MaxValue; pinPulled = false; }
        else if (grenadeSlotSince == long.MaxValue) grenadeSlotSince = now;
        if (!foreground || buyOpen) { if (!foreground) pinPulled = false; return; }
        // Key 4 again with a grenade already in hand: the next kind you carry (CS).
        if (csKeys.Pressed('4') && inHand && now - grenadeSlotSince > 150 && !pinPulled) grenadeHand = GrenadeRules.Next(carried, grenadeHand);
        var (left, leftTap) = csKeys.State((char)0x01); var (right, rightTap) = csKeys.State((char)0x02);
        if (!inHand || cs.Phase is not ("live" or "planted")) { pinPulled = false; return; }
        // A click shorter than a tick counts, but not one left over from the gun before the switch.
        if (now - grenadeSlotSince < 150) { leftTap = false; rightTap = false; }
        if (!pinPulled && (left || right || leftTap || rightTap))
        {
            pinPulled = true; throwLeft = left || leftTap; throwRight = right || rightTap;
            roundAudio.Play(new RoundCue("nade-pin"), null);
            if (left || right) return; // held: it goes when they let go
        }
        if (!pinPulled) return;
        if (left || right) { throwLeft = left; throwRight = right; return; }
        // Released: the throw, with the buttons last held (both: medium, right: underhand).
        pinPulled = false;
        var strength = throwLeft && throwRight ? 0.5 : throwRight ? 0 : 1;
        if (ownRecent.Count == 0) { csRefusal = "AimMod can’t see where you are (no pose feed from AimModCore)."; csRefusalUntil = now + 2500; return; }
        var eye = ownRecent[^1];
        grenadeThrownAt = now;
        roundAudio.Play(new RoundCue("nade-throw"), null);
        CsCommand("throw", new { kind = grenadeHand, strength, o = new[] { Math.Round(eye.X, 1), Math.Round(eye.Y, 1), Math.Round(eye.Z, 1) }, r = new[] { Math.Round(eye.Pitch, 2), Math.Round(eye.Yaw, 2) } });
    }

    // ---- grenades.tsv for AimModCore -------------------------------------------------------------
    //   AIMMOD_GRENADES_1\t<seq>
    //   match\t<scenario>
    //   hand\t<kind|->\t<pin pulled 0/1>\t<thrown at, local unix ms, 0>
    //   fly\t<id>\t<kind>\t<thrown, local ms>\t<goes off, local ms, 0 unknown>\t<keys>\t<t x y z vx vy vz motion> x keys   (t: ms after the throw)
    //   smoke\t<id>\t<x>\t<y>\t<z>\t<start>\t<end>
    //   fire\t<id>\t<kind>\t<x>\t<y>\t<z>\t<radius>\t<start>\t<end>
    //   decoy\t<id>\t<x>\t<y>\t<z>\t<start>\t<end>
    //   blast\t<id>\t<kind>\t<x>\t<y>\t<z>\t<at>
    // Rewritten on change and every second (AimModCore drops it after 5 s).
    string? lastGrenades; long grenadesWrittenAt, grenadesSequence;
    internal static string GrenadesBody(string scenario, string? hand, bool pin, long thrownLocal, IEnumerable<CsGrenadeView> grenades, long hostToLocal)
    {
        static string F(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
        static string L(long v) => v.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("match\t").Append(scenario).Append('\n');
        sb.Append("hand\t").Append(hand ?? "-").Append('\t').Append(pin ? 1 : 0).Append('\t').Append(L(Math.Max(0, thrownLocal))).Append('\n');
        foreach (var g in grenades.Take(48))
        {
            if (!(GrenadeRules.Find(g.Kind) is not null || g.Kind == "extinguished")) continue;
            var start = Math.Max(0, g.At - hostToLocal); var end = g.Ends is { } e ? Math.Max(0, e - hostToLocal) : 0;
            if (g.State == "flying")
            {
                var keys = GrenadePhysics.Unflat(g.Keys);
                if (keys.Count == 0) continue;
                sb.Append("fly\t").Append(g.Id).Append('\t').Append(g.Kind).Append('\t').Append(L(start)).Append('\t').Append(L(end)).Append('\t').Append(keys.Count);
                foreach (var k in keys) sb.Append('\t').Append(F(k.T)).Append('\t').Append(F(k.X)).Append('\t').Append(F(k.Y)).Append('\t').Append(F(k.Z)).Append('\t').Append(F(k.Vx)).Append('\t').Append(F(k.Vy)).Append('\t').Append(F(k.Vz)).Append('\t').Append(k.Motion);
                sb.Append('\n');
            }
            else if (g.Pos is { Length: 3 } p)
            {
                var at = F(p[0]) + "\t" + F(p[1]) + "\t" + F(p[2]);
                if (g.State == "smoke") sb.Append("smoke\t").Append(g.Id).Append('\t').Append(at).Append('\t').Append(L(start)).Append('\t').Append(L(end)).Append('\n');
                else if (g.State == "fire") sb.Append("fire\t").Append(g.Id).Append('\t').Append(g.Kind).Append('\t').Append(at).Append('\t').Append(F(g.Radius)).Append('\t').Append(L(start)).Append('\t').Append(L(end)).Append('\n');
                else if (g.State == "decoy") sb.Append("decoy\t").Append(g.Id).Append('\t').Append(at).Append('\t').Append(L(start)).Append('\t').Append(L(end)).Append('\n');
                else if (g.State == "blast") sb.Append("blast\t").Append(g.Id).Append('\t').Append(g.Kind).Append('\t').Append(at).Append('\t').Append(L(start)).Append('\n');
            }
        }
        return sb.ToString();
    }
    void WriteGrenades(MatchSnapshot match, CsView cs, CsPlayerView me)
    {
        if (outputFolder is null) return;
        var inHand = me.Alive && grenadeHand is not null && poseTracker?.Weapon == CsRules.GrenadeSlot;
        var body = GrenadesBody(RoundScenario(match), inHand ? grenadeHand : null, inHand && pinPulled, grenadeThrownAt, cs.Grenades ?? [], HostOffset());
        var now = clock();
        if (body == lastGrenades && now - grenadesWrittenAt < 1000) return;
        lastGrenades = body; grenadesWrittenAt = now;
        try { AtomicFile.WriteText(Path.Combine(outputFolder, GrenadesFile), "AIMMOD_GRENADES_1\t" + ++grenadesSequence + "\n" + body); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- sounds ------------------------------------------------------------------------------------
    readonly GrenadeSoundPlan grenadeSounds = new();
    void GrenadeSoundCues(MatchSnapshot match, CsView cs)
    {
        (double X, double Y, double Yaw)? listener = ownRecent.Count > 0 ? (ownRecent[^1].X, ownRecent[^1].Y, ownRecent[^1].Yaw) : null;
        foreach (var cue in grenadeSounds.Update(match.Id, cs, SelfId, clock() + HostOffset())) roundAudio.Play(cue, listener);
    }

    // Everything above for the local player, once a service tick in a CS round (CsInput).
    void CsGrenades(MatchSnapshot match, CsView cs, CsPlayerView me, bool foreground)
    {
        CsGrenadeInput(match, cs, me, foreground);
        WriteGrenades(match, cs, me);
        GrenadeSoundCues(match, cs);
    }

    // ---- what grenades change for others -----------------------------------------------------------
    // The white of a flash and the grey inside a smoke for the HUD (0..1).
    internal (double Flash, double Smoke) GrenadeVeil(CsView cs, CsPlayerView me, long hostNow)
    {
        var flash = GrenadeRules.FlashAlpha(me.Flash, hostNow);
        var smoke = ownRecent.Count > 0 && me.Alive ? GrenadeRules.SmokeInside(cs.Grenades, [ownRecent[^1].X, ownRecent[^1].Y, ownRecent[^1].Z], hostNow) : 0;
        return (flash, smoke);
    }

    // Bots see nothing through a smoke and nothing at all while a flash has them white (host).
    internal static Dictionary<string, BotSight> GrenadeSight(CsView? cs, IReadOnlyDictionary<string, BotSight> sight, IReadOnlyList<BotPlayer> players, long hostNow)
    {
        var result = new Dictionary<string, BotSight>(StringComparer.Ordinal);
        foreach (var (member, s) in sight)
        {
            if (cs is null) { result[member] = s; continue; }
            var me = cs.Players.FirstOrDefault(p => p.Member == member);
            if (GrenadeRules.FlashAlpha(me?.Flash, hostNow) >= 0.6) { result[member] = s with { Visible = new HashSet<int>() }; continue; }
            // The bot's eye as the brain has it (its avatar's floor plus eye height), else above its capsule.
            var eye = players.FirstOrDefault(p => p.Member == member) is { } self ? new[] { self.X, self.Y, self.Z } : new[] { s.X, s.Y, s.Z + BotBrain.EyeAboveCentre };
            var visible = s.Visible.Where(tag => players.FirstOrDefault(p => p.Tag == tag) is not { } target || !GrenadeRules.SmokeBlocks(cs.Grenades, eye, [target.X, target.Y, target.Z], hostNow)).ToHashSet();
            result[member] = visible.Count == s.Visible.Count ? s : s with { Visible = visible };
        }
        return result;
    }

    // Bot grenades (BotGrenades): their buys and throws, applied like a bot's other actions.
    readonly BotGrenades botGrenades = new();
    internal IBotGrenadePolicy? BotGrenadePolicy { get; set; }
    void StepBotGrenades(BotWorld world)
    {
        if (core is null || world.Cs is null) return;
        var step = (BotGrenadePolicy ?? botGrenades).Step(world);
        foreach (var (bot, item) in step.Buys) core.Apply(bot, "buy", JsonSerializer.SerializeToElement(new { item }, Protocol.Json), library);
        foreach (var t in step.Throws) core.BotThrow(t.Bot, t.Kind, t.From, t.Velocity);
    }
}
