using System.Globalization;
using System.Text;

namespace AimMod.InGame;

/// <summary>A game command request (see in-game/native-mod/DESIGN.md "Game commands").</summary>
sealed record GameCommandRequest(string? Action, string? Scenario, string? Mode, double? TimeScale, double? TargetSize, double? TargetSpeed, double? MapScale, string? Weapon,
    int? Width = null, int? Height = null, string? Out = null, ThumbnailView[]? Views = null, long? Seed = null, string? Then = null);

/// <summary>One thumbnail camera: location (cm), pitch/yaw (degrees), horizontal FOV.</summary>
sealed record ThumbnailView(double X, double Y, double Z, double Pitch, double Yaw, double Fov);

/// <summary>AimModCore's answer to one request (core-command-result.tsv).</summary>
sealed record GameCommandResult(long Sequence, string State, string Code, string Message);

/// <summary>
/// File transport to AimModCore's game control. The native mod validates
/// every field again and refuses while a challenge runs; this side only
/// rejects obviously malformed requests and assigns increasing sequences.
/// </summary>
sealed class GameCommands(string output)
{
    readonly object gate = new();
    long last;

    /// <summary>Capabilities AimModCore currently advertises (core-active.tsv).</summary>
    public static IReadOnlySet<string> Capabilities(string output, DateTime? utcNow = null)
    {
        try
        {
            var path = Path.Combine(output, "core-active.tsv");
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 1024) return new HashSet<string>();
            var cells = File.ReadAllText(path).TrimEnd('\r', '\n').Split('\t');
            if (cells.Length != 4 || cells[0] != "AIMMOD_CORE_1" || !long.TryParse(cells[2], out var stamp)) return new HashSet<string>();
            var now = new DateTimeOffset(DateTime.SpecifyKind(utcNow ?? DateTime.UtcNow, DateTimeKind.Utc)).ToUnixTimeSeconds();
            if (Math.Abs(now - stamp) > 3) return new HashSet<string>();
            return cells[3].Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new HashSet<string>(); }
    }

    static bool SafeName(string? s) => s is { Length: > 0 and <= 256 } && !s.Any(char.IsControl) && s.Trim().Length > 0;

    /// <summary>Scenarios whose map AimModCore may load itself (ensure-map): the ones AimMod generates.</summary>
    public static bool MapFixAllowed(string? scenario) => SafeName(scenario) && scenario!.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) < 0
        && (scenario.StartsWith("AimMod Match - ", StringComparison.Ordinal) || scenario.StartsWith("AimMod Probe ", StringComparison.Ordinal) || scenario.StartsWith("AimMod - ", StringComparison.Ordinal));

    /// <summary>Writes the request; returns its sequence, or null with a reason.</summary>
    public (long? Sequence, string? Error) Send(GameCommandRequest request)
    {
        if (request.Action is not ("load-scenario" or "start-scenario" or "reset-overrides" or "refresh-scenarios" or "capture-thumbnail" or "end-run" or "quit-run" or "ensure-map")) return (null, "invalid-command");
        var named = request.Action is "load-scenario" or "start-scenario" or "capture-thumbnail" or "end-run" or "ensure-map";
        if (request.Action == "capture-thumbnail")
        {
            if (request.Width is not (>= 64 and <= 3840) || request.Height is not (>= 64 and <= 2160)) return (null, "invalid-thumbnail");
            if (request.Out is not { Length: >= 5 and <= 128 } outName || !outName.EndsWith(".png", StringComparison.Ordinal) || outName.StartsWith('.') || outName.Contains("..")
                || !outName.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '(' or ')')) return (null, "invalid-thumbnail");
            if (request.Views is not { Length: >= 1 and <= 4 } views || views.Any(v => !double.IsFinite(v.X) || !double.IsFinite(v.Y) || !double.IsFinite(v.Z)
                || Math.Abs(v.X) > 1e7 || Math.Abs(v.Y) > 1e7 || Math.Abs(v.Z) > 1e7 || v.Pitch is < -90 or > 90 || !double.IsFinite(v.Yaw) || Math.Abs(v.Yaw) > 3600 || v.Fov is < 5 or > 170))
                return (null, "invalid-thumbnail");
        }
        if (named && !SafeName(request.Scenario)) return (null, "invalid-scenario");
        if (request.Weapon is not null && !SafeName(request.Weapon)) return (null, "invalid-override");
        if (request.Mode is not (null or "freeplay" or "challenge")) return (null, "invalid-mode");
        // end-run: freeplay AimMod match scenarios only; then = stop (default) or reset.
        if (request.Then is not null && (request.Action != "end-run" || request.Then is not ("stop" or "reset"))) return (null, "invalid-command");
        if (request.Action == "end-run" && request.Scenario?.StartsWith("AimMod Match - ", StringComparison.Ordinal) != true) return (null, "not-a-match");
        // ensure-map: AimMod's own scenarios only, never with a mode or overrides (AimModCore also refuses challenges and benchmarks).
        if (request.Action == "ensure-map" && !MapFixAllowed(request.Scenario)) return (null, "not-a-match");
        if (request.Action == "ensure-map" && (request.Mode is not null || request.TimeScale is not null || request.TargetSize is not null || request.TargetSpeed is not null || request.MapScale is not null || request.Weapon is not null))
            return (null, "invalid-command");
        if (request.Seed is not null && (request.Action != "start-scenario" || request.Seed is < 0 or > uint.MaxValue)) return (null, "invalid-seed");
        // Never in ranked play (AimModCore enforces the same rules): a challenge runs exactly as published.
        if (request.Mode == "challenge" && (request.TimeScale is not null || request.TargetSize is not null || request.TargetSpeed is not null || request.MapScale is not null || request.Weapon is not null))
            return (null, "overrides-freeplay-only");
        if (request.Seed is not null && request.Mode == "challenge" && request.Scenario?.StartsWith("AimMod Match - ", StringComparison.Ordinal) != true) return (null, "seed-not-allowed");
        var text = new StringBuilder("AIMMOD_CORE_COMMAND_1\n");
        long sequence;
        lock (gate) { sequence = Math.Max(last + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); last = sequence; }
        void Field(string key, string? value) { if (value is not null) text.Append(key).Append('\t').Append(value).Append('\n'); }
        void Number(string key, double? value) { if (value is double v) Field(key, v.ToString("R", CultureInfo.InvariantCulture)); }
        Field("seq", sequence.ToString(CultureInfo.InvariantCulture));
        Field("action", request.Action);
        if (named) Field("scenario", request.Scenario);
        if (request.Action == "capture-thumbnail")
        {
            Field("width", request.Width!.Value.ToString(CultureInfo.InvariantCulture));
            Field("height", request.Height!.Value.ToString(CultureInfo.InvariantCulture));
            Field("out", request.Out);
            for (int i = 0; i < request.Views!.Length; i++)
            {
                var v = request.Views[i];
                Field("view" + (i + 1), string.Join(',', new[] { v.X, v.Y, v.Z, v.Pitch, v.Yaw, v.Fov }.Select(n => n.ToString("R", CultureInfo.InvariantCulture))));
            }
        }
        if (request.Action == "end-run") Field("then", request.Then);
        if (request.Action == "start-scenario")
        {
            Field("mode", request.Mode ?? "freeplay");
            Number("timeScale", request.TimeScale); Number("targetSize", request.TargetSize); Number("targetSpeed", request.TargetSpeed); Number("mapScale", request.MapScale);
            Field("weapon", request.Weapon);
            if (request.Seed is long seed) Field("seed", seed.ToString(CultureInfo.InvariantCulture));
        }
        AtomicFile.WriteText(Path.Combine(output, "core-command.tsv"), text.ToString());
        return (sequence, null);
    }

    /// <summary>The newest answer AimModCore wrote.</summary>
    public GameCommandResult? Result() => Results() is { Count: > 0 } all ? all[^1] : null;

    /// <summary>The newest answer to one request, if it is still among the recent ones.</summary>
    public GameCommandResult? ResultFor(long sequence) => Results().LastOrDefault(r => r.Sequence == sequence);

    /// <summary>The recent answers (AimModCore keeps the last 8, oldest first, one per line).</summary>
    public IReadOnlyList<GameCommandResult> Results()
    {
        try
        {
            var path = Path.Combine(output, "core-command-result.tsv");
            if (!File.Exists(path) || new FileInfo(path).Length > 32768) return [];
            var list = new List<GameCommandResult>();
            foreach (var line in File.ReadAllText(path).Split('\n'))
            {
                var cells = line.TrimEnd('\r').Split('\t');
                if (cells.Length != 5 || cells[0] != "AIMMOD_CORE_RESULT_1" || !long.TryParse(cells[1], out var sequence)) continue;
                list.Add(new(sequence, cells[2], cells[3], NativeRuns.Decode(cells[4])));
            }
            return list;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
