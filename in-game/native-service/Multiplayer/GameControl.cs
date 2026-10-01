namespace AimMod.InGame.Multiplayer;

// Starting runs for the lobby goes through AimModCore's game commands
// (in-game/native-mod/DESIGN.md, "Game commands"). Capabilities: "load" loads
// a scenario, "start" starts one in freeplay or challenge.
interface IGameControl
{
    IReadOnlySet<string> Capabilities { get; }
    long? Load(string scenario);
    long? Start(string scenario, string mode);
    GameCommandResult? Result { get; }
}

sealed class CoreGameControl(string output) : IGameControl
{
    readonly GameCommands commands = new(output);
    public IReadOnlySet<string> Capabilities => GameCommands.Capabilities(output);
    // Lobby overrides are baked into the generated match scenario, so the
    // runtime override fields stay empty and ranked rules stay simple.
    public long? Load(string scenario) => Capabilities.Contains("load") ? commands.Send(new("load-scenario", scenario, null, null, null, null, null, null)).Sequence : null;
    public long? Start(string scenario, string mode) => Capabilities.Contains("start") ? commands.Send(new("start-scenario", scenario, mode, null, null, null, null, null)).Sequence : null;
    public GameCommandResult? Result => commands.Result();
}

sealed class NoGameControl : IGameControl
{
    public IReadOnlySet<string> Capabilities { get; } = new HashSet<string>();
    public long? Load(string scenario) => null;
    public long? Start(string scenario, string mode) => null;
    public GameCommandResult? Result => null;
}
