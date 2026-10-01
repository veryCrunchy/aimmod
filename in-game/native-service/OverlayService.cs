using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AimMod.InGame;

sealed partial class Hub
{
    // Public presentation fields for the overlay rank and profile widgets; no tokens or account identity beyond the handle.
    public BenchmarkSummary[] RankedBenchmarks => account is null ? [] : benchmarkItems;
    public string? AccountLabel => account?.Label;
    /// <summary>A benchmark page fetched earlier (the Benchmarks page or the overlay's own refresh), without network.</summary>
    public BenchmarkDetail? CachedBenchmark(uint id)
    {
        if (account?.Handle is not { } handle || !benchmarkGate.Wait(0)) return null;
        try { return benchmarkPages.TryGetValue((handle, id), out var page) ? page.Page : null; }
        finally { benchmarkGate.Release(); }
    }
}

/// <summary>
/// Overlay scenes for every surface: the scene store, the read-only feed that widgets
/// draw (live run, session, history, KovaaK's settings, profile, ranks, match and
/// tournament), the motion samples (mouse path, input display) and the current
/// crosshair image. The workspace maps the editable routes; the OBS listener maps
/// the same reads, gated by the OBS switch. Display only: nothing here changes the
/// game, its inputs or any score.
/// </summary>
sealed class OverlayService(OverlaySettings settings, OverlayScenes scenes, Func<object> live, Func<string?> liveScenario, Func<object?> board, Func<object?> tournament,
    Hub? hub, KovaaksSettings kovaaks, OverlayHistory history, OverlayMotion motion, Func<long>? clock = null)
{
    public OverlayScenes Scenes => scenes;
    long nextBenchmarkFetch;
    long Now() => clock?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    internal bool Enabled(string surface) => surface switch { "obs" => settings.Current.ObsEnabled, "game" => settings.Current.GameEnabled, "preview" => true, _ => false };

    public object Feed(string surface)
    {
        if (!Enabled(surface)) return new { v = 1, enabled = false, storeRevision = scenes.Revision };
        var now = Now(); var scenario = liveScenario();
        return new
        {
            v = 1, enabled = true, storeRevision = scenes.Revision, live = live(), session = history.Session(now), recent = history.Recent(), scenario = history.Scenario(scenario),
            kovaaks = kovaaks.Read(), profile = Profile(), benchmarks = Benchmarks(scenario ?? history.Current.Latest, now), board = board(), tournament = tournament(),
        };
    }
    object Profile() => new { linked = hub?.LinkedHandle is not null, name = hub?.AccountLabel ?? hub?.LinkedHandle, handle = hub?.LinkedHandle };
    object[] Benchmarks(string? scenario, long now)
    {
        if (hub is null) return [];
        var list = hub.RankedBenchmarks.OrderByDescending(b => b.Rank is not null).ThenByDescending(b => b.Rank?.Index ?? 0).Take(12).ToArray();
        // Keep the top benchmark's page fresh (cached for five minutes by the Hub client).
        if (list.Length > 0 && now >= nextBenchmarkFetch)
        {
            nextBenchmarkFetch = now + 10 * 60 * 1000; var id = list[0].Id; var client = hub;
            _ = Task.Run(async () => { try { await client.BenchmarkPage(id, CancellationToken.None); } catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException) { } });
        }
        return list.Select(b => Benchmark(b, scenario)).ToArray();
    }
    object Benchmark(BenchmarkSummary b, string? scenario)
    {
        double? progress = null; string? next = null, scenarioRank = null;
        if (scenario is not null && hub?.CachedBenchmark(b.Id) is { } page)
            foreach (var row in page.Categories.SelectMany(c => c.Scenarios))
            {
                if (!row.Name.Equals(scenario, StringComparison.OrdinalIgnoreCase)) continue;
                scenarioRank = row.Rank?.Name;
                var above = row.Thresholds.FirstOrDefault(t => t.Score > row.Score); var below = row.Thresholds.LastOrDefault(t => t.Score <= row.Score);
                if (above is not null) { next = above.Rank; var from = below?.Score ?? 0; progress = above.Score > from ? Math.Clamp((row.Score - from) / (above.Score - from), 0, 1) : null; }
                break;
            }
        return new { id = b.Id, name = b.Name, rank = b.Rank?.Name, rankIndex = b.Rank?.Index, scenarioRank, progress, next };
    }

    static string Surface(HttpRequest request, bool obs) => obs ? "obs" : request.Query["surface"].ToString() is "game" ? "game" : "preview";
    void MapReads(IEndpointRouteBuilder routes, string prefix, bool obs)
    {
        routes.MapGet(prefix + "/overlay-scenes", () => Results.Bytes(scenes.Bytes, "application/json"));
        routes.MapGet(prefix + "/overlay-feed", (HttpRequest request) => Results.Json(Feed(Surface(request, obs))));
        routes.MapGet(prefix + "/overlay-motion", (HttpRequest request) =>
        {
            if (!Enabled(Surface(request, obs))) return Results.Json(new { path = (object?)null, input = (object?)null });
            return Results.Json(motion.Read(request.Query["path"] == "1", request.Query["input"] == "1"));
        });
        routes.MapGet(prefix + "/overlay-crosshair", (HttpRequest request) =>
        {
            if (!Enabled(Surface(request, obs))) return Results.NotFound();
            var file = kovaaks.CrosshairPath(request.Query["file"].ToString());
            return file is null ? Results.NotFound() : Results.File(file, "image/png");
        });
    }
    public void MapObs(IEndpointRouteBuilder routes, string prefix) => MapReads(routes, prefix, true);
    public void MapWorkspace(IEndpointRouteBuilder routes, string prefix)
    {
        MapReads(routes, prefix, false);
        routes.MapPost(prefix + "/overlay-scenes", async (HttpRequest request, CancellationToken token) =>
        {
            if (request.Headers["X-AimMod-UI"] != "1") return Results.StatusCode(403);
            if (!request.HasJsonContentType()) return Results.StatusCode(415);
            if (request.ContentLength is not long length || length is <= 0 or > OverlayScenes.Limit) return Results.StatusCode(413);
            try { var body = new byte[(int)length]; await request.Body.ReadExactlyAsync(body, token); return Results.Bytes(scenes.Save(body), "application/json"); }
            catch (Exception ex) when (ex is JsonException or EndOfStreamException or InvalidOperationException) { return Results.BadRequest(new { error = "Invalid overlay scenes." }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.Json(new { error = "Overlay scenes could not be saved." }, statusCode: 500); }
        });
    }
}
