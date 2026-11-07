using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AimMod.InGame;

// Only presentation snapshots enter this registry. Never reflect workspace state or credentials.
static class OverlayDataCatalog
{
    internal sealed record Field(string Path, string Type, string Unit, string Description, JsonElement Value);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // Typed source registration is centralized here, including nullable fields and empty lists.
    internal static readonly (string Path, Type Type)[] Sources = [("multiplayer", typeof(Multiplayer.OverlayMultiplayer)), ("replay", typeof(OverlayReplay)),
        ("mouse", typeof(OverlayMouse)), ("kovaaks", typeof(KovaaksSettingsView)), ("benchmarks", typeof(OverlayBenchmark[])), ("profile", typeof(OverlayProfile)), ("board", typeof(Multiplayer.Board))];
    static Type analysisType = typeof(object);
    internal static void RegisterAnalysis(Type type) => Volatile.Write(ref analysisType, type);
    static string Unit(string path) => path switch {
        "mouse.speed" => "deg/s", "kovaaks.cm360" => "cm/360", "kovaaks.fov" => "deg", "replay.speed" => "x",
        _ when path.EndsWith("accuracy", StringComparison.OrdinalIgnoreCase) || path.EndsWith("hsPercent") || path.EndsWith(".percent") => "%",
        _ when path.EndsWith("time") || path.EndsWith("duration") || path.EndsWith("Seconds") || path.EndsWith("seconds") => "s",
        _ when path.EndsWith("At") || path.EndsWith(".at") || path.EndsWith("now") || path.EndsWith("milliseconds") || path.EndsWith("readyCheck") || path.EndsWith("Deadline") || path.EndsWith(".lagMs") => "ms",
        _ when path.EndsWith(".adr") => "damage/round",
        _ when path.EndsWith("yaw") || path.EndsWith("pitch") => "deg",
        _ when path.EndsWith("money") => "credits", _ => "" };
    internal static IEnumerable<(string Path, string Type)> Describe(Type type, string path, int depth = 0)
    {
        if (depth > 10) yield break;
        type = Nullable.GetUnderlyingType(type) ?? type;
        var primitive = type == typeof(string) ? "string" : type == typeof(bool) ? "boolean" : type.IsPrimitive || type == typeof(decimal) ? "number" : null;
        if (primitive is not null) { yield return (path, primitive); yield break; }
        var element = type.IsArray ? type.GetElementType() : type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ? type.GetGenericArguments()[0] : null;
        yield return (path, element is null ? "object" : "array");
        if (element is not null) { foreach (var f in Describe(element, path + ".0", depth + 1)) yield return f; yield break; }
        if (type == typeof(object) || type.Namespace?.StartsWith("System") == true) yield break;
        foreach (var p in type.GetProperties()) foreach (var f in Describe(p.PropertyType, path + "." + JsonNamingPolicy.CamelCase.ConvertName(p.Name), depth + 1)) yield return f;
    }
    // Register nullable fields here so their paths and types survive menu and offline states.
    static readonly (string Prefix, string Type, string Unit, string Names)[] Schema =
    [
        ("live", "number", "", "score,shots,hits,kills,damage,personalBest,opponentScore,projectedScore,projectedDelta,scorePerMinute,killsPerSecond"),
        ("live", "number", "s", "seconds,opponentDuration,remainingSeconds,durationSeconds,lastTimeToKillSeconds"),
        ("live", "number", "%", "accuracy"),
        ("live", "boolean", "", "available,active,paused,replay,transient,projectionEstimated"),
        ("live", "string", "", "scenario,attemptId,opponentName,opponentSource"),
        ("session", "number", "", "runs,pbs,scenarios,bestRatio,startedAt"),
        ("session", "number", "s", "seconds"), ("session", "number", "%", "accuracy"),
        ("session", "array", "", "graph"),
        ("session.graph.0", "string", "", "scenario"), ("session.graph.0", "number", "", "score,ratio"), ("session.graph.0", "boolean", "", "pb"),
        ("recent.0", "string", "", "scenario"), ("recent.0", "number", "", "score,at"), ("recent.0", "number", "%", "accuracy,delta"), ("recent.0", "boolean", "", "pb"),
        ("scenario", "number", "", "best,attempts,average,last,lastAt"), ("scenario", "string", "", "name"),
        ("profile", "boolean", "", "linked"), ("profile", "string", "", "name,handle"),
        ("", "array", "", "recent,benchmarks"), ("", "object", "", "board,tournament,kovaaks")
    ];
    internal static Field[] Read(object feed)
    {
        var root = JsonSerializer.SerializeToElement(feed, Json);
        var fields = new List<Field>();
        void Walk(JsonElement value, string path, int depth)
        {
            if (depth > 10 || fields.Count >= 4096) return;
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (path.Length > 0) fields.Add(new(path, "object", "", "Presentation group: " + path, value.Clone()));
                foreach (var p in value.EnumerateObject()) Walk(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name, depth + 1);
                return;
            }
            var type = value.ValueKind switch { JsonValueKind.Number => "number", JsonValueKind.True or JsonValueKind.False => "boolean", JsonValueKind.Array => "array", JsonValueKind.String => "string", _ => "unknown" };
            var unit = path.EndsWith("accuracy", StringComparison.OrdinalIgnoreCase) ? "%" : path.Contains("seconds", StringComparison.OrdinalIgnoreCase) ? "s" : "";
            fields.Add(new(path, type, unit, "Presentation value: " + path, value.Clone()));
            if (value.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in value.EnumerateArray().Take(32)) Walk(item, path + "." + index++, depth + 1);
            }
        }
        Walk(root, "", 0);
        if (root.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True)
        {
            foreach (var source in Sources.Append((Path: "mouse.analysis", Type: Volatile.Read(ref analysisType))))
                foreach (var entry in Describe(source.Type, source.Path))
                {
                    var index = fields.FindIndex(f => f.Path == entry.Path);
                    var value = index >= 0 ? fields[index].Value : JsonSerializer.SerializeToElement<object?>(null);
                    var field = new Field(entry.Path, entry.Type, Unit(entry.Path), entry.Path.Replace('.', ' ') + "; current local value, null when unavailable; array index 0 describes a row.", value);
                    if (index >= 0) fields[index] = field; else fields.Add(field);
                }
            foreach (var group in Schema)
                foreach (var name in group.Names.Split(','))
                {
                    var path = group.Prefix.Length == 0 ? name : group.Prefix + "." + name;
                    var index = fields.FindIndex(f => f.Path == path);
                    var value = index >= 0 ? fields[index].Value : JsonSerializer.SerializeToElement<object?>(null);
                    var field = new Field(path, group.Type, group.Unit, "Overlay " + path.Replace('.', ' ') + "; null when unavailable.", value);
                    if (index >= 0) fields[index] = field; else fields.Add(field);
                }
        }
        var definitions = fields.ToDictionary(f => f.Path, StringComparer.Ordinal);
        return fields.Select(f => {
            var canonical = string.Join('.', f.Path.Split('.').Select(p => int.TryParse(p, out _) ? "0" : p));
            var type = definitions.TryGetValue(canonical, out var definition) && definition.Type != "unknown" ? definition.Type : f.Type;
            return f with { Type = type, Unit = f.Unit.Length == 0 ? Unit(f.Path) : f.Unit };
        }).OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
    }
    internal static string Event(object snapshot) => "data: " + JsonSerializer.Serialize(snapshot, Json) + "\n\n";
    internal static async Task Stream(HttpContext context, Func<object> snapshot)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        try
        {
            while (!context.RequestAborted.IsCancellationRequested)
            {
                await context.Response.WriteAsync(Event(snapshot()), context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await Task.Delay(500, context.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    }
}
