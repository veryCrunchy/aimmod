using System.Text.Json;

namespace AimMod.InGame;
static class BenchmarkChecks
{
    sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public string Path = "", Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(token);
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("""
            {"userHandle":"synthetic","benchmarkId":7,"benchmarkName":"Synthetic","categories":[]}
            """, System.Text.Encoding.UTF8, "application/json") };
        }
    }
    public static void Run()
    {
        int count = 0;
        void Check(bool ok, string name) { count++; if (!ok) throw new Exception(name); }
        using var profile = JsonDocument.Parse("""
        {"benchmarks":[{"benchmarkId":7,"benchmarkName":"Synthetic ranked","overallRank":{"rankName":"Silver","rankIndex":2}},
        {"benchmarkId":"8","benchmarkName":"Synthetic unranked","overallRank":{"rankName":"No Rank"}},
        {"benchmarkId":7,"benchmarkName":"Duplicate"},{"benchmarkId":-1,"benchmarkName":"Invalid"},null]}
        """);
        var summaries = BenchmarkData.Summaries(profile.RootElement);
        Check(summaries.Length == 2, "IDs validated and deduplicated");
        Check(summaries[0].Rank?.Name == "Silver" && summaries[1].Rank is null, "Native ranked filter follows desktop No Rank semantics");
        using var doc = JsonDocument.Parse("""
        {"benchmarkId":7,"benchmarkName":"Synthetic","categories":[{"categoryName":"Tracking","scenarios":[
        {"scenarioName":"Synthetic scenario","score":150,"thresholds":[{"rankName":"Gold","score":200},{"rankName":"Bronze","score":100},{"rankName":"Bad","score":-1}]},
        {"scenarioName":"Unplayed","thresholds":[]},null]}],"accessToken":"never forward","userHandle":"private-test"}
        """);
        var page = BenchmarkData.Detail(doc.RootElement, 7)!;
        Check(page.Categories[0].Scenarios.Length == 2, "Invalid scenario entries omitted");
        Check(page.Categories[0].Scenarios[0].Thresholds.Select(t => t.Score).SequenceEqual(new double[] {100, 200}), "Actual thresholds sorted with invalid negative removed");
        Check(page.Categories[0].Scenarios[1].Score == 0, "Protobuf omitted zero score respected");
        Check(BenchmarkData.Detail(doc.RootElement, 8) is null, "Mismatched benchmark response rejected");
        var json = JsonSerializer.Serialize(page);
        Check(!json.Contains("never forward") && !json.Contains("private-test"), "Only explicit public presentation fields cross local API");
        var folder = Path.Combine(Path.GetTempPath(), "aimmod-benchmark-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            AccountVault.Save(Path.Combine(folder, "account.bin"), new HubAccount("synthetic", "Synthetic", "test-external", "test-secret"));
            File.WriteAllText(Path.Combine(folder, "hub-cache.json"), JsonSerializer.Serialize(new { Handle = "synthetic", Runs = System.Array.Empty<Run>(), Benchmarks = System.Array.Empty<Row>(), Partial = false, BenchmarkItems = summaries }));
            var handler = new Handler();
            using var hub = new Hub(folder, handler, historyEnabled: () => false);
            Check(JsonSerializer.Serialize(hub.BenchmarkList()).Contains("Synthetic ranked"), "Summary IDs and rank metadata persist offline");
            Check(hub.BenchmarkPage(999, default).GetAwaiter().GetResult() is null && handler.Calls == 0, "Unknown ID cannot issue remote request");
            Check(hub.BenchmarkPage(7, default).GetAwaiter().GetResult()?.Id == 7, "Explicit benchmark drilldown works while background history is paused");
            using var requestBody = JsonDocument.Parse(handler.Body);
            Check(handler.Path == "/aimmod.hub.v1.HubService/GetBenchmarkPage" && requestBody.RootElement.GetProperty("handle").GetString() == "synthetic" && requestBody.RootElement.GetProperty("benchmarkId").GetUInt32() == 7, "Exact existing desktop RPC request contract");
            hub.BenchmarkPage(7, default).GetAwaiter().GetResult();
            Check(handler.Calls == 1, "Repeated drilldown reuses bounded cache");
            hub.Enqueue("unlink"); hub.Tick(default).GetAwaiter().GetResult();
            Check(hub.BenchmarkPage(7, default).GetAwaiter().GetResult() is null && !JsonSerializer.Serialize(hub.BenchmarkList()).Contains("Synthetic ranked"), "Unlink hides cached benchmark identity and detail");
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine($"PASS {count} benchmark checks");
    }
}
