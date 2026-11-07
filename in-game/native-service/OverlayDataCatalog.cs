using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AimMod.InGame;

// Only presentation snapshots enter this registry. Never reflect workspace state or credentials.
static class OverlayDataCatalog
{
    internal sealed record Field(string Path, string Type, string Unit, string Description, JsonElement Value);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // Register nullable fields here so their paths and types survive menu and offline states.
    static readonly (string Prefix, string Type, string Unit, string Names)[] Schema =
    [
        ("mouse.flick", "number", "ms", "reactionMs,ballisticMs,correctionMs,verificationMs"),
        ("mouse.flick", "number", "", "corrections,throughput,primaryEndRatio"),
        ("mouse.tracking", "number", "deg", "horizontalError,verticalError,yawBias,pitchBias"),
        ("mouse.tracking", "number", "ms", "lag,reversalDelay"),
        ("mouse.tracking", "number", "", "behindShare,sparc"),
        ("live", "number", "", "score,shots,hits,kills,damage,personalBest,opponentScore,projectedScore,projectedDelta,scorePerMinute,killsPerSecond"),
        ("live", "number", "s", "seconds,opponentDuration,remainingSeconds,durationSeconds,lastTimeToKillSeconds"),
        ("live", "number", "%", "accuracy"),
        ("live", "boolean", "", "available,active,paused,replay,transient,projectionEstimated"),
        ("live", "string", "", "scenario,attemptId,opponentName,opponentSource"),
        ("session", "number", "", "runs,pbs,scenarios,bestRatio,startedAt"),
        ("session", "number", "s", "seconds"), ("session", "number", "%", "accuracy"),
        ("session", "array", "", "graph"),
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
        return fields.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
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
