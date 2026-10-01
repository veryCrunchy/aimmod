namespace AimMod.InGame.Multiplayer;

// Starting runs for the lobby goes through AimModCore's game commands
// (in-game/native-mod/DESIGN.md, "Game commands"). Capabilities: "load" loads
// a scenario, "start" starts one in freeplay or challenge.
interface IGameControl
{
    IReadOnlySet<string> Capabilities { get; }
    long? Load(string scenario);
    // seed: the shared match seed (0..4294967295), so every player gets the same targets.
    long? Start(string scenario, string mode, long? seed = null);
    // Re-index local scenarios after AimMod wrote one ("refresh" capability).
    long? Refresh();
    GameCommandResult? Result { get; }
    // True while KovaaK's runs a challenge (core-scene.json); null when unknown.
    bool? ChallengeRunning => null;
    // True while KovaaK's still shows its loading screen (core-scene.json loading); null when unknown.
    bool? SceneLoading => null;
    // Leave the current run the way pause > Quit does, never submitting it ("quit" capability).
    long? QuitRun() => null;
}

sealed class CoreGameControl(string output) : IGameControl
{
    readonly GameCommands commands = new(output);
    public IReadOnlySet<string> Capabilities => GameCommands.Capabilities(output);
    // Lobby overrides are baked into the generated match scenario, so the
    // runtime override fields stay empty and ranked rules stay simple.
    public long? Load(string scenario) => Capabilities.Contains("load") ? commands.Send(new("load-scenario", scenario, null, null, null, null, null, null)).Sequence : null;
    public long? Start(string scenario, string mode, long? seed = null) => Capabilities.Contains("start") ? commands.Send(new("start-scenario", scenario, MatchScenario.SafeMode(scenario, mode), null, null, null, null, null, Seed: seed)).Sequence : null;
    // AimModCore accepts refresh-scenarios wherever it can load scenarios ("load"; "refresh" if advertised).
    public long? Refresh() => Capabilities.Contains("refresh") || Capabilities.Contains("load") ? commands.Send(new("refresh-scenarios", null, null, null, null, null, null, null)).Sequence : null;
    public GameCommandResult? Result => commands.Result();
    public bool? ChallengeRunning => GameScene.Read(output) is { Available: true } scene ? scene.InChallenge : null;
    public bool? SceneLoading => GameScene.Read(output) is { Available: true } scene ? scene.Loading : null;
    public long? QuitRun() => Capabilities.Contains("quit") ? commands.Send(new("quit-run", null, null, null, null, null, null, null)).Sequence : null;
}

sealed class NoGameControl : IGameControl
{
    public IReadOnlySet<string> Capabilities { get; } = new HashSet<string>();
    public long? Load(string scenario) => null;
    public long? Start(string scenario, string mode, long? seed = null) => null;
    public long? Refresh() => null;
    public GameCommandResult? Result => null;
}
