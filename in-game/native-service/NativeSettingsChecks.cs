using System.Text;
using System.Text.Json;

namespace AimMod.InGame;
static class NativeSettingsChecks
{
    public static void Run()
    {
        int count = 0;
        void Check(bool result, string name) { count++; if (!result) throw new Exception(name); }
        var directory = Path.Combine(Path.GetTempPath(), "aimmod-settings-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new NativeSettings(directory);
            NativeSettingsValue Apply(string json) => store.ApplyJson(Encoding.UTF8.GetBytes(json));
            Check(store.Current == new NativeSettingsValue(), "New install preserves existing enabled behavior");
            Check(NativeSettings.Decode(File.ReadAllText(Path.Combine(directory, "native-settings.tsv"))) == store.Current, "Canonical game control file matches API");
            Check(Apply("{\"replayRecordingEnabled\":false}") == new NativeSettingsValue(false, true), "Recording patch retains unrelated history setting");
            Check(new NativeSettings(directory).Current == store.Current, "Disabled capture survives restart");
            Check(Apply("{\"hubHistoryEnabled\":false}") == new NativeSettingsValue(false, false), "Remote history disabled independently");
            foreach (var invalid in new[] { "{}", "[]", "{\"replayRecordingEnabled\":1}", "{\"replayRecordingEnabled\":null}", "{\"hubHistoryEnabled\":\"false\"}", "{\"uploadToken\":false}", "{\"hubHistoryEnabled\":true,\"hubHistoryEnabled\":false}", "{\"HubHistoryEnabled\":true}", "{\"replayRecordingEnabled\":true,\"unexpected\":false}" })
            {
                bool rejected = false; try { Apply(invalid); } catch (JsonException) { rejected = true; }
                Check(rejected && store.Current == new NativeSettingsValue(false, false), "Invalid setting never changes preferences");
            }
            File.WriteAllText(Path.Combine(directory, "native-settings.tsv"), "damaged");
            store = new NativeSettings(directory);
            Check(store.ReadFailed && store.Current == new NativeSettingsValue(false, false), "Corrupt saved preferences fail closed");
            Apply("{\"hubHistoryEnabled\":false}");
            Check(!store.ReadFailed && new NativeSettings(directory).Current == store.Current, "Saving repairs damaged preferences without enabling capture");
            Parallel.Invoke(() => Apply("{\"replayRecordingEnabled\":true}"), () => Apply("{\"hubHistoryEnabled\":true}"));
            Check(store.Current == new NativeSettingsValue(true, true), "Concurrent independent updates are retained");
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Atomic settings writes leave no temporary files");
            Check(JsonSerializer.Serialize(store.Current).Contains("\"replayRecordingEnabled\":true"), "Stable browser property names");
            Console.WriteLine($"{count} native settings checks passed");
        }
        finally { Directory.Delete(directory, true); }
    }
}
