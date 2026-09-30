using System.Text.Json;

namespace AimMod.InGame;

sealed record BenchmarkRank(string Name, int Index);
sealed record BenchmarkSummary(uint Id, string Name, string Author, string Type, BenchmarkRank? Rank);
sealed record BenchmarkThreshold(string Rank, int Index, double Score);
sealed record BenchmarkScenario(string Name, string Slug, double Score, BenchmarkRank? Rank, BenchmarkThreshold[] Thresholds);
sealed record BenchmarkCategory(string Name, BenchmarkScenario[] Scenarios);
sealed record BenchmarkDetail(uint Id, string Name, string Author, string Type, BenchmarkRank? Rank, BenchmarkCategory[] Categories);

// Match the existing desktop HubService/GetBenchmarkPage contract. Public presentation
// fields only: no access token, external account identity, arbitrary URL or HTML forwarding.
static class BenchmarkData
{
    static string Text(JsonElement e, string key) => HubHistory.Text(e, key)[..Math.Min(HubHistory.Text(e, key).Length, 512)];
    static IEnumerable<JsonElement> Array(JsonElement e, string key, int limit) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Take(limit) : [];
    static uint Id(JsonElement e) => HubHistory.Number(e, "benchmarkId") is double n && n > 0 && n <= uint.MaxValue && n == Math.Truncate(n) ? (uint)n : 0;
    static int Index(JsonElement e) => (int)Math.Clamp(HubHistory.Number(e, "rankIndex") ?? 0, 0, 10000);
    static BenchmarkRank? Rank(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var rank)) return null;
        var name = Text(rank, "rankName").Trim();
        return name.Length == 0 || name.Equals("no rank", StringComparison.OrdinalIgnoreCase) || name.Equals("unranked", StringComparison.OrdinalIgnoreCase) ? null : new(name, Index(rank));
    }
    internal static BenchmarkSummary[] Summaries(JsonElement root) => Array(root, "benchmarks", 512)
        .Where(e => Id(e) > 0 && Text(e, "benchmarkName").Length > 0)
        .Select(e => new BenchmarkSummary(Id(e), Text(e, "benchmarkName"), Text(e, "benchmarkAuthor"), Text(e, "benchmarkType"), Rank(e, "overallRank")))
        .DistinctBy(e => e.Id).ToArray();
    internal static BenchmarkDetail? Detail(JsonElement root, uint id)
    {
        if (Id(root) != id || Text(root, "benchmarkName").Length == 0) return null;
        var budget = 512;
        var categories = new List<BenchmarkCategory>();
        foreach (var category in Array(root, "categories", 64))
        {
            var scenarios = new List<BenchmarkScenario>();
            foreach (var scenario in Array(category, "scenarios", Math.Min(128, budget)))
            {
                if (Text(scenario, "scenarioName").Length == 0) continue;
                var score = HubHistory.Number(scenario, "score") ?? 0; // Protobuf omits numeric zero.
                var thresholds = Array(scenario, "thresholds", 64)
                    .Where(t => HubHistory.Number(t, "score") is >= 0 && Text(t, "rankName").Length > 0)
                    .Select(t => new BenchmarkThreshold(Text(t, "rankName"), Index(t), HubHistory.Number(t, "score")!.Value))
                    .OrderBy(t => t.Score).ThenBy(t => t.Index).ToArray();
                scenarios.Add(new(Text(scenario, "scenarioName"), Text(scenario, "scenarioSlug"), score, Rank(scenario, "scenarioRank"), thresholds));
                budget--;
            }
            if (scenarios.Count > 0) categories.Add(new(Text(category, "categoryName"), scenarios.ToArray()));
            if (budget <= 0) break;
        }
        return new(id, Text(root, "benchmarkName"), Text(root, "benchmarkAuthor"), Text(root, "benchmarkType"), Rank(root, "overallRank"), categories.ToArray());
    }
}

sealed partial class Hub
{
    BenchmarkSummary[] benchmarkItems = [];
    readonly SemaphoreSlim benchmarkGate = new(1, 1);
    readonly Dictionary<(string Handle, uint Id), (DateTimeOffset At, BenchmarkDetail Page)> benchmarkPages = new();
    public object BenchmarkList() => new { linked = account is not null, items = account is null ? System.Array.Empty<BenchmarkSummary>() : benchmarkItems };
    public async Task<BenchmarkDetail?> BenchmarkPage(uint id, CancellationToken token)
    {
        var handle = account?.Handle;
        if (id == 0 || string.IsNullOrEmpty(handle) || !benchmarkItems.Any(x => x.Id == id)) return null;
        await benchmarkGate.WaitAsync(token);
        try
        {
            if (!string.Equals(account?.Handle, handle, StringComparison.Ordinal)) return null;
            var key = (handle, id);
            if (benchmarkPages.TryGetValue(key, out var cached) && clock.GetUtcNow() - cached.At < TimeSpan.FromMinutes(5)) return cached.Page;
            using var doc = await Rpc("GetBenchmarkPage", new { handle, benchmarkId = id }, token);
            // An unlink/account switch while fetching must not return the former account's page.
            if (!string.Equals(account?.Handle, handle, StringComparison.Ordinal)) return null;
            if (!string.Equals(HubHistory.Text(doc.RootElement, "userHandle"), handle, StringComparison.OrdinalIgnoreCase)) return null;
            var page = BenchmarkData.Detail(doc.RootElement, id);
            if (page is null) return null;
            if (benchmarkPages.Count >= 16) benchmarkPages.Remove(benchmarkPages.MinBy(e => e.Value.At).Key);
            benchmarkPages[key] = (clock.GetUtcNow(), page);
            return page;
        }
        finally { benchmarkGate.Release(); }
    }
}
