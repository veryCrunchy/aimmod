using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using AimMod.InGame;
using AimMod.Setup;
using Microsoft.Win32;

// Checks for the installer's decisions (version compare, required installer,
// channel switch, game-running gating) with synthetic feeds, and its install,
// update, repair and uninstall flow against a fake HTTPS host and a synthetic
// game folder. See in-game/docs/install-lifecycle.md.
var count = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("Setup check failed: " + name); count++; }
async Task Throws<T>(Func<Task> action, string name) where T : Exception
{
    var thrown = false;
    try { await action(); } catch (T) { thrown = true; }
    Check(thrown, name);
}

var setup = SemanticVersion.Parse("1.0.0");
string Hex(char c) => new(c, 64);
UpdateFeed Feed(string version, string channel = "stable", long minimumBuild = 0, string? notes = null, string? minimumInstaller = null, string? installerUrl = null, string? sha = null, long size = 1000, string? manifestSha = null, string? url = null) =>
    new(UpdateFeed.SchemaName, channel, version, "2026-10-01T00:00:00Z", notes, minimumBuild, manifestSha ?? Hex('a'),
        new(url ?? $"https://example.invalid/AimMod-InGame-{version}.zip", sha ?? Hex('b'), size), minimumInstaller, installerUrl);
byte[] Bytes(UpdateFeed feed) => JsonSerializer.SerializeToUtf8Bytes(feed);

// ---- required installer ----
var plain = FeedCheck.Read(Bytes(Feed("0.4.0")), setup, "stable");
Check(plain.State == FeedState.Ready && plain.Feed!.Version == "0.4.0" && plain.InstallerUrl == FeedCheck.DefaultInstallerUrl("stable"), "a feed without installer fields is installable (older feeds)");
var needsNew = FeedCheck.Read(Bytes(Feed("0.5.0", minimumInstaller: "1.1.0", installerUrl: "https://example.invalid/AimMod-Setup.exe")), setup, "stable");
Check(needsNew.State == FeedState.InstallerOutdated && needsNew.RequiredInstaller == "1.1.0" && needsNew.InstallerUrl == "https://example.invalid/AimMod-Setup.exe", "an older installer is told a new one is required, with the feed's link");
Check(FeedCheck.Read(Bytes(Feed("0.5.0", minimumInstaller: "1.0.0")), setup, "stable").State == FeedState.Ready, "the same installer version is enough");
Check(FeedCheck.Read(Bytes(Feed("0.5.0", minimumInstaller: "0.9.0")), setup, "stable").State == FeedState.Ready, "a newer installer installs older-format releases");
Check(FeedCheck.Read(Bytes(Feed("0.5.0", minimumInstaller: "1.0.0")), SemanticVersion.Parse("1.0.0-beta.2"), "stable").State == FeedState.InstallerOutdated, "a prerelease installer is older than its release");
Check(FeedCheck.Read(Bytes(Feed("0.5.0", minimumInstaller: "1.1.0", installerUrl: "http://example.invalid/AimMod-Setup.exe")), setup, "beta").InstallerUrl == FeedCheck.DefaultInstallerUrl("beta"), "a non-HTTPS installer link falls back to the channel release");
var future = Encoding.UTF8.GetBytes("{\"schema\":\"aimmod.ingame.feed/2\",\"channel\":\"stable\",\"something\":{\"new\":true},\"minimumInstallerVersion\":\"2.0.0\",\"installerUrl\":\"https://example.invalid/v2/AimMod-Setup.exe\"}");
var futureStatus = FeedCheck.Read(future, setup, "stable");
Check(futureStatus.State == FeedState.InstallerOutdated && futureStatus.InstallerUrl == "https://example.invalid/v2/AimMod-Setup.exe" && futureStatus.RequiredInstaller == "2.0.0", "a feed of a later format still says which installer it needs");
Check(FeedCheck.Read("{\"schema\":\"aimmod.ingame.feed/3\"}"u8, setup, "beta") is { State: FeedState.InstallerOutdated, InstallerUrl: var link } && link == FeedCheck.DefaultInstallerUrl("beta"), "an unknown later format without fields asks for the new installer");
Check(FeedCheck.Read("{\"schema\":\"something-else\"}"u8, setup, "stable").State == FeedState.Unreachable, "an unrelated document is not a feed");
Check(FeedCheck.Read("[1,"u8, setup, "stable").State == FeedState.Unreachable, "a malformed feed is rejected");
Check(FeedCheck.Read(Bytes(Feed("0.4.0", channel: "beta")), setup, "stable").State == FeedState.Unreachable, "a feed for another channel is rejected");
Check(FeedCheck.Read(Bytes(Feed("0.4.0", minimumInstaller: "1.0")), setup, "stable").State == FeedState.Unreachable, "an invalid installer version is rejected");

// ---- decisions ----
const string Game = @"C:\Synthetic\steamapps\common\FPSAimTrainer\FPSAimTrainer\Binaries\Win64";
FeedStatus Ready(string version, string channel = "stable", long minimumBuild = 0) => new(FeedState.Ready, Feed(version, channel, minimumBuild));
InstalledInfo Installed(string? version, string? channel = "stable", bool managed = true, bool repair = false) => new(version, channel, managed, repair, repair ? ["The UE4SS loader (dwmapi.dll) is missing: dwmapi.dll"] : []);
SetupView Decide(InstalledInfo? installed, FeedStatus feed, string channel = "stable", bool running = false, string? game = Game, long? build = 1000, bool cached = true, bool busy = false) =>
    SetupDecision.Decide(new(setup, channel, game, running, installed, feed, build, cached, true, busy));

Check(SetupDecision.Compare(SemanticVersion.Parse("0.4.0"), SemanticVersion.Parse("0.4.0-beta.9")) > 0, "a release is newer than its betas");
Check(SetupDecision.Compare(SemanticVersion.Parse("0.4.0-beta.10"), SemanticVersion.Parse("0.4.0-beta.9")) > 0, "beta numbers compare numerically");
Check(SetupDecision.Compare(SemanticVersion.Parse("0.10.0"), SemanticVersion.Parse("0.9.3")) > 0, "versions compare numerically");

var noGame = Decide(null, Ready("0.4.0"), game: null);
Check(noGame.State == SetupState.NoGame && !noGame.CanInstall && !noGame.CanUninstall && !noGame.CanPrimary && !noGame.Blocked, "no game: nothing to do until a folder is picked");
var fresh = Decide(null, Ready("0.4.0"));
Check(fresh.State == SetupState.NotInstalled && fresh.Primary == SetupAction.Install && fresh.CanPrimary && fresh.TargetVersion == "0.4.0" && !fresh.CanUninstall && !fresh.CanRepair, "fresh install offers the latest version");
var update = Decide(Installed("0.3.2"), Ready("0.4.0"));
Check(update.State == SetupState.UpdateAvailable && update.Primary == SetupAction.Update && update.PrimaryLabel == "Update to 0.4.0" && update.CanUninstall && update.CanRepair, "an older install offers the update");
var current = Decide(Installed("0.4.0"), Ready("0.4.0"));
Check(current.State == SetupState.UpToDate && current.Primary == SetupAction.None && current.CanRepair && current.CanUninstall, "up to date: repair and uninstall only");
Check(Decide(Installed("0.4.0"), Ready("0.4.0"), cached: false).CanRepair, "up to date without a saved copy repairs from the feed");
Check(!Decide(Installed("0.3.2"), Ready("0.4.0"), cached: false).CanRepair, "no repair source for an older version without a saved copy");
var broken = Decide(Installed("0.4.0", repair: true), Ready("0.4.0"));
Check(broken.State == SetupState.NeedsRepair && broken.Primary == SetupAction.Repair && broken.CanPrimary && broken.Detail.Contains("dwmapi.dll"), "a damaged install offers the repair");
var brokenOld = Decide(Installed("0.3.2", repair: true), Ready("0.4.0"));
Check(brokenOld.State == SetupState.UpdateAvailable && brokenOld.Detail.Contains("repair"), "a damaged older install is fixed by the update");

// Channel switch: the same install seen from either channel.
var betaInstall = Installed("0.5.0-beta.3", channel: "beta");
var toStable = Decide(betaInstall, Ready("0.4.0"), channel: "stable");
Check(toStable.State == SetupState.NewerThanChannel && toStable.Primary == SetupAction.SwitchChannel && toStable.Title == "Switch to Stable" && toStable.PrimaryLabel == "Install 0.4.0" && toStable.CanPrimary, "beta to stable is an explicit switch to the older stable release");
Check(Decide(betaInstall, Ready("0.5.0-beta.4", channel: "beta"), channel: "beta").State == SetupState.UpdateAvailable, "on beta the next beta is an update");
Check(Decide(betaInstall, Ready("0.5.0", channel: "beta"), channel: "beta").State == SetupState.UpdateAvailable, "the beta channel offers the stable release of the same version");
Check(Decide(Installed("0.4.0"), Ready("0.5.0-beta.1", channel: "beta"), channel: "beta").State == SetupState.UpdateAvailable, "stable to beta updates to the newer beta");
var stableNewer = Decide(Installed("0.4.1"), Ready("0.4.0"));
Check(stableNewer.State == SetupState.NewerThanChannel && stableNewer.Title.Contains("newer than the Stable release"), "a newer install on the same channel is not called a switch");

// The game is running: same screen, no action that touches files.
foreach (var (installed, feed, name) in new[] { ((InstalledInfo?)null, Ready("0.4.0"), "install"), (Installed("0.3.2"), Ready("0.4.0"), "update"), (Installed("0.4.0", repair: true), Ready("0.4.0"), "repair") })
{
    var blocked = Decide(installed, feed, running: true);
    Check(blocked.Blocked && blocked.BlockedMessage == SetupDecision.GameRunningMessage && !blocked.CanPrimary, $"game running blocks the {name}");
    Check(blocked.State == Decide(installed, feed).State, $"game running keeps the {name} screen");
}
Check(Decide(Installed("0.4.0"), Ready("0.4.0"), running: true).CanChangeChannel, "the channel can be changed while the game runs");
Check(Decide(null, Ready("0.4.0"), running: true, game: null).BlockedMessage is null, "no game folder, nothing to block");
var working = Decide(Installed("0.3.2"), Ready("0.4.0"), busy: true);
Check(working.Blocked && !working.CanPrimary && !working.CanChangeChannel && working.BlockedMessage is null, "busy: everything waits for the running action");

// A new installer is required: nothing from the feed is installed.
var outdated = Decide(Installed("0.3.2"), needsNew);
Check(outdated.State == SetupState.InstallerOutdated && !outdated.CanInstall && !outdated.CanPrimary && outdated.Title == "A new installer is required", "an outdated installer refuses the new package");
Check(outdated.CanUninstall && outdated.CanRepair, "an outdated installer can still repair from the saved copy and uninstall");
Check(Decide(null, needsNew).State == SetupState.InstallerOutdated, "an outdated installer refuses a first install");

var offline = Decide(Installed("0.4.0"), new(FeedState.Unreachable, Message: "Could not reach GitHub to get the latest AimMod."));
Check(offline.State == SetupState.Offline && offline.Primary == SetupAction.Retry && offline.CanPrimary && offline.CanRepair && offline.CanUninstall, "offline: retry, repair from the saved copy, uninstall");
Check(Decide(Installed("0.4.0", repair: true), new(FeedState.Unreachable)).State == SetupState.NeedsRepair, "offline repair from the saved copy");
Check(Decide(null, FeedStatus.Loading) is { State: SetupState.Checking, CanPrimary: false }, "checking: nothing to click yet");
var oldGame = Decide(Installed("0.3.2"), Ready("0.4.0", minimumBuild: 2000), build: 1000);
Check(oldGame.State == SetupState.NeedsNewerGame && !oldGame.CanInstall, "a release for a newer game build is held");
Check(Decide(Installed("0.3.2"), Ready("0.4.0", minimumBuild: 2000), build: null).State == SetupState.UpdateAvailable, "an unknown game build is not held");
var developer = Decide(Installed(null, channel: null, managed: false), Ready("0.4.0"));
Check(developer.State == SetupState.DeveloperInstall && developer.Primary == SetupAction.Install && developer.CanPrimary && !developer.CanRepair, "a developer install can be replaced by a release");

// ---- release notes ----
var notes = ReleaseNotes.Summarize("## [0.4.0](https://example.invalid/compare) (2026-10-01)\n\n\n### Features\n\n* **kovaaks:** one-click installer ([#41](https://example.invalid/41)) ([0a1b2c3](https://example.invalid/c))\n* **kovaaks:** faster `replays`\n\n### Bug Fixes\n\n- plain fix\n<!-- hidden -->\n");
Check(notes.Count == 5 && notes[0] == new NoteLine(NoteKind.Heading, "Features") && notes[1] == new NoteLine(NoteKind.Bullet, "One-click installer") && notes[2].Text == "Faster replays" && notes[4] == new NoteLine(NoteKind.Bullet, "Plain fix"), "release-please notes become a short change log");
Check(ReleaseNotes.Summarize(string.Join("\n", Enumerable.Range(1, 40).Select(i => $"* item {i}")), 5) is { Count: 6 } longNotes && longNotes[^1].Text.Contains("release notes"), "long notes are cut with a pointer to the release page");
Check(ReleaseNotes.Summarize(null).Count == 0 && ReleaseNotes.Summarize("### Features\n").Count == 0, "empty notes");

// ---- the install flow against a fake HTTPS host and a synthetic game ----
var temp = Path.Combine(Path.GetTempPath(), "aimmod-setup-checks-" + Guid.NewGuid().ToString("N"));
var keyPath = @"Software\AimMod-Setup-Checks-" + Guid.NewGuid().ToString("N");
try
{
    var win64 = Path.Combine(temp, "lib", "steamapps", "common", "FPSAimTrainer", "FPSAimTrainer", "Binaries", "Win64");
    Directory.CreateDirectory(win64);
    File.WriteAllText(Path.Combine(win64, InstallLayout.GameExe + ".exe"), "game");
    File.WriteAllText(Path.Combine(win64, "dwmapi.dll"), "someone else's proxy");
    var original = Tree(win64);
    var output = Path.Combine(temp, "local", "AimMod", "KovaaksNative");
    var http = new Files();
    var gameRunning = false;
    var engine = new SetupEngine(output, http, _ => gameRunning);

    var (zip1, feed1) = Release(Path.Combine(temp, "pkg1"), "1.0.0", "stable", "1", http);
    var (zip2, feed2) = Release(Path.Combine(temp, "pkg2"), "1.1.0", "stable", "2", http);
    var (_, beta) = Release(Path.Combine(temp, "pkg3"), "1.2.0-beta.1", "beta", "3", http);
    http.Content[engine.FeedUrl("stable")] = Bytes(feed1);
    http.Content[engine.FeedUrl("beta")] = Bytes(beta);
    Check(engine.FeedUrl("beta").EndsWith("/aimmod-ingame-beta/aimmod-ingame-beta.json") && engine.FeedUrl("stable").StartsWith("https://github.com/verycrunchy/aimmod/"), "the installer reads the service's GitHub feeds");
    var status = await engine.CheckFeed("stable", setup, CancellationToken.None);
    Check(status.State == FeedState.Ready && status.Feed!.Version == "1.0.0", "feed fetched over HTTPS");
    Check((await engine.CheckFeed("beta", setup, CancellationToken.None)).Feed!.Version == "1.2.0-beta.1", "the channel picks the feed");
    var unpublished = new SetupEngine(Path.Combine(temp, "other"), new Files(), _ => false);
    Check((await unpublished.CheckFeed("stable", setup, CancellationToken.None)).State == FeedState.NotPublished && !unpublished.HasChannelPreference, "a channel without a release is reported as not published");
    Check(Decide(null, new(FeedState.NotPublished, Message: "No Stable release has been published yet.")) is { State: SetupState.Offline, Primary: SetupAction.Retry } np && np.Detail.Contains("Switch to Beta"), "not published suggests the other channel");
    Check(engine.ReadInstalled(win64) is null && engine.SavedChannel() == "stable", "nothing installed yet");

    gameRunning = true;
    await Throws<GameRunningException>(() => engine.InstallFromFeed(win64, feed1, "install", null, CancellationToken.None), "no install while the game runs");
    Check(Tree(win64) == original && !http.Requested.Any(u => u.EndsWith(".zip")), "nothing downloaded or changed while the game runs");
    gameRunning = false;

    var reports = new List<SetupProgress>();
    var installed = await engine.InstallFromFeed(win64, feed1, "install", new Sync(reports.Add), CancellationToken.None);
    Check(installed.Version == "1.0.0" && InstallManifest.Read(win64)!.Version == "1.0.0" && File.ReadAllText(Path.Combine(win64, "dwmapi.dll")) == "proxy-1", "install from the feed");
    Check(File.ReadAllText(Path.Combine(win64, "dwmapi.dll.aimmod-backup")) == "someone else's proxy", "the replaced file is backed up");
    Check(reports.Any(r => r.Text.StartsWith("Downloading")) && reports[^1].Fraction == 1, "progress is reported");
    Check(engine.RepairCached && File.Exists(Path.Combine(Path.GetDirectoryName(output)!, "Repair-AimMod.cmd")), "the package is saved for repairs and the repair script placed");
    Check(InstallState.ReadJson<LifecycleResult>(InstallState.ResultPath(engine.UpdatesRoot)) is { Kind: "install", Ok: true, Version: "1.0.0" }, "the workspace sees the install");
    Check(!Directory.EnumerateDirectories(Path.Combine(engine.UpdatesRoot, "setup")).Any(), "the download folder is cleaned up");

    // A swapped or corrupted download is rejected before anything changes.
    var before = Tree(win64);
    var tampered = feed2 with { Package = feed2.Package with { Sha256 = Hex('0') } };
    await Throws<ReleaseFormatException>(() => engine.InstallFromFeed(win64, tampered, "update", null, CancellationToken.None), "a zip not matching the feed is rejected");
    var wrongManifest = feed2 with { ManifestSha256 = Hex('c') };
    await Throws<ReleaseFormatException>(() => engine.InstallFromFeed(win64, wrongManifest, "update", null, CancellationToken.None), "a manifest not matching the feed is rejected");
    Check(Tree(win64) == before && InstallManifest.Read(win64)!.Version == "1.0.0", "nothing changed by rejected downloads");

    // A package this installer cannot read (later manifest format) is refused.
    var (_, futurePackage) = Release(Path.Combine(temp, "pkg-future"), "1.5.0", "stable", "f", http, schema: "aimmod.ingame.release/2");
    await Throws<ReleaseFormatException>(() => engine.InstallFromFeed(win64, futurePackage, "update", null, CancellationToken.None), "a package of a later format is refused");
    Check(Tree(win64) == before, "nothing changed by a refused package");

    var view = SetupDecision.Decide(new(setup, "stable", win64, false, engine.ReadInstalled(win64), new(FeedState.Ready, feed2), null, engine.RepairCached, true));
    Check(view.State == SetupState.UpdateAvailable, "the next release is offered");
    var updated = await engine.InstallFromFeed(win64, feed2, "update", null, CancellationToken.None);
    Check(updated.Message.Contains("from 1.0.0") && InstallManifest.Read(win64)!.Version == "1.1.0" && File.ReadAllText(Path.Combine(win64, "dwmapi.dll")) == "proxy-2", "update with the game closed");
    Check(VerifiedPackage.Open(InstallState.PreviousPackageCache(output)).Manifest.Version == "1.0.0", "the previous release is kept for a rollback");

    // Repair works offline from the saved copy.
    File.Delete(Path.Combine(win64, "dwmapi.dll"));
    Check(engine.ReadInstalled(win64)!.NeedsRepair, "a missing proxy is a needed repair");
    var requested = http.Requested.Count;
    await engine.Repair(win64, null, null, CancellationToken.None);
    Check(File.ReadAllText(Path.Combine(win64, "dwmapi.dll")) == "proxy-2" && !engine.ReadInstalled(win64)!.NeedsRepair && http.Requested.Count == requested, "repair from the saved copy, nothing downloaded");

    // Switching to beta installs the beta and the service follows the channel.
    await engine.InstallFromFeed(win64, beta, "install", null, CancellationToken.None);
    Check(InstallManifest.Read(win64)!.Version == "1.2.0-beta.1" && engine.SavedChannel() == "beta" && new UpdateSettings(output).Current.Channel == "beta", "the chosen channel is saved for the service's updates");
    var back = SetupDecision.Decide(new(setup, "stable", win64, false, engine.ReadInstalled(win64), new(FeedState.Ready, feed2), null, true, true));
    Check(back.State == SetupState.NewerThanChannel && back.Primary == SetupAction.SwitchChannel, "back to stable is offered as a switch");
    await engine.InstallFromFeed(win64, feed2, "install", null, CancellationToken.None);
    Check(InstallManifest.Read(win64)!.Version == "1.1.0" && engine.SavedChannel() == "stable", "switched back to stable");

    // A staged service update older than what the installer placed is dropped.
    var stagedRoot = Path.Combine(engine.UpdatesRoot, "staged", "1.0.5-test");
    Directory.CreateDirectory(stagedRoot);
    File.WriteAllText(Path.Combine(engine.UpdatesRoot, "staged.json"), "{\"version\":\"1.0.5\",\"channel\":\"stable\",\"folder\":\"1.0.5-test\",\"manifestSha256\":\"" + Hex('d') + "\",\"notes\":null,\"stagedAt\":\"2026-10-01T00:00:00Z\"}");
    await engine.Repair(win64, null, null, CancellationToken.None);
    Check(!File.Exists(Path.Combine(engine.UpdatesRoot, "staged.json")) && !Directory.Exists(stagedRoot), "an older staged update never applies over the installed release");

    // Apps & features (a throwaway key, never the real entry).
    var registration = new AppRegistration(keyPath, Path.Combine(temp, "local", "AimMod", "Setup"));
    var fakeSetup = Path.Combine(temp, "Downloads", "AimMod-Setup.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(fakeSetup)!);
    File.WriteAllText(fakeSetup, "setup");
    var exe = registration.Register(win64, "1.1.0", 5 << 20, fakeSetup, setup);
    using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
        Check(key?.GetValue("DisplayVersion") as string == "1.1.0" && (key.GetValue("UninstallString") as string)!.Contains("--uninstall") && (int)key.GetValue("EstimatedSize")! == 5120, "registered in Apps & features");
    Check(exe == registration.SetupExe && File.ReadAllText(exe) == "setup" && registration.GameFolder == win64, "the installer copy and game folder are remembered");

    gameRunning = true;
    Check(BlockedWhileRunning(() => engine.Uninstall(win64, false)), "no uninstall while the game runs");
    gameRunning = false;
    var dataFile = Path.Combine(output, "history.json");
    File.WriteAllText(dataFile, "{}");
    var removed = engine.Uninstall(win64, removeData: false);
    Check(Tree(win64) == original && removed.Message.Contains("kept"), "uninstall restores the game folder exactly");
    Check(File.Exists(dataFile) && File.Exists(Path.Combine(output, "update-settings.json")) && !Directory.Exists(Path.Combine(output, "package")), "uninstall keeps history and settings, drops the package copies");
    Check(!File.Exists(Path.Combine(Path.GetDirectoryName(output)!, "Repair-AimMod.cmd")), "the repair script is removed");
    Check(!registration.Unregister(fakeSetup), "uninstalling from a downloaded installer removes the copy at once");
    Check(Registry.CurrentUser.OpenSubKey(keyPath) is null && !Directory.Exists(registration.SetupFolder), "the Apps & features entry and installer copy are removed");
    registration.Register(win64, "1.1.0", 1, fakeSetup, setup);
    Check(registration.Unregister(registration.SetupExe) && Directory.Exists(registration.SetupFolder), "the running copy is left for removal after exit");
    Directory.Delete(registration.SetupFolder, true);

    await engine.InstallFromFeed(win64, feed2, "install", null, CancellationToken.None);
    engine.Uninstall(win64, removeData: true);
    Check(Tree(win64) == original && !Directory.Exists(output) && !Directory.Exists(Path.GetDirectoryName(output)), "\"also remove my data\" deletes the AimMod data folder");
    Check(engine.Uninstall(win64, removeData: false).Message.Contains("removed"), "uninstall without an install only cleans up");
}
finally
{
    try { Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); } catch (Exception) { }
    try { Directory.Delete(temp, true); } catch (IOException) { }
}
Console.WriteLine($"{count} setup checks passed");
return 0;

bool BlockedWhileRunning(Action action) { try { action(); return false; } catch (GameRunningException) { return true; } }

// A package folder, its zip on the fake host and its feed.
static (byte[] Zip, UpdateFeed Feed) Release(string folder, string version, string channel, string tag, Files http, string schema = ReleaseManifest.SchemaName)
{
    var files = new Dictionary<string, string>
    {
        ["dwmapi.dll"] = "proxy-" + tag,
        ["ue4ss/UE4SS.dll"] = "ue4ss",
        ["ue4ss/UE4SS-settings.ini"] = "[Hooks]\n",
        ["ue4ss/Mods/AimModCore/dlls/main.dll"] = "core-" + tag,
        ["ue4ss/Mods/AimModCore/service/AimMod.InGame.exe"] = "service-" + tag,
    };
    var entries = new List<ReleaseFile>();
    foreach (var (path, text) in files)
    {
        var full = Path.Combine(folder, "files", ReleasePaths.ToWindows(path));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = Encoding.UTF8.GetBytes(text);
        File.WriteAllBytes(full, bytes);
        entries.Add(new(path, Sha256Hex.Of(bytes), bytes.Length));
    }
    var manifest = new ReleaseManifest(schema, "AimMod in-game", version, channel, "2026-10-01T00:00:00Z", null,
        new("v3.0.1-1152-ge3ba1016", new string('a', 64)), new(ReleaseManifest.KovaaksAppId, 0, [new("3.9.11", 1000)]), ["AimModCore"], entries.ToArray());
    var json = JsonSerializer.SerializeToUtf8Bytes(manifest);
    File.WriteAllBytes(Path.Combine(folder, InstallLayout.PackageManifest), json);
    File.WriteAllText(Path.Combine(folder, "Repair-AimMod.cmd"), "@echo off\r\n");
    using var memory = new MemoryStream();
    using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            archive.CreateEntryFromFile(file, Path.GetRelativePath(folder, file).Replace('\\', '/'));
    var zip = memory.ToArray();
    var url = $"https://example.invalid/aimmod-ingame-v{version}/AimMod-InGame-{version}.zip";
    http.Content[url] = zip;
    var feed = new UpdateFeed(UpdateFeed.SchemaName, channel, version, "2026-10-01T00:00:00Z", "Notes for " + version, 0, Sha256Hex.Of(json), new(url, Sha256Hex.Of(zip), zip.Length));
    return (zip, feed);
}

// Every file under the folder with its content hash, for exact before/after comparisons.
static string Tree(string folder) => string.Join("\n", Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
    .Select(f => Path.GetRelativePath(folder, f) + "=" + Sha256Hex.OfFile(f)));

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
sealed class Sync(Action<SetupProgress> report) : IProgress<SetupProgress> { public void Report(SetupProgress value) => report(value); }
