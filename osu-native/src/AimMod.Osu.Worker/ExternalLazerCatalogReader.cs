using System.Text.Json;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using Realms;

namespace AimMod.Osu.Worker;

/// <summary>
/// Reads catalog metadata from AimMod's private schema-51 Realm copy. The field
/// names follow ppy/osu commit 1032a7c31581513c8be751e46f0940e1c95ed252.
/// </summary>
public sealed class DynamicRealmLazerLibraryCatalogReader : ILazerLibraryCatalogReader
{
    public Task<ExternalLazerCatalogSearchResult> ReadCatalogAsync(
        LazerLibrarySnapshot snapshot,
        ExternalLazerCatalogSearchRequest query,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => readCatalog(snapshot, validateQuery(query), cancellationToken), cancellationToken);

    private static ExternalLazerCatalogSearchResult readCatalog(
        LazerLibrarySnapshot snapshot,
        ExternalLazerCatalogSearchRequest query,
        CancellationToken cancellationToken)
    {
        validateSnapshot(snapshot);

        var configuration = new RealmConfiguration(snapshot.DatabasePath)
        {
            IsDynamic = true,
            IsReadOnly = true,
            SchemaVersion = RealmLazerLibrarySnapshotFactory.SupportedSchemaVersion,
        };

        using Realm realm = Realm.GetInstance(configuration);
        return query.Kind == ExternalLazerCatalogEntryKind.BeatmapSets
            ? readBeatmapSets(realm, query, cancellationToken)
            : readReplays(realm, query, cancellationToken);
    }

    private static ExternalLazerCatalogSearchResult readBeatmapSets(
        Realm realm,
        ExternalLazerCatalogSearchRequest query,
        CancellationToken cancellationToken)
    {
        (Dictionary<string, int> scoreCounts, Dictionary<string, int> replayCounts, int scanned) = readScoreCounts(realm, cancellationToken);
        var sets = new List<ExternalLazerBeatmapSet>();

        foreach (IRealmObject set in realm.DynamicApi.All("BeatmapSet"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkRowLimit(++scanned);
            if (get<bool>(set, "DeletePending"))
                continue;

            var difficulties = new List<ExternalLazerBeatmapDifficulty>();
            var searchable = new List<string>();
            DateTimeOffset? lastPlayed = null;

            foreach (IRealmObjectBase beatmap in set.DynamicApi.GetList<IRealmObjectBase>("Beatmaps"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkRowLimit(++scanned);
                if (!matchesDifficultyFilters(beatmap, query))
                    continue;

                ExternalLazerBeatmapDifficulty difficulty = toDifficulty(beatmap, scoreCounts);
                difficulties.Add(difficulty);
                searchable.Add(difficulty.Name);
                appendMetadataSearchTerms(beatmap, searchable);

                DateTimeOffset? played = get<DateTimeOffset?>(beatmap, "LastPlayed");
                if (played > lastPlayed)
                    lastPlayed = played;
            }

            if (difficulties.Count == 0 || !matchesSearch(query.SearchText, searchable))
                continue;
            if (difficulties.Count > ExternalLazerCatalogProtocol.MaximumDifficultiesPerSet)
                throw new ExternalLazerLibraryException("catalog_result_too_large", "A beatmap set exceeds the catalog difficulty limit.");

            IRealmObjectBase? metadata = getObject(set.DynamicApi.GetList<IRealmObjectBase>("Beatmaps").First(), "Metadata");
            string title = text(metadata, "Title");
            string artist = text(metadata, "Artist");
            string creator = metadata is null ? string.Empty : text(getObject(metadata, "Author"), "Username");
            int localReplayCount = difficulties.Sum(difficulty => replayCounts.GetValueOrDefault(difficulty.BeatmapHash));
            string backgroundHash = readBackgroundHash(set, metadata);

            sets.Add(new ExternalLazerBeatmapSet(
                get<Guid>(set, "ID"),
                get<int>(set, "OnlineID"),
                title,
                artist,
                creator,
                text(metadata, "Source"),
                get<DateTimeOffset>(set, "DateAdded"),
                lastPlayed,
                difficulties,
                localReplayCount,
                backgroundHash));
        }

        IEnumerable<ExternalLazerBeatmapSet> ordered = query.Sort switch
        {
            ExternalLazerCatalogSort.RecentlyPlayed => sets.OrderByDescending(set => set.LastPlayed).ThenBy(set => set.Title, StringComparer.OrdinalIgnoreCase),
            ExternalLazerCatalogSort.Title => sets.OrderBy(set => set.Title, StringComparer.OrdinalIgnoreCase).ThenBy(set => set.Artist, StringComparer.OrdinalIgnoreCase),
            ExternalLazerCatalogSort.StarRating => sets.OrderByDescending(set => set.Difficulties.Max(difficulty => difficulty.StarRating)).ThenBy(set => set.Title, StringComparer.OrdinalIgnoreCase),
            _ => sets.OrderByDescending(set => set.DateAdded).ThenBy(set => set.Title, StringComparer.OrdinalIgnoreCase),
        };
        ExternalLazerBeatmapSet[] page = ordered.Skip(query.Offset).Take(query.Limit).ToArray();
        return new ExternalLazerCatalogSearchResult(query.Kind, page, Array.Empty<ExternalLazerReplaySummary>(), sets.Count, query.Offset, query.Limit);
    }

    private static ExternalLazerCatalogSearchResult readReplays(
        Realm realm,
        ExternalLazerCatalogSearchRequest query,
        CancellationToken cancellationToken)
    {
        var replays = new List<ExternalLazerReplaySummary>();
        var backgroundHashes = new Dictionary<Guid, string>();
        int scanned = 0;

        foreach (IRealmObject score in realm.DynamicApi.All("Score"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkRowLimit(++scanned);
            if (get<bool>(score, "DeletePending"))
                continue;

            IRealmObjectBase? beatmap = getObject(score, "BeatmapInfo");
            IRealmObjectBase? set = beatmap is null ? null : getObject(beatmap, "BeatmapSet");
            if (beatmap is null || set is null || get<bool>(set, "DeletePending") || !matchesDifficultyFilters(beatmap, query))
                continue;

            IRealmObjectBase? metadata = getObject(beatmap, "Metadata");
            Guid setId = get<Guid>(set, "ID");
            if (!backgroundHashes.TryGetValue(setId, out string? backgroundHash))
            {
                backgroundHash = readBackgroundHash(set, metadata);
                backgroundHashes.Add(setId, backgroundHash);
            }
            IRealmObjectBase? user = getObject(score, "User");
            IReadOnlyList<string> mods = readMods(get<string>(score, "Mods") ?? string.Empty);
            var searchable = new List<string>
            {
                text(metadata, "Title"), text(metadata, "TitleUnicode"), text(metadata, "Artist"), text(metadata, "ArtistUnicode"),
                text(metadata, "Source"), text(metadata, "Tags"), text(beatmap, "DifficultyName"), text(user, "Username"),
            };
            searchable.AddRange(mods);
            if (!matchesSearch(query.SearchText, searchable))
                continue;

            replays.Add(new ExternalLazerReplaySummary(
                get<Guid>(score, "ID"),
                get<Guid>(set, "ID"),
                get<Guid>(beatmap, "ID"),
                text(beatmap, "Hash"),
                text(metadata, "Title"),
                text(metadata, "Artist"),
                text(beatmap, "DifficultyName"),
                text(getObject(score, "Ruleset"), "ShortName"),
                text(user, "Username"),
                get<DateTimeOffset>(score, "Date"),
                finite(get<double>(beatmap, "StarRating")),
                finite(get<double>(score, "Accuracy")),
                get<long>(score, "TotalScore"),
                Math.Max(0, get<int>(score, "MaxCombo")),
                readMissCount(get<string>(score, "Statistics") ?? string.Empty),
                finiteNullable(get<double?>(score, "PP")),
                mods,
                hasReplayFile(score),
                backgroundHash));
        }

        IEnumerable<ExternalLazerReplaySummary> ordered = query.Sort switch
        {
            ExternalLazerCatalogSort.Title => replays.OrderBy(replay => replay.Title, StringComparer.OrdinalIgnoreCase).ThenBy(replay => replay.Difficulty, StringComparer.OrdinalIgnoreCase),
            ExternalLazerCatalogSort.StarRating => replays.OrderByDescending(replay => replay.StarRating).ThenByDescending(replay => replay.PlayedAt),
            ExternalLazerCatalogSort.Score => replays.OrderByDescending(replay => replay.TotalScore).ThenByDescending(replay => replay.PlayedAt),
            ExternalLazerCatalogSort.Accuracy => replays.OrderByDescending(replay => replay.Accuracy).ThenByDescending(replay => replay.TotalScore),
            _ => replays.OrderByDescending(replay => replay.PlayedAt),
        };
        ExternalLazerReplaySummary[] page = ordered.Skip(query.Offset).Take(query.Limit).ToArray();
        return new ExternalLazerCatalogSearchResult(query.Kind, Array.Empty<ExternalLazerBeatmapSet>(), page, replays.Count, query.Offset, query.Limit);
    }

    private static (Dictionary<string, int> Scores, Dictionary<string, int> Replays, int Scanned) readScoreCounts(
        Realm realm,
        CancellationToken cancellationToken)
    {
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var replays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int scanned = 0;
        foreach (IRealmObject score in realm.DynamicApi.All("Score"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkRowLimit(++scanned);
            if (get<bool>(score, "DeletePending"))
                continue;

            string hash = text(score, "BeatmapHash");
            if (hash.Length == 0)
                continue;
            scores[hash] = scores.GetValueOrDefault(hash) + 1;
            if (hasReplayFile(score))
                replays[hash] = replays.GetValueOrDefault(hash) + 1;
        }

        return (scores, replays, scanned);
    }

    private static ExternalLazerBeatmapDifficulty toDifficulty(IRealmObjectBase beatmap, IReadOnlyDictionary<string, int> scoreCounts)
    {
        string hash = text(beatmap, "Hash");
        IRealmObjectBase? difficulty = getObject(beatmap, "Difficulty");
        return new ExternalLazerBeatmapDifficulty(
            get<Guid>(beatmap, "ID"),
            get<int>(beatmap, "OnlineID"),
            hash,
            text(beatmap, "MD5Hash"),
            text(beatmap, "DifficultyName"),
            text(getObject(beatmap, "Ruleset"), "ShortName"),
            finite(get<double>(beatmap, "StarRating")),
            finite(get<double>(beatmap, "BPM")),
            finite(get<double>(beatmap, "Length")),
            finite(get<float>(difficulty, "CircleSize")),
            finite(get<float>(difficulty, "ApproachRate")),
            finite(get<float>(difficulty, "OverallDifficulty")),
            finite(get<float>(difficulty, "DrainRate")),
            scoreCounts.GetValueOrDefault(hash));
    }

    private static bool matchesDifficultyFilters(IRealmObjectBase beatmap, ExternalLazerCatalogSearchRequest query)
    {
        string ruleset = text(getObject(beatmap, "Ruleset"), "ShortName");
        double stars = get<double>(beatmap, "StarRating");
        return (query.RulesetShortName.Length == 0 || string.Equals(ruleset, query.RulesetShortName, StringComparison.OrdinalIgnoreCase))
               && (query.MinimumStars is null || stars >= query.MinimumStars)
               && (query.MaximumStars is null || stars <= query.MaximumStars);
    }

    private static void appendMetadataSearchTerms(IRealmObjectBase beatmap, ICollection<string> terms)
    {
        IRealmObjectBase? metadata = getObject(beatmap, "Metadata");
        terms.Add(text(metadata, "Title"));
        terms.Add(text(metadata, "TitleUnicode"));
        terms.Add(text(metadata, "Artist"));
        terms.Add(text(metadata, "ArtistUnicode"));
        terms.Add(text(metadata, "Source"));
        terms.Add(text(metadata, "Tags"));
        terms.Add(text(getObject(metadata, "Author"), "Username"));
    }

    private static bool matchesSearch(string searchText, IReadOnlyCollection<string> values)
    {
        if (searchText.Length == 0)
            return true;
        string[] terms = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(term => values.Any(value => value.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool hasReplayFile(IRealmObjectBase score)
    {
        bool found = false;
        int count = 0;
        foreach (IEmbeddedObject file in score.DynamicApi.GetList<IEmbeddedObject>("Files"))
        {
            if (++count > ExternalLazerCatalogProtocol.MaximumFilesPerScore)
                throw new ExternalLazerLibraryException("catalog_result_too_large", "A score exceeds the catalog file-reference limit.");
            found |= text(file, "Filename").EndsWith(".osr", StringComparison.OrdinalIgnoreCase);
        }

        return found;
    }

    private static string readBackgroundHash(IRealmObjectBase set, IRealmObjectBase? metadata)
    {
        string backgroundName = text(metadata, "BackgroundFile");
        if (backgroundName.Length == 0)
            return string.Empty;

        foreach (IEmbeddedObject file in set.DynamicApi.GetList<IEmbeddedObject>("Files"))
        {
            if (!string.Equals(text(file, "Filename"), backgroundName, StringComparison.OrdinalIgnoreCase))
                continue;

            string hash = text(getObject(file, "File"), "Hash").ToLowerInvariant();
            return hash.Length == 64 && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
                ? hash
                : string.Empty;
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> readMods(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > ExternalLazerCatalogProtocol.MaximumTextFieldLength)
            return Array.Empty<string>();

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            return document.RootElement.EnumerateArray()
                           .Select(element => element.TryGetProperty("acronym", out JsonElement acronym) ? acronym.GetString() : null)
                           .Where(acronym => !string.IsNullOrWhiteSpace(acronym))
                           .Select(acronym => clamp(acronym!, ExternalLazerCatalogProtocol.MaximumModAcronymLength))
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .Take(ExternalLazerCatalogProtocol.MaximumMods)
                           .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static int readMissCount(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > ExternalLazerCatalogProtocol.MaximumTextFieldLength)
            return 0;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return 0;
            if ((document.RootElement.TryGetProperty("Miss", out JsonElement miss)
                 || document.RootElement.TryGetProperty("miss", out miss)
                 || document.RootElement.TryGetProperty("1", out miss))
                && miss.TryGetInt32(out int count))
            {
                return Math.Max(0, count);
            }
        }
        catch (JsonException)
        {
        }

        return 0;
    }

    internal static ExternalLazerCatalogSearchRequest ValidateQuery(ExternalLazerCatalogSearchRequest query) => validateQuery(query);

    private static ExternalLazerCatalogSearchRequest validateQuery(ExternalLazerCatalogSearchRequest query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!Enum.IsDefined(query.Kind)
            || !Enum.IsDefined(query.Sort)
            || query.SearchText is null
            || query.SearchText.Length > ExternalLazerCatalogProtocol.MaximumSearchTextLength
            || query.RulesetShortName is null
            || query.RulesetShortName.Length > ExternalLazerCatalogProtocol.MaximumRulesetShortNameLength
            || query.Offset is < 0 or > ExternalLazerCatalogProtocol.MaximumOffset
            || query.Limit is < 1 or > ExternalLazerCatalogProtocol.MaximumPageSize
            || invalidStars(query.MinimumStars)
            || invalidStars(query.MaximumStars)
            || query.MinimumStars is { } minimum && query.MaximumStars is { } maximum && minimum > maximum)
        {
            throw new ExternalLazerLibraryException("catalog_query_invalid", "The external-library catalog query is outside the supported bounds.");
        }

        return query with { SearchText = query.SearchText.Trim(), RulesetShortName = query.RulesetShortName.Trim() };
    }

    private static bool invalidStars(double? value) =>
        value is { } number && (!double.IsFinite(number) || number < 0 || number > ExternalLazerCatalogProtocol.MaximumStarRating);

    private static void validateSnapshot(LazerLibrarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Path.IsPathFullyQualified(snapshot.DatabasePath)
            || !File.Exists(snapshot.DatabasePath)
            || !string.Equals(Path.GetExtension(snapshot.DatabasePath), ".realm", StringComparison.OrdinalIgnoreCase)
            || (File.GetAttributes(snapshot.DatabasePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ExternalLazerLibraryException("snapshot_invalid", "The private lazer Realm snapshot is unavailable.");
        }
    }

    private static void checkRowLimit(int count)
    {
        if (count > ExternalLazerCatalogProtocol.MaximumSnapshotRows)
            throw new ExternalLazerLibraryException("catalog_too_large", "The lazer catalog exceeds AimMod's metadata row limit.");
    }

    private static string text(IRealmObjectBase? value, string property) =>
        value is null ? string.Empty : clamp(get<string>(value, property) ?? string.Empty, ExternalLazerCatalogProtocol.MaximumTextFieldLength);

    private static string clamp(string value, int maximumLength) => value.Length <= maximumLength ? value : value[..maximumLength];

    private static T get<T>(IRealmObjectBase? value, string property) =>
        value is null ? default! : value.DynamicApi.Get<T>(property);

    private static IRealmObjectBase? getObject(IRealmObjectBase? value, string property) =>
        value?.DynamicApi.Get<IRealmObjectBase?>(property);

    private static double finite(double value) => double.IsFinite(value) ? value : 0;

    private static double? finiteNullable(double? value) => value is { } number && double.IsFinite(number) ? number : null;

    private static float finite(float value) => float.IsFinite(value) ? value : 0;
}
