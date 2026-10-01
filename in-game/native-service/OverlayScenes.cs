using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AimMod.InGame;

/// <summary>
/// The overlay scene store (overlay-scenes.json): scenes of widgets for OBS and the
/// in-game HUD view, their themes and the profile (peripherals). The shape matches
/// ui/overlay-model.js; the service bounds every value (types, ranges, lengths,
/// counts) and writes one canonical form. Widget options are bounded generically:
/// the UI applies each widget's own option schema when it reads them.
/// </summary>
sealed class OverlayScenes
{
    public const int Limit = 256 * 1024, MaxScenes = 12, MaxWidgets = 32, MaxOptions = 24, MaxPeripherals = 12;
    internal static readonly string[] Types = ["live-stats", "pb-pace", "session", "session-graph", "scenario", "rank", "settings", "peripherals", "crosshair", "mouse-path", "input", "recent", "standings", "bracket", "profile", "now-playing", "clock", "text", "image"];
    internal static readonly string[] Themes = ["mint", "minimal", "contrast", "midnight", "ember"];
    static readonly Dictionary<string, (string Accent, string Text, string Surface, double Alpha, int Radius, bool Borders)> ThemeDefaults = new()
    {
        ["mint"] = ("#27e4a1", "#eef5f1", "#0b1110", 0.88, 10, true), ["minimal"] = ("#ffffff", "#ffffff", "#000000", 0.42, 6, false),
        ["contrast"] = ("#ffe14d", "#ffffff", "#000000", 1, 4, true), ["midnight"] = ("#66ccff", "#eef3f8", "#0b1018", 0.88, 10, true),
        ["ember"] = ("#f0b45a", "#f7efe4", "#14100b", 0.88, 10, true),
    };
    static readonly Regex Id = new("^[a-z0-9-]{1,24}$", RegexOptions.CultureInvariant), Hex = new("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant), OptionKey = new("^[A-Za-z_]{1,24}$", RegexOptions.CultureInvariant);
    static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
    readonly object gate = new();
    readonly string path;
    JsonObject current;
    byte[] bytes;
    /// <summary>Changes on every save; overlay pages reload the scenes when it does.</summary>
    public long Revision { get; private set; } = DateTime.UtcNow.Ticks;

    public OverlayScenes(string directory, OverlaySettingsValue legacy)
    {
        Directory.CreateDirectory(directory); path = Path.Combine(directory, "overlay-scenes.json");
        JsonObject? loaded = null;
        try { if (File.Exists(path) && new FileInfo(path).Length <= Limit) loaded = Clean(JsonNode.Parse(File.ReadAllBytes(path), documentOptions: new JsonDocumentOptions { MaxDepth = 12 })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { loaded = null; }
        // First start (or damaged file): the earlier live stats and VS cards, in their saved places.
        current = loaded ?? Clean(Migrate(legacy))!;
        bytes = Encode(current);
        if (loaded is null) try { AtomicFile.WriteText(path, System.Text.Encoding.UTF8.GetString(bytes)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public byte[] Bytes { get { lock (gate) return bytes; } }
    public JsonObject Current { get { lock (gate) return (JsonObject)current.DeepClone(); } }
    public JsonArray Peripherals { get { lock (gate) return (JsonArray)current["profile"]!["peripherals"]!.DeepClone(); } }
    static byte[] Encode(JsonObject value) => JsonSerializer.SerializeToUtf8Bytes(value, Compact);

    /// <summary>Validates and saves a complete store. Throws JsonException when it is not one.</summary>
    public byte[] Save(ReadOnlySpan<byte> body)
    {
        if (body.Length is <= 0 or > Limit) throw new JsonException("size");
        var node = JsonNode.Parse(body, documentOptions: new JsonDocumentOptions { MaxDepth = 12 });
        var clean = Clean(node) ?? throw new JsonException("shape");
        lock (gate)
        {
            var encoded = Encode(clean);
            AtomicFile.WriteText(path, System.Text.Encoding.UTF8.GetString(encoded));
            current = clean; bytes = encoded; Revision++;
            return bytes;
        }
    }

    // ---- canonical form (mirrors overlay-model.js normalize) ----
    static double Num(JsonNode? n, double lo, double hi, double def) => n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? Math.Clamp(d, lo, hi) : def;
    static bool Bool(JsonNode? n, bool def) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;
    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static string Text(JsonNode? n, int max, string def = "")
    {
        var s = Str(n); if (s is null) return def;
        s = new string(s.Where(c => !char.IsControl(c)).ToArray());
        if (s.Length > max) s = s[..max];
        // Never end on half a surrogate pair.
        if (s.Length > 0 && char.IsHighSurrogate(s[^1])) s = s[..^1];
        return s;
    }
    static string Color(JsonNode? n) => Str(n) is { } s && Hex.IsMatch(s) ? s.ToLowerInvariant() : "";
    static string? Ident(JsonNode? n) => Str(n) is { } s && Id.IsMatch(s) ? s : null;
    static double Round3(double v) => Math.Round(v, 3);
    static string DefaultLayout(string type) => type is "live-stats" or "session" or "settings" or "scenario" ? "horizontal" : "vertical";
    static (bool Menu, bool Scenario, bool Match) DefaultShow(string type) => type switch
    {
        "live-stats" or "pb-pace" or "mouse-path" or "input" => (false, true, true),
        "standings" => (false, false, true),
        _ => (true, true, true),
    };
    internal static JsonObject? CleanWidget(JsonNode? raw)
    {
        if (raw is not JsonObject o) return null;
        var type = Str(o["type"]); var id = Ident(o["id"]);
        if (type is null || !Types.Contains(type) || id is null) return null;
        var show = o["show"] as JsonObject; var d = DefaultShow(type);
        var opts = new JsonObject();
        if (o["opts"] is JsonObject source)
            foreach (var (key, value) in source)
            {
                if (opts.Count >= MaxOptions || !OptionKey.IsMatch(key) || value is not JsonValue v) continue;
                if (v.TryGetValue<bool>(out var b)) opts[key] = b;
                else if (v.TryGetValue<double>(out var x) && double.IsFinite(x) && Math.Abs(x) < 1e7) opts[key] = Round3(x);
                else if (v.TryGetValue<string>(out _)) opts[key] = Text(v, key == "url" ? 512 : 160);
            }
        var layout = Str(o["layout"]); var background = Str(o["background"]) == "none" ? "none" : Color(o["background"]);
        return new JsonObject
        {
            ["id"] = id, ["type"] = type,
            ["x"] = Math.Round(Num(o["x"], 0, 1900, 0)), ["y"] = Math.Round(Num(o["y"], 0, 1060, 0)),
            ["w"] = Math.Round(Num(o["w"], 60, 1920, 400)), ["h"] = Math.Round(Num(o["h"], 40, 1080, 120)),
            ["visible"] = Bool(o["visible"], true), ["opacity"] = Round3(Num(o["opacity"], 0.1, 1, 1)), ["font"] = Round3(Num(o["font"], 0.6, 2.5, 1)),
            ["variant"] = Str(o["variant"]) == "compact" ? "compact" : "expanded",
            ["layout"] = layout is "horizontal" or "vertical" ? layout : DefaultLayout(type),
            ["labels"] = Bool(o["labels"], true), ["accent"] = Color(o["accent"]), ["background"] = background, ["panel"] = Round3(Num(o["panel"], -1, 1, -1)),
            ["show"] = new JsonObject { ["menu"] = Bool(show?["menu"], d.Menu), ["scenario"] = Bool(show?["scenario"], d.Scenario), ["match"] = Bool(show?["match"], d.Match) },
            ["opts"] = opts,
        };
    }
    static JsonObject CleanTheme(JsonNode? raw)
    {
        var o = raw as JsonObject; var preset = Str(o?["preset"]) is { } p && Themes.Contains(p) ? p : "mint"; var t = ThemeDefaults[preset];
        var accent = Color(o?["accent"]); var text = Color(o?["text"]); var surface = Color(o?["surface"]);
        return new JsonObject
        {
            ["preset"] = preset, ["accent"] = accent.Length > 0 ? accent : t.Accent, ["text"] = text.Length > 0 ? text : t.Text, ["surface"] = surface.Length > 0 ? surface : t.Surface,
            ["alpha"] = Round3(Num(o?["alpha"], 0, 1, t.Alpha)), ["radius"] = Math.Round(Num(o?["radius"], 0, 24, t.Radius)), ["borders"] = Bool(o?["borders"], t.Borders),
        };
    }
    internal static JsonObject? CleanScene(JsonNode? raw)
    {
        if (raw is not JsonObject o || Ident(o["id"]) is not { } id) return null;
        var name = Text(o["name"], 48).Trim(); var widgets = new JsonArray(); var seen = new HashSet<string>(StringComparer.Ordinal);
        if (o["widgets"] is JsonArray list)
            foreach (var w in list) { if (widgets.Count >= MaxWidgets) break; if (CleanWidget(w) is { } c && seen.Add((string)c["id"]!)) widgets.Add(c); }
        return new JsonObject { ["id"] = id, ["name"] = name.Length > 0 ? name : "Scene", ["theme"] = CleanTheme(o["theme"]), ["widgets"] = widgets };
    }
    /// <summary>The canonical store, or null when the input is not a store with at least one scene.</summary>
    internal static JsonObject? Clean(JsonNode? raw)
    {
        if (raw is not JsonObject o || o["scenes"] is not JsonArray list) return null;
        var scenes = new JsonArray(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in list) { if (scenes.Count >= MaxScenes) break; if (CleanScene(s) is { } c && ids.Add((string)c["id"]!)) scenes.Add(c); }
        if (scenes.Count == 0) return null;
        var first = (string)scenes[0]!["id"]!;
        string Pick(JsonNode? n) => Str(n) is { } v && ids.Contains(v) ? v : first;
        var peripherals = new JsonArray();
        if (o["profile"] is JsonObject profile && profile["peripherals"] is JsonArray gear)
            foreach (var g in gear)
            {
                if (peripherals.Count >= MaxPeripherals) break;
                if (g is not JsonObject item) continue;
                var label = Text(item["label"], 32).Trim(); var value = Text(item["value"], 80).Trim();
                if (label.Length > 0 || value.Length > 0) peripherals.Add(new JsonObject { ["label"] = label, ["value"] = value });
            }
        return new JsonObject { ["v"] = 1, ["obsScene"] = Pick(o["obsScene"]), ["gameScene"] = Pick(o["gameScene"]), ["scenes"] = scenes, ["profile"] = new JsonObject { ["peripherals"] = peripherals } };
    }

    // ---- migration from overlay-settings.json (mirrors overlay-model.js defaultStore) ----
    static JsonObject Widget(string type, string id, double x, double y, double w, double h, bool visible = true, string? variant = null) =>
        new() { ["id"] = id, ["type"] = type, ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h, ["visible"] = visible, ["variant"] = variant ?? "expanded" };
    static JsonObject LegacyScene(string id, string name, OverlayLayout layout)
    {
        double sw = Math.Clamp(layout.Stats.Width, 140, 600) * 1.1, vw = Math.Clamp(layout.Versus.Width, 140, 600) * 1.25;
        double X(OverlayPlacement p, double w) => Math.Round(Math.Clamp(1920 * p.X / 100, 0, 1920 - w));
        double Y(OverlayPlacement p, double h) => Math.Round(Math.Clamp(1080 * p.Y / 100, 0, 1080 - h));
        return new JsonObject
        {
            ["id"] = id, ["name"] = name, ["theme"] = new JsonObject { ["preset"] = "mint", ["alpha"] = Math.Clamp(layout.Opacity, 0, 1) * 0.88 },
            ["widgets"] = new JsonArray(
                Widget("live-stats", "stats", X(layout.Stats, sw), Y(layout.Stats, 120), Math.Round(sw), 120, layout.Stats.Visible),
                Widget("pb-pace", "versus", X(layout.Versus, vw), Y(layout.Versus, 150), Math.Round(vw), 150, layout.Versus.Visible)),
        };
    }
    internal static JsonObject Migrate(OverlaySettingsValue legacy)
    {
        var game = new OverlayLayout(legacy.Opacity, legacy.Stats, legacy.Versus);
        var setup = new JsonObject
        {
            ["id"] = "setup", ["name"] = "Setup card", ["theme"] = new JsonObject { ["preset"] = "mint" },
            ["widgets"] = new JsonArray(Widget("settings", "w1", 60, 60, 460, 260), Widget("peripherals", "w2", 560, 60, 420, 220), Widget("crosshair", "w3", 1020, 60, 200, 200), Widget("profile", "w4", 60, 960, 400, 84)),
        };
        var scenes = new JsonArray(LegacyScene("game", "In-game HUD", game), LegacyScene("stream", "OBS", legacy.Obs ?? game), setup);
        // Named layouts saved on the earlier Layout tab become scenes of their own.
        for (int i = 0; i < legacy.Layouts.Length && scenes.Count < MaxScenes; i++) scenes.Add(LegacyScene("layout-" + (i + 1), legacy.Layouts[i].Name, legacy.Layouts[i].Layout));
        return new JsonObject
        {
            ["v"] = 1, ["obsScene"] = "stream", ["gameScene"] = "game",
            ["scenes"] = scenes,
            ["profile"] = new JsonObject { ["peripherals"] = new JsonArray() },
        };
    }
}
