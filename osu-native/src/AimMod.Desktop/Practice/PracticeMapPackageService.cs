using System.IO.Compression;
using System.Text;
using osu.Game.Utils;

namespace AimMod.Desktop.Practice;

public static class PracticeMapPackageService
{
    private const int copy_buffer_size = 81920;

    public static async Task<string> CreateAsync(
        PracticeMapExportResult export,
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(export.DirectoryPath));
        string archive = Path.GetFullPath(archivePath);
        if (!string.Equals(Path.GetExtension(archive), ".osz", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Practice maps must be packaged as an .osz archive.");
        if (!File.Exists(export.BeatmapPath) || !File.Exists(export.AudioPath)
            || new FileInfo(export.BeatmapPath).Length == 0 || new FileInfo(export.AudioPath).Length == 0)
            throw new InvalidDataException("The practice map export is incomplete.");
        PracticeSourceBeatmap beatmap = OsuPracticeBeatmapReader.Read(export.BeatmapPath);
        if (!string.Equals(beatmap.Metadata.AudioFilename, Path.GetFileName(export.AudioPath), StringComparison.Ordinal))
            throw new InvalidDataException("The practice beatmap does not reference its packaged audio file.");
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
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: copy_buffer_size,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8))
                {
                    await addAsync(zip, export.BeatmapPath, cancellationToken).ConfigureAwait(false);
                    await addAsync(zip, export.AudioPath, cancellationToken).ConfigureAwait(false);
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            validateLazerArchive(partial, export);
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
        entry.ExternalAttributes = (int)FileAttributes.Normal;
        await using Stream source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: copy_buffer_size,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using Stream destination = entry.Open();
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static void validateLazerArchive(string path, PracticeMapExportResult export)
    {
        if (!ZipUtils.IsZipArchive(path))
            throw new InvalidDataException("The generated practice map is not a lazer-compatible ZIP archive.");

        using ZipArchive zip = ZipFile.OpenRead(path);
        string beatmapName = Path.GetFileName(export.BeatmapPath);
        string audioName = Path.GetFileName(export.AudioPath);
        string[] expected = [beatmapName, audioName];
        string[] entries = zip.Entries.Select(entry => entry.FullName).ToArray();
        if (entries.Length != expected.Length
            || entries.Any(entry => entry.Contains('/') || entry.Contains('\\'))
            || !entries.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(expected.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The generated practice map archive has an invalid root structure.");

        ZipArchiveEntry beatmap = zip.GetEntry(beatmapName)
                                  ?? throw new InvalidDataException("The generated practice map archive is missing its beatmap.");
        using var reader = new StreamReader(beatmap.Open(), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        string content = reader.ReadToEnd();
        if (!content.StartsWith("osu file format v", StringComparison.Ordinal)
            || !content.Contains($"AudioFilename:{audioName}", StringComparison.Ordinal)
            || !content.Contains("[HitObjects]", StringComparison.Ordinal))
            throw new InvalidDataException("The generated practice map archive contains an invalid beatmap.");
    }
}
