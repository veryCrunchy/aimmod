using System.Text.Json;
using AimMod.Osu.Runtime;

namespace AimMod.Desktop.Practice;

public sealed record PracticeDeliveryState(DateTimeOffset? SentAt = null, bool Confirmed = false);
public sealed record PracticeDeliverySummary(int Confirmed, int Pending, int Failed);

/// <summary>Durable import handoff. A successful launch is pending until the map hashes appear in osu!.</summary>
public sealed class AutomaticPracticeDelivery(string ledgerPath)
{
    public async Task<PracticeDeliverySummary> DeliverAsync(IReadOnlyList<SavedPracticeMap> maps, ISet<string>? installed,
        Func<SavedPracticeMap, CancellationToken, Task<LazerBeatmapInstallResult>> send, DateTimeOffset now, CancellationToken token)
    {
        Dictionary<string, PracticeDeliveryState> ledger;
        try { ledger = JsonSerializer.Deserialize<Dictionary<string, PracticeDeliveryState>>(File.ReadAllText(ledgerPath)) ?? []; }
        catch (Exception e) when (e is IOException or JsonException) { ledger = []; }
        int confirmed = 0, pending = 0, failed = 0, sent = 0;
        foreach (var map in maps)
        {
            token.ThrowIfCancellationRequested();
            var hashes = map.Tracking?.Difficulties.Select(d => d.Md5).ToArray() ?? [];
            var previous = ledger.GetValueOrDefault(map.Id) ?? new();
            if (installed is null && previous.Confirmed || installed is not null && hashes.Length > 0 && hashes.All(installed.Contains))
            { ledger[map.Id] = previous with { Confirmed = true }; confirmed++; }
            else if (sent < 2 && (previous.SentAt is null || installed is not null && now - previous.SentAt >= TimeSpan.FromMinutes(10)))
            {
                sent++;
                var result = await send(map, token).ConfigureAwait(false);
                if (result.Status is LazerBeatmapInstallStatus.Sent or LazerBeatmapInstallStatus.LazerStarted)
                { ledger[map.Id] = new(now); pending++; }
                else failed++;
            }
            else pending++;
            Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
            File.WriteAllText(ledgerPath + ".tmp", JsonSerializer.Serialize(ledger));
            File.Move(ledgerPath + ".tmp", ledgerPath, true);
        }
        return new(confirmed, pending, failed);
    }
}
