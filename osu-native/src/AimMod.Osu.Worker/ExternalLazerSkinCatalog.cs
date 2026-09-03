using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using Realms;
using Realms.Exceptions;

namespace AimMod.Osu.Worker;

internal interface IExternalLazerSkinCatalogBackend
{
    ValueTask<ExternalLazerSkinCatalogSearchResult> SearchAsync(
        ExternalLazerSkinCatalogSearchRequest request,
        CancellationToken cancellationToken);
}

internal sealed class ExternalLazerSkinCatalogBackend : IExternalLazerSkinCatalogBackend
{
    private readonly ILazerLibrarySnapshotFactory snapshotFactory;
    private readonly ILazerSkinCatalogReader reader;
    private readonly ExternalLazerLibraryValidator validator;

    public ExternalLazerSkinCatalogBackend()
        : this(new RealmLazerLibrarySnapshotFactory(), new DynamicRealmLazerSkinCatalogReader(), new ExternalLazerLibraryValidator())
    {
    }

    internal ExternalLazerSkinCatalogBackend(
        ILazerLibrarySnapshotFactory snapshotFactory,
        ILazerSkinCatalogReader reader,
        ExternalLazerLibraryValidator validator)
    {
        this.snapshotFactory = snapshotFactory;
        this.reader = reader;
        this.validator = validator;
    }

    public async ValueTask<ExternalLazerSkinCatalogSearchResult> SearchAsync(
        ExternalLazerSkinCatalogSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = DynamicRealmLazerSkinCatalogReader.ValidateQuery(request);

        string snapshotDirectory = Directory.CreateTempSubdirectory("aimmod-lazer-skins-").FullName;
        LazerLibrarySnapshot? snapshot = null;
        try
        {
            ValidatedExternalLazerLibraryLocation location = validator.Validate(
                new ExternalLazerLibraryLocation(request.LibraryRoot, snapshotDirectory));
            snapshot = await snapshotFactory.CreateSnapshotAsync(location, cancellationToken).ConfigureAwait(false);
            return await reader.ReadCatalogAsync(snapshot, request, cancellationToken).ConfigureAwait(false);
        }
        catch (ExternalLazerLibraryException exception)
        {
            throw new RuntimeCommandException(exception.Code, exception.Message);
        }
        catch (RealmException)
        {
            throw new RuntimeCommandException("skin_catalog_read_failed", "AimMod could not read the private lazer skin catalog snapshot.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RuntimeCommandException("skin_catalog_read_failed", "AimMod could not read the private lazer skin catalog snapshot.");
        }
        finally
        {
            if (snapshot is not null)
                await snapshotFactory.DeleteSnapshotAsync(snapshot).ConfigureAwait(false);

            deleteOwnedSnapshotDirectory(snapshotDirectory);
        }
    }

    private static void deleteOwnedSnapshotDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || !Path.GetFileName(path).StartsWith("aimmod-lazer-skins-", StringComparison.Ordinal))
        {
            throw new RuntimeCommandException("snapshot_cleanup_failed", "AimMod refused to clean an unrecognised skin snapshot directory.");
        }

        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RuntimeCommandException("snapshot_cleanup_failed", "AimMod could not remove its private skin snapshot directory.");
        }
    }
}

public sealed class DynamicRealmLazerSkinCatalogReader : ILazerSkinCatalogReader
{
    private static readonly string[] preview_names =
    {
        "menu-background@2x.png",
        "menu-background@2x.jpg",
        "menu-background@2x.jpeg",
        "menu-background.png",
        "menu-background.jpg",
        "menu-background.jpeg",
    };

    public Task<ExternalLazerSkinCatalogSearchResult> ReadCatalogAsync(
        LazerLibrarySnapshot snapshot,
        ExternalLazerSkinCatalogSearchRequest query,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => readCatalog(snapshot, ValidateQuery(query), cancellationToken), cancellationToken);

    internal static ExternalLazerSkinCatalogSearchRequest ValidateQuery(ExternalLazerSkinCatalogSearchRequest query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.SearchText is null
            || query.SearchText.Length > ExternalLazerSkinProtocol.MaximumSearchTextLength
            || query.Offset is < 0 or > ExternalLazerSkinProtocol.MaximumOffset
            || query.Limit is < 1 or > ExternalLazerSkinProtocol.MaximumPageSize
            || query.SkinId == Guid.Empty)
        {
            throw new ExternalLazerLibraryException("skin_catalog_query_invalid", "The installed-skin query is outside the supported bounds.");
        }

        return query with { SearchText = query.SearchText.Trim() };
    }

    private static ExternalLazerSkinCatalogSearchResult readCatalog(
        LazerLibrarySnapshot snapshot,
        ExternalLazerSkinCatalogSearchRequest query,
        CancellationToken cancellationToken)
    {
        validateSnapshot(snapshot);
        var configuration = new RealmConfiguration(snapshot.DatabasePath)
        {
            IsDynamic = true,
            IsReadOnly = true,
            SchemaVersion = RealmLazerLibrarySnapshotFactory.SupportedSchemaVersion,
        };

        var skins = new List<ExternalLazerSkinSummary>();
        using Realm realm = Realm.GetInstance(configuration);
        int scanned = 0;
        foreach (IRealmObject skin in realm.DynamicApi.All("Skin"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++scanned > ExternalLazerSkinProtocol.MaximumSkins)
                throw new ExternalLazerLibraryException("skin_catalog_too_large", "The lazer skin catalog exceeds AimMod's row limit.");
            if (get<bool>(skin, "DeletePending"))
                continue;
            Guid skinId = get<Guid>(skin, "ID");
            if (query.SkinId is { } requestedSkinId && skinId != requestedSkinId)
                continue;

            string name = text(skin, "Name");
            string creator = text(skin, "Creator");
            if (!matchesSearch(query.SearchText, name, creator))
                continue;

            (int fileCount, string previewHash, string previewLogicalName) = readFiles(skin);
            skins.Add(new ExternalLazerSkinSummary(
                skinId,
                name.Length == 0 ? "Unnamed skin" : name,
                creator,
                normaliseOptionalHash(text(skin, "Hash")),
                get<bool>(skin, "Protected"),
                fileCount,
                previewHash,
                previewLogicalName));
        }

        ExternalLazerSkinSummary[] ordered = skins
            .OrderByDescending(skin => skin.IsBuiltIn)
            .ThenBy(skin => skin.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(skin => skin.Creator, StringComparer.OrdinalIgnoreCase)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToArray();
        return new ExternalLazerSkinCatalogSearchResult(ordered, skins.Count, query.Offset, query.Limit);
    }

    private static (int FileCount, string PreviewHash, string PreviewLogicalName) readFiles(IRealmObject skin)
    {
        int count = 0;
        var previews = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (IEmbeddedObject usage in skin.DynamicApi.GetList<IEmbeddedObject>("Files"))
        {
            if (++count > ExternalLazerSkinProtocol.MaximumFilesPerSkin)
                throw new ExternalLazerLibraryException("skin_too_large", "An installed skin contains too many files to display safely.");

            string logicalName = text(usage, "Filename");
            string hash = getObject(usage, "File") is { } file ? normaliseOptionalHash(text(file, "Hash")) : string.Empty;
            if (hash.Length == 64)
                previews.TryAdd(logicalName.Replace('\\', '/'), hash);
        }

        foreach (string candidate in preview_names)
        {
            KeyValuePair<string, string>? match = previews.FirstOrDefault(entry =>
                string.Equals(entry.Key, candidate, StringComparison.OrdinalIgnoreCase)
                || entry.Key.EndsWith('/' + candidate, StringComparison.OrdinalIgnoreCase));
            if (match is { Value.Length: 64 } found)
                return (count, found.Value, found.Key);
        }

        return (count, string.Empty, string.Empty);
    }

    private static bool matchesSearch(string query, string name, string creator)
    {
        if (query.Length == 0)
            return true;
        string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(term => name.Contains(term, StringComparison.OrdinalIgnoreCase)
                                 || creator.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string normaliseOptionalHash(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : string.Empty;

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

    private static string text(IRealmObjectBase value, string property)
    {
        string text = get<string>(value, property) ?? string.Empty;
        return text.Length <= ExternalLazerSkinProtocol.MaximumTextFieldLength
            ? text
            : text[..ExternalLazerSkinProtocol.MaximumTextFieldLength];
    }

    private static T get<T>(IRealmObjectBase value, string property) => value.DynamicApi.Get<T>(property);

    private static IRealmObjectBase? getObject(IRealmObjectBase value, string property) =>
        value.DynamicApi.Get<IRealmObjectBase?>(property);
}
