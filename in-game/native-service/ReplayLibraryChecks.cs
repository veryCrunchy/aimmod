using System.Text.Json;

namespace AimMod.InGame;
static class ReplayLibraryChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "aimmod-library-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new ReplayCatalog(root);
            var exports = Path.Combine(root, "exports");
            var library = new ReplayLibrary(catalog, root, exports);
            var header = JsonSerializer.Serialize(new { kind = "header", version = 1, id = "synthetic", scenario = "Synthetic scenario", recordedAt = "2026-01-01T00:00:00Z", coordinates = "unreal-centimeters" });
            var replay = header + "\n{\"kind\":\"frame\",\"t\":0,\"camera\":[0,0,0,0,0,0,90],\"actors\":[]}\n{\"kind\":\"frame\",\"t\":1,\"camera\":[0,0,0,0,0,0,90],\"actors\":[]}\n{\"kind\":\"end\",\"reason\":\"completed\",\"frames\":2,\"inputEvents\":0}\n";
            var path = Path.Combine(root, "replays", "synthetic.amreplay");
            File.WriteAllText(path, replay);
            File.WriteAllText(Path.Combine(root, "replays", "active.partial"), "recording");
            File.WriteAllText(Path.Combine(root, "completed.tsv"), "score history");
            var count = 0;
            void Check(bool ok, string reason) { count++; if (!ok) throw new Exception("Replay library: " + reason); }
            Check(!library.Delete("../completed") && !library.Export("../completed") && !library.Favorite("../completed", true), "reject traversal for every mutation");
            Check(library.Favorite("synthetic", true), "favorite stored");
            using (var list = JsonDocument.Parse(JsonSerializer.Serialize(new ReplayLibrary(catalog, root, exports).List())))
                Check(list.RootElement[0].GetProperty("favorite").GetBoolean(), "favorite survives reopen");
            Check(library.Export("synthetic") && File.ReadAllText(Path.Combine(exports, "synthetic.amreplay")) == replay, "export preserves exact replay bytes");
            Check(library.Export("synthetic") && Directory.GetFiles(exports).Length == 2, "second export does not overwrite");
            Check(library.Delete("synthetic") && !File.Exists(path), "explicit deletion removes replay");
            Check(File.ReadAllText(Path.Combine(root, "replays", "active.partial")) == "recording", "active recording untouched");
            Check(File.ReadAllText(Path.Combine(root, "completed.tsv")) == "score history", "scores untouched");
            Check(!library.Delete("synthetic"), "missing replay reported");
            Console.WriteLine($"{count} replay library checks passed.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
