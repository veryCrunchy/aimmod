using System.Globalization;
using System.Text;
using System.Text.Json;
using AimMod.Desktop.LocalLibrary;

namespace AimMod.Desktop.Creator;

// Each entry covers one continuous stretch of footage. Pauses/cuts require a
// separate entry with its own clock mapping, even when the URL is the same.
public sealed record FootageRecording(Guid Id, string Title, string Location,
    DateTimeOffset WallClockStart, double VideoStartSeconds, double VideoEndSeconds, string Player = "");

public sealed record FootageMoment(Guid Id, Guid RecordingId, string Title, double Seconds,
    string? ScoreKey = null);

public sealed record FootageLibrary(int Version, FootageRecording[] Recordings, FootageMoment[] Moments)
{
    public static FootageLibrary Empty => new(1, [], []);
    public FootageChannel[] Channels { get; init; } = [];
}
public sealed record FootageChannel(string Player, string TwitchLogin, int? OsuUserId = null, string? LinkedTwitchUserId = null);

public sealed record FootageMatch(FootageRecording Recording, double Seconds, bool Confirmed);

public static class FootageIndex
{
    public const double MaximumDurationSeconds = 7 * 24 * 3600;
    public static FootageChannel? FindOwnChannel(FootageLibrary library, int? osuUserId, string? twitchUserId) =>
        osuUserId is > 0 && !string.IsNullOrEmpty(twitchUserId)
            ? library.Channels.FirstOrDefault(c => c.OsuUserId == osuUserId && c.LinkedTwitchUserId == twitchUserId)
            : null;

    // Include player, clock and origin because imported score GUIDs are not
    // guaranteed to be unique across libraries. Never infer a play's start from
    // its map duration: failed runs and imported timestamps differ.
    public static string ScoreKey(LocalReplay score) =>
        score.OnlineScoreId > 0
            ? $"osu:{(score.LegacyScore ? "legacy" : "solo")}:{score.RulesetShortName}:{score.OnlineScoreId}"
            : $"{score.Origin}:{score.ScoreId:N}:{score.PlayedAt.ToUniversalTime():O}:{score.Player}";

    public static IReadOnlyList<FootageMatch> Find(LocalReplay score, FootageLibrary library)
    {
        string key = ScoreKey(score);
        return library.Recordings.Where(r => string.IsNullOrEmpty(r.Player) || string.Equals(r.Player, score.Player, StringComparison.OrdinalIgnoreCase)).Select(recording =>
        {
            FootageMoment? known = library.Moments.LastOrDefault(m => m.RecordingId == recording.Id && m.ScoreKey == key);
            double seconds = known?.Seconds ?? recording.VideoStartSeconds + (score.PlayedAt - recording.WallClockStart).TotalSeconds;
            return new FootageMatch(recording, seconds, known is not null);
        }).Where(m => m.Seconds >= m.Recording.VideoStartSeconds && m.Seconds < m.Recording.VideoEndSeconds)
          .OrderByDescending(m => m.Confirmed).ThenBy(m => m.Recording.Title, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static FootageRecording Align(FootageRecording recording, LocalReplay score, double seconds)
    {
        ValidatePosition(recording, seconds);
        return recording with { WallClockStart = score.PlayedAt.AddSeconds(recording.VideoStartSeconds - seconds) };
    }

    public static void ValidatePosition(FootageRecording recording, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < recording.VideoStartSeconds || seconds >= recording.VideoEndSeconds)
            throw new ArgumentException("Choose a timestamp inside this recording's time range.");
    }

    public static void Validate(FootageLibrary library)
    {
        if (library.Version != 1 || library.Recordings is null || library.Moments is null || library.Channels is null)
            throw new InvalidDataException("This footage library uses an unsupported format.");
        if (library.Recordings.Length > 5000 || library.Moments.Length > 50000)
            throw new InvalidDataException("The footage library is full. Remove old entries before adding more.");
        if (library.Channels.Length > 1000 || library.Channels.Any(c => string.IsNullOrWhiteSpace(c.Player) || c.Player.Length > 100
            || TwitchVodDiscovery.NormaliseChannel(c.TwitchLogin) != c.TwitchLogin || c.TwitchLogin.Length == 0))
            throw new InvalidDataException("Check the Twitch channel and osu! player association.");
        if (library.Recordings.Select(r => r.Id).Distinct().Count() != library.Recordings.Length
            || library.Moments.Select(m => m.Id).Distinct().Count() != library.Moments.Length)
            throw new InvalidDataException("The footage library contains duplicate entries.");
        foreach (FootageRecording r in library.Recordings)
        {
            if (r.Id == Guid.Empty || string.IsNullOrWhiteSpace(r.Title) || r.Title.Length > 300
                || string.IsNullOrWhiteSpace(r.Location) || r.Location.Length > 4096
                || !IsSupportedLocation(r.Location) || !double.IsFinite(r.VideoStartSeconds)
                || !double.IsFinite(r.VideoEndSeconds) || r.VideoStartSeconds < 0
                || r.VideoEndSeconds <= r.VideoStartSeconds || r.VideoEndSeconds > MaximumDurationSeconds
                || r.WallClockStart == default || r.Player is null || r.Player.Length > 100)
                throw new InvalidDataException("Check the recording title, video location and time range.");
        }
        var recordings = library.Recordings.ToDictionary(r => r.Id);
        foreach (FootageMoment m in library.Moments)
        {
            if (m.Id == Guid.Empty || string.IsNullOrWhiteSpace(m.Title) || m.Title.Length > 500
                || m.ScoreKey?.Length > 500 || !recordings.TryGetValue(m.RecordingId, out FootageRecording? recording))
                throw new InvalidDataException("A saved moment has no valid recording or title.");
            ValidatePosition(recording, m.Seconds);
        }
    }

    public static bool IsSupportedLocation(string location)
    {
        if (TryVideoUri(location, out _)) return true;
        return Path.IsPathFullyQualified(location) && !location.StartsWith("\\\\", StringComparison.Ordinal)
            && new[] { ".mp4", ".mkv", ".mov", ".webm", ".avi", ".m4v", ".flv", ".ts" }
                .Contains(Path.GetExtension(location), StringComparer.OrdinalIgnoreCase);
    }

    public static bool TryVideoUri(string location, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(location, UriKind.Absolute, out Uri? candidate)
            || candidate.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(candidate.UserInfo)
            || !candidate.IsDefaultPort) return false;
        string host = candidate.Host.ToLowerInvariant();
        bool valid = host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "youtu.be"
            ? (host == "youtu.be" ? candidate.AbsolutePath.Trim('/').Length > 0
                : candidate.AbsolutePath == "/watch" && query(candidate).Any(p => p.Key == "v" && p.Value.Length > 0)
                  || candidate.AbsolutePath.StartsWith("/live/", StringComparison.Ordinal))
            : host is "twitch.tv" or "www.twitch.tv" && candidate.AbsolutePath.StartsWith("/videos/", StringComparison.Ordinal)
                && long.TryParse(candidate.AbsolutePath[8..].Trim('/'), out long id) && id > 0;
        if (valid) uri = candidate;
        return valid;
    }

    public static Uri? TimestampUrl(string location, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > MaximumDurationSeconds
            || !TryVideoUri(location, out Uri? uri)) return null;
        long s = (long)Math.Floor(seconds);
        string value = uri!.Host.Contains("twitch", StringComparison.Ordinal)
            ? $"{s / 3600}h{s / 60 % 60}m{s % 60}s" : $"{s}s";
        var parameters = query(uri).Where(p => p.Key is not ("t" or "start" or "time_continue"))
            .Append(new KeyValuePair<string, string>("t", value));
        return new UriBuilder(uri) { Fragment = "", Query = string.Join('&', parameters.Select(p =>
            Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))) }.Uri;
    }

    private static IEnumerable<KeyValuePair<string, string>> query(Uri uri) => uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
        .Select(p => new KeyValuePair<string, string>(Uri.UnescapeDataString(p[0]), p.Length > 1 ? Uri.UnescapeDataString(p[1]) : ""));

    public static string Timecode(double seconds)
    {
        long s = (long)Math.Floor(Math.Max(0, seconds));
        return $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00}";
    }

    public static bool TryTimecode(string text, out double seconds)
    {
        seconds = 0;
        string[] parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3) return false;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                || n < 0 || i > 0 && n >= 60) return false;
            seconds = seconds * 60 + n;
        }
        return seconds <= MaximumDurationSeconds;
    }

    public static bool TryWallClock(string text, out DateTimeOffset value)
    {
        // Require an offset so sharing a machine or daylight-saving changes do
        // not silently move the index by an hour.
        return DateTimeOffset.TryParseExact(text.Trim(), ["yyyy-MM-dd HH:mm:ss zzz", "yyyy-MM-ddTHH:mm:sszzz", "O"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    public static string ExportCsv(FootageLibrary library)
    {
        static string cell(string value)
        {
            value = value.Replace('\r', ' ').Replace('\n', ' ');
            if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+')
                || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        var result = new StringBuilder("Recording,Moment,Timestamp,Video link\r\n");
        foreach (FootageMoment moment in library.Moments.OrderBy(m => m.RecordingId).ThenBy(m => m.Seconds))
        {
            FootageRecording? recording = library.Recordings.FirstOrDefault(r => r.Id == moment.RecordingId);
            if (recording is null) continue;
            // Local paths and score identities never leave the library in an export.
            result.AppendLine(string.Join(',', new[] { recording.Title, moment.Title, Timecode(moment.Seconds),
                TimestampUrl(recording.Location, moment.Seconds)?.AbsoluteUri ?? "" }.Select(cell)));
        }
        return result.ToString();
    }
}

public sealed class FootageLibraryStore(string path)
{
    private const long maximumBytes = 32 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<FootageLibrary> LoadAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return FootageLibrary.Empty;
            if (new FileInfo(path).Length > maximumBytes) throw new InvalidDataException("The footage library is too large to open.");
            await using var stream = File.OpenRead(path);
            FootageLibrary library = await JsonSerializer.DeserializeAsync<FootageLibrary>(stream, cancellationToken: token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The footage library could not be read.");
            FootageIndex.Validate(library);
            return library;
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(FootageLibrary library, CancellationToken token = default)
    {
        FootageIndex.Validate(library);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(library);
        if (bytes.Length > maximumBytes) throw new InvalidDataException("The footage library is full. Remove old entries before adding more.");
        await gate.WaitAsync(token).ConfigureAwait(false);
        string temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllBytesAsync(temp, bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temp, path, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            finally { gate.Release(); }
        }
    }
}
