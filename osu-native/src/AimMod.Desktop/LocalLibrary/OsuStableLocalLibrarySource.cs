using System.Security.Cryptography;
using System.Text;
using AimMod.Osu.Runtime.Contracts;
using OsuParsers.Database;
using OsuParsers.Database.Objects;
using OsuParsers.Decoders;
using OsuParsers.Enums;

namespace AimMod.Desktop.LocalLibrary;

public sealed class OsuStableLocalLibrarySource : ILocalLibrarySource, ILocalLibraryProgressSource
{
    private readonly string installRoot;
    private readonly string songsRoot;
    private readonly object snapshotLock = new();
    private Task<InMemoryLocalLibrarySource>? snapshotTask;
    private DatabaseStamp snapshotStamp;
    private LocalLibraryProgress? progress;
    public LocalLibraryProgress? Progress => Volatile.Read(ref progress);

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
            if (snapshotTask is null || snapshotTask.IsFaulted || snapshotTask.IsCanceled || current != snapshotStamp)
            {
                snapshotStamp = current;
                snapshotTask = Task.Run(() =>
                {
                    try { return buildSnapshot(); }
                    finally { Volatile.Write(ref progress, null); }
                }, CancellationToken.None);
            }
            task = snapshotTask ??= Task.Run(buildSnapshot, CancellationToken.None);
        }
        return task.WaitAsync(cancellationToken);
    }

    private InMemoryLocalLibrarySource buildSnapshot()
    {
        Volatile.Write(ref progress, new("Reading osu!stable beatmap database"));
        OsuDatabase beatmapDatabase = decodeSharedDatabase(
            Path.Combine(installRoot, "osu!.db"),
            DatabaseDecoder.DecodeOsu);
        Volatile.Write(ref progress, new("Reading osu!stable score history"));
        ScoresDatabase? scoreDatabase = tryDecodeScores(Path.Combine(installRoot, "scores.db"));

        Dictionary<string, List<Score>> scoresByBeatmap = (scoreDatabase?.Scores ?? [])
            .Where(group => !string.IsNullOrWhiteSpace(group.Item1))
            .GroupBy(group => group.Item1, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(entry => entry.Item2).ToList(),
                StringComparer.OrdinalIgnoreCase);

        Volatile.Write(ref progress, new("Finding osu!stable replay files"));
        var replayPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var knownScores = scoresByBeatmap.Values.SelectMany(scores => scores).Select(StableReplayHeaders.Key).ToHashSet(StringComparer.Ordinal);
        foreach (string folder in new[] { Path.Combine(installRoot, "Data", "r"), Path.Combine(installRoot, "Replays") })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (string path in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                if (!Path.GetExtension(path).Equals(".osr", StringComparison.OrdinalIgnoreCase)) continue;
                Score? replay = StableReplayHeaders.Read(path);
                if (replay is null) continue;
                string key = StableReplayHeaders.Key(replay);
                replayPaths.TryAdd(key, path);
                if (!knownScores.Add(key)) continue;
                if (!scoresByBeatmap.TryGetValue(replay.BeatmapMD5Hash, out var scores))
                    scoresByBeatmap[replay.BeatmapMD5Hash] = scores = [];
                scores.Add(replay);
            }
        }

        var beatmapsByHash = new Dictionary<string, StableBeatmap>(StringComparer.OrdinalIgnoreCase);
        DbBeatmap[] standardMaps = beatmapDatabase.Beatmaps.Where(beatmap => beatmap.Ruleset == Ruleset.Standard).ToArray();
        int checkedMaps = 0;
        foreach (DbBeatmap beatmap in standardMaps)
        {
            Volatile.Write(ref progress, new("Checking osu!stable beatmaps", ++checkedMaps, standardMaps.Length));
            string? beatmapPath = resolveLibraryFile(beatmap.FolderName, beatmap.FileName);
            if (beatmapPath is null || string.IsNullOrWhiteSpace(beatmap.MD5Hash))
                continue;

            string folderPath = Path.GetDirectoryName(beatmapPath)!;
            bool installed = File.Exists(beatmapPath);
            string backgroundPath = installed ? resolveBackground(beatmapPath, folderPath) : string.Empty;
            double stars = beatmap.StandardStarRating.TryGetValue(Mods.None, out double noModStars)
                ? noModStars
                : beatmap.StandardStarRating.Values.DefaultIfEmpty().Min();
            int localScoreCount = scoresByBeatmap.GetValueOrDefault(beatmap.MD5Hash)?.Count ?? 0;
            beatmapsByHash[beatmap.MD5Hash] = new StableBeatmap(beatmap, installed ? beatmapPath : string.Empty, backgroundPath, stars, localScoreCount);
        }

        Volatile.Write(ref progress, new("Matching osu!stable scores and replay files"));
        LocalReplay[] replays = scoresByBeatmap
            .SelectMany(group => group.Value.Select(score => createReplay(score, beatmapsByHash.GetValueOrDefault(group.Key), replayPaths.GetValueOrDefault(StableReplayHeaders.Key(score)))))
            .Where(replay => replay is not null)
            .Select(replay => replay!)
            .ToArray();

        Dictionary<string, DateTimeOffset> lastPlayedByHash = replays
            .GroupBy(replay => replay.BeatmapHash, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Max(replay => replay.PlayedAt), StringComparer.OrdinalIgnoreCase);

        Volatile.Write(ref progress, new("Preparing beatmap groups"));
        LocalBeatmapSet[] sets = beatmapsByHash.Values
            .Where(beatmap => beatmap.BeatmapPath.Length > 0)
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

    private LocalReplay? createReplay(Score score, StableBeatmap? beatmap, string? indexedReplayPath)
    {
        if (score.Ruleset != Ruleset.Standard)
            return null;

        int totalHits = score.Count300 + score.Count100 + score.Count50 + score.CountMiss;
        double accuracy = totalHits == 0
            ? 0
            : (score.Count300 * 300d + score.Count100 * 100d + score.Count50 * 50d) / (totalHits * 300d);
        string replayPath = indexedReplayPath ?? resolveReplayPath(score.ReplayMD5Hash);
        string[] mods = enumerateMods(score.Mods);
        DateTimeOffset playedAt = new(DateTime.SpecifyKind(score.ScoreTimestamp, DateTimeKind.Utc));
        var statistics = new PpScoreStatistics(score.Count300, score.Count100, score.Count50, score.CountMiss, 0, 0);

        return new LocalReplay(
            stableGuid("score", score.ReplayMD5Hash.Length > 0
                ? score.ReplayMD5Hash
                : $"{score.BeatmapMD5Hash}:{score.PlayerName}:{playedAt.UtcTicks}:{score.ReplayScore}"),
            beatmap is null ? Guid.Empty : stableGuid("set", setKey(beatmap.Entry)),
            stableGuid("beatmap", score.BeatmapMD5Hash),
            beatmap?.Entry.Title ?? "Beatmap not installed",
            beatmap?.Entry.Artist ?? string.Empty,
            beatmap?.Entry.Difficulty ?? string.Empty,
            "osu",
            score.PlayerName,
            playedAt,
            beatmap?.Entry.StandardStarRating.GetValueOrDefault(score.Mods, beatmap.StarRating) ?? 0,
            accuracy,
            score.ReplayScore,
            score.Combo,
            score.CountMiss,
            null,
            mods,
            replayPath.Length > 0,
            score.BeatmapMD5Hash,
            beatmap?.BackgroundPath ?? string.Empty,
            statistics,
            OnlineScoreId: Math.Max(0, score.ScoreId),
            BeatmapPath: beatmap?.BeatmapPath ?? string.Empty,
            ReplayPath: replayPath,
            Origin: LocalLibraryOrigin.Stable,
            OnlineBeatmapId: Math.Max(0, beatmap?.Entry.BeatmapId ?? 0));
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
            // Library thumbnails do not need hit objects or slider geometry.
            string name = BeatmapDecoder.Decode(File.ReadLines(beatmapPath)
                .TakeWhile(line => !line.Trim().Equals("[HitObjects]", StringComparison.OrdinalIgnoreCase)))
                .EventsSection.BackgroundImage;
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

    private static string[] enumerateMods(Mods value) => new osu.Game.Rulesets.Osu.OsuRuleset()
        .ConvertFromLegacyMods((osu.Game.Beatmaps.Legacy.LegacyMods)(int)value)
        .Select(mod => mod.Acronym)
        .ToArray();

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

    private DatabaseStamp getStamp() => new(fileStamp(Path.Combine(installRoot, "osu!.db")), fileStamp(Path.Combine(installRoot, "scores.db")),
        directoryStamp(Path.Combine(installRoot, "Data", "r")), directoryStamp(Path.Combine(installRoot, "Replays")));

    private static long directoryStamp(string path) => Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path).Ticks : 0;

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

    private readonly record struct DatabaseStamp(long Beatmaps, long Scores, long ReplayCache, long Exports);
}
