namespace AimMod.Desktop;

/// <summary>Evicts reproducible hash-named cache files. Never accepts directories as entries.</summary>
internal static class DiskCacheBudget
{
    private static readonly object gate = new();

    public static void Trim(string directory, string extension, int maximumCount, long maximumBytes,
        TimeSpan lifetime, string preservedPath)
    {
        lock (gate)
        {
            for (string? ancestor = Path.GetFullPath(directory); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
                if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Cache directory cannot use symbolic links.");
            var entries = new DirectoryInfo(directory).EnumerateFiles("*" + extension)
                .Where(file => Path.GetFileNameWithoutExtension(file.Name) is { Length: 64 } hash &&
                               hash.All(char.IsAsciiHexDigit) && (file.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderBy(file => file.LastWriteTimeUtc).ToList();
            long bytes = entries.Sum(file => file.Length);
            int count = entries.Count;
            DateTime cutoff = DateTime.UtcNow - lifetime;
            foreach (FileInfo file in entries)
            {
                if (string.Equals(file.FullName, Path.GetFullPath(preservedPath), StringComparison.OrdinalIgnoreCase)) continue;
                if (file.LastWriteTimeUtc >= cutoff && count <= maximumCount && bytes <= maximumBytes) continue;
                long length = file.Length;
                try { file.Delete(); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                bytes -= length;
                count--;
            }
            if (bytes > maximumBytes || count > maximumCount)
            {
                // A locked old entry must not allow every subsequent write to add
                // more bytes. Reject the newly published, reproducible cache item.
                FileInfo? added = entries.FirstOrDefault(file => string.Equals(file.FullName,
                    Path.GetFullPath(preservedPath), StringComparison.OrdinalIgnoreCase));
                added?.Delete();
                throw new IOException("Cache could not be kept within its storage budget.");
            }
        }
    }
}
