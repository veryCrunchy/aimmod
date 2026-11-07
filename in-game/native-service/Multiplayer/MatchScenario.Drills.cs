namespace AimMod.InGame.Multiplayer;

static partial class MatchScenario
{
    // Reuse the base map, weapons and profile references. Offsets are in world
    // units around its bot spawn; the reference distance is 1000 cm.
    public static string GenerateDrill(string baseText, DrillSpec spec)
    {
        if (!PracticeDrills.Valid(spec)) throw new ArgumentException("Invalid drill parameters.");
        var (header, sections, map, nl) = Parse(baseText);
        var player = header.Get("PlayerProfile");
        if (player is null || map is null || !sections.Any(s => s.Title == "[Bot Profile]"))
            throw new ArgumentException("Choose a scenario with embedded bots and a map.");
        var bodies = sections.Where(s => s.Title == "[Character Profile]" && s.Get("Name") != player).ToArray();
        if (bodies.Length == 0) throw new ArgumentException("The scenario has no target body.");
        header.Set("Name", PracticeDrills.Name(spec, baseText));
        header.Set("Description", PracticeDrills.Marker + "Not ranked. Spawn offsets use a 1000 cm reference distance.");
        header.Set("SearchTags", "AimMod, AimMod Drill, Freeplay");
        header.Set("IsChallenge", "false");
        header.Set("Timelimit", "60.0");
        header.Set("ScorePerHit", "0.0");header.Set("ScorePerKill", "0.0");header.Set("ScorePerDamage", "0.0");
        var tracking = spec.Template is "tracking" or "reactive";
        var radius = 1000*Math.Tan(spec.Radius*Math.PI/180);
        var offset = 1000*Math.Tan(spec.Amplitude*Math.PI/360);
        var angle = spec.Sector*Math.PI/4;
        var y = offset*Math.Cos(angle); var z = offset*Math.Sin(angle);
        foreach (var body in bodies) {
            foreach (var key in new[] { "MainBBRadius", "ProjBBRadius", "MainBBHeadRadius", "ProjBBHeadRadius" }) body.Set(key,Num(radius));
            body.Set("MainBBHeight",Num(radius*2));body.Set("ProjBBHeight",Num(radius*2));
            body.Set("SpawnOffsetMin", "X=0.0 Y="+Num(Math.Min(y*.8,y*1.2))+" Z="+Num(Math.Min(z*.8,z*1.2)));
            body.Set("SpawnOffsetMax", "X=0.0 Y="+Num(Math.Max(y*.8,y*1.2))+" Z="+Num(Math.Max(z*.8,z*1.2)));
            body.Set("MaxSpeed",Num(tracking ? spec.Speed*Math.PI/180*1000 : 0));
            body.Set("Acceleration",Num(tracking ? spec.Speed*Math.PI/180*10000 : 0));
        }
        foreach (var bot in sections.Where(s => s.Title == "[Bot Profile]")) {
            bot.Set("NoDodging",tracking ? "false" : "true");bot.Set("StandStillUntilHurt","false");
            bot.Set("DisableScoring","true");bot.Set("UseWeapons","false");
        }
        foreach (var dodge in sections.Where(s => s.Title == "[Dodge Profile]")) {
            dodge.Set("ToggleLeftRight",tracking ? "true" : "false");dodge.Set("ToggleForwardBack","false");
            dodge.Set("MinLRTimeChange",spec.Template == "reactive" ? "0.2" : "1.5");
            dodge.Set("MaxLRTimeChange",spec.Template == "reactive" ? "0.6" : "2.0");
        }
        var result = Write(header,sections,map,nl);
        if (Validate(baseText,result).Count != 0) throw new ArgumentException("The base scenario has unsupported profile references.");
        return result;
    }
}
