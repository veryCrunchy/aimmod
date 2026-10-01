namespace AimMod.InGame;

sealed record HealthProblem(string Code, string Path, string Message);
sealed record GameBuildCheck(long? SteamBuildId, bool Tested, string? TestedVersion, string? Warning);
sealed record HealthReport(bool Installed, bool Managed, string? Version, IReadOnlyList<HealthProblem> Problems, GameBuildCheck Game)
{
    public bool NeedsRepair => Problems.Count > 0;
}

// Compares the game folder with ue4ss\aimmod-install.json: the UE4SS proxy
// (dwmapi.dll), UE4SS itself and its settings, every AimMod mod file, and the
// mods.txt / mods.json entries. A Steam update or "Verify integrity of game
// files" can remove or replace any of these.
static class InstallHealth
{
    public static HealthReport Inspect(string win64, InstallManifest? manifest, ReleaseManifest? release = null, string? runningService = null)
    {
        var build = InstallLayout.SteamBuildId(win64);
        var game = CheckBuild(build, release?.Game);
        if (manifest is null) return new(false, false, null, [new("not-installed", "", "AimMod is not installed in this game folder.")], game);
        var problems = new List<HealthProblem>();
        foreach (var (path, sha) in manifest.Files)
        {
            string full;
            try { full = Path.GetFullPath(Path.Combine(win64, path)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { problems.Add(new("invalid-entry", path, "The install record is damaged.")); continue; }
            var kind = path.Equals("dwmapi.dll", StringComparison.OrdinalIgnoreCase) ? "the UE4SS loader (dwmapi.dll)"
                : path.Equals(@"ue4ss\UE4SS-settings.ini", StringComparison.OrdinalIgnoreCase) ? "the UE4SS settings"
                : path.StartsWith(@"ue4ss\Mods\", StringComparison.OrdinalIgnoreCase) ? "an AimMod file" : "a UE4SS file";
            if (!File.Exists(full)) { problems.Add(new("missing", path, $"{Capital(kind)} is missing: {path}")); continue; }
            string actual;
            try { actual = Sha256Hex.OfFile(full); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (!Sha256Hex.Same(actual, sha))
            {
                var mismatch = runningService is not null && string.Equals(Path.GetFullPath(runningService), full, StringComparison.OrdinalIgnoreCase)
                    ? "version-mismatch" : "changed";
                problems.Add(new(mismatch, path, $"{Capital(kind)} was replaced or changed: {path}"));
            }
        }
        var entries = ModList.Read(Path.Combine(win64, "ue4ss", "Mods"));
        foreach (var mod in manifest.Mods)
        {
            if (!entries.TryGetValue(mod, out var enabled)) problems.Add(new("mod-list", mod, $"{mod} is missing from ue4ss\\Mods\\mods.txt."));
            else if (!enabled) problems.Add(new("mod-disabled", mod, $"{mod} is turned off in ue4ss\\Mods\\mods.txt."));
        }
        return new(true, manifest.Managed, manifest.Version, problems, game);
    }
    static string Capital(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    public static GameBuildCheck CheckBuild(long? build, GameRequirements? requirements)
    {
        if (build is null || requirements is null) return new(build, false, null, null);
        var tested = requirements.TestedBuilds.FirstOrDefault(b => b.SteamBuildId == build);
        if (tested is not null) return new(build, true, tested.Version, null);
        if (requirements.MinimumSteamBuildId > 0 && build < requirements.MinimumSteamBuildId)
            return new(build, false, null, "This KovaaK's build is older than this AimMod release supports. Update KovaaK's in Steam.");
        var known = string.Join(", ", requirements.TestedBuilds.Select(b => b.Version).Distinct());
        return new(build, false, null, $"KovaaK's was updated (Steam build {build}). AimMod was tested on {(known.Length > 0 ? known : "other builds")}; if something misbehaves, check for an AimMod update.");
    }
}
