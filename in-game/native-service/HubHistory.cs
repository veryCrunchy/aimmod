using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AimMod.InGame;

static class HubHistory
{
    // Match Hub's slugifyScenarioName in api/internal/store/read.go exactly.
    internal static string ScenarioSlug(string name)
    {
        var result = new StringBuilder(); var dash = false;
        foreach (var c in name.Trim().ToLowerInvariant())
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') { result.Append(c); dash = false; }
            else if (c is ' ' or '-' or '_' or '\'' or '.' && !dash && result.Length > 0) { result.Append('-'); dash = true; }
        return result.ToString().Trim('-');
    }
    public static string PublicId(string externalId, string localId) => "run_" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(externalId.Trim().ToLowerInvariant() + ":" + localId.Trim())))[..32].ToLowerInvariant();

    public static DateTimeOffset Date(string value) =>
        DateTimeOffset.TryParseExact(value, "yyyy.MM.dd-HH.mm.ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local) ? local :
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant) ? instant : DateTimeOffset.MinValue;

    // Local records win: Hub previews do not carry local replay or movement data.
    // Never use approximate score/time matching; separate legitimate runs can tie.
    public static Run[] Merge(IEnumerable<Run> local, IEnumerable<Run> remote, string externalId)
    {
        var result = new Dictionary<string, Run>(StringComparer.Ordinal);
        foreach (var run in local)
        {
            var key = externalId.Length > 0 && !run.Id.StartsWith("native:", StringComparison.Ordinal)
                && !run.Id.StartsWith("run_", StringComparison.Ordinal) ? PublicId(externalId, run.Id) : run.Id;
            result.TryAdd(key, run);
        }
        foreach (var run in remote) result.TryAdd(run.Id, run);
        return result.Values.OrderByDescending(r => Date(r.Timestamp)).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
    }
    internal static string Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    internal static double? Number(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return null;
        double n;
        if (v.ValueKind == JsonValueKind.Number ? v.TryGetDouble(out n) : v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out n))
            return double.IsFinite(n) ? n : null;
        return null;
    }
    public static Run? Parse(JsonElement e, string handle)
    {
        if (!Text(e, "userHandle").Equals(handle, StringComparison.OrdinalIgnoreCase)) return null;
        var id = Text(e, "runId"); if (id.Length == 0) id = Text(e, "sessionId");
        var scenario = Text(e, "scenarioName"); var stamp = Text(e, "playedAtIso");
        // Protobuf JSON omits default numeric values. Score zero is valid.
        var score = e.TryGetProperty("score", out _) ? Number(e, "score") : 0;
        var duration = Number(e, "durationMs"); var accuracy = Number(e, "accuracy");
        if (id.Length == 0 || scenario.Length == 0 || score is null || duration is null or <= 0 || Date(stamp) == DateTimeOffset.MinValue) return null;
        return new(id, scenario, score.Value, accuracy is >= 0 and <= 100 ? accuracy : null,
            duration.Value / 1000, 0, 0, stamp, null, null, null, null, false);
    }
    public static IEnumerable<Run> ReadArray(JsonElement root, string key, string handle)
    {
        if (!root.TryGetProperty(key, out var items) || items.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in items.EnumerateArray())
        { if (item.ValueKind == JsonValueKind.Object && Parse(item, handle) is Run run) yield return run; }
    }
}
