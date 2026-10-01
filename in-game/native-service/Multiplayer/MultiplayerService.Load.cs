using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// The load gate (all modes): a match starts only when every player's KovaaK's shows
// the round's scenario with its map at its scale, not loading (core-scene.json).
// "Done loaded" from the game command isn't enough: a live CS match once stayed on
// the previous map while the scenario name had already switched. The check itself
// is ScenarioLoader's, shared with replay playback.
sealed partial class MultiplayerService
{
    public const long MapMismatchRetryMs = ScenarioLoader.RetryMs, MapMismatchFailMs = ScenarioLoader.FailMs;
    string? verifyKey, waitSent; string? failedScenario;
    // The shared load check: scene, map and scale, ensure-map, retry and time limit.
    // Its reload goes through the round plan, so the lobby sees the round loading again.
    ScenarioLoader? loadGate;
    ScenarioLoader LoadGate => loadGate ??= new ScenarioLoader(game, clock, library) { Reload = ReloadScenario };

    // Loading phase: report loaded only once the scene matches for two polls in a row.
    // The right scenario with the wrong map is fixed right away (ensure-map); anything else
    // is retried once after 15 s; the problem is reported 15 s after the fix.
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
        // Held by a challenge run: say so (everyone sees why the match waits), without failing the load.
        if (plan.State == "blocked")
        {
            if (waitSent != key)
            {
                waitSent = key;
                Command("loaded", JsonSerializer.SerializeToElement(new { match = match.Id, round = match.Round, attempt = match.LoadAttempt, ok = false, pending = true, reason = "Still in a challenge run." }));
            }
            return;
        }
        if (plan.State is not ("ready" or "started")) return;
        if (verifyKey != key) { verifyKey = key; LoadGate.Verify(plan.Scenario); }
        var status = LoadGate.Tick();
        switch (status.Phase)
        {
            case ScenarioLoadPhase.Loaded:
                plan = plan with { Map = "ok", Message = "Your map loaded. The countdown starts when everyone has loaded." };
                Report(true, null);
                break;
            case ScenarioLoadPhase.Failed:
                plan = plan with { Map = "failed", Message = status.Message + " The host can retry or end the match." };
                Report(false, status.Message);
                break;
            default:
                plan = plan with { Map = status.Map, Message = status.Message };
                break;
        }
    }

    // The loader's reload: the round's scenario again, as a new load of the round plan.
    long? ReloadScenario(string scenario, string problem)
    {
        if (plan is null || plan.Scenario != scenario || game.Load(plan.Scenario) is not long again) return null;
        plan = plan with { State = "loading", Map = "checking", Message = ScenarioLoader.ReloadMessage(plan.Scenario, problem), LoadSequence = again };
        return again;
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
