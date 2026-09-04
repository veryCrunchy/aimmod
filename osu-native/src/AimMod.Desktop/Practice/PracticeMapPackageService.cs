using System.IO.Compression;

namespace AimMod.Desktop.Practice;

public static class PracticeMapPackageService
{
    public static string Create(PracticeMapExportResult export, string archivePath)
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
        try
        {
            using var stream = new FileStream(archive, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
            add(zip, export.BeatmapPath);
            add(zip, export.AudioPath);
            return archive;
        }
        catch
        {
            try { File.Delete(archive); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void add(ZipArchive archive, string path)
    {
        ZipArchiveEntry entry = archive.CreateEntry(Path.GetFileName(path), CompressionLevel.Optimal);
        using Stream source = File.OpenRead(path);
        using Stream destination = entry.Open();
        source.CopyTo(destination);
    }
}
