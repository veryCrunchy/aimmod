using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// KovaaK's binds against its defaults: read from Input.ini, compared only for what the match's
// scenario uses, shown in the lobby, on the load screen and once in game; never rebinds or blocks.
static partial class MultiplayerChecks
{
    const string WalkPort = "Name=Synthetic Walk Port\nPlayerCharacters=Walker\nBotCharacters=target.bot\nTimelimit=60.0\nPlayerProfile=Walker\nMapName=synthetic_map.map\nMapScale=4.0\n\n"
        + "[Bot Profile]\nName=target\nCharacterProfile=target\n\n"
        + "[Character Profile]\nName=Walker\nMaxHealth=100.0\nWeaponProfileNames=Synthetic Rifle;;;;;;;\nMaxSpeed=1000.0\nJumpVelocityMax=1208.0\nCanCrouch=true\nAbilityProfileNames=Synthetic Walk.abilsprint;;;\n\n"
        + "[Character Profile]\nName=target\nMaxSpeed=800.0\nAbilityProfileNames=;;;\n\n"
        + "[Sprint Ability Profile]\nName=Synthetic Walk\nSpeedModifier=0.52\n\n"
        + "[Weapon Profile]\nName=Synthetic Rifle\nType=Hitscan\nTimeBetweenShots=0.1\nMagazineMax=30\n\n[Map Data]\nreflex map version 8\nglobal\n";

    static void Binds(string root)
    {
        // Input.ini as KovaaK's writes it once a bind changes.
        var ini = new[]
        {
            "[/Script/Engine.InputSettings]",
            "ActionMappings=(ActionName=\"Ability1\",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=Q)",
            "ActionMappings=(ActionName=\"Crouch\",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=LeftControl)",
            "ActionMappings=(ActionName=\"Crouch\",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=C)",
            "ActionMappings=(ActionName=\"Reload\",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=None)",
            "ActionMappings=(ActionName=\"Jump\",bShift=False,bCtrl=True,bAlt=False,bCmd=False,Key=SpaceBar)",
            "AxisMappings=(AxisName=\"MoveForward\",Scale=1.000000,Key=W)",
            "AxisMappings=(AxisName=\"MoveForward\",Scale=-1.000000,Key=S)",
            "[/Script/Engine.Console]",
            "ActionMappings=(ActionName=\"Fire\",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=None)",
        };
        var bound = GameBinds.Parse(ini);
        Check(bound.Of("Ability1").SequenceEqual(["Q"]) && bound.Of("Crouch").SequenceEqual(["LeftControl", "C"]) && bound.Of("Reload").Count == 0 && bound.Of("Jump").SequenceEqual(["Ctrl+SpaceBar"]),
            "Input.ini binds are read per action: several keys, Key=None as no key, modifiers kept");
        Check(bound.Of("Fire").SequenceEqual(["LeftMouseButton"]) && bound.Of("MoveRight-").SequenceEqual(["A"]) && bound.Of("MoveForward-").SequenceEqual(["S"]),
            "Actions Input.ini doesn't list (or lists outside InputSettings) keep KovaaK's defaults");
        Check(GameBinds.Defaults["Ability1"] == "LeftShift" && GameBinds.Defaults["Crouch"] == "LeftControl" && GameBinds.Defaults["Jump"] == "SpaceBar" && GameBinds.Defaults["Reload"] == "R" && GameBinds.Defaults["Fire"] == "LeftMouseButton",
            "The standard set is KovaaK's 3.9.11 DefaultInput.ini");

        var cs = GameBinds.Uses(WalkPort, LobbyModes.Cs);
        Check(cs.Any(u => u is { Action: "Ability1", Label: "Walk" }) && cs.Any(u => u.Action == "Crouch") && cs.Any(u => u.Action == "Jump") && cs.Any(u => u.Action == "Reload")
            && cs.Any(u => u is { Action: "Weapon2", Label: "Pistol" }), "A CS port uses walk (sprint profile slower than 1), crouch, jump, reload and the pistol slot");
        var quake = GameBinds.Uses(WalkPort.Replace("SpeedModifier=0.52", "SpeedModifier=1.3").Replace("CanCrouch=true", "CanCrouch=false").Replace("MagazineMax=30", "MagazineMax=0"), LobbyModes.Deathmatch);
        Check(quake.Any(u => u is { Action: "Ability1", Label: "Sprint" }) && !quake.Any(u => u.Action is "Crouch" or "Reload" or "Weapon1" or "Weapon2"),
            "A sprint port uses sprint; no crouch, reload or slot keys where the scenario has none");
        Check(GameBinds.Uses(BaseScenario, LobbyModes.Race).Select(u => u.Action).Order().SequenceEqual(new[] { "Fire", "Jump", "MoveForward+", "MoveForward-", "MoveRight+", "MoveRight-" }.Order()),
            "A plain scenario only asks for moving, jumping and firing");

        var rows = GameBinds.Rows(cs, bound, [("Plant / defuse", "E"), ("Buy", "B")]);
        var issues = GameBinds.Issues(rows);
        Check(issues.SequenceEqual(["Jump is on Ctrl+Space here (usually Space)", "Walk is on Q here (usually Shift)", "Reload has no key"]),
            "Only used actions off their usual key are reported, in short lines: " + string.Join(" | ", issues));
        Check(GameBinds.Issues(GameBinds.Rows(cs, GameBinds.Bound.Standard, [("Plant / defuse", "E"), ("Buy", "B")])).Count == 0, "KovaaK's defaults report nothing");
        var clash = GameBinds.Parse(["[/Script/Engine.InputSettings]", "ActionMappings=(ActionName=\"Reload\",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=B)"]);
        Check(GameBinds.Issues(GameBinds.Rows(cs, clash, [("Buy", "B")])).SequenceEqual(["Reload is on B here (usually R)", "Buy and Reload are both on B"]),
            "A used KovaaK's action on one of AimMod's own keys is named");
        Check(GameBinds.KeyName("ThumbMouseButton") == "Mouse 4" && GameBinds.KeyName("Ctrl+Two") == "Ctrl+2" && GameBinds.KeyName("F") == "F", "Keys read as players know them");

        // The service: lobby row, load screen line, one in-game notice per match; nothing waits on it.
        var game = Path.Combine(root, "binds-game");
        WriteText(Path.Combine(game, "Saved", "SaveGames", "Scenarios", "Synthetic Walk Port.sce"), WalkPort);
        var walkOnQ = GameBinds.Parse(ini.Where(l => !l.Contains("Jump", StringComparison.Ordinal) && !l.Contains("Reload", StringComparison.Ordinal)));
        foreach (var variant in new[] { "hide", "timeout", "standard" })
        {
            long now = 8_000_000;
            var output = Path.Combine(root, "binds-output-" + variant);
            Directory.CreateDirectory(output);
            var service = new MultiplayerService(new OfflineTransport(), new ContentLibrary(game), new FakeGame("load", "start") { Root = game }, () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, output, simulation: true, () => now, autoTick: false, seed: 13);
            if (variant != "standard") service.Binds = () => walkOnQ;
            JsonElement Lobby() => JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("lobby");
            JsonElement Notice() => JsonDocument.Parse(service.NoticeText()).RootElement;
            string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool KeysNotice() => Str(Notice(), "id")?.StartsWith("keys-", StringComparison.Ordinal) == true;
            void Run(int ms) { for (var t = 0; t < ms; t += 100) { now += 100; service.Tick(); } }
            service.Act("create", J(new { mode = "deathmatch", scenario = "Synthetic Walk Port" }));
            service.Act("sim", J(new { op = "add" }));
            Run(12_000);
            var view = Lobby().GetProperty("binds");
            var expected = variant == "standard" ? Array.Empty<string>() : ["Walk is on Q here (usually Shift)"];
            Check(view.GetProperty("issues").EnumerateArray().Select(i => i.GetString()).SequenceEqual(expected)
                && view.GetProperty("rows").EnumerateArray().Any(r => Str(r, "label") == "Walk" && Str(r, "keys") == (variant == "standard" ? "Shift" : "Q") && r.GetProperty("standard").GetBoolean() == (variant == "standard"))
                && view.GetProperty("rows").EnumerateArray().Any(r => Str(r, "label") == "Scoreboard" && r.GetProperty("aimmod").GetBoolean()),
                "The lobby lists the match's keys and any that differ (" + variant + ")");
            Check(service.Act("start", default).Ok, "A differing bind never blocks the start (" + variant + ")");
            Run(100);
            Check(Str(Lobby().GetProperty("match"), "phase") == MatchPhases.Loading && Str(Notice(), "title")!.StartsWith("Waiting for everyone to load", StringComparison.Ordinal) && Str(Notice(), "note") == (variant == "standard" ? null : expected[0]),
                "The load screen notice carries the keybind line (" + variant + ")");
            for (var i = 0; i < 100 && Str(Lobby().GetProperty("match"), "phase") == MatchPhases.Loading; i++) Run(100);
            Check(Str(Notice(), "title")!.StartsWith("Match starting", StringComparison.Ordinal) && Str(Notice(), "note") == (variant == "standard" ? null : expected[0]),
                "So does the countdown (" + variant + ")");
            for (var i = 0; i < 400 && Str(Lobby().GetProperty("match"), "phase") != MatchPhases.Live; i++) Run(100);
            Check(Str(Lobby().GetProperty("match"), "phase") == MatchPhases.Live, "The match goes live (" + variant + ")");
            if (variant == "standard") Check(!KeysNotice(), "Standard binds: no keybind notice in game");
            else
            {
                var toast = Notice();
                Check(KeysNotice() && Str(toast, "title") == expected[0] && Str(toast, "eyebrow") == "AimMod · Keybinds" && !toast.GetProperty("interactive").GetBoolean(),
                    "In game, a short notice names the bind once the match is live, without taking clicks (" + variant + ")");
                if (variant == "hide") { service.Hotkey(); Check(!KeysNotice(), "The lobby key hides it"); }
                Run(variant == "hide" ? 1000 : 8000);
                Check(!KeysNotice(), variant == "hide" ? "Hidden stays hidden for the match" : "It ends by itself after a few seconds, once per match");
            }
            service.Act("leave", default);
            Run(300);
            service.Dispose();
        }
    }
}
