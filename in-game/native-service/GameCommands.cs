using System.Globalization;
using System.Text;

namespace AimMod.InGame;

/// <summary>A game command request (see in-game/native-mod/DESIGN.md "Game commands").</summary>
sealed record GameCommandRequest(string? Action, string? Scenario, string? Mode, double? TimeScale, double? TargetSize, double? TargetSpeed, double? MapScale, string? Weapon);

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

    /// <summary>Writes the request; returns its sequence, or null with a reason.</summary>
    public (long? Sequence, string? Error) Send(GameCommandRequest request)
    {
        if (request.Action is not ("load-scenario" or "start-scenario" or "reset-overrides")) return (null, "invalid-command");
        if (request.Action != "reset-overrides" && !SafeName(request.Scenario)) return (null, "invalid-scenario");
        if (request.Weapon is not null && !SafeName(request.Weapon)) return (null, "invalid-override");
        if (request.Mode is not (null or "freeplay" or "challenge")) return (null, "invalid-mode");
        var text = new StringBuilder("AIMMOD_CORE_COMMAND_1\n");
        long sequence;
        lock (gate) { sequence = Math.Max(last + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); last = sequence; }
        void Field(string key, string? value) { if (value is not null) text.Append(key).Append('\t').Append(value).Append('\n'); }
        void Number(string key, double? value) { if (value is double v) Field(key, v.ToString("R", CultureInfo.InvariantCulture)); }
        Field("seq", sequence.ToString(CultureInfo.InvariantCulture));
        Field("action", request.Action);
        if (request.Action != "reset-overrides") Field("scenario", request.Scenario);
        if (request.Action == "start-scenario")
        {
            Field("mode", request.Mode ?? "freeplay");
            Number("timeScale", request.TimeScale); Number("targetSize", request.TargetSize); Number("targetSpeed", request.TargetSpeed); Number("mapScale", request.MapScale);
            Field("weapon", request.Weapon);
        }
        AtomicFile.WriteText(Path.Combine(output, "core-command.tsv"), text.ToString());
        return (sequence, null);
    }

    public GameCommandResult? Result()
    {
        try
        {
            var path = Path.Combine(output, "core-command-result.tsv");
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            var cells = File.ReadAllText(path).TrimEnd('\r', '\n').Split('\t');
            if (cells.Length != 5 || cells[0] != "AIMMOD_CORE_RESULT_1" || !long.TryParse(cells[1], out var sequence)) return null;
            return new(sequence, cells[2], cells[3], NativeRuns.Decode(cells[4]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
