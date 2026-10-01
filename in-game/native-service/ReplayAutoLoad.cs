using AimMod.InGame.Multiplayer;

namespace AimMod.InGame;

/// <summary>
/// Loads a pending replay's scenario by itself through the shared ScenarioLoader
/// (load-scenario, which never starts a run, then the scene, map and scale check)
/// and shows its progress on the start gate. The gate starts the replay once the
/// game shows its world. A scenario this machine lacks is named, with a Workshop
/// download when a map port provides it; the load runs again once it's installed.
/// </summary>
sealed class ReplayAutoLoad
{
    public const long InstalledDelayMs = 3_000, ChallengeDelayMs = 2_000, ReloadDelayMs = 2_000;
    readonly ReplayStartGate gate;
    readonly IGameControl game;
    readonly Func<long> clock;
    readonly Func<string, ScenarioSource?> source;
    readonly ScenarioLoader loader;
    string? id; int attempt; bool started;

    public ReplayAutoLoad(ReplayStartGate gate, IGameControl game, Func<long> clock, ContentLibrary? library, Func<string, ScenarioSource?> source)
    {
        this.gate = gate; this.game = game; this.clock = clock; this.source = source;
        loader = new ScenarioLoader(game, clock, library) { Subject = "the replay’s scenario", LogLabel = "Replay load" };
    }

    public ScenarioLoadStatus Status => loader.Status;

    public void Tick()
    {
        var pending = gate.Pending;
        if (pending is null) { if (id is not null) { id = null; loader.Reset(); } return; }
        if (pending.Id != id || pending.Attempt != attempt) { id = pending.Id; attempt = pending.Attempt; started = false; readyAt = 0; loader.Reset(); }
        var now = clock();
        if (started && Restart(pending, now)) started = false;
        if (!started)
        {
            // Only when the game shows another scenario, or the replay's on another map; the pause
            // menu and a run are the player's. Without game commands the gate says to load it by hand.
            if (pending.Reason is not ("scenario-mismatch" or "map-mismatch") || !game.Capabilities.Contains("load")) return;
            loader.Load(pending.Scenario, (pending.MapName, pending.MapScale));
            started = true;
        }
        Show(pending, loader.Tick());
    }

    // A finished load starts again when what blocked it has gone: the scenario got installed, the
    // challenge ended, or the player loaded another scenario after this one had loaded.
    bool Restart(PendingReplayStart pending, long now)
    {
        var status = loader.Status;
        if (status.Phase == ScenarioLoadPhase.Loaded) return pending.Reason == "scenario-mismatch" && Delay(now, ReloadDelayMs);
        if (status.Phase != ScenarioLoadPhase.Failed) return false;
        return status.Failure switch
        {
            ScenarioLoadFailure.Missing => source(pending.Scenario)?.Installed == true && Delay(now, InstalledDelayMs),
            ScenarioLoadFailure.ChallengeActive => game.ChallengeRunning != true && Delay(now, ChallengeDelayMs),
            _ => false,
        };
    }

    // True once the delay since the first time it was asked has passed (the game indexes a new file meanwhile).
    long readyAt;
    bool Delay(long now, long delay)
    {
        if (readyAt == 0) readyAt = now + delay;
        if (now < readyAt) return false;
        readyAt = 0;
        return true;
    }

    void Show(PendingReplayStart pending, ScenarioLoadStatus status)
    {
        var name = pending.Scenario;
        switch (status.Phase)
        {
            case ScenarioLoadPhase.Loading or ScenarioLoadPhase.Checking:
                gate.Report(pending.Id, pending.Attempt, new("scenario-loading", ScenarioLoader.LoadingMessage(name)));
                break;
            case ScenarioLoadPhase.Loaded:
                // The gate checks the world itself and starts the replay.
                gate.Report(pending.Id, pending.Attempt, null);
                break;
            case ScenarioLoadPhase.Failed:
                gate.Report(pending.Id, pending.Attempt, Failure(name, status, out var offer), offer);
                break;
        }
    }

    ReplayStartBlock? Failure(string name, ScenarioLoadStatus status, out object? offer)
    {
        offer = null;
        switch (status.Failure)
        {
            case ScenarioLoadFailure.Missing:
                var from = source(name);
                if (from is { Downloadable: true })
                {
                    offer = new { workshop = true, state = from.Download, percent = from.Percent };
                    return new("scenario-missing", from.Download is "queued" or "downloading"
                        ? "Downloading “" + name + "” from the Steam Workshop" + (from.Percent is int p ? " (" + p + "%)" : "") + "…"
                        : "“" + name + "” isn’t installed. Download it from the Steam Workshop; the replay starts when it’s installed.");
                }
                return new("scenario-missing", "“" + name + "” isn’t installed. Install it in KovaaK’s, then press Retry now.");
            case ScenarioLoadFailure.ChallengeActive:
                return new("challenge-active", "Finish or quit your run (Esc, then Quit); “" + name + "” loads after that.");
            case ScenarioLoadFailure.Unavailable:
                return null;
            default:
                return new("load-failed", "Couldn’t load “" + name + "”. " + status.Message);
        }
    }
}
