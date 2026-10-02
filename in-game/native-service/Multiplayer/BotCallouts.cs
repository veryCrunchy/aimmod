namespace AimMod.InGame.Multiplayer;

// Bots type their callouts in chat like players (MultiplayerService.BotSay): short, natural and
// varied, team only, with the place from the map (its callout zones, else the site, else near a
// site, a spawn or mid). What they say comes from what the brain noticed (BotBrain's BotStep.Events)
// and the throws the service made for them:
//  - enemies spotted, how many and where ("2 B site", "one long, he's low"); the last one;
//  - utility thrown ("Smoking A main", "Flashing out", "Molly on the bomb");
//  - the plan ("Going A", "Split B", "Lurking B", "I'll hold A"), rotations ("Rotating B");
//  - the bomb ("Planting A", "Planted A", "Bomb down mid", "Defusing, cover me", "They're defusing!");
//  - its own death ("I died A, 2 there, one low"), being low, a decoy called out (Hard), saving.
// How chatty: every bot its own nature (a steady coin), Easy ones say less and type slower. At most
// one line a bot every 3 s, and a team never sees the same line twice within 5 s. "gg"/"nt" style
// lines at a round's end go to everyone, rarely.
sealed record BotEvent(string Bot, string Kind, int Count = 0, double[]? At = null, string? Place = null, string? Detail = null, long T = 0);
sealed record BotChat(string Bot, bool TeamOnly, string Text);

sealed class BotCallouts(int seed = 0)
{
    public const long PerBotMs = 3000, SameLineMs = 5000, StaleMs = 3500, ImportantStaleMs = 6000;
    readonly Random random = seed == 0 ? new Random() : new Random(seed);
    readonly Dictionary<string, long> lastSaid = new(StringComparer.Ordinal);
    readonly List<(string Team, string Text, long T)> recent = [];
    readonly List<(long At, long Since, BotChat Line, string Team, bool Important)> pending = [];

    // The kinds always said (if the bot types at all); the others by its chattiness.
    static bool Important(string kind) => kind is "planting" or "planted" or "dropped" or "defusing" or "defuse-heard" or "died" or "last" or "save" or "decoy";

    // How chatty a bot is: its own nature (0.5..1), less for Easy.
    public static double Chattiness(string bot, string? skill) =>
        (0.5 + BotStrategy.Hash(bot + "#chat") % 1000 / 2000.0) * (skill == BotSkills.Easy ? 0.6 : skill == BotSkills.Hard ? 1.0 : 0.85);
    // How long it takes to type (ms): Easy bots are slow.
    public static (int Lo, int Hi) Typing(string? skill) => skill switch { BotSkills.Easy => (900, 2200), BotSkills.Hard => (100, 400), _ => (250, 800) };

    // One step: the lines to post now. `teamOf`: a bot's team key; `skillOf`: its difficulty.
    public IReadOnlyList<BotChat> Step(long now, IEnumerable<BotEvent> events, Func<string, string> teamOf, Func<string, string?> skillOf) => Step(now, events, teamOf, skillOf, null);
    // `say` (checks): the line for an event instead of one of its usual ones.
    internal IReadOnlyList<BotChat> Step(long now, IEnumerable<BotEvent> events, Func<string, string> teamOf, Func<string, string?> skillOf, Func<BotEvent, string?>? say)
    {
        string? Line(BotEvent e) => say is not null ? say(e) : Text(e, random.Next(1000));
        foreach (var e in events)
        {
            var skill = skillOf(e.Bot);
            var important = Important(e.Kind);
            if (e.Kind == "round-end")
            {
                // Rare, to everyone.
                if (random.NextDouble() < 0.15 * Chattiness(e.Bot, skill) && Line(e) is { } bye)
                    pending.Add((now + 600 + random.Next(1500), now, new BotChat(e.Bot, false, bye), "all", false));
                continue;
            }
            var chance = important ? (skill == BotSkills.Easy ? 0.85 : 1) : Chattiness(e.Bot, skill);
            if (random.NextDouble() >= chance || Line(e) is not { } text) continue;
            var (lo, hi) = Typing(skill);
            pending.Add((now + lo + random.Next(hi - lo), now, new BotChat(e.Bot, true, text), teamOf(e.Bot), important));
        }
        recent.RemoveAll(r => now - r.T > SameLineMs);
        var said = new List<BotChat>();
        foreach (var p in pending.OrderBy(x => x.At).ToList())
        {
            if (now - p.Since > (p.Important ? ImportantStaleMs : StaleMs)) { pending.Remove(p); continue; } // too late to matter
            if (p.At > now) continue;
            if (lastSaid.TryGetValue(p.Line.Bot, out var last) && now - last < PerBotMs) continue; // its turn later
            pending.Remove(p);
            if (p.Line.TeamOnly && recent.Any(r => r.Team == p.Team && string.Equals(r.Text, p.Line.Text, StringComparison.OrdinalIgnoreCase))) continue; // a teammate just said it
            lastSaid[p.Line.Bot] = now;
            recent.Add((p.Team, p.Line.Text, now));
            said.Add(p.Line);
        }
        return said;
    }

    static string Pick(int variant, params string[] options) => options[variant % options.Length];
    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // The line for an event (one of a few ways to say it), or null when there is nothing to say.
    public static string? Text(BotEvent e, int variant)
    {
        var p = e.Place;
        var n = e.Count;
        string Many() => n <= 1 ? Pick(variant, "one", "1") : n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        switch (e.Kind)
        {
            case "spotted":
            {
                var low = e.Detail == "low" ? Pick(variant / 3, ", one low", ", he's low", ", one lit") : "";
                if (p is null) return n <= 1 ? Pick(variant, "Contact", "One here", "Enemy here") + low : n + Pick(variant, " here", " enemies here", " on me") + low;
                return (n <= 1 ? Pick(variant, "One " + p, "1 " + p, "Enemy " + p, "One at " + p) : Pick(variant, n + " " + p, n + " enemies " + p, n + " at " + p, n + " " + p + "!")) + low;
            }
            case "last":
                return p is null ? Pick(variant, "Last one", "One left") : Pick(variant, "Last one " + p, "Last one, " + p, "Last " + p);
            case "utility":
                return e.Detail switch
                {
                    GrenadeRules.Smoke => p is null ? "Smoke out" : Pick(variant, "Smoking " + p, "Smoke " + p, "Smoke going " + p),
                    GrenadeRules.Flash => p is null || variant % 3 == 0 ? Pick(variant, "Flashing out", "Flash out", "Flashing") : "Flashing " + p,
                    GrenadeRules.Molotov or GrenadeRules.Incendiary => e.Place == "the bomb" ? Pick(variant, "Molly on the bomb", "Burning the bomb") : p is null ? "Molly out" : Pick(variant, "Molly " + p, "Burning " + p),
                    GrenadeRules.He => p is null ? "Nade out" : Pick(variant, "HE " + p, "Nade " + p, "HE out"),
                    GrenadeRules.Decoy => "Decoy out",
                    _ => null,
                };
            case "plan":
            {
                var d = e.Detail ?? "";
                var site = d.Contains(' ') ? d[(d.IndexOf(' ') + 1)..] : "";
                return d.Split(' ')[0] switch
                {
                    "go" => Pick(variant, "Going " + site, site + " exec", "Let's go " + site, "Hit " + site),
                    "rush" => Pick(variant, "Rush " + site, "Rushing " + site + ", go go", "Rush " + site + " no stop"),
                    "split" => Pick(variant, "Split " + site, "Splitting " + site, "Split " + site + ", half each way"),
                    "default" => Pick(variant, "Default, spread out", "Slow default", "Default, play for info"),
                    "lurk" => Pick(variant, "Lurking " + site, "I'll lurk " + site, "Lurk " + site),
                    "anchor" => Pick(variant, "I'll hold " + site, "Anchoring " + site, "Got " + site, site + " for me"),
                    "forward" => Pick(variant, "Playing " + (p ?? "mid"), "Pushing up for info", "I'll take " + (p ?? "mid")),
                    "stack" => Pick(variant, "Stack " + site, "Stacking " + site),
                    _ => null,
                };
            }
            case "rotate":
                return p is null ? "Rotating" : Pick(variant, "Rotating " + p, "Coming " + p, "On my way " + p, "Rotating " + p + ", hold");
            case "retake":
                return p is null ? "Retake, group up" : Pick(variant, "Group up for the retake " + p, "Retake " + p + " together", "Wait for me, retake " + p);
            case "planting":
                return Pick(variant, p is null ? "Planting" : "Planting " + p, "Planting, cover me", p is null ? "Planting, cover" : "Planting " + p + ", cover");
            case "planted":
                return p is null ? "Planted" : Pick(variant, "Planted " + p, "Bomb planted " + p, "Bomb's down " + p);
            case "dropped":
                return p is null ? "Bomb dropped" : Pick(variant, "Bomb down " + p, "Dropped the bomb " + p, "Bomb's dropped " + p);
            case "defusing":
                return Pick(variant, "Defusing, cover me", "On the bomb, cover", "Defusing!");
            case "defuse-heard":
                return Pick(variant, "They're defusing!", "Defuse!", "He's on the bomb!", "Stick! They're defusing");
            case "died":
            {
                var there = n <= 1 ? Pick(variant / 2, "1 there", "one there") : n + " there";
                var low = e.Detail == "low" ? Pick(variant / 3, ", one low", ", one's lit", ", hit one") : "";
                return Cap((p is null ? Pick(variant, "Dead", "I died") : Pick(variant, "I died " + p, "Dead " + p, "Died " + p)) + ", " + there + low);
            }
            case "low":
                return e.Count > 0 ? Pick(variant, "I'm low", "Low, " + e.Count + " hp", "Lit, " + e.Count) : "I'm low";
            case "decoy":
                return p is null ? "That's a decoy" : Pick(variant, "Decoy " + p + ", ignore it", "That's a decoy " + p, "Fake " + p + ", decoy");
            case "save":
                return Pick(variant, "Saving", "I'm saving", "Saving, can't make it");
            case "round-end":
                return e.Detail == "won" ? Pick(variant, "nt", "gg", "nice try") : Pick(variant, "nice", "nice one", "ns", "wp");
        }
        return null;
    }

    // Where a point is, as players call it: the map's callout zone, else its site, else near a site,
    // a spawn, or mid.
    public static string? Place(MapObjectives? map, double[]? at)
    {
        if (map is null || at is not { Length: >= 3 }) return null;
        if (map.CalloutAt(at[0], at[1], at[2]) is { Length: > 0 } callout) return callout;
        if (map.SiteAt(at[0], at[1], at[2]) is { Length: > 0 } site) return site + " site";
        foreach (var z in map.BombSites.OrderBy(z => Math.Pow((z.Min[0] + z.Max[0]) / 2 - at[0], 2) + Math.Pow((z.Min[1] + z.Max[1]) / 2 - at[1], 2)))
        {
            var half = Math.Sqrt(Math.Pow(z.Max[0] - z.Min[0], 2) + Math.Pow(z.Max[1] - z.Min[1], 2)) / 2;
            var d = Math.Sqrt(Math.Pow((z.Min[0] + z.Max[0]) / 2 - at[0], 2) + Math.Pow((z.Min[1] + z.Max[1]) / 2 - at[1], 2));
            if (d < Math.Max(half * 2.5, 1800)) return z.Name;
            break;
        }
        foreach (var side in new[] { CsRules.T, CsRules.CT })
            if (map.BuyZones(side).Any(z => z.Contains(at[0], at[1], at[2], 400))) return side + " spawn";
        return "mid";
    }
}
