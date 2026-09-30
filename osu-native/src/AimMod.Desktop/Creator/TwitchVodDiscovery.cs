using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AimMod.Desktop.Creator;

public sealed record TwitchVod(string Id, string BroadcasterId, string Title, DateTimeOffset CreatedAt, double DurationSeconds)
{
    public FootageRecording ForPlayer(string player) => new(
        new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("twitch:" + Id + ":" + player.ToLowerInvariant())).AsSpan(0, 16)),
        Title, "https://www.twitch.tv/videos/" + Id, CreatedAt, 0, DurationSeconds, player);
}
public sealed record TwitchVodSearch(TwitchVod[] Matches, int PagesChecked, bool Complete, bool UsedCachedMetadata);
public interface ITwitchVodDiscovery
{
    bool IsConfigured { get; }
    Task<TwitchAccount?> SavedAccountAsync(CancellationToken token);
    Task<TwitchDeviceCode> BeginAsync(CancellationToken token);
    Task<TwitchAccount> CompleteAsync(TwitchDeviceCode code, CancellationToken token);
    Task DisconnectAsync(CancellationToken token);
    Task<TwitchVodSearch> FindAsync(string channel, DateTimeOffset scoreAt, CancellationToken token);
    Task<TwitchVodSearch> ListArchivesAsync(string channel, CancellationToken token) => throw new NotSupportedException("Archive listing is unavailable.");
}

public sealed partial class TwitchVodDiscovery : ITwitchVodDiscovery, IDisposable
{
    private readonly TwitchConnection connection;
    private readonly HttpClient http;
    private readonly TimeProvider clock;
    private readonly int maximumPages;
    private readonly SemaphoreSlim searchGate = new(1, 1);
    private readonly Dictionary<string, CachedPage> pages = new();
    private readonly Dictionary<string, (string Id, DateTimeOffset At)> users = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan cacheLifetime = TimeSpan.FromMinutes(10);
    private sealed record Page(TwitchVod[] Videos, string Cursor);
    private sealed record CachedPage(Page Page, DateTimeOffset At);

    public TwitchVodDiscovery(TwitchConnection connection, HttpMessageHandler? handler = null, TimeProvider? clock = null, int maximumPages = 20)
    {
        this.connection = connection; this.clock = clock ?? TimeProvider.System;
        this.maximumPages = Math.Clamp(maximumPages, 1, 20);
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    }
    public bool IsConfigured => connection.IsConfigured;
    public Task<TwitchAccount?> SavedAccountAsync(CancellationToken token) => connection.SavedAccountAsync(token);
    public Task<TwitchDeviceCode> BeginAsync(CancellationToken token) => connection.BeginAsync(token);
    public Task<TwitchAccount> CompleteAsync(TwitchDeviceCode code, CancellationToken token) => connection.CompleteAsync(code, token);
    public async Task DisconnectAsync(CancellationToken token)
    {
        await searchGate.WaitAsync(token).ConfigureAwait(false);
        try { await connection.DisconnectAsync(token).ConfigureAwait(false); pages.Clear(); users.Clear(); }
        finally { searchGate.Release(); }
    }

    public async Task<TwitchVodSearch> FindAsync(string channel, DateTimeOffset scoreAt, CancellationToken token)
        => await searchAsync(channel, scoreAt, token).ConfigureAwait(false);

    public Task<TwitchVodSearch> ListArchivesAsync(string channel, CancellationToken token)
        => searchAsync(channel, null, token);

    private async Task<TwitchVodSearch> searchAsync(string channel, DateTimeOffset? scoreAt, CancellationToken token)
    {
        string login = NormaliseChannel(channel);
        if (login.Length == 0) throw new ArgumentException("Enter a Twitch channel name or channel link.");
        if (scoreAt < DateTimeOffset.UnixEpoch) throw new ArgumentException("This score has no usable date for a broadcast lookup.");
        await searchGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string access = await connection.AccessTokenAsync(token).ConfigureAwait(false);
            string broadcaster;
            if (users.TryGetValue(login, out var user) && clock.GetUtcNow() - user.At < cacheLifetime) broadcaster = user.Id;
            else
            {
                JsonElement response = await get("users?login=" + Uri.EscapeDataString(login), access, token).ConfigureAwait(false);
                JsonElement data = response.GetProperty("data");
                if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0) throw new InvalidOperationException("This Twitch channel could not be found. Check its name.");
                if (data.GetArrayLength() != 1 || !string.Equals(TwitchConnection.required(data[0], "login"), login, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Twitch returned a different channel. Try again.");
                broadcaster = TwitchConnection.required(data[0], "id");
                if (!numeric(broadcaster)) throw new InvalidDataException("Twitch returned an invalid channel.");
                if (users.Count >= 64) users.Remove(users.MinBy(u => u.Value.At).Key);
                users[login] = (broadcaster, clock.GetUtcNow());
            }
            var matches = new Dictionary<string, TwitchVod>();
            string cursor = ""; var visited = new HashSet<string>();
            bool cached = false;
            for (int index = 0; index < maximumPages; index++)
            {
                token.ThrowIfCancellationRequested();
                if (!visited.Add(cursor)) return new(matches.Values.ToArray(), index, false, cached);
                string key = broadcaster + ":" + cursor;
                Page page;
                if (pages.TryGetValue(key, out CachedPage? entry) && clock.GetUtcNow() - entry.At < cacheLifetime && (scoreAt is null || scoreAt <= entry.At))
                { page = entry.Page; cached = true; }
                else
                {
                    JsonElement response = await get("videos?user_id=" + broadcaster + "&type=archive&sort=time&first=100"
                        + (cursor.Length == 0 ? "" : "&after=" + Uri.EscapeDataString(cursor)), access, token).ConfigureAwait(false);
                    page = parsePage(response, broadcaster);
                    if (pages.Count >= 32) pages.Remove(pages.MinBy(p => p.Value.At).Key);
                    pages[key] = new(page, clock.GetUtcNow());
                }
                foreach (TwitchVod video in page.Videos)
                    if (scoreAt is null || scoreAt >= video.CreatedAt && scoreAt < video.CreatedAt.AddSeconds(video.DurationSeconds)) matches[video.Id] = video;
                // A lower bound accounts for long/overlapping archives. Stopping
                // at the first video older than the score would miss these.
                bool coveredWindow = scoreAt is { } at && page.Videos.Length > 0 && page.Videos.Min(v => v.CreatedAt) <= at.AddSeconds(-FootageIndex.MaximumDurationSeconds);
                if (page.Cursor.Length == 0 || coveredWindow) return new(matches.Values.OrderBy(v => v.CreatedAt).ToArray(), index + 1, true, cached);
                cursor = page.Cursor;
            }
            return new(matches.Values.OrderBy(v => v.CreatedAt).ToArray(), maximumPages, false, cached);
        }
        finally { searchGate.Release(); }
    }

    private async Task<JsonElement> get(string path, string access, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.twitch.tv/helix/" + path);
        connection.AddHeaders(request, access);
        var reply = await TwitchConnection.SendAsync(http, request, token).ConfigureAwait(false);
        TwitchConnection.ensure(reply.Status);
        return reply.Body;
    }

    private static Page parsePage(JsonElement body, string broadcaster)
    {
        JsonElement data = body.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > 100) throw new InvalidDataException("Twitch returned an invalid archive page.");
        var videos = new List<TwitchVod>();
        foreach (JsonElement item in data.EnumerateArray())
        {
            string id = TwitchConnection.required(item, "id");
            if (!numeric(id) || TwitchConnection.required(item, "user_id") != broadcaster
                || TwitchConnection.required(item, "type") != "archive"
                || !item.GetProperty("created_at").TryGetDateTimeOffset(out DateTimeOffset created)
                || !TryDuration(TwitchConnection.required(item, "duration"), out double seconds))
                throw new InvalidDataException("Twitch returned incomplete archive information.");
            // Zero-duration archives can be returned while a broadcast starts.
            if (seconds == 0) continue;
            string title = TwitchConnection.required(item, "title");
            videos.Add(new(id, broadcaster, title[..Math.Min(300, title.Length)], created, seconds));
        }
        string cursor = body.TryGetProperty("pagination", out var pagination) && pagination.TryGetProperty("cursor", out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        if (cursor.Length > 2048) throw new InvalidDataException("Twitch returned an invalid archive cursor.");
        // The server promises descending creation time for sort=time. Reject a
        // broken ordering before using it to decide that older pages are covered.
        if (!videos.Select(v => v.CreatedAt).SequenceEqual(videos.Select(v => v.CreatedAt).OrderDescending()))
            throw new InvalidDataException("Twitch returned archives out of order. Try again.");
        return new(videos.ToArray(), cursor);
    }

    public static string NormaliseChannel(string value)
    {
        value = value.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            if (uri.Scheme != "https" || uri.Host is not ("twitch.tv" or "www.twitch.tv") || uri.UserInfo.Length > 0 || !uri.IsDefaultPort) return "";
            value = uri.AbsolutePath.Trim('/');
        }
        return channelRegex().IsMatch(value) ? value.ToLowerInvariant() : "";
    }
    private static bool numeric(string value) => value.Length <= 24 && value.Length > 0 && value.All(char.IsAsciiDigit);
    public static bool TryDuration(string text, out double seconds)
    {
        seconds = 0; Match match = durationRegex().Match(text);
        if (!match.Success || text.Length == 0) return false;
        int[] scales = [3600, 60, 1];
        for (int i = 1; i <= 3; i++)
        {
            if (!match.Groups[i].Success) continue;
            if (!int.TryParse(match.Groups[i].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return false;
            seconds += (double)n * scales[i - 1];
        }
        return seconds <= FootageIndex.MaximumDurationSeconds;
    }
    [GeneratedRegex("^[a-zA-Z0-9_]{1,25}$", RegexOptions.CultureInvariant)] private static partial Regex channelRegex();
    [GeneratedRegex("^(?:(\\d{1,6})h)?(?:(\\d{1,6})m)?(?:(\\d{1,6})s)?$", RegexOptions.CultureInvariant)] private static partial Regex durationRegex();
    public void Dispose() { http.Dispose(); connection.Dispose(); }
}
