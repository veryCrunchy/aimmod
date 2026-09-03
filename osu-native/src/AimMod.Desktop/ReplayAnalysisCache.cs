using System.Text.Json;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop;

/// <summary>
/// Persists completed, deterministic replay analysis results between AimMod runs.
/// The cache contains judgement data only. It never stores replay files or osu! credentials.
/// </summary>
public sealed class ReplayAnalysisCache
{
    internal const int CurrentVersion = 1;
    internal const int MaximumEntries = 100;
    internal const long MaximumFileBytes = 64 * 1024 * 1024;

    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string path;
    private readonly SemaphoreSlim writeGate = new(1, 1);

    public ReplayAnalysisCache(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    public IReadOnlyDictionary<Guid, ReplayAnalysisResult> Load()
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > MaximumFileBytes)
                return new Dictionary<Guid, ReplayAnalysisResult>();

            using FileStream stream = File.Open(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.SequentialScan,
            });
            CacheDocument? document = JsonSerializer.Deserialize<CacheDocument>(stream, jsonOptions);
            if (document is null || document.Version != CurrentVersion)
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
            var document = new CacheDocument(CurrentVersion, entries);
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
                    await JsonSerializer.SerializeAsync(stream, document, jsonOptions, cancellationToken).ConfigureAwait(false);
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
        result.Judgements.Count <= ReplayAnalysisProtocol.MaximumJudgements &&
        result.Pauses.Count <= ReplayAnalysisProtocol.MaximumPauses;

    internal sealed record CacheDocument(int Version, IReadOnlyList<CacheEntry> Entries);
    internal sealed record CacheEntry(Guid ScoreId, ReplayAnalysisResult? Result);
}
