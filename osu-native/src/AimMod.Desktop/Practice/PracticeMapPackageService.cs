using System.IO.Compression;

namespace AimMod.Desktop.Practice;

public static class PracticeMapPackageService
{
    public static async Task<string> CreateAsync(
        PracticeMapExportResult export,
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(export.DirectoryPath));
        string archive = Path.GetFullPath(archivePath);
        if (!File.Exists(export.BeatmapPath) || !File.Exists(export.AudioPath))
            throw new InvalidDataException("The practice map export is incomplete.");
        if (archive.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The practice archive must be outside its source folder.");
        if (File.Exists(archive))
            throw new IOException("Practice-map packaging refuses to overwrite an existing archive.");

        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        string partial = Path.Combine(Path.GetDirectoryName(archive)!, $".{Path.GetFileName(archive)}.{Guid.NewGuid():N}.partial");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var stream = new FileStream(
                             partial,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             bufferSize: 81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                await addAsync(zip, export.BeatmapPath, cancellationToken).ConfigureAwait(false);
                await addAsync(zip, export.AudioPath, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, archive, overwrite: false);
            return archive;
        }
        catch
        {
            try { File.Delete(partial); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static async Task addAsync(ZipArchive archive, string path, CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(Path.GetFileName(path), CompressionLevel.Optimal);
        await using Stream source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using Stream destination = entry.Open();
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }
}
