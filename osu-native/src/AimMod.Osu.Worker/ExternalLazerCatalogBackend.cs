using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using Realms.Exceptions;

namespace AimMod.Osu.Worker;

internal interface IExternalLazerCatalogBackend
{
    ValueTask<ExternalLazerCatalogSearchResult> SearchAsync(
        ExternalLazerCatalogSearchRequest request,
        CancellationToken cancellationToken);
}

internal sealed class ExternalLazerCatalogBackend : IExternalLazerCatalogBackend
{
    private readonly ILazerLibrarySnapshotFactory snapshotFactory;
    private readonly ILazerLibraryCatalogReader catalogReader;
    private readonly ExternalLazerLibraryValidator validator;

    public ExternalLazerCatalogBackend()
        : this(new RealmLazerLibrarySnapshotFactory(), new DynamicRealmLazerLibraryCatalogReader(), new ExternalLazerLibraryValidator())
    {
    }

    internal ExternalLazerCatalogBackend(
        ILazerLibrarySnapshotFactory snapshotFactory,
        ILazerLibraryCatalogReader catalogReader,
        ExternalLazerLibraryValidator validator)
    {
        this.snapshotFactory = snapshotFactory;
        this.catalogReader = catalogReader;
        this.validator = validator;
    }

    public async ValueTask<ExternalLazerCatalogSearchResult> SearchAsync(
        ExternalLazerCatalogSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = DynamicRealmLazerLibraryCatalogReader.ValidateQuery(request);

        string snapshotDirectory = Directory.CreateTempSubdirectory("aimmod-lazer-catalog-").FullName;
        LazerLibrarySnapshot? snapshot = null;
        try
        {
            ValidatedExternalLazerLibraryLocation location = validator.Validate(
                new ExternalLazerLibraryLocation(request.LibraryRoot, snapshotDirectory));
            snapshot = await snapshotFactory.CreateSnapshotAsync(location, cancellationToken).ConfigureAwait(false);
            return await catalogReader.ReadCatalogAsync(snapshot, request, cancellationToken).ConfigureAwait(false);
        }
        catch (ExternalLazerLibraryException exception)
        {
            throw new RuntimeCommandException(exception.Code, exception.Message);
        }
        catch (RealmException)
        {
            throw new RuntimeCommandException("catalog_read_failed", "AimMod could not read the private lazer catalog snapshot.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RuntimeCommandException("catalog_read_failed", "AimMod could not read the private lazer catalog snapshot.");
        }
        finally
        {
            if (snapshot is not null)
            {
                try
                {
                    await snapshotFactory.DeleteSnapshotAsync(snapshot).ConfigureAwait(false);
                }
                catch (ExternalLazerLibraryException exception)
                {
                    throw new RuntimeCommandException(exception.Code, exception.Message);
                }
            }

            deleteOwnedSnapshotDirectory(snapshotDirectory);
        }
    }

    private static void deleteOwnedSnapshotDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || !Path.GetFileName(path).StartsWith("aimmod-lazer-catalog-", StringComparison.Ordinal))
        {
            throw new RuntimeCommandException("snapshot_cleanup_failed", "AimMod refused to clean an unrecognised catalog snapshot directory.");
        }

        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RuntimeCommandException("snapshot_cleanup_failed", "AimMod could not remove its private catalog snapshot directory.");
        }
    }
}
