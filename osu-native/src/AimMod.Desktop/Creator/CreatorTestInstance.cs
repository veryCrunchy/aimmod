using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AimMod.Desktop.Hub;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.ScoreHistory;

namespace AimMod.Desktop.Creator;

/// <summary>Explicit, read-only public-profile instance. Never imports local account credentials.</summary>
internal sealed record CreatorTestInstance(string Player, string Channel, LocalReplay[] Scores, string StorageName)
{
    public static async Task<CreatorTestInstance> LoadAsync(string configurationPath, CancellationToken token = default)
    {
        string path = Path.GetFullPath(configurationPath);
        if (new FileInfo(path).Length > 16 * 1024) throw new InvalidDataException("Creator instance configuration is too large.");
        var configuration = JsonSerializer.Deserialize<Configuration>(await File.ReadAllTextAsync(path, token),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing creator instance configuration.");
        string channel = TwitchVodDiscovery.NormaliseChannel(configuration.TwitchChannel ?? "");
        if (configuration.OsuUserId <= 0 || channel.Length == 0)
            throw new InvalidDataException("Provide an osu! user ID and Twitch channel.");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var service = new HubPublicAccountScoreHistoryService(http, OsuHubSyncClient.DefaultBaseUri, null,
            TimeSpan.FromSeconds(30), configuration.OsuUserId);
        var account = await service.FetchAccountAsync(token);
        if (account.Profile is null || account.Profile.UserId != configuration.OsuUserId)
            throw new InvalidDataException("Could not fetch this player's public osu! scores. Try launching again.");
        var scores = account.Scores.Select(s => ToReplay(s, account.Profile.Username)).ToArray();
        // Include the configuration path so two test configurations cannot share credentials or edits.
        string identity = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        string storage = "aimmod-creator-test-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
        Console.WriteLine($"Loaded {scores.Length} public scores for {account.Profile.Username}. Partial best/recent API window; not complete play history.");
        Console.WriteLine($"Isolated storage: {storage}");
        return new(account.Profile.Username, channel, scores, storage);
    }

    internal static LocalReplay ToReplay(ScoreHistoryEntry score, string player) => FootageScoreLookup.FromHistory(score, player);
    private sealed record Configuration(int OsuUserId, string? TwitchChannel);
}
