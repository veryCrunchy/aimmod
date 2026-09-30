using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AimMod.InGame;
sealed record AdviceFeedback(string Scope, string Id, string Feedback, DateTimeOffset UpdatedAt);
sealed record AdviceHistory(string Key, string Scope, string Id, string Title, string Body, string Tip, DateTimeOffset SeenAt);
sealed record AdviceState(AdviceFeedback[] Feedback, AdviceHistory[] History);
sealed class CoachingFeedback
{
    const int RequestLimit = 65536;
    readonly object gate = new();
    readonly string path;
    readonly TimeProvider clock;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    AdviceState current = new([], []);
    public AdviceState Current { get { lock (gate) return current; } }
    public CoachingFeedback(string output, TimeProvider? clock = null)
    {
        Directory.CreateDirectory(output); path = Path.Combine(output, "coaching-feedback.json"); this.clock = clock ?? TimeProvider.System;
        if (!File.Exists(path)) return;
        try
        {
            if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new JsonException();
            var state = JsonSerializer.Deserialize<AdviceState>(File.ReadAllBytes(path), Json);
            if (state?.Feedback is null || state.History is null || state.Feedback.Length > 200 || state.History.Length > 200) throw new JsonException();
            if (state.Feedback.Any(x => x is null || !ValidScope(x.Scope) || !ValidId(x.Id) || x.Feedback is not ("helpful" or "not_for_me")) || state.History.Any(x => x is null || !ValidScope(x.Scope) || !ValidId(x.Id) || x.Title is null || x.Title.Length > 256 || x.Body is null || x.Body.Length > 4096 || x.Tip is null || x.Tip.Length > 2048)) throw new JsonException();
            current = state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { /* Preserve damaged private file until the user saves new feedback. */ }
    }
    static bool ValidId(string? id) => id is { Length: > 0 and <= 128 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    static bool ValidScope(string? scope) => scope == "all" || scope is { Length: > 9 and <= 521 } && scope.StartsWith("scenario:", StringComparison.Ordinal) && !scope.Any(char.IsControl);
    static string Text(JsonElement e, string name, int max, bool empty = false)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) throw new JsonException();
        var value = v.GetString()!; if (value.Length > max || !empty && value.Length == 0) throw new JsonException(); return value;
    }
    static void Keys(JsonElement e, params string[] keys)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new JsonException();
        var names = e.EnumerateObject().Select(x => x.Name).ToArray();
        if (names.Length != keys.Length || names.Distinct().Count() != names.Length || names.Any(n => !keys.Contains(n))) throw new JsonException();
    }
    public AdviceState ApplyJson(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is <= 0 or > RequestLimit) throw new JsonException();
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 5 });
        var root = doc.RootElement; if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        var action = Text(root, "action", 16); var scope = Text(root, "scope", 521); if (!ValidScope(scope)) throw new JsonException();
        lock (gate)
        {
            var next = current;
            if (action == "observe")
            {
                Keys(root, "action", "scope", "cards"); var cards = root.GetProperty("cards");
                if (cards.ValueKind != JsonValueKind.Array || cards.GetArrayLength() > 16) throw new JsonException();
                var history = current.History.ToList();
                foreach (var card in cards.EnumerateArray())
                {
                    Keys(card, "id", "title", "body", "tip");
                    var id = Text(card, "id", 128); if (!ValidId(id)) throw new JsonException();
                    var title = Text(card, "title", 256); var body = Text(card, "body", 4096, true); var tip = Text(card, "tip", 2048, true);
                    var key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { scope, id, title, body, tip })));
                    if (history.Any(x => x.Key == key)) continue;
                    history.Insert(0, new(key, scope, id, title, body, tip, clock.GetUtcNow()));
                }
                var bounded = history.Take(200).ToArray();
                if (bounded.SequenceEqual(current.History)) return current;
                next = current with { History = bounded };
            }
            else if (action == "feedback")
            {
                Keys(root, "action", "scope", "id", "feedback"); var id = Text(root, "id", 128); var feedback = Text(root, "feedback", 16);
                if (!ValidId(id) || feedback is not ("helpful" or "not_for_me" or "none") || !current.History.Any(x => x.Scope == scope && x.Id == id)) throw new JsonException();
                var rows = current.Feedback.Where(x => !(x.Scope == scope && x.Id == id)).ToList();
                if (feedback != "none") rows.Insert(0, new(scope, id, feedback, clock.GetUtcNow()));
                next = current with { Feedback = rows.Take(200).ToArray() };
            }
            else throw new JsonException();
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(JsonSerializer.SerializeToUtf8Bytes(next, Json)); file.Flush(true); }
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            current = next; return current;
        }
    }
    public void MapEndpoints(IEndpointRouteBuilder app, string prefix)
    {
        app.MapGet(prefix + "/coaching-feedback", () => Results.Json(Current));
        app.MapPost(prefix + "/coaching-feedback", async (HttpRequest request, CancellationToken token) => {
            if (request.Headers["X-AimMod-UI"] != "1") return Results.StatusCode(403);
            if (!request.HasJsonContentType()) return Results.StatusCode(415);
            if (request.ContentLength is not long length || length is <= 0 or > RequestLimit) return Results.StatusCode(413);
            try { var bytes = new byte[(int)length]; await request.Body.ReadExactlyAsync(bytes, token); return Results.Json(ApplyJson(bytes)); }
            catch (Exception ex) when (ex is JsonException or EndOfStreamException) { return Results.BadRequest(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.StatusCode(503); }
        });
    }
}
