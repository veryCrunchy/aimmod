using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// The load gate (all modes): a match starts only when every player's KovaaK's shows
// the round's scenario with its map at its scale, not loading (core-scene.json).
// "Done loaded" from the game command isn't enough: a live CS match once stayed on
// the previous map while the scenario name had already switched.
sealed partial class MultiplayerService
{
    readonly Dictionary<string, (string? MapName, double? MapScale)> expectedMaps = new(StringComparer.Ordinal);
    public const long MapMismatchRetryMs = 15_000, MapMismatchFailMs = 30_000;
    string? verifyKey; long verifySince, verifyRetriedAt; int verifyGood; string? failedScenario;

    // The map a round's scenario loads: from the match scenario AimMod built, else the base file.
    (string? MapName, double? MapScale) ExpectedMap(string scenario)
    {
        if (expectedMaps.TryGetValue(scenario, out var known)) return known;
        try
        {
            if (library.PathOf("scenario", scenario) is { } path) return expectedMaps[scenario] = MatchScenario.MapOf(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (null, null);
    }

    // What this machine's game shows against the plan: null while it isn't there yet.
    internal static string? SceneProblem(GameScene? scene, string scenario, (string? MapName, double? MapScale) expected)
    {
        if (scene is null) return "AimModCore isn’t reporting the game’s scene.";
        if (!scene.Available || scene.Loading) return "loading";
        if (!string.Equals(scene.Scenario, scenario, StringComparison.Ordinal)) return "KovaaK’s shows “" + scene.Scenario + "”, not the match scenario.";
        if (expected.MapName is { } map && !MatchScenario.SameMap(scene.MapName, map)) return "KovaaK’s kept the map “" + scene.MapName + "” instead of “" + map + "”.";
        if (expected.MapScale is { } scale && scene.MapScale is { } shown && Math.Abs(shown - scale) > 0.01) return "The map loaded at scale " + shown + " instead of " + scale + ".";
        return null;
    }

    // Loading phase: report loaded only once the scene matches for two polls in a row;
    // retry the load once after 15 s of a wrong map, report the problem after 30 s.
    void VerifyLoaded(MatchSnapshot match, string key)
    {
        if (plan is null || loadedSent == key) return;
        void Report(bool ok, string? reason)
        {
            loadedSent = key;
            if (!ok) failedScenario = plan.Scenario;
            Command("loaded", JsonSerializer.SerializeToElement(new { match = match.Id, round = match.Round, attempt = match.LoadAttempt, ok, reason }));
        }
        if (plan.State == "error") { Report(false, plan.Message); return; }
        // Without AimModCore's game commands the player loads by hand; nothing to verify.
        if (plan.State == "manual") { Report(true, null); return; }
        if (plan.State is not ("ready" or "started")) return;
        if (verifyKey != key) { verifyKey = key; verifySince = clock(); verifyRetriedAt = 0; verifyGood = 0; }
        var problem = SceneProblem(game.Scene, plan.Scenario, ExpectedMap(plan.Scenario));
        if (problem is null) { if (++verifyGood >= 2) Report(true, null); return; }
        verifyGood = 0;
        var waited = clock() - verifySince;
        if (waited >= MapMismatchRetryMs && verifyRetriedAt == 0 && problem != "loading" && game.Load(plan.Scenario) is long again)
        {
            verifyRetriedAt = clock();
            plan = plan with { State = "loading", Message = "The map didn’t load. Loading “" + plan.Scenario + "” again…", LoadSequence = again };
            verifySince = clock() - MapMismatchRetryMs; // the second wait ends at the fail limit
            return;
        }
        if (waited >= MapMismatchFailMs) Report(false, problem == "loading" ? "KovaaK’s is still loading the map." : problem);
    }

    // Keep a failed match scenario for debugging when the game folder copy goes (last 3).
    void KeepFailedScenario(string? keepName)
    {
        if (failedScenario is null || failedScenario == keepName || outputFolder is null || library.Root is not { } root) return;
        try
        {
            var source = Path.Combine(root, "Saved", "SaveGames", "Scenarios", failedScenario + ".sce");
            if (File.Exists(source))
            {
                var folder = Path.Combine(outputFolder, "match-debug");
                Directory.CreateDirectory(folder);
                File.Copy(source, Path.Combine(folder, failedScenario + ".sce"), overwrite: true);
                foreach (var old in new DirectoryInfo(folder).GetFiles("*.sce").OrderByDescending(f => f.LastWriteTimeUtc).Skip(3)) old.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        failedScenario = null;
    }
}
