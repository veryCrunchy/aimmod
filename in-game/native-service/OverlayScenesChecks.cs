using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AimMod.InGame;

// Overlay scenes: the store's canonical form and persistence, the migration from the
// earlier overlay settings, the feed's history numbers, KovaaK's settings parsing,
// the motion buffer, and the workspace and OBS routes. Synthetic data only.
static class OverlayScenesChecks
{
    sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Overlay checks must not contact Hub.");
    }
    static int count;
    static void Check(bool ok, string name) { count++; if (!ok) throw new Exception("Overlay scenes check failed: " + name); }

    public static async Task Run()
    {
        count = 0;
        Store(); Migration(); History(); Settings(); Motion();
        await Routes();
        Console.WriteLine($"PASS {count} overlay scene checks");
    }

    static string Widget(string id, string type, string extra = "") => "{\"id\":\"" + id + "\",\"type\":\"" + type + "\"" + extra + "}";
    static void Store()
    {
        var folder = Temp();
        try
        {
            var store = new OverlayScenes(folder, OverlaySettingsValue.Default);
            var raw = "{\"v\":1,\"obsScene\":\"nope\",\"gameScene\":\"b\",\"evil\":1,\"scenes\":[{\"id\":\"a\",\"name\":\"  A  \",\"theme\":{\"preset\":\"neon\",\"alpha\":9},\"widgets\":["
                + Widget("w1", "live-stats", ",\"x\":-5,\"y\":99999,\"w\":1,\"opacity\":7,\"font\":0.1,\"variant\":\"tiny\",\"accent\":\"#ABCDEF\",\"background\":\"url(x)\",\"show\":{\"menu\":true},\"opts\":{\"m_score\":false,\"bad key\":1,\"text\":\"" + new string('x', 400) + "\",\"nested\":{\"a\":1}},\"extra\":1")
                + "," + Widget("w1", "clock") + "," + Widget("BAD", "clock") + "," + Widget("w2", "flamethrower") + "]},{\"id\":\"a\"},{\"id\":\"b\",\"name\":\"\"}],"
                + "\"profile\":{\"peripherals\":[{\"label\":\"Mouse\",\"value\":\"Example\\u0001 mouse\"},{\"label\":\"\",\"value\":\"\"}],\"secret\":1}}";
            var saved = JsonNode.Parse(store.Save(Encoding.UTF8.GetBytes(raw)))!.AsObject();
            Check(string.Join(',', saved.Select(p => p.Key)) == "v,obsScene,gameScene,scenes,profile", "only known store fields are kept");
            var scenes = saved["scenes"]!.AsArray();
            Check(scenes.Count == 2 && (string)saved["obsScene"]! == "a" && (string)saved["gameScene"]! == "b" && (string)scenes[0]!["name"]! == "A" && (string)scenes[1]!["name"]! == "Scene", "scene ids are unique and assignments valid");
            Check((string)scenes[0]!["theme"]!["preset"]! == "mint" && (double)scenes[0]!["theme"]!["alpha"]! == 1, "theme preset and alpha bounded");
            var w = scenes[0]!["widgets"]!.AsArray();
            Check(w.Count == 1, "duplicate, malformed and unknown widgets dropped");
            var x = w[0]!.AsObject();
            Check((double)x["x"]! == 0 && (double)x["y"]! == 1060 && (double)x["w"]! == 60 && (double)x["opacity"]! == 1 && (double)x["font"]! == 0.6, "numbers clamp to the canvas and ranges");
            Check((string)x["variant"]! == "expanded" && (string)x["layout"]! == "horizontal" && (string)x["accent"]! == "#abcdef" && (string)x["background"]! == "" && x["extra"] is null, "enumerations, colours and unknown fields");
            Check((bool)x["show"]!["menu"]! && (bool)x["show"]!["scenario"]! && (bool)x["show"]!["match"]!, "contexts default per widget");
            var opts = x["opts"]!.AsObject();
            Check(opts.Count == 2 && (bool)opts["m_score"]! == false && ((string)opts["text"]!).Length == 160 && opts["nested"] is null, "options bounded generically");
            Check((string)saved["profile"]!["peripherals"]![0]!["value"]! == "Example mouse" && saved["profile"]!["peripherals"]!.AsArray().Count == 1 && saved["profile"]!["secret"] is null, "peripherals cleaned");
            Check(Encoding.UTF8.GetString(store.Save(store.Bytes)) == Encoding.UTF8.GetString(store.Bytes), "canonical form is idempotent");
            var again = new OverlayScenes(folder, OverlaySettingsValue.Default);
            Check(again.Bytes.AsSpan().SequenceEqual(store.Bytes), "scenes survive restart");
            var revision = store.Revision; store.Save(store.Bytes); Check(store.Revision == revision + 1, "every save changes the revision");
            foreach (var bad in new[] { "[]", "{}", "{\"scenes\":[]}", "{\"scenes\":[{\"id\":\"Bad Id\"}]}", "not json" })
            {
                var rejected = false;
                try { store.Save(Encoding.UTF8.GetBytes(bad)); } catch (JsonException) { rejected = true; }
                Check(rejected && again.Bytes.AsSpan().SequenceEqual(new OverlayScenes(folder, OverlaySettingsValue.Default).Bytes), "invalid store rejected without touching the saved one: " + bad);
            }
            var big = "{\"scenes\":[" + string.Join(',', Enumerable.Range(0, 20).Select(i => "{\"id\":\"s" + i + "\",\"widgets\":[" + string.Join(',', Enumerable.Range(0, 40).Select(j => Widget("w" + j, "text"))) + "]}")) + "]}";
            var bounded = JsonNode.Parse(store.Save(Encoding.UTF8.GetBytes(big)))!["scenes"]!.AsArray();
            Check(bounded.Count == OverlayScenes.MaxScenes && bounded[0]!["widgets"]!.AsArray().Count == OverlayScenes.MaxWidgets, "scene and widget counts bounded");
            var oversize = false; try { store.Save(new byte[OverlayScenes.Limit + 1]); } catch (JsonException) { oversize = true; }
            Check(oversize, "oversized store rejected");
            File.WriteAllText(Path.Combine(folder, "overlay-scenes.json"), "{damaged");
            var recovered = JsonNode.Parse(new OverlayScenes(folder, OverlaySettingsValue.Default).Bytes)!;
            Check(recovered["scenes"]!.AsArray().Count == 3, "a damaged file starts from the migrated defaults");
        }
        finally { Directory.Delete(folder, true); }
    }

    static void Migration()
    {
        var legacy = OverlaySettingsValue.Default with
        {
            Opacity = 0.5, Stats = new(true, 1, 100, 560), Versus = new(false, 50, 10, 300),
            Obs = new OverlayLayout(1, new(true, 0, 0, 200), new(true, 100, 100, 300)),
            Layouts = [new SavedOverlayLayout("Practice", new OverlayLayout(1, new(true, 10, 10, 300), new(true, 90, 90, 300)))],
        };
        var m = OverlayScenes.Clean(OverlayScenes.Migrate(legacy))!;
        var scenes = m["scenes"]!.AsArray();
        string Shape(JsonNode w) => (string)w["type"]! + ":" + (double)w["x"]! + "," + (double)w["y"]! + "," + (double)w["w"]! + "," + (bool)w["visible"]!;
        var game = scenes[0]!["widgets"]!.AsArray().Select(w => Shape(w!)).ToArray();
        // The same numbers as ui/overlay-model.test.cjs (defaultStore): one migration on both sides.
        Check(string.Join('|', game) == "live-stats:19,960,616,True|pb-pace:960,108,375,False", "in-game cards keep their places: " + string.Join('|', game));
        Check((double)scenes[0]!["theme"]!["alpha"]! == 0.44, "legacy opacity becomes the scene's panel opacity");
        var obs = scenes[1]!["widgets"]!.AsArray().Select(w => Shape(w!)).ToArray();
        Check(string.Join('|', obs) == "live-stats:0,0,220,True|pb-pace:1545,930,375,True", "OBS cards keep their places: " + string.Join('|', obs));
        Check((string)m["obsScene"]! == "stream" && (string)m["gameScene"]! == "game", "surfaces keep their scene");
        Check(scenes.Count == 4 && (string)scenes[3]!["name"]! == "Practice" && (string)scenes[3]!["id"]! == "layout-1", "named layouts become scenes");
        Check(scenes[2]!["widgets"]!.AsArray().Any(w => (string)w!["type"]! == "settings"), "a settings card scene is added");
    }

    static Run R(string id, string scenario, double score, DateTimeOffset at, double? accuracy = 50) => new(id, scenario, score, accuracy, 60, 0, 0, at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture), null, null, null, null, false);
    static void History()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var runs = new[]
        {
            R("1", "Synthetic A", 100, t0.AddHours(-5)), R("2", "Synthetic A", 120, t0.AddHours(-4.9)),
            // Two hours later: a new session.
            R("3", "Synthetic A", 110, t0), R("4", "Synthetic A", 130, t0.AddMinutes(2), null), R("5", "Synthetic B", 50, t0.AddMinutes(4)), R("6", "Synthetic A", 125, t0.AddMinutes(6)),
        }.Reverse().ToArray();
        var h = new OverlayHistory(); h.Update(runs);
        var now = t0.AddMinutes(10).ToUnixTimeMilliseconds();
        using var session = JsonDocument.Parse(JsonSerializer.Serialize(h.Session(now), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var s = session.RootElement;
        Check(s.GetProperty("runs").GetInt32() == 4 && s.GetProperty("pbs").GetInt32() == 1 && s.GetProperty("scenarios").GetInt32() == 2, "a break over an hour starts a new session; PBs counted against earlier bests");
        Check(Math.Abs(s.GetProperty("accuracy").GetDouble() - 50) < 1e-9 && s.GetProperty("seconds").GetDouble() == 240, "unknown accuracy is left out of the average");
        var graph = s.GetProperty("graph").EnumerateArray().ToArray();
        Check(graph.Length == 4 && Math.Abs(graph[0].GetProperty("ratio").GetDouble() - 110.0 / 120) < 1e-3 && graph[1].GetProperty("pb").GetBoolean() && graph[2].GetProperty("ratio").GetDouble() == 1, "graph: share of the best before each run");
        Check(Math.Abs(s.GetProperty("bestRatio").GetDouble() - 130.0 / 120) < 1e-3, "best ratio of the session");
        using var later = JsonDocument.Parse(JsonSerializer.Serialize(h.Session(t0.AddHours(3).ToUnixTimeMilliseconds())));
        Check(later.RootElement.GetProperty("runs").GetInt32() == 0, "an hour after the last run the session is over");
        using var recent = JsonDocument.Parse(JsonSerializer.Serialize(h.Recent(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var r = recent.RootElement.EnumerateArray().ToArray();
        Check(r.Length == 6 && r[0].GetProperty("score").GetDouble() == 125 && Math.Abs(r[0].GetProperty("delta").GetDouble() - (125.0 / 130 - 1) * 100) < 0.01 && r[2].GetProperty("pb").GetBoolean(), "recent runs newest first with change against the best before");
        using var sc = JsonDocument.Parse(JsonSerializer.Serialize(h.Scenario(null), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Check(sc.RootElement.GetProperty("name").GetString() == "Synthetic A" && sc.RootElement.GetProperty("best").GetDouble() == 130 && sc.RootElement.GetProperty("attempts").GetInt32() == 5 && sc.RootElement.GetProperty("last").GetDouble() == 125, "scenario info for the latest scenario");
        using var other = JsonDocument.Parse(JsonSerializer.Serialize(h.Scenario("synthetic b"), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Check(other.RootElement.GetProperty("attempts").GetInt32() == 1, "the live scenario is matched case-insensitively");
        var empty = new OverlayHistory(); Check(JsonSerializer.Serialize(empty.Scenario(null)) == "null" && JsonSerializer.Serialize(empty.Session(now)).Contains("\"runs\":0"), "no history: empty values, never invented ones");
    }

    static void Settings()
    {
        const string primary = "﻿{\"integerSettings\":{\"EIntegerSettingId::DPI\":800},\"floatSettings\":{\"EFloatSettingId::XSens\":1.5,\"EFloatSettingId::FOV\":103},\"stringSettings\":{\"EStringSettingId::SensScaleString\":\"Quake/Source\",\"EStringSettingId::FOVScaleString\":\"Overwatch\",\"EStringSettingId::CurrentThemeName\":\"Synthetic Theme\"}}";
        const string weapons = "﻿CrosshairColor=X=1.000 Y=0.500 Z=0.000\r\nCrosshairScale=1.5\r\nCrosshairFile=synthetic_cross.png\r\nOverrideSens=false\r\nHorizontalSens=99\r\nBodyHitSound=hitA;hitA;None;hitB\r\nHeadHitSound=hitA\r\n";
        const string scales = "[{\"ScaleName\":\"Quake/Source\",\"Sens\":{\"InchesFormula\":\"360 / (Inches * 0.022 * DPI)\"}}]";
        var v = KovaaksSettings.Parse(primary, weapons, scales, name => name == "synthetic_cross.png");
        Check(v.Available && v.Dpi == 800 && v.Sens == 1.5 && v.SensScale == "Quake/Source" && v.Fov == 103 && v.FovScale == "Overwatch" && v.Theme == "Synthetic Theme", "global settings read");
        Check(v.Cm360 == Math.Round(360 / (1.5 * 0.022 * 800) * 2.54, 2), "cm/360 from the scale's formula: " + v.Cm360);
        Check(v.Crosshair == "synthetic_cross.png" && v.CrosshairImage == "synthetic_cross.png" && v.CrosshairScale == 1.5 && v.CrosshairColor == "#ff8000", "crosshair file, scale and colour");
        Check(v.HitSounds!.SequenceEqual(new[] { "hitA", "hitB" }), "hit sounds unique, None left out");
        var over = KovaaksSettings.Parse(primary.Replace("Quake/Source", "cm/360"), weapons.Replace("OverrideSens=false", "OverrideSens=true\r\nSensScale=cm/360"), null);
        Check(over.Sens == 99 && over.Cm360 == 99 && over.CrosshairImage is null, "weapon override wins; no image unless the file exists");
        Check(KovaaksSettings.Parse(null, null, null) == KovaaksSettingsView.Unavailable && KovaaksSettings.Parse("{broken", null, null).Available, "missing files unavailable, damaged settings never throw");
        Check(KovaaksSettings.Parse(primary.Replace("Quake/Source", "Unknown scale"), null, scales).Cm360 is null, "an unknown scale has no cm/360");
        foreach (var bad in new[] { "../x.png", "a/b.png", "x.jpg", " x.png", "", "..png" }) Check(!KovaaksSettings.SafeImageName(bad), "unsafe crosshair name refused: " + bad);
        var folder = Temp();
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "Saved", "SaveGames")); Directory.CreateDirectory(Path.Combine(folder, "crosshairs"));
            File.WriteAllText(Path.Combine(folder, "Saved", "SaveGames", "PrimaryUserSettings.json"), primary);
            File.WriteAllText(Path.Combine(folder, "Saved", "SaveGames", "weaponsettings.ini"), weapons);
            File.WriteAllBytes(Path.Combine(folder, "crosshairs", "synthetic_cross.png"), [137, 80, 78, 71]);
            File.WriteAllBytes(Path.Combine(folder, "crosshairs", "other.png"), [137, 80, 78, 71]);
            var reader = new KovaaksSettings(folder);
            Check(reader.Read().CrosshairImage == "synthetic_cross.png", "the current crosshair image is found");
            Check(reader.CrosshairPath("synthetic_cross.png") is not null && reader.CrosshairPath("other.png") is null && reader.CrosshairPath("../weaponsettings.ini") is null, "only the current crosshair image is served");
            Check(new KovaaksSettings(null).Read() == KovaaksSettingsView.Unavailable, "no game folder, no settings");
        }
        finally { Directory.Delete(folder, true); }
    }

    static void Motion()
    {
        var folder = Temp();
        try
        {
            long now = 10_000;
            var m = new OverlayMotion(folder, () => now);
            m.Merge([new(9000, [0, 0, 0, 1, 350, 0, 90]), new(9500, [0, 0, 0, 2, 355, 0, 90]), new(9500, [0, 0, 0, 9, 9, 0, 90])]);
            var w = m.Window(now);
            Check(w.Length == 2 && w[1][0] == 5000 && w[1][1] == 355 && w[1][2] == 2, "poses kept in order as time, yaw, pitch");
            m.Merge([new(16000, [0, 0, 0, 3, 10, 0, 90])]); now = 16100;
            Check(m.Window(now).Length == 1, "the trail keeps five seconds");
            now = 18000; Check(m.Window(now).Length == 0, "a stale trail is empty");
            m.Merge([new(1000, [0, 0, 0, 0, 0, 0, 90])]); now = 1100; Check(m.Window(now).Length == 1, "a clock that jumps back starts over");
            Check(OverlayMotion.Keys.Select(k => k.Name).SequenceEqual(new[] { "w", "a", "s", "d", "space", "shift", "ctrl", "lmb", "rmb" }), "the input display reads a fixed set of keys only");
            using var path = JsonDocument.Parse(JsonSerializer.Serialize(m.Read(true, false)));
            Check(File.Exists(Path.Combine(folder, "self-pose.request")) && path.RootElement.GetProperty("input").ValueKind == JsonValueKind.Null, "the path asks AimModCore for poses; input stays off unless asked");
        }
        finally { Directory.Delete(folder, true); }
    }

    static async Task Routes()
    {
        var folder = Temp();
        try
        {
            using var hub = new Hub(folder, new NoNetwork(), openBrowser: _ => throw new InvalidOperationException("No browser in overlay checks."));
            var game = Path.Combine(folder, "game"); Directory.CreateDirectory(Path.Combine(game, "Saved", "SaveGames"));
            File.WriteAllText(Path.Combine(game, "Saved", "SaveGames", "PrimaryUserSettings.json"), "{\"integerSettings\":{\"EIntegerSettingId::DPI\":1200}}");
            await using var host = new WorkspaceHost(hub, folder, gameFolder: game);
            await host.Start(CancellationToken.None);
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            var root = host.Url[..^3];
            using (var scenes = JsonDocument.Parse(await client.GetStringAsync(root + "/overlay-scenes")))
                Check(scenes.RootElement.GetProperty("scenes").GetArrayLength() == 3 && scenes.RootElement.GetProperty("gameScene").GetString() == "game", "first start serves the migrated scenes");
            Check(File.Exists(Path.Combine(folder, "overlay-scenes.json")), "the migrated scenes are saved");
            foreach (var asset in new[] { ("overlay-model.js", "application/javascript"), ("overlay-widgets.js", "application/javascript"), ("scene", "text/html"), ("overlay", "text/html") })
                using (var response = await client.GetAsync(root + "/" + asset.Item1))
                    Check(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == asset.Item2 && (await response.Content.ReadAsStringAsync()).Length > 100, "overlay asset served: " + asset.Item1);
            using (var feed = JsonDocument.Parse(await client.GetStringAsync(root + "/overlay-feed")))
            {
                var f = feed.RootElement;
                Check(f.GetProperty("enabled").GetBoolean() && f.GetProperty("kovaaks").GetProperty("dpi").GetInt32() == 1200 && !f.GetProperty("live").GetProperty("active").GetBoolean() && f.GetProperty("session").GetProperty("runs").GetInt32() == 0, "the editor preview feed reads settings and stays empty without data");
                Check(f.GetProperty("profile").GetProperty("linked").GetBoolean() == false && f.GetProperty("benchmarks").GetArrayLength() == 0, "no account: no profile or ranks");
            }
            using (var feed = JsonDocument.Parse(await client.GetStringAsync(root + "/overlay-feed?surface=game")))
                Check(!feed.RootElement.GetProperty("enabled").GetBoolean() && !feed.RootElement.TryGetProperty("kovaaks", out _), "the in-game surface is off until switched on, and then sends nothing");
            using (var motion = JsonDocument.Parse(await client.GetStringAsync(root + "/overlay-motion?surface=game&input=1")))
                Check(motion.RootElement.GetProperty("input").ValueKind == JsonValueKind.Null, "motion off with its surface");
            var store = await client.GetStringAsync(root + "/overlay-scenes");
            using (var response = await client.PostAsync(root + "/overlay-scenes", new StringContent(store, Encoding.UTF8, "application/json")))
                Check(response.StatusCode == HttpStatusCode.Forbidden, "saving requires the UI header");
            async Task<HttpResponseMessage> Post(string body, string type = "application/json")
            {
                var request = new HttpRequestMessage(HttpMethod.Post, root + "/overlay-scenes"); request.Headers.Add("X-AimMod-UI", "1"); request.Content = new StringContent(body, Encoding.UTF8, type);
                return await client.SendAsync(request);
            }
            using (var response = await Post("{\"scenes\":[]}")) Check(response.StatusCode == HttpStatusCode.BadRequest, "an empty store is refused");
            using (var response = await Post(store, "text/plain")) Check(response.StatusCode == HttpStatusCode.UnsupportedMediaType, "JSON only");
            var changed = JsonNode.Parse(store)!; changed["scenes"]![0]!["name"] = "Synthetic HUD"; changed["profile"]!["peripherals"] = new JsonArray(new JsonObject { ["label"] = "Mouse", ["value"] = "Example" });
            long before;
            using (var f = JsonDocument.Parse(await client.GetStringAsync(root + "/overlay-feed"))) before = f.RootElement.GetProperty("storeRevision").GetInt64();
            using (var response = await Post(changed.ToJsonString()))
                Check(response.StatusCode == HttpStatusCode.OK && (await response.Content.ReadAsStringAsync()).Contains("Synthetic HUD"), "a valid store saves and returns the canonical form");
            using (var f = JsonDocument.Parse(await client.GetStringAsync(root + "/overlay-feed"))) Check(f.RootElement.GetProperty("storeRevision").GetInt64() == before + 1, "the feed announces the new revision");
            // OBS: the same reads on the OBS listener, off until the OBS switch is on, never writable.
            using var setup = JsonDocument.Parse(await client.GetStringAsync(root + "/overlay-setup"));
            var obsBase = setup.RootElement.GetProperty("obsBase").GetString()!;
            Check(setup.RootElement.GetProperty("obsUrl").GetString() == obsBase + "/overlay?surface=obs" && setup.RootElement.GetProperty("tournamentUrl").GetString() == obsBase + "/tournament", "setup lists the OBS base and the earlier links");
            using (var response = await client.GetAsync(obsBase + "/scene?id=game")) Check(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "text/html", "scene sources served on OBS");
            using (var response = await client.GetAsync(obsBase + "/overlay?surface=obs")) Check(response.StatusCode == HttpStatusCode.OK, "the earlier OBS link still works");
            using (var feed = JsonDocument.Parse(await client.GetStringAsync(obsBase + "/overlay-feed?surface=preview")))
                Check(!feed.RootElement.GetProperty("enabled").GetBoolean() && !feed.RootElement.TryGetProperty("live", out _), "OBS feed sends nothing while OBS is off, whatever the page asks");
            using (var scenes = JsonDocument.Parse(await client.GetStringAsync(obsBase + "/overlay-scenes")))
                Check(scenes.RootElement.GetProperty("scenes")[0].GetProperty("name").GetString() == "Synthetic HUD", "OBS reads the saved scenes");
            var obsPost = new HttpRequestMessage(HttpMethod.Post, obsBase + "/overlay-scenes"); obsPost.Headers.Add("X-AimMod-UI", "1"); obsPost.Content = new StringContent(store, Encoding.UTF8, "application/json");
            using (var response = await client.SendAsync(obsPost)) Check(response.StatusCode == HttpStatusCode.Forbidden, "OBS listener cannot save scenes");
            using (var response = await client.GetAsync(obsBase + "/overlay-crosshair?file=..%2F..%2Fsecret.png")) Check(response.StatusCode == HttpStatusCode.NotFound, "no file reads through the crosshair route");
            var on = new HttpRequestMessage(HttpMethod.Post, root + "/overlay-settings"); on.Headers.Add("X-AimMod-UI", "1"); on.Content = new StringContent("{\"obsEnabled\":true}", Encoding.UTF8, "application/json");
            using (var response = await client.SendAsync(on)) Check(response.StatusCode == HttpStatusCode.OK, "OBS switched on");
            using (var feed = JsonDocument.Parse(await client.GetStringAsync(obsBase + "/overlay-feed")))
                Check(feed.RootElement.GetProperty("enabled").GetBoolean() && feed.RootElement.GetProperty("kovaaks").GetProperty("dpi").GetInt32() == 1200, "OBS feed once switched on");
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
    static string Temp() { var f = Path.Combine(Path.GetTempPath(), "aimmod-overlay-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(f); return f; }
}
