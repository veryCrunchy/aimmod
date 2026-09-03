using System.Text.Json;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime;

namespace AimMod.Desktop.PpTargets;

public sealed record PpTargetWorkspaceSnapshot(
    DateTimeOffset CachedAt,
    PpTargetPreferenceProfile Profile,
    IReadOnlyList<LocalBeatmapSet> LocalSets,
    IReadOnlyList<OfficialBeatmapSet> Catalog,
    IReadOnlyDictionary<int, PpTargetEstimate> ExactEstimates,
    int OnlineBestCount,
    string ScoreDataStatus,
    string SearchText,
    double MinimumStars,
    double MaximumStars,
    OfficialBeatmapCategory Category);

public sealed class PpTargetWorkspaceCache
{
    public static readonly TimeSpan Freshness = TimeSpan.FromHours(6);

    private const int current_version = 1;
    private static readonly JsonSerializerOptions json_options = new(JsonSerializerDefaults.Web);

    private readonly string path;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim writeGate = new(1, 1);

    public PpTargetWorkspaceCache(string path, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The PP target workspace cache path must be absolute.", nameof(path));

        this.path = path;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public PpTargetWorkspaceSnapshot? Load()
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using FileStream stream = File.OpenRead(path);
            CacheDocument? document = JsonSerializer.Deserialize<CacheDocument>(stream, json_options);
            return document?.Version == current_version ? document.Snapshot : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public bool IsFresh(PpTargetWorkspaceSnapshot snapshot) =>
        snapshot.CachedAt <= timeProvider.GetUtcNow()
        && timeProvider.GetUtcNow() - snapshot.CachedAt <= Freshness;

    public async Task SaveAsync(PpTargetWorkspaceSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            PpTargetWorkspaceSnapshot current = snapshot with { CachedAt = timeProvider.GetUtcNow() };
            temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, new CacheDocument(current_version, current), json_options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"[AimMod] Could not persist the PP target workspace cache: {exception.Message}");
        }
        finally
        {
            if (temporaryPath is not null && File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"[AimMod] Could not remove PP target cache temporary file: {exception.Message}");
                }
            }

            writeGate.Release();
        }
    }

    private sealed record CacheDocument(int Version, PpTargetWorkspaceSnapshot Snapshot);
}
