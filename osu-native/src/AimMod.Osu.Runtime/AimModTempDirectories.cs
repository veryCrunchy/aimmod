namespace AimMod.Osu.Runtime;

/// <summary>
/// Creates, removes and sweeps the private temporary directories that AimMod's lazer
/// snapshot and asset staging code leaves in the system temp directory. Every member is
/// best-effort: cleanup never throws and never replaces the result of the work it follows.
/// </summary>
public static class AimModTempDirectories
{
    public const string SnapshotPrefix = "aimmod-lazer-snapshot-";
    public const string CatalogPrefix = "aimmod-lazer-catalog-";
    public const string SkinsPrefix = "aimmod-lazer-skins-";
    public const string AssetsPrefix = "aimmod-lazer-assets-";

    public static readonly TimeSpan DefaultStaleAge = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Asset staging leases can back an open replay for a long session, and a restarted worker
    /// in the same session sweeps too, so their directories are only swept when much older.
    /// </summary>
    public static readonly TimeSpan AssetsStaleAge = TimeSpan.FromHours(12);

    private static readonly string[] sweptPrefixes = [SnapshotPrefix, CatalogPrefix, SkinsPrefix, AssetsPrefix];
    private static int sweepStarted;

    public static string Create(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        EnsureSwept();
        return Directory.CreateTempSubdirectory(prefix).FullName;
    }

    /// <summary>
    /// Starts a one-time background sweep of stale directories for this process.
    /// </summary>
    public static void EnsureSwept()
    {
        if (Interlocked.Exchange(ref sweepStarted, 1) != 0)
            return;

        _ = Task.Run(() => SweepStale());
    }

    public static bool TryDelete(string? path, string prefix)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;

        try
        {
            if (!Directory.Exists(path))
                return true;
            if (!Path.GetFileName(Path.TrimEndingDirectorySeparator(path)).StartsWith(prefix, StringComparison.Ordinal)
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            deleteTree(path);
            return !Directory.Exists(path);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes managed directories under <paramref name="root"/> that have not been modified
    /// for <paramref name="maximumAge"/> (by default <see cref="DefaultStaleAge"/>, or
    /// <see cref="AssetsStaleAge"/> for asset staging). Directories still held open by another
    /// process are left for a later sweep.
    /// </summary>
    public static int SweepStale(TimeSpan? maximumAge = null, string? root = null, DateTime? utcNow = null)
    {
        int removed = 0;
        try
        {
            string searchRoot = root ?? Path.GetTempPath();
            DateTime now = utcNow ?? DateTime.UtcNow;
            foreach (string prefix in sweptPrefixes)
            {
                DateTime threshold = now - (maximumAge ?? (prefix == AssetsPrefix ? AssetsStaleAge : DefaultStaleAge));
                foreach (string directory in Directory.EnumerateDirectories(searchRoot, prefix + "*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(directory) >= threshold)
                            continue;
                        if (TryDelete(directory, prefix))
                            removed++;
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }

        return removed;
    }

    private static void deleteTree(string directory)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0)
                    Directory.Delete(entry, recursive: false);
                else
                    File.Delete(entry);
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                deleteTree(entry);
            }
            else
            {
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(entry, FileAttributes.Normal);
                File.Delete(entry);
            }
        }

        Directory.Delete(directory, recursive: false);
    }
}
