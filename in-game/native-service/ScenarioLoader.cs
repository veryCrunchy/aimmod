using AimMod.InGame.Multiplayer;

namespace AimMod.InGame;

/// <summary>Why a scenario load ended without the game showing the scenario.</summary>
enum ScenarioLoadFailure
{
    None,
    /// <summary>AimModCore can't load scenarios here (not running, or a game build without the entry points).</summary>
    Unavailable,
    /// <summary>KovaaK's doesn't have the scenario (load-scenario answered unknown-scenario).</summary>
    Missing,
    /// <summary>A challenge is running; AimModCore never interrupts one.</summary>
    ChallengeActive,
    /// <summary>Any other refusal of load-scenario.</summary>
    Rejected,
    /// <summary>No answer in time, or KovaaK's was still loading at the limit.</summary>
    Timeout,
    /// <summary>The game kept showing another scenario, map or scale after the fixes.</summary>
    WrongScene,
}

enum ScenarioLoadPhase { Idle, Loading, Checking, Loaded, Failed }

/// <summary>
/// Progress of one load. Map is the map check as the lobby's round box shows it:
/// checking, wrong, ok or failed. Code is AimModCore's answer code for a refusal.
/// </summary>
sealed record ScenarioLoadStatus(ScenarioLoadPhase Phase, string Map, string Message, ScenarioLoadFailure Failure = ScenarioLoadFailure.None, string? Code = null)
{
    public static readonly ScenarioLoadStatus Idle = new(ScenarioLoadPhase.Idle, "checking", "");
    public bool Done => Phase is ScenarioLoadPhase.Loaded or ScenarioLoadPhase.Failed;
}

/// <summary>
/// Loads a scenario in KovaaK's and checks that the game really shows it. The one
/// load path for the multiplayer load gate and for replay playback. Tick-driven:
/// call Tick a few times a second; every step reads core-scene.json through the
/// game control.
///
/// 1. load-scenario (AimModCore's game command: the scenario browser's own load,
///    without playing, so it never starts a run, let alone a challenge);
/// 2. its answer (busy is asked again; unknown-scenario means it isn't installed);
/// 3. core-scene.json shows the scenario, not loading, with the scenario file's
///    MapName at its MapScale, on two polls in a row;
/// 4. the right scenario on the wrong map: ensure-map at once (AimModCore applies the
///    scenario's own map; it only does that for AimMod's scenarios, never in a
///    challenge, benchmark or the editor), else the scenario is loaded again; any
///    other wrong scene is loaded again after 15 s; still wrong 15 s after the fix fails.
/// Stock and Workshop scenarios rely on KovaaK's normal load; nothing here toggles
/// the play type.
/// </summary>
sealed class ScenarioLoader(IGameControl game, Func<long> clock, ContentLibrary? library = null)
{
    public const long RetryMs = 15_000, FailMs = 30_000, AnswerMs = 45_000, BusyRetryMs = 1_000;
    readonly Dictionary<string, (string? MapName, double? MapScale)> expectedMaps = new(StringComparer.Ordinal);

    /// <summary>How a reload is sent: load-scenario by default; the lobby routes it through its round plan.</summary>
    public Func<string, string, long?>? Reload { get; init; }
    /// <summary>How messages name the scenario the game should show.</summary>
    public string Subject { get; init; } = "the match scenario";
    /// <summary>Prefix of the log line written when AimModCore and core-scene.json disagree.</summary>
    public string LogLabel { get; init; } = "Load gate";

    string? scenario;
    (string? MapName, double? MapScale) fallback;
    long? loadSequence; long loadSince, busyAt; bool answered;
    bool checking; long checkSince, retriedAt; int good;
    long? mapFix; string? mapFixError; bool mapConfirmed, disagreeLogged;

    public ScenarioLoadStatus Status { get; private set; } = ScenarioLoadStatus.Idle;
    public string? Scenario => scenario;

    /// <summary>Loads the scenario (load-scenario) and checks it. Fallback: the map to expect when the scenario file isn't in the library.</summary>
    public ScenarioLoadStatus Load(string name, (string? MapName, double? MapScale) fallbackMap = default)
    {
        Begin(name, fallbackMap);
        if (!game.Capabilities.Contains("load") || game.Load(name) is not long sequence)
            return Fail(ScenarioLoadFailure.Unavailable, "AimMod can’t load scenarios in this game build. Open “" + name + "” in KovaaK’s.");
        Sent(sequence);
        return Status = new(ScenarioLoadPhase.Loading, "checking", LoadingMessage(name));
    }

    /// <summary>The caller has loaded the scenario already (the lobby's round plan): check it only.</summary>
    public void Verify(string name, (string? MapName, double? MapScale) fallbackMap = default)
    {
        Begin(name, fallbackMap);
        StartChecking();
        Status = new(ScenarioLoadPhase.Checking, "checking", "Checking the map…");
    }

    public void Reset() { scenario = null; loadSequence = null; checking = false; Status = ScenarioLoadStatus.Idle; }

    public static string LoadingMessage(string name) => "Loading “" + name + "”…";
    public static string ReloadMessage(string name, string problem) => "The map didn’t load (" + problem.TrimEnd('.') + "). Loading “" + name + "” again…";

    public ScenarioLoadStatus Tick()
    {
        if (scenario is null || Status.Done) return Status;
        var now = clock();
        if (loadSequence is long sequence && !answered)
        {
            var result = game.ResultFor(sequence);
            if (result is { State: "done" }) answered = true;
            else if (result is { State: "error" })
            {
                // Busy (a scenario or another command is under way): ask again shortly.
                if (result.Code == "busy" && now - loadSince < AnswerMs)
                {
                    if (busyAt == 0) busyAt = now;
                    else if (now - busyAt >= BusyRetryMs && game.Load(scenario) is long again) { loadSequence = again; busyAt = 0; }
                    return Status;
                }
                // Someone loaded it meanwhile (the lobby after a challenge ended): carry on with the check.
                if (game.Scene is { Available: true, Loading: false } shown && string.Equals(shown.Scenario, scenario, StringComparison.Ordinal)) answered = true;
                else return Refused(result);
            }
            else if (now - loadSince >= AnswerMs) return Fail(ScenarioLoadFailure.Timeout, "KovaaK’s didn’t finish loading “" + scenario + "”.");
            else return Status;
        }
        if (!checking) StartChecking();
        return Check(now);
    }

    void Begin(string name, (string? MapName, double? MapScale) fallbackMap)
    {
        scenario = name; fallback = fallbackMap;
        loadSequence = null; answered = false; busyAt = 0; checking = false;
    }

    void Sent(long sequence) { loadSequence = sequence; loadSince = clock(); answered = false; busyAt = 0; }

    void StartChecking()
    {
        checking = true; checkSince = clock(); retriedAt = 0; good = 0;
        mapFix = null; mapFixError = null; mapConfirmed = false; disagreeLogged = false;
    }

    ScenarioLoadStatus Fail(ScenarioLoadFailure failure, string message, string? code = null) => Status = new(ScenarioLoadPhase.Failed, "failed", message, failure, code);

    ScenarioLoadStatus Refused(GameCommandResult result) => result.Code switch
    {
        "unknown-scenario" => Fail(ScenarioLoadFailure.Missing, "“" + scenario + "” isn’t installed.", result.Code),
        "challenge-active" => Fail(ScenarioLoadFailure.ChallengeActive, "A challenge is running. Finish or quit it first.", result.Code),
        "unsupported" => Fail(ScenarioLoadFailure.Unavailable, "AimMod can’t load scenarios in this game build. Open “" + scenario + "” in KovaaK’s.", result.Code),
        "timeout" => Fail(ScenarioLoadFailure.Timeout, "KovaaK’s didn’t finish loading “" + scenario + "”.", result.Code),
        _ => Fail(ScenarioLoadFailure.Rejected, "KovaaK’s couldn’t load “" + scenario + "” (" + result.Code + (result.Message.Length > 0 ? ": " + result.Message : "") + ").", result.Code),
    };

    /// <summary>The map a scenario AimMod just wrote will load (the lobby's match scenario).</summary>
    public void Expect(string name, (string? MapName, double? MapScale) map) => expectedMaps[name] = map;

    /// <summary>The map a scenario loads, from its file in the library (cached per name).</summary>
    public (string? MapName, double? MapScale) ExpectedMap(string name)
    {
        if (expectedMaps.TryGetValue(name, out var known)) return known;
        try
        {
            if (library?.PathOf("scenario", name) is { } path) return expectedMaps[name] = MatchScenario.MapOf(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (null, null);
    }

    /// <summary>What the game shows against the plan: null once it's there; "loading" while KovaaK's loads.</summary>
    internal static string? SceneProblem(GameScene? scene, string scenario, (string? MapName, double? MapScale) expected, string subject = "the match scenario")
    {
        if (scene is null) return "AimModCore isn’t reporting the game’s scene.";
        if (!scene.Available || scene.Loading) return "loading";
        if (!string.Equals(scene.Scenario, scenario, StringComparison.Ordinal)) return "KovaaK’s shows “" + scene.Scenario + "”, not " + subject + ".";
        if (expected.MapName is { } map && !MatchScenario.SameMap(scene.MapName, map)) return "KovaaK’s kept the map “" + scene.MapName + "” instead of “" + map + "”.";
        if (expected.MapScale is { } scale && scene.MapScale is { } shown && Math.Abs(shown - scale) > 0.01) return "The map loaded at scale " + shown + " instead of " + scale + ".";
        return null;
    }

    ScenarioLoadStatus Check(long now)
    {
        var name = scenario!;
        var scene = game.Scene;
        var expected = ExpectedMap(name);
        if (expected is (null, null)) expected = fallback;
        var problem = SceneProblem(scene, name, expected, Subject);
        // AimModCore answered ensure-map: an unsupported game falls back to loading the scenario again.
        if (mapFix is long fix && game.ResultFor(fix) is { State: "done" or "error" } fixResult)
        {
            mapFix = null;
            if (fixResult.State == "done" && fixResult.Code is "map-ok" or "map-loaded") mapConfirmed = true;
            else if (fixResult.Code == "unsupported" && problem is not null && LoadAgain(name, problem)) return Status;
            else if (fixResult.State == "error") mapFixError = fixResult.Code + (fixResult.Message.Length > 0 ? ": " + fixResult.Message : "");
        }
        // AimModCore verified the map itself (ensure-map done): that counts, even if a scene report
        // still names another map, as long as it shows the scenario, not loading.
        if (problem is not null && problem != "loading" && mapConfirmed && scene is { Available: true, Loading: false } && string.Equals(scene.Scenario, name, StringComparison.Ordinal))
        {
            if (!disagreeLogged)
            {
                disagreeLogged = true;
                Console.Error.WriteLine(LogLabel + ": AimModCore loaded the map of " + name + " (ensure-map), but core-scene.json shows " + scene.MapName + " at " + scene.MapScale + "; counting the map as loaded.");
            }
            problem = null;
        }
        if (problem is null)
        {
            if (++good < 2) return Status = new(ScenarioLoadPhase.Checking, "checking", "Checking the map…");
            return Status = new(ScenarioLoadPhase.Loaded, "ok", "Loaded “" + name + "”.");
        }
        good = 0;
        Status = problem == "loading" ? new(ScenarioLoadPhase.Checking, "checking", "KovaaK’s is loading the map…") : new(ScenarioLoadPhase.Checking, "wrong", problem);
        var waited = now - checkSince;
        var mapWrong = problem != "loading" && scene is { } current && string.Equals(current.Scenario, name, StringComparison.Ordinal);
        if (problem != "loading" && retriedAt == 0 && ((mapWrong && game.Capabilities.Contains("map")) || waited >= RetryMs))
        {
            retriedAt = now;
            // The second wait ends at the fail limit.
            if (FixWrongMap(name, problem, mapWrong)) { checkSince = now - RetryMs; return Status; }
        }
        if (waited >= FailMs)
        {
            var reason = problem == "loading" ? "KovaaK’s is still loading the map." : problem + (mapFixError is { } why ? " (AimMod’s map load: " + why + ")" : "");
            return Fail(problem == "loading" ? ScenarioLoadFailure.Timeout : ScenarioLoadFailure.WrongScene, reason);
        }
        return Status;
    }

    // The one place a wrong map is handled, once per load; true while a fix is under way (the
    // check restarts). The right scenario with the wrong map: AimModCore loads the scenario's own
    // map through KovaaK's map pipeline (ensure-map, "map" capability). Otherwise, or when
    // AimModCore answers unsupported, the scenario is loaded again.
    bool FixWrongMap(string name, string problem, bool mapWrong)
    {
        if (mapWrong && game.EnsureMap(name) is long fix)
        {
            mapFix = fix;
            Status = new(ScenarioLoadPhase.Checking, "checking", "KovaaK’s kept the previous map. Loading the map of “" + name + "”…");
            return true;
        }
        return LoadAgain(name, problem);
    }

    bool LoadAgain(string name, string problem)
    {
        if ((Reload is { } reload ? reload(name, problem) : game.Load(name)) is not long again) return false;
        Sent(again);
        Status = new(ScenarioLoadPhase.Checking, "checking", ReloadMessage(name, problem));
        return true;
    }
}
