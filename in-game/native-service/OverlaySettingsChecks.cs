using System.Text;
using System.Text.Json;

namespace AimMod.InGame;
static class OverlaySettingsChecks
{
    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "aimmod-overlay-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); int count = 0;
        void Check(bool ok, string name) { count++; if (!ok) throw new Exception(name); }
        try
        {
            var store = new OverlaySettings(folder);
            Check(!store.Current.GameEnabled && !store.Current.ObsEnabled, "New installation never exposes an OBS surface without enabling it");
            store.ApplyJson(Encoding.UTF8.GetBytes("{\"gameEnabled\":true,\"stats\":{\"x\":50,\"visible\":false},\"opacity\":0.75}"));
            Check(store.Current.Stats.X == 50 && !store.Current.Stats.Visible && store.Current.Stats.Width == 560 && store.Current.GameEnabled, "Partial layout updates preserve unrelated fields");
            Check(JsonSerializer.Serialize(new OverlaySettings(folder).Current) == JsonSerializer.Serialize(store.Current), "Overlay settings survive restart");
            var game = store.Current;
            store.ApplyJson(Encoding.UTF8.GetBytes("{\"obs\":{\"opacity\":0.3,\"stats\":{\"x\":82}}}"));
            Check(store.Current.Stats == game.Stats && store.Current.Opacity == game.Opacity && store.Current.Obs?.Stats.X == 82, "OBS layout changes never move game HUD");
            store.ApplyJson(Encoding.UTF8.GetBytes("{\"layoutAction\":\"save\",\"name\":\"Broadcast\",\"surface\":\"obs\"}"));
            Check(store.Current.Layouts.Single().Layout.Opacity == 0.3, "Named preset captures chosen surface");
            store.ApplyJson(Encoding.UTF8.GetBytes("{\"layoutAction\":\"apply\",\"name\":\"Broadcast\",\"surface\":\"game\"}"));
            Check(store.Current.Stats.X == 82 && store.Current.GameEnabled == game.GameEnabled && store.Current.ObsEnabled == game.ObsEnabled, "Applying preset copies layout without enabling a surface");
            Check(JsonSerializer.Serialize(new OverlaySettings(folder).Current) == JsonSerializer.Serialize(store.Current), "Independent layouts and named presets roundtrip together");
            store.ApplyJson(Encoding.UTF8.GetBytes("{\"layoutAction\":\"save\",\"name\":\"broadcast\",\"surface\":\"game\"}"));
            Check(store.Current.Layouts.Length == 1, "Preset names replace case-insensitively");
            store.ApplyJson(Encoding.UTF8.GetBytes("{\"layoutAction\":\"delete\",\"name\":\"Broadcast\",\"surface\":\"game\"}"));
            Check(store.Current.Layouts.Length == 0 && store.Current.Stats.X == 82, "Deleting preset keeps applied layout");
            File.Delete(Path.Combine(folder, "overlay-layouts.json"));
            store = new OverlaySettings(folder);
            Check(store.Current.Obs == new OverlayLayout(store.Current.Opacity, store.Current.Stats, store.Current.Versus), "Legacy shared layout migrates identically to OBS");
            var canonical = File.ReadAllText(Path.Combine(folder, "overlay-settings.json"));
            store.ApplyJson(Encoding.UTF8.GetBytes("{\"obs\":{\"stats\":{\"x\":11}}}"));
            Check(File.ReadAllText(Path.Combine(folder, "overlay-settings.json")) == canonical, "OBS edits never rewrite native game control file");
            for (int i = 0; i < 20; i++) store.ApplyJson(Encoding.UTF8.GetBytes($"{{\"layoutAction\":\"save\",\"name\":\"Layout {i}\",\"surface\":\"game\"}}"));
            bool bounded = false;
            try { store.ApplyJson(Encoding.UTF8.GetBytes("{\"layoutAction\":\"save\",\"name\":\"Overflow\",\"surface\":\"game\"}")); } catch (JsonException) { bounded = true; }
            Check(bounded && new OverlaySettings(folder).Current.Layouts.Length == 20, "Saved layout limit remains bounded and all names roundtrip");
            foreach (var invalid in new[] {"{}", "null", "{\"opacity\":2}", "{\"gameEnabled\":1}", "{\"obsEnabled\":true,\"obsEnabled\":false}", "{\"stats\":{\"width\":0}}", "{\"stats\":{\"x\":-1}}", "{\"stats\":{\"url\":\"http://example.invalid\"}}", "{\"stats\":{}}", "{\"obs\":{},\"gameEnabled\":true}", "{\"layoutAction\":\"save\",\"name\":\"\",\"surface\":\"game\"}", "{\"layoutAction\":1,\"name\":\"x\",\"surface\":\"game\"}"})
            {
                var before = store.Current; bool rejected = false;
                try { store.ApplyJson(Encoding.UTF8.GetBytes(invalid)); } catch (JsonException) { rejected = true; }
                Check(rejected && before == store.Current, "Invalid overlay patch never changes saved layout");
            }
            File.WriteAllText(Path.Combine(folder, "overlay-settings.json"), "{\"gameEnabled\":true}");
            Check(!new OverlaySettings(folder).Current.GameEnabled, "Incomplete persisted file fails closed");
            Check(!Directory.EnumerateFiles(folder, "*.tmp").Any(), "Atomic writes clean temporary files");
            var legacyFolder = Path.Combine(folder, "legacy"); var legacy = new OverlaySettings(legacyFolder);
            legacy.ApplyJson(Encoding.UTF8.GetBytes("{\"gameEnabled\":true,\"obsEnabled\":true,\"opacity\":0.37,\"stats\":{\"visible\":false,\"x\":2,\"y\":12,\"width\":240},\"versus\":{\"x\":75,\"y\":12,\"width\":300}}"));
            legacy.ApplyJson(Encoding.UTF8.GetBytes("{\"obs\":{\"stats\":{\"x\":2,\"y\":12,\"width\":240},\"versus\":{\"x\":75,\"y\":12,\"width\":300}}}"));
            legacy.ApplyJson(Encoding.UTF8.GetBytes("{\"layoutAction\":\"save\",\"name\":\"Saved original\",\"surface\":\"game\"}"));
            legacy = new OverlaySettings(legacyFolder);
            Check(legacy.Current.Stats.Width == 560 && legacy.Current.Stats.Y == 100 && legacy.Current.Obs!.Stats.Width == 560, "Untouched original placements migrate to bottom edge on both surfaces");
            Check(legacy.Current.GameEnabled && legacy.Current.ObsEnabled && legacy.Current.Opacity == .37 && !legacy.Current.Stats.Visible && legacy.Current.Layouts.Single().Layout.Stats.Width == 240, "Migration preserves visibility, opacity, enabled surfaces and explicitly saved layouts");
            legacy.ApplyJson(Encoding.UTF8.GetBytes("{\"stats\":{\"x\":3,\"y\":12,\"width\":240},\"versus\":{\"x\":75,\"y\":12,\"width\":300}}"));
            Check(new OverlaySettings(legacyFolder).Current.Stats.X == 3 && new OverlaySettings(legacyFolder).Current.Stats.Width == 240, "Any custom placement prevents migration of its surface");
            Console.WriteLine($"PASS {count} overlay settings checks");
        }
        finally { Directory.Delete(folder, true); }
    }
}
