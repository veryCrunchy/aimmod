using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AimMod.InGame;

// Install lifecycle checks: hashes, manifests, staging, the transactional
// applier with rollback, repair detection and the update settings. Synthetic
// folders under %TEMP% only; nothing touches the game or the network.
static class LifecycleChecks
{

    sealed class Files : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Content = new();
        public readonly List<string> Requested = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            var response = Content.TryGetValue(url, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    // A package folder: files\<path> plus its manifest.
    static (string Root, byte[] Manifest) Package(string folder, string version, Dictionary<string, string> files, string channel = "stable", string[]? mods = null, long minimumBuild = 0)
    {
        Directory.CreateDirectory(folder);
        var entries = new List<ReleaseFile>();
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(folder, "files", ReleasePaths.ToWindows(path));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var bytes = Encoding.UTF8.GetBytes(text);
            File.WriteAllBytes(full, bytes);
            entries.Add(new(path, Sha256Hex.Of(bytes), bytes.Length));
        }
        var manifest = new ReleaseManifest(ReleaseManifest.SchemaName, "AimMod in-game", version, channel, "2026-01-01T00:00:00Z", null,
            new("v3.0.1-1152-ge3ba1016", new string('a', 64)), new(ReleaseManifest.KovaaksAppId, minimumBuild, [new("3.9.11", 1000)]),
            mods ?? ["AimModCore"], entries.ToArray());
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest);
        File.WriteAllBytes(Path.Combine(folder, InstallLayout.PackageManifest), json);
        return (folder, json);
    }
    static Dictionary<string, string> Release(string tag) => new()
    {
        ["dwmapi.dll"] = "proxy-" + tag,
        ["ue4ss/UE4SS.dll"] = "ue4ss",
        ["ue4ss/UE4SS-settings.ini"] = "[Hooks]\nHookEngineTick = 1\n",
        ["ue4ss/Mods/AimModCore/dlls/main.dll"] = "core-" + tag,
        ["ue4ss/Mods/AimModCore/service/AimMod.InGame.exe"] = "service-" + tag,
    };
    static string Game(string root, long build = 1000)
    {
        var win64 = Path.Combine(root, "steamapps", "common", "FPSAimTrainer", "FPSAimTrainer", "Binaries", "Win64");
        Directory.CreateDirectory(win64);
        File.WriteAllText(Path.Combine(win64, InstallLayout.GameExe + ".exe"), "game");
        File.WriteAllText(Path.Combine(root, "steamapps", "appmanifest_824270.acf"), $"\"AppState\"\n{{\n\t\"appid\"\t\t\"824270\"\n\t\"buildid\"\t\t\"{build}\"\n}}\n");
        return win64;
    }
    // Every file under the folder with its content hash, for exact before/after comparisons.
    static string Tree(string folder) => string.Join("\n", Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(f => Path.GetRelativePath(folder, f) + "=" + Sha256Hex.OfFile(f)));
    static byte[] Zip(string packageRoot, Action<ZipArchive>? extra = null)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach (var file in Directory.GetFiles(packageRoot, "*", SearchOption.AllDirectories))
                archive.CreateEntryFromFile(file, Path.GetRelativePath(packageRoot, file).Replace('\\', '/'));
            extra?.Invoke(archive);
        }
        return memory.ToArray();
    }

    public static async Task Run()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("Lifecycle check failed: " + name); count++; }
        void Throws<T>(Action action, string name) where T : Exception { var thrown = false; try { action(); } catch (T) { thrown = true; } Check(thrown, name); }

        // ---- versions and paths ----
        var ordered = new[] { "0.9.9", "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.10.0" };
        for (int i = 1; i < ordered.Length; i++) Check(SemanticVersion.Parse(ordered[i - 1]) < SemanticVersion.Parse(ordered[i]), "semver order " + ordered[i]);
        foreach (var bad in new[] { "1.0", "01.0.0", "1.0.0-01", "v1.0.0", "1.0.0+build", "", "1.0.0-" }) Check(!SemanticVersion.TryParse(bad, out _), "invalid version " + bad);
        foreach (var good in new[] { "dwmapi.dll", "ue4ss/UE4SS.dll", "ue4ss/Mods/AimModCore/dlls/main.dll" }) Check(ReleasePaths.IsAllowed(good), "allowed path " + good);
        foreach (var bad in new[] { "../dwmapi.dll", "ue4ss/../../x.dll", "C:/x.dll", "/ue4ss/x", "ue4ss\\x.dll", "FPSAimTrainer-Win64-Shipping.exe", "steam_api64.dll", "ue4ss/Mods/x.dll:stream", "ue4ss/CON", "ue4ss/a./b", "ue4ss//b", "ue4ss/ b" })
            Check(!ReleasePaths.IsAllowed(bad), "rejected path " + bad);

        var temp = Path.Combine(Path.GetTempPath(), "aimmod-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // ---- manifests ----
            var (root1, manifest1) = Package(Path.Combine(temp, "pkg-1.0.0"), "1.0.0", Release("1"));
            Check(ReleaseManifest.Parse(manifest1).Version == "1.0.0", "manifest parses");
            var text = Encoding.UTF8.GetString(manifest1);
            foreach (var (bad, name) in new[] {
                (text.Replace("aimmod.ingame.release/1", "aimmod.ingame.release/2"), "unknown schema"),
                (text.Replace("\"1.0.0\"", "\"1.0\""), "bad version"),
                (text.Replace("\"stable\"", "\"nightly\""), "bad channel"),
                (text.Replace("\"ue4ss/UE4SS.dll\"", "\"../UE4SS.dll\""), "unsafe path"),
                (text.Replace("\"ue4ss/UE4SS.dll\"", "\"dwmapi.dll\""), "duplicate path"),
                (text.Replace("824270", "1"), "other game"),
                (text.Replace("[\"AimModCore\"]", "[\"AimModSteam\"]"), "mod without files"),
                ("{}", "empty object"), ("[", "malformed") })
                Throws<ReleaseFormatException>(() => ReleaseManifest.Parse(Encoding.UTF8.GetBytes(bad)), "manifest rejects " + name);

            // ---- package verification ----
            Check(VerifiedPackage.Open(root1).ManifestSha256 == Sha256Hex.Of(manifest1), "package verifies against its manifest");
            Throws<ReleaseFormatException>(() => VerifiedPackage.Open(root1, new string('0', 64)), "manifest hash pinned by the feed");
            var (resized, _) = Package(Path.Combine(temp, "pkg-resized"), "1.0.0", Release("1"));
            File.AppendAllText(Path.Combine(resized, "files", "dwmapi.dll"), "!");
            Throws<ReleaseFormatException>(() => VerifiedPackage.Open(resized), "file with another size rejected");
            File.Delete(Path.Combine(resized, "files", "dwmapi.dll"));
            Throws<ReleaseFormatException>(() => VerifiedPackage.Open(resized), "missing file rejected");
            var (tampered, _) = Package(Path.Combine(temp, "pkg-tampered"), "1.0.0", Release("1"));
            File.WriteAllText(Path.Combine(tampered, "files", "ue4ss", "Mods", "AimModCore", "dlls", "main.dll"), "core-X");
            Throws<ReleaseFormatException>(() => VerifiedPackage.Open(tampered), "tampered file (same size) rejected");

            // ---- applier: fresh install over a foreign UE4SS ----
            var win64 = Game(Path.Combine(temp, "lib"));
            File.WriteAllText(Path.Combine(win64, "dwmapi.dll"), "someone-elses-proxy");
            Directory.CreateDirectory(Path.Combine(win64, "ue4ss", "Mods", "OtherMod"));
            File.WriteAllText(Path.Combine(win64, "ue4ss", "Mods", "mods.txt"), "OtherMod : 1\r\nGone : 1\r\n");
            var original = Tree(win64);
            var state = Path.Combine(temp, "state", "updates");
            var gameUp = false;
            var applier = new PackageApplier(state, _ => gameUp);
            var package1 = VerifiedPackage.Open(root1);
            gameUp = true;
            Throws<InstallException>(() => applier.Apply(win64, package1, "install"), "install refused while the game runs");
            Check(Tree(win64) == original, "refused install changes nothing");
            gameUp = false;
            var installed = applier.Apply(win64, package1, "install");
            var record = InstallManifest.Read(win64)!;
            Check(installed.Changed == 5 && record.Version == "1.0.0" && record.Managed && record.Files.Count == 5, "install records release");
            Check(File.ReadAllText(Path.Combine(win64, "dwmapi.dll")) == "proxy-1" && File.ReadAllText(Path.Combine(win64, "dwmapi.dll.aimmod-backup")) == "someone-elses-proxy" && record.Backups["dwmapi.dll"] == "dwmapi.dll.aimmod-backup", "foreign proxy kept as backup");
            var lists = ModList.Read(Path.Combine(win64, "ue4ss", "Mods"));
            Check(lists.Keys.First() == "AimModCore" && lists["AimModCore"] && lists["OtherMod"] && !lists.ContainsKey("Gone"), "mod list: ours first, existing mods kept");
            Check(File.ReadAllText(Path.Combine(win64, "ue4ss", "Mods", "mods.json")).Contains("\"mod_name\": \"AimModCore\""), "mods.json written");
            Check(record.Files.All(f => f.Path.Contains('\\') || f.Path == "dwmapi.dll") && record.Files.All(f => f.Sha256 == f.Sha256.ToUpperInvariant()), "install manifest matches the PowerShell installer format");
            Check(InstallManifest.Parse(record.ToJson()).Backups.Count == record.Backups.Count, "install manifest round-trips");

            // ---- repair detection ----
            var health = InstallHealth.Inspect(win64, record, package1.Manifest);
            Check(health.Installed && !health.NeedsRepair && health.Game.Tested && health.Game.TestedVersion == "3.9.11", "healthy install");
            File.Delete(Path.Combine(win64, "dwmapi.dll"));
            File.WriteAllText(Path.Combine(win64, "ue4ss", "UE4SS-settings.ini"), "[Hooks]\nHookEngineTick = 0\n");
            File.WriteAllText(Path.Combine(win64, "ue4ss", "Mods", "mods.txt"), "AimModCore : 0\r\n");
            health = InstallHealth.Inspect(win64, InstallManifest.Read(win64), package1.Manifest);
            var codes = health.Problems.Select(p => p.Code + ":" + p.Path).ToArray();
            Check(codes.Contains("missing:dwmapi.dll") && codes.Contains(@"changed:ue4ss\UE4SS-settings.ini") && codes.Contains("mod-disabled:AimModCore"), "verify-files damage detected");
            File.WriteAllText(Path.Combine(win64, "ue4ss", "Mods", "mods.txt"), "OtherMod : 1\r\n");
            Check(InstallHealth.Inspect(win64, InstallManifest.Read(win64)).Problems.Any(p => p.Code == "mod-list"), "missing mod entry detected");
            var serviceExe = Path.Combine(win64, "ue4ss", "Mods", "AimModCore", "service", "AimMod.InGame.exe");
            File.WriteAllText(Path.Combine(win64, "ue4ss", "Mods", "AimModCore", "dlls", "main.dll"), "core-0");
            Check(InstallHealth.Inspect(win64, InstallManifest.Read(win64)).Problems.Any(p => p.Code == "changed" && p.Path.EndsWith("main.dll")), "mod version mismatch detected");
            Check(!InstallHealth.Inspect(Game(Path.Combine(temp, "empty")), null).Installed, "missing install detected");
            var unknown = InstallHealth.CheckBuild(2000, package1.Manifest.Game);
            Check(!unknown.Tested && unknown.Warning!.Contains("3.9.11"), "unknown game build warns");
            Check(InstallHealth.CheckBuild(500, package1.Manifest.Game with { MinimumSteamBuildId = 900 }).Warning!.Contains("older"), "old game build warns");
            Check(InstallLayout.SteamBuildId(win64) == 1000, "Steam build id read from appmanifest");

            // Repair re-applies the same package and fixes everything.
            var repaired = applier.Apply(win64, package1, "repair");
            Check(repaired.Changed == 3 && !InstallHealth.Inspect(win64, InstallManifest.Read(win64), package1.Manifest).NeedsRepair, "repair restores the install");
            Check(InstallManifest.Read(win64)!.Backups["dwmapi.dll"] == "dwmapi.dll.aimmod-backup" && File.ReadAllText(Path.Combine(win64, "dwmapi.dll.aimmod-backup")) == "someone-elses-proxy", "repair keeps the original backup");
            Check(ModList.Read(Path.Combine(win64, "ue4ss", "Mods"))["AimModCore"], "repair re-enables the mod");

            // ---- update with a failure part-way: exact rollback ----
            var files2 = Release("2"); files2.Remove("ue4ss/UE4SS-settings.ini"); files2["ue4ss/Mods/AimModNativeUI/Scripts/main.lua"] = "lua";
            var (root2, _) = Package(Path.Combine(temp, "pkg-1.1.0"), "1.1.0", files2, mods: ["AimModCore", "AimModNativeUI"]);
            var package2 = VerifiedPackage.Open(root2);
            var before = Tree(win64);
            var placed = 0;
            applier.AfterFile = _ => { if (++placed == 3) throw new IOException("synthetic disk failure"); };
            Throws<InstallException>(() => applier.Apply(win64, package2, "update"), "failing update reports an error");
            Check(Tree(win64) == before, "failed update rolled back exactly");
            Check(!Directory.Exists(Path.Combine(win64, "ue4ss", "Mods", "AimModNativeUI")), "folders created by a failed update removed");
            applier.AfterFile = _ => throw new InvalidOperationException("crash");
            Throws<InstallException>(() => applier.Apply(win64, package2, "update"), "unexpected exception rolled back");
            Check(Tree(win64) == before, "rolled back after an unexpected exception");
            applier.AfterFile = null;

            // A crash leaves the journal "applying": the next run undoes it.
            placed = 0;
            var crashing = new PackageApplier(state, _ => false) { AfterFile = _ => { if (++placed > 2) throw new SimulatedCrashException(); } };
            try { crashing.Apply(win64, package2, "update"); } catch (SimulatedCrashException) { }
            Check(crashing.HasInterrupted && Tree(win64) != before, "a crash leaves a pending journal");
            gameUp = true;
            Throws<InstallException>(() => applier.RecoverInterrupted(), "recovery waits for the game to close");
            gameUp = false;
            Check(applier.RecoverInterrupted() && !applier.HasInterrupted && Tree(win64) == before, "interrupted install undone on the next run");

            var updated = applier.Apply(win64, package2, "update");
            record = InstallManifest.Read(win64)!;
            Check(updated.PreviousVersion == "1.0.0" && record.Version == "1.1.0" && File.ReadAllText(Path.Combine(win64, "dwmapi.dll")) == "proxy-2", "update applied");
            Check(!File.Exists(Path.Combine(win64, "ue4ss", "UE4SS-settings.ini")), "files dropped from a release are removed");
            Check(ModList.Read(Path.Combine(win64, "ue4ss", "Mods")).Keys.Take(2).SequenceEqual(["AimModCore", "AimModNativeUI"]), "new mods enabled");
            Check(applier.RollbackTarget == "1.0.0", "rollback offered");
            Check(applier.RollbackLast() == "1.0.0" && Tree(win64) == before, "rollback restores the previous version exactly");
            Check(applier.RollbackTarget is null, "rollback is not offered twice");

            // Uninstall restores the original game folder (with an empty Mods folder list as the PS uninstaller leaves it).
            var kept = applier.Uninstall(win64);
            Check(kept == 0 && File.ReadAllText(Path.Combine(win64, "dwmapi.dll")) == "someone-elses-proxy" && !File.Exists(InstallLayout.ManifestPath(win64)), "uninstall restores the foreign proxy");
            Check(Tree(win64) == original, "uninstall restores the original game folder exactly");

            // ---- AimMod cosmetics paks: Content\Paks\~AimMod, recorded, repaired and removed like every file ----
            Check(ReleasePaths.IsAllowed("paks/~AimMod/AimModCosmetics-1.pak"), "allowed pak path");
            foreach (var bad in new[] { "paks/~AimMod/AimModCosmetics-1_P.pak", "paks/x.pak", "paks/~AimMod/sub/x.pak", "paks/~AimMod/x.txt", "paks/~Other/x.pak",
                "paks/~AimMod/../x.pak", "Paks/~AimMod/x.pak", "paks/~aimmod/x.pak", "paks/~AimMod/.x.pak", "paks/~AimMod/a..pak" })
                Check(!ReleasePaths.IsAllowed(bad), "rejected pak path " + bad);
            Throws<InstallException>(() => InstallLayout.Resolve(win64, @"paks\~Other\x.pak"), "pak records stay in ~AimMod");
            Throws<InstallException>(() => InstallLayout.Resolve(win64, @"paks\~AimMod\..\..\x.pak"), "pak records cannot escape");
            var pakGame = Game(Path.Combine(temp, "paklib"));
            var gamePaks = InstallLayout.ContentPaks(pakGame);
            var withPak = Release("p"); withPak["paks/~AimMod/AimModCosmetics-1.pak"] = "pak-1";
            var (pakRoot, _) = Package(Path.Combine(temp, "pkg-pak"), "1.2.0", withPak);
            var pakPackage = VerifiedPackage.Open(pakRoot);
            var gameRoot = Path.GetFullPath(Path.Combine(pakGame, "..", ".."));
            var noPaksFolder = Tree(gameRoot);
            Throws<InstallException>(() => applier.Apply(pakGame, pakPackage, "install"), "pak install refused without the game's Content\\Paks");
            Check(Tree(gameRoot) == noPaksFolder, "refused pak install changes nothing");
            Directory.CreateDirectory(gamePaks);
            File.WriteAllText(Path.Combine(gamePaks, InstallLayout.GamePak), "game-pak");
            var beforePak = Tree(gameRoot);
            applier.Apply(pakGame, pakPackage, "install");
            var pakFile = Path.Combine(gamePaks, "~AimMod", "AimModCosmetics-1.pak");
            var pakRecord = InstallManifest.Read(pakGame)!;
            Check(File.ReadAllText(pakFile) == "pak-1", "pak placed in Content\\Paks\\~AimMod");
            Check(pakRecord.Files.Any(f => f.Path == @"paks\~AimMod\AimModCosmetics-1.pak") && pakRecord.CreatedDirectories.Contains(@"paks\~AimMod"), "pak and its folder recorded in the install manifest");
            Check(!InstallHealth.Inspect(pakGame, pakRecord, pakPackage.Manifest).NeedsRepair, "pak install healthy");
            File.WriteAllText(pakFile, "pak-X");
            Check(InstallHealth.Inspect(pakGame, InstallManifest.Read(pakGame), pakPackage.Manifest).Problems.Any(p => p.Code == "changed" && p.Path == @"paks\~AimMod\AimModCosmetics-1.pak"), "changed pak detected");
            Check(applier.Apply(pakGame, pakPackage, "repair").Changed == 1 && File.ReadAllText(pakFile) == "pak-1", "repair restores the pak");
            var (noPakRoot, _) = Package(Path.Combine(temp, "pkg-nopak"), "1.3.0", Release("q"));
            applier.Apply(pakGame, VerifiedPackage.Open(noPakRoot), "update");
            Check(!File.Exists(pakFile), "pak dropped from a release is removed");
            Check(applier.RollbackLast() == "1.2.0" && File.ReadAllText(pakFile) == "pak-1", "rollback restores the pak");
            Check(applier.Uninstall(pakGame) == 0 && !Directory.Exists(Path.Combine(gamePaks, "~AimMod")) && File.Exists(Path.Combine(gamePaks, InstallLayout.GamePak)), "uninstall removes AimMod paks and their folder, never the game's pak");
            Check(Tree(gameRoot) == beforePak, "pak uninstall restores the game folder exactly");

            // ---- updater: hash-pinned feed, download, verify, stage ----
            var handler = new Files();
            var updatesRoot = Path.Combine(temp, "updater");
            var updater = new Updater(updatesRoot, handler);
            var zipBytes = Zip(root2);
            var feedUrl = "https://example.invalid/feed/aimmod-ingame-stable.json";
            byte[] Feed(string version, string sha, long size, string url, string channel = "stable", long minimum = 0, string? manifestSha = null) =>
                JsonSerializer.SerializeToUtf8Bytes(new UpdateFeed(UpdateFeed.SchemaName, channel, version, "2026-01-01T00:00:00Z", "Faster replays.", minimum, manifestSha ?? package2.ManifestSha256, new(url, sha, size)));
            void Publish(byte[] feed) => handler.Content[feedUrl] = feed;
            handler.Content["https://example.invalid/AimMod-InGame-1.1.0.zip"] = zipBytes;
            var installed1 = InstallManifest.Parse(record.ToJson().Replace("\"1.1.0\"", "\"1.0.0\""));
            var prefs = new UpdatePreferences();
            Publish(Feed("1.1.0", Sha256Hex.Of(zipBytes), zipBytes.Length, "https://example.invalid/AimMod-InGame-1.1.0.zip"));
            var check = await updater.Check(prefs, feedUrl, installed1, 1000, CancellationToken.None);
            var staged = updater.Staged();
            Check(check.State == UpdateState.Ready && check.Notes == "Faster replays." && staged?.Version == "1.1.0", "update downloaded, verified and staged");
            Check(updater.OpenStaged(staged!).Manifest.Version == "1.1.0", "staged package re-verifies before applying");
            var requests = handler.Requested.Count;
            Check((await updater.Check(prefs, feedUrl, installed1, 1000, CancellationToken.None)).State == UpdateState.Ready && handler.Requested.Count == requests + 1, "staged update is not downloaded again");
            File.AppendAllText(Path.Combine(updater.StagedFolder(staged!), "files", "dwmapi.dll"), "!");
            Throws<ReleaseFormatException>(() => updater.OpenStaged(staged!), "staged files changed after download rejected");
            updater.ClearStaged();
            Check(updater.Staged() is null && !Directory.EnumerateDirectories(Path.Combine(updatesRoot, "staged")).Any(), "staging cleared");

            async Task Rejected(string name, byte[] feed, InstallManifest? current = null, long? build = 1000)
            {
                Publish(feed);
                var rejected = false;
                try { var r = await updater.Check(prefs, feedUrl, current ?? installed1, build, CancellationToken.None); rejected = r.State != UpdateState.Ready; }
                catch (ReleaseFormatException) { rejected = true; }
                Check(rejected && updater.Staged() is null, name);
            }
            await Rejected("malformed feed rejected", "{\"schema\":\"aimmod.ingame.feed/1\"}"u8.ToArray());
            await Rejected("package hash mismatch rejected", Feed("1.1.0", new string('0', 64), zipBytes.Length, "https://example.invalid/AimMod-InGame-1.1.0.zip"));
            await Rejected("package larger than announced rejected", Feed("1.1.0", Sha256Hex.Of(zipBytes), zipBytes.Length - 1, "https://example.invalid/AimMod-InGame-1.1.0.zip"));
            await Rejected("plain HTTP package rejected", Feed("1.1.0", Sha256Hex.Of(zipBytes), zipBytes.Length, "http://example.invalid/AimMod-InGame-1.1.0.zip"));
            await Rejected("manifest not matching the feed rejected", Feed("1.1.0", Sha256Hex.Of(zipBytes), zipBytes.Length, "https://example.invalid/AimMod-InGame-1.1.0.zip", manifestSha: new string('b', 64)));
            await Rejected("feed for another channel rejected", Feed("1.1.0", Sha256Hex.Of(zipBytes), zipBytes.Length, "https://example.invalid/AimMod-InGame-1.1.0.zip", channel: "beta"));
            await Rejected("same version is up to date", Feed("1.1.0", Sha256Hex.Of(zipBytes), zipBytes.Length, "https://example.invalid/AimMod-InGame-1.1.0.zip"), current: record);
            await Rejected("downgrade ignored", Feed("0.9.0", Sha256Hex.Of(zipBytes), zipBytes.Length, "https://example.invalid/AimMod-InGame-1.1.0.zip"));
            await Rejected("update needing a newer game is held", Feed("1.1.0", Sha256Hex.Of(zipBytes), zipBytes.Length, "https://example.invalid/AimMod-InGame-1.1.0.zip", minimum: 1001));
            await Rejected("developer installs are not updated", Feed("1.1.0", Sha256Hex.Of(zipBytes), zipBytes.Length, "https://example.invalid/AimMod-InGame-1.1.0.zip"), current: InstallManifest.Parse("{\"files\":[]}"));
            Publish(Feed("1.1.0", Sha256Hex.Of(zipBytes), zipBytes.Length, "https://example.invalid/AimMod-InGame-1.1.0.zip", channel: "beta"));
            Check((await updater.Check(new UpdatePreferences(true, "beta"), feedUrl, installed1, 1000, CancellationToken.None)).State == UpdateState.Ready, "stable release offered on the beta channel");
            updater.ClearStaged();
            // A zip whose files do not match its own manifest (pinned by the feed) is rejected.
            var (forged, _) = Package(Path.Combine(temp, "pkg-forged"), "1.1.0", files2, mods: ["AimModCore", "AimModNativeUI"]);
            File.WriteAllText(Path.Combine(forged, "files", "dwmapi.dll"), "proxy-X");
            var forgedZip = Zip(forged);
            handler.Content["https://example.invalid/forged.zip"] = forgedZip;
            var forgedManifest = Sha256Hex.Of(File.ReadAllBytes(Path.Combine(forged, InstallLayout.PackageManifest)));
            await Rejected("package files not matching the manifest rejected", Feed("1.1.0", Sha256Hex.Of(forgedZip), forgedZip.Length, "https://example.invalid/forged.zip", manifestSha: forgedManifest));
            Check(!handler.Requested.Any(u => u.EndsWith(".sig")), "no signature files are requested");
            // Zip entries the manifest does not list are never extracted (zip slip, extra executables).
            var evil = Zip(root2, archive =>
            {
                using (var w = new StreamWriter(archive.CreateEntry("../../escape.txt").Open())) w.Write("x");
                using (var w = new StreamWriter(archive.CreateEntry("files/ue4ss/Mods/AimModCore/dlls/extra.dll").Open())) w.Write("x");
            });
            var evilFolder = Path.Combine(temp, "evil", "out");
            Directory.CreateDirectory(Path.Combine(temp, "evil"));
            File.WriteAllBytes(Path.Combine(temp, "evil", "evil.zip"), evil);
            Updater.ExtractVerified(Path.Combine(temp, "evil", "evil.zip"), evilFolder, package2.ManifestSha256);
            Check(!File.Exists(Path.Combine(temp, "escape.txt")) && !File.Exists(Path.Combine(temp, "evil", "escape.txt")) && !File.Exists(Path.Combine(evilFolder, "files", "ue4ss", "Mods", "AimModCore", "dlls", "extra.dll")), "unlisted zip entries ignored");
            Check(VerifiedPackage.Open(evilFolder, package2.ManifestSha256).Manifest.Version == "1.1.0", "listed entries extracted and verified");

            // ---- update preferences ----
            var settingsDir = Path.Combine(temp, "settings");
            Directory.CreateDirectory(settingsDir);
            var settings = new UpdateSettings(settingsDir);
            Check(settings.Current == new UpdatePreferences() && settings.FeedUrl(settings.Current).StartsWith("https://github.com/verycrunchy/aimmod/releases/download/aimmod-ingame-stable/aimmod-ingame-stable.json"), "defaults: on, stable, GitHub feed");
            var freshDir = Path.Combine(temp, "settings-fresh");
            Directory.CreateDirectory(freshDir);
            Check(new UpdateSettings(freshDir, "beta").Current.Channel == "beta" && new UpdateSettings(freshDir, "stable").Current.Channel == "stable", "without saved settings the channel follows the installed package");
            Check(settings.ApplyJson("{\"channel\":\"beta\"}"u8.ToArray()) == new UpdatePreferences(true, "beta") && new UpdateSettings(settingsDir).Current.Channel == "beta", "channel saved");
            Check(settings.FeedUrl(settings.Current).EndsWith("aimmod-ingame-beta.json"), "beta feed");
            foreach (var bad in new[] { "{}", "[]", "{\"channel\":\"nightly\"}", "{\"autoUpdate\":1}", "{\"feedUrl\":\"https://x\"}", "{\"autoUpdate\":true,\"autoUpdate\":false}" })
                Throws<JsonException>(() => settings.ApplyJson(Encoding.UTF8.GetBytes(bad)), "invalid update setting rejected: " + bad);
            File.WriteAllText(Path.Combine(settingsDir, "update-settings.json"), "{\"autoUpdate\":true,\"channel\":\"stable\",\"feedUrl\":\"http://insecure.invalid/{channel}.json\"}");
            Check(!new UpdateSettings(settingsDir).Current.AutoUpdate, "insecure feed override disables updates");
            File.WriteAllText(Path.Combine(settingsDir, "update-settings.json"), "{\"autoUpdate\":true,\"channel\":\"beta\",\"feedUrl\":\"https://hub.invalid/feeds/{channel}.json\"}");
            Check(new UpdateSettings(settingsDir, "stable").Current.Channel == "beta", "a saved channel wins over the installed package");
            var hubSettings = new UpdateSettings(settingsDir);
            Check(hubSettings.FeedUrl(hubSettings.Current) == "https://hub.invalid/feeds/beta.json", "feed location is configurable");

            // ---- service hand-off state machine via the command line ----
            var cliRoot = Path.Combine(temp, "cli");
            var cliGame = Game(Path.Combine(cliRoot, "lib"));
            var output = Path.Combine(cliRoot, "local");
            Check(Lifecycle.RunCommand(["--install", "--package", root1, "--game-dir", cliGame, "--output", output], _ => false) == 0, "install command");
            Check(InstallManifest.Read(cliGame)!.Version == "1.0.0" && File.Exists(Path.Combine(Lifecycle.PackageCache(output), InstallLayout.PackageManifest)), "install caches the package for repairs");
            File.Delete(Path.Combine(cliGame, "dwmapi.dll"));
            Check(Lifecycle.RunCommand(["--install-status", "--game-dir", cliGame, "--output", output], _ => false) == 3, "status reports a needed repair");
            Check(Lifecycle.RunCommand(["--repair", "--game-dir", cliGame, "--output", output], _ => true) == 2 && !File.Exists(Path.Combine(cliGame, "dwmapi.dll")), "repair waits for the game to close");
            Check(Lifecycle.RunCommand(["--repair", "--game-dir", cliGame, "--output", output], _ => false) == 0 && File.Exists(Path.Combine(cliGame, "dwmapi.dll")), "repair from the cached package");
            // Stage 1.1.0 as the service would, then apply it as the post-exit hand-off does.
            var cliUpdater = new Updater(Path.Combine(output, "updates"), handler);
            Publish(Feed("1.1.0", Sha256Hex.Of(zipBytes), zipBytes.Length, "https://example.invalid/AimMod-InGame-1.1.0.zip"));
            Check((await cliUpdater.Check(prefs, feedUrl, InstallManifest.Read(cliGame), 1000, CancellationToken.None)).State == UpdateState.Ready, "service stages the update");
            Check(Lifecycle.RunCommand(["--apply-pending", "--game-dir", cliGame, "--output", output], _ => false) == 0, "hand-off applies the staged update");
            Check(InstallManifest.Read(cliGame)!.Version == "1.1.0" && cliUpdater.Staged() is null, "update applied and staging cleared");
            var lifecycle = new Lifecycle(output, cliGame, handler, _ => false);
            var snapshot = JsonSerializer.Serialize(lifecycle.Snapshot());
            Check(snapshot.Contains("\"version\":\"1.1.0\"") && snapshot.Contains("\"kind\":\"update\"") && snapshot.Contains("\"available\":true"), "UI snapshot reports the update and rollback");
            lifecycle.Act("dismiss");
            Check(!JsonSerializer.Serialize(lifecycle.Snapshot()).Contains("\"kind\":\"update\""), "result dismissed");
            lifecycle.Act("repair");
            Check(JsonSerializer.Serialize(lifecycle.Snapshot()).Contains("\"requested\":true"), "repair requested from the UI");
            Throws<JsonException>(() => lifecycle.Act("format-disk"), "unknown action rejected");
            Check(Lifecycle.RunCommand(["--apply-pending", "--game-dir", cliGame, "--output", output], _ => false) == 0 && !JsonSerializer.Serialize(lifecycle.Snapshot()).Contains("\"requested\":true"), "requested repair applied after close");
            Check(Lifecycle.RunCommand(["--rollback", "--game-dir", cliGame, "--output", output], _ => false) == 0 && InstallManifest.Read(cliGame)!.Version == "1.0.0", "rollback command");
            Check(VerifiedPackage.Open(Lifecycle.PackageCache(output)).Manifest.Version == "1.0.0", "rollback restores the cached package");
            Check(Lifecycle.RunCommand(["--uninstall", "--game-dir", cliGame, "--output", output], _ => false) == 0 && !File.Exists(Path.Combine(cliGame, "dwmapi.dll")) && !Directory.Exists(Path.Combine(cliGame, "ue4ss")), "uninstall removes everything it created");
            await lifecycle.DisposeAsync();
        }
        finally { try { Directory.Delete(temp, true); } catch (IOException) { } }
        Console.WriteLine($"{count} install lifecycle checks passed");
    }
}
