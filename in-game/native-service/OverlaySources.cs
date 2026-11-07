using System.Text.Json;

namespace AimMod.InGame;

sealed record OverlayReplay(string? Id, string? Scenario, double Time, double Duration, bool Playing, double Speed, bool Visible, ReplayStats? Stats);
sealed record OverlayMousePoint(double Milliseconds, double Yaw, double Pitch);
sealed record OverlayMouse(double? Speed, OverlayMousePoint[] Trail, object? Analysis);
sealed record OverlayProfile(bool Linked, string? Name, string? Handle, string? Avatar);
sealed record OverlayBenchmark(uint Id, string Name, string Author, string? Rank, int? RankIndex, string? ScenarioRank, double? Progress, string? Next);

// Optional analysis producers publish bounded, already public metrics here. No game commands.
sealed class OverlayAnalysisSource
{
    JsonElement? current;
    readonly object gate = new();
    public object? Read() { lock (gate) return current; }
    public void Publish<T>(T metrics)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metrics, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (bytes.Length > 32768) throw new ArgumentException("Analysis snapshot too large.");
        using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        OverlayDataCatalog.RegisterAnalysis(typeof(T));
        lock (gate) current = parsed.RootElement.Clone();
    }
}
