using System.Text.Json;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop;

/// <summary>
/// Persists completed, deterministic replay analysis results between AimMod runs.
/// The cache contains judgement data only. It never stores replay files or osu! credentials.
/// </summary>
public sealed class ReplayAnalysisCache
{
    internal const int CurrentVersion = 3;
    internal const int MaximumEntries = 500;
    internal const long MaximumFileBytes = 256 * 1024 * 1024;

    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string path;
    private readonly long maximumFileBytes;
    private readonly SemaphoreSlim writeGate = new(1, 1);

    public ReplayAnalysisCache(string path) : this(path, MaximumFileBytes)
    {
    }

    internal ReplayAnalysisCache(string path, long maximumFileBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumFileBytes < 128 || maximumFileBytes > MaximumFileBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        this.path = Path.GetFullPath(path);
        this.maximumFileBytes = maximumFileBytes;
    }

    public IReadOnlyDictionary<Guid, ReplayAnalysisResult> Load()
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length <= 0 || file.Length > maximumFileBytes)
                return new Dictionary<Guid, ReplayAnalysisResult>();

            using FileStream stream = File.Open(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.SequentialScan,
            });
            CacheDocument? document = JsonSerializer.Deserialize<CacheDocument>(stream, jsonOptions);
            if (document is null || document.Version != CurrentVersion || document.Entries is null)
                return new Dictionary<Guid, ReplayAnalysisResult>();

            return document.Entries
                           .Where(entry => entry.ScoreId != Guid.Empty && isValid(entry.Result))
                           .TakeLast(MaximumEntries)
                           .ToDictionary(entry => entry.ScoreId, entry => entry.Result!);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new Dictionary<Guid, ReplayAnalysisResult>();
        }
    }

    public IReadOnlyDictionary<Guid, ReplayAnalysisResult> LoadMatching(
        IReadOnlyDictionary<Guid, ReplayAnalysisContentIdentity> currentContentIdentities)
    {
        ArgumentNullException.ThrowIfNull(currentContentIdentities);

        return Load().Where(pair => currentContentIdentities.TryGetValue(pair.Key, out ReplayAnalysisContentIdentity? current)
                                    && contentIdentityMatches(pair.Value.ContentIdentity, current))
                     .ToDictionary();
    }

    public IReadOnlyDictionary<Guid, ReplayAnalysisResult> LoadMatching(IEnumerable<LocalReplay> currentReplays)
    {
        ArgumentNullException.ThrowIfNull(currentReplays);

        Dictionary<Guid, string> beatmapIdentities = currentReplays
                                                     .Where(replay => replay.ScoreId != Guid.Empty && isSha256(replay.BeatmapHash))
                                                     .GroupBy(replay => replay.ScoreId)
                                                     .ToDictionary(group => group.Key, group => group.Last().BeatmapHash);

        return Load().Where(pair => beatmapIdentities.TryGetValue(pair.Key, out string? beatmapSha256)
                                    && string.Equals(
                                        pair.Value.ContentIdentity!.BeatmapSha256,
                                        beatmapSha256,
                                        StringComparison.OrdinalIgnoreCase))
                     .ToDictionary();
    }

    public async Task SaveAsync(
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analyses);

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            CacheEntry[] entries = analyses
                                   .Where(pair => pair.Key != Guid.Empty && isValid(pair.Value))
                                   .TakeLast(MaximumEntries)
                                   .Select(pair => new CacheEntry(pair.Key, pair.Value))
                                   .ToArray();
            string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

            try
            {
                await using (FileStream stream = File.Open(temporaryPath, new FileStreamOptions
                             {
                                 Mode = FileMode.CreateNew,
                                 Access = FileAccess.Write,
                                 Share = FileShare.None,
                                 Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                             }))
                {
                    // Budget by encoded bytes, not only entry count: long replays
                    // contain far more judgements than short ones. Keep the newest
                    // complete results, then write them in their original order.
                    byte[] prefix = System.Text.Encoding.UTF8.GetBytes($"{{\"version\":{CurrentVersion},\"entries\":[");
                    long remaining = maximumFileBytes - prefix.Length - 2;
                    var encoded = new List<byte[]>();
                    foreach (CacheEntry entry in entries.Reverse())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entry, jsonOptions);
                        long required = bytes.LongLength + (encoded.Count > 0 ? 1 : 0);
                        if (required > remaining) continue;
                        encoded.Add(bytes);
                        remaining -= required;
                    }
                    await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
                    encoded.Reverse();
                    for (int index = 0; index < encoded.Count; index++)
                    {
                        if (index > 0) await stream.WriteAsync(new byte[] { (byte)',' }, cancellationToken).ConfigureAwait(false);
                        await stream.WriteAsync(encoded[index], cancellationToken).ConfigureAwait(false);
                    }
                    await stream.WriteAsync(new byte[] { (byte)']', (byte)'}' }, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static bool isValid(ReplayAnalysisResult? result) =>
        result is not null &&
        string.Equals(result.EngineVersion, ReplayAnalysisProtocol.EngineVersion, StringComparison.Ordinal) &&
        isValid(result.ContentIdentity) &&
        result.Judgements is not null &&
        result.Judgements.Count <= ReplayAnalysisProtocol.MaximumJudgements &&
        result.Pauses is not null &&
        result.Summary is not null &&
        result.Pauses.Count <= ReplayAnalysisProtocol.MaximumPauses;

    private static bool isValid(ReplayAnalysisContentIdentity? identity) =>
        identity is not null &&
        isSha256(identity.BeatmapSha256) &&
        isSha256(identity.ReplaySha256);

    private static bool contentIdentityMatches(
        ReplayAnalysisContentIdentity? cached,
        ReplayAnalysisContentIdentity? current) =>
        isValid(cached) &&
        isValid(current) &&
        string.Equals(cached!.BeatmapSha256, current!.BeatmapSha256, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(cached.ReplaySha256, current.ReplaySha256, StringComparison.OrdinalIgnoreCase);

    private static bool isSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    internal sealed record CacheDocument(int Version, IReadOnlyList<CacheEntry> Entries);
    internal sealed record CacheEntry(Guid ScoreId, ReplayAnalysisResult? Result);
}
