using System.Security.Cryptography;
using System.Text;
using AimMod.Osu.Runtime.Contracts;
using OsuParsers.Database;
using OsuParsers.Database.Objects;
using OsuParsers.Decoders;
using OsuParsers.Enums;

namespace AimMod.Desktop.LocalLibrary;

public sealed class OsuStableLocalLibrarySource : ILocalLibrarySource
{
    private readonly string installRoot;
    private readonly string songsRoot;
    private readonly object snapshotLock = new();
    private Task<InMemoryLocalLibrarySource>? snapshotTask;
    private DatabaseStamp snapshotStamp;

    public OsuStableLocalLibrarySource(string installRoot, string songsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(songsRoot);
        if (!Path.IsPathFullyQualified(installRoot) || !Path.IsPathFullyQualified(songsRoot))
            throw new ArgumentException("osu!stable library paths must be absolute.");

        this.installRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        this.songsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(songsRoot));
    }

    public async ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(
        LocalLibraryQuery query,
        CancellationToken cancellationToken = default)
    {
        InMemoryLocalLibrarySource snapshot = await getSnapshot(cancellationToken).ConfigureAwait(false);
        return await snapshot.SearchBeatmapSetsAsync(query, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(
        LocalLibraryQuery query,
        CancellationToken cancellationToken = default)
    {
        InMemoryLocalLibrarySource snapshot = await getSnapshot(cancellationToken).ConfigureAwait(false);
        return await snapshot.SearchReplaysAsync(query, cancellationToken).ConfigureAwait(false);
    }

    public void Invalidate()
    {
        lock (snapshotLock)
            snapshotTask = null;
    }

    private Task<InMemoryLocalLibrarySource> getSnapshot(CancellationToken cancellationToken)
    {
        Task<InMemoryLocalLibrarySource> task;
        lock (snapshotLock)
        {
            DatabaseStamp current = getStamp();
            if (snapshotTask is null || current != snapshotStamp)
            {
                snapshotStamp = current;
                snapshotTask = Task.Run(buildSnapshot, CancellationToken.None);
            }
            task = snapshotTask ??= Task.Run(buildSnapshot, CancellationToken.None);
        }
        return task.WaitAsync(cancellationToken);
    }

    private InMemoryLocalLibrarySource buildSnapshot()
    {
        OsuDatabase beatmapDatabase = decodeSharedDatabase(
            Path.Combine(installRoot, "osu!.db"),
            DatabaseDecoder.DecodeOsu);
        ScoresDatabase? scoreDatabase = tryDecodeScores(Path.Combine(installRoot, "scores.db"));

        Dictionary<string, List<Score>> scoresByBeatmap = (scoreDatabase?.Scores ?? [])
            .Where(group => !string.IsNullOrWhiteSpace(group.Item1))
            .GroupBy(group => group.Item1, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(entry => entry.Item2).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var beatmapsByHash = new Dictionary<string, StableBeatmap>(StringComparer.OrdinalIgnoreCase);
        foreach (DbBeatmap beatmap in beatmapDatabase.Beatmaps.Where(beatmap => beatmap.Ruleset == Ruleset.Standard))
        {
            string? beatmapPath = resolveLibraryFile(beatmap.FolderName, beatmap.FileName);
            if (beatmapPath is null || !File.Exists(beatmapPath) || string.IsNullOrWhiteSpace(beatmap.MD5Hash))
                continue;

            string folderPath = Path.GetDirectoryName(beatmapPath)!;
            string backgroundPath = resolveBackground(beatmapPath, folderPath);
            double stars = beatmap.StandardStarRating.TryGetValue(Mods.None, out double noModStars)
                ? noModStars
                : beatmap.StandardStarRating.Values.DefaultIfEmpty().Min();
            int localScoreCount = scoresByBeatmap.GetValueOrDefault(beatmap.MD5Hash)?.Count ?? 0;
            beatmapsByHash[beatmap.MD5Hash] = new StableBeatmap(beatmap, beatmapPath, backgroundPath, stars, localScoreCount);
        }

        LocalReplay[] replays = scoresByBeatmap
            .SelectMany(group => group.Value.Select(score => createReplay(score, beatmapsByHash.GetValueOrDefault(group.Key))))
            .Where(replay => replay is not null)
            .Select(replay => replay!)
            .ToArray();

        Dictionary<string, DateTimeOffset> lastPlayedByHash = replays
            .GroupBy(replay => replay.BeatmapHash, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Max(replay => replay.PlayedAt), StringComparer.OrdinalIgnoreCase);

        LocalBeatmapSet[] sets = beatmapsByHash.Values
            .GroupBy(beatmap => setKey(beatmap.Entry))
            .Select(group => createSet(group, lastPlayedByHash))
            .ToArray();
        return new InMemoryLocalLibrarySource(sets, replays);
    }

    private LocalBeatmapSet createSet(
        IGrouping<string, StableBeatmap> group,
        IReadOnlyDictionary<string, DateTimeOffset> lastPlayedByHash)
    {
        StableBeatmap representative = group.First();
        DbBeatmap entry = representative.Entry;
        LocalBeatmapDifficulty[] difficulties = group.Select(beatmap => new LocalBeatmapDifficulty(
            stableGuid("beatmap", beatmap.Entry.MD5Hash),
            beatmap.Entry.BeatmapId,
            beatmap.Entry.Difficulty,
            "osu",
            beatmap.StarRating,
            beatmap.Entry.TimingPoints.Where(point => !point.Inherited).Select(point => point.BPM).DefaultIfEmpty().Max(),
            beatmap.Entry.TotalTime,
            beatmap.Entry.CircleSize,
            beatmap.Entry.ApproachRate,
            beatmap.Entry.OverallDifficulty,
            beatmap.Entry.HPDrain,
            beatmap.LocalScoreCount,
            beatmap.Entry.MD5Hash)).OrderBy(difficulty => difficulty.StarRating).ToArray();
        string folder = Path.GetDirectoryName(representative.BeatmapPath)!;
        DateTimeOffset dateAdded = new DirectoryInfo(folder).CreationTimeUtc;
        DateTimeOffset? lastPlayed = group.Select(beatmap => lastPlayedByHash.GetValueOrDefault(beatmap.Entry.MD5Hash))
                                                .Where(value => value != default)
                                                .Select<DateTimeOffset, DateTimeOffset?>(value => value)
                                                .Max();

        return new LocalBeatmapSet(
            stableGuid("set", group.Key),
            entry.BeatmapSetId,
            entry.Title,
            entry.Artist,
            entry.Creator,
            entry.Source,
            dateAdded,
            lastPlayed,
            difficulties,
            difficulties.Sum(difficulty => difficulty.LocalScoreCount ?? 0),
            representative.BackgroundPath);
    }

    private LocalReplay? createReplay(Score score, StableBeatmap? beatmap)
    {
        if (beatmap is null || score.Ruleset != Ruleset.Standard)
            return null;

        int totalHits = score.Count300 + score.Count100 + score.Count50 + score.CountMiss;
        double accuracy = totalHits == 0
            ? 0
            : (score.Count300 * 300d + score.Count100 * 100d + score.Count50 * 50d) / (totalHits * 300d);
        string replayPath = resolveReplayPath(score.ReplayMD5Hash);
        string[] mods = enumerateMods(score.Mods);
        DateTimeOffset playedAt = new(DateTime.SpecifyKind(score.ScoreTimestamp, DateTimeKind.Utc));
        var statistics = new PpScoreStatistics(score.Count300, score.Count100, score.Count50, score.CountMiss, 0, 0);

        return new LocalReplay(
            stableGuid("score", score.ReplayMD5Hash.Length > 0
                ? score.ReplayMD5Hash
                : $"{score.BeatmapMD5Hash}:{score.PlayerName}:{playedAt.UtcTicks}:{score.ReplayScore}"),
            stableGuid("set", setKey(beatmap.Entry)),
            stableGuid("beatmap", beatmap.Entry.MD5Hash),
            beatmap.Entry.Title,
            beatmap.Entry.Artist,
            beatmap.Entry.Difficulty,
            "osu",
            score.PlayerName,
            playedAt,
            beatmap.StarRating,
            accuracy,
            score.ReplayScore,
            score.Combo,
            score.CountMiss,
            null,
            mods,
            replayPath.Length > 0,
            beatmap.Entry.MD5Hash,
            beatmap.BackgroundPath,
            statistics,
            OnlineScoreId: Math.Max(0, score.ScoreId),
            BeatmapPath: beatmap.BeatmapPath,
            ReplayPath: replayPath,
            Origin: LocalLibraryOrigin.Stable);
    }

    private string resolveReplayPath(string replayHash)
    {
        if (!validHash(replayHash))
            return string.Empty;
        string path = Path.GetFullPath(Path.Combine(installRoot, "Data", "r", replayHash + ".osr"));
        return isWithin(path, installRoot) && File.Exists(path) ? path : string.Empty;
    }

    private string? resolveLibraryFile(string folderName, string fileName)
    {
        if (string.IsNullOrWhiteSpace(folderName) || string.IsNullOrWhiteSpace(fileName))
            return null;
        string path = Path.GetFullPath(Path.Combine(songsRoot, folderName, fileName));
        return isWithin(path, songsRoot) ? path : null;
    }

    private static string resolveBackground(string beatmapPath, string folderPath)
    {
        try
        {
            string name = BeatmapDecoder.Decode(beatmapPath).EventsSection.BackgroundImage;
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;
            string path = Path.GetFullPath(Path.Combine(folderPath, name));
            return isWithin(path, folderPath) && File.Exists(path) ? path : string.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            return string.Empty;
        }
    }

    private static T decodeSharedDatabase<T>(string path, Func<Stream, T> decode)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var snapshot = new MemoryStream(source.Length > int.MaxValue ? 0 : (int)source.Length);
        source.CopyTo(snapshot);
        snapshot.Position = 0;
        return decode(snapshot);
    }

    private static ScoresDatabase? tryDecodeScores(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return decodeSharedDatabase(path, DatabaseDecoder.DecodeScores);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }

    private static string[] enumerateMods(Mods value) => Enum.GetValues<Mods>()
        .Where(mod => mod != Mods.None && value.HasFlag(mod) && isAtomicFlag(mod))
        .Select(mod => mod.ToString())
        .ToArray();

    private static bool isAtomicFlag(Mods mod)
    {
        int value = (int)mod;
        return value > 0 && (value & (value - 1)) == 0;
    }

    private static string setKey(DbBeatmap beatmap) => beatmap.BeatmapSetId > 0
        ? beatmap.BeatmapSetId.ToString()
        : beatmap.FolderName;

    private static bool validHash(string value) => value.Length == 32 && value.All(Uri.IsHexDigit);

    private static Guid stableGuid(string kind, string value)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"osu-stable:{kind}:{value.ToLowerInvariant()}"));
        return new Guid(digest.AsSpan(0, 16));
    }

    private static bool isWithin(string path, string root)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative != ".."
               && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !Path.IsPathFullyQualified(relative);
    }

    private DatabaseStamp getStamp() => new(fileStamp(Path.Combine(installRoot, "osu!.db")), fileStamp(Path.Combine(installRoot, "scores.db")));

    private static long fileStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? HashCode.Combine(info.Length, info.LastWriteTimeUtc.Ticks) : 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private sealed record StableBeatmap(
        DbBeatmap Entry,
        string BeatmapPath,
        string BackgroundPath,
        double StarRating,
        int LocalScoreCount);

    private readonly record struct DatabaseStamp(long Beatmaps, long Scores);
}
