using System.Diagnostics;

namespace AimMod.Osu.Worker;

/// <summary>Private, leased scratch space. Never points at the user's osu! library.</summary>
internal sealed class ReplayWorkerStorage : IDisposable
{
    internal const long MaximumBytes = 256L * 1024 * 1024;
    internal const long MinimumFreeBytes = 2L * 1024 * 1024 * 1024;
    private const string run_prefix = "run-";
    private readonly FileStream lease;
    private readonly long maximumBytes;
    private readonly Func<long> freeBytes;
    private int limitExceeded;
    public string Root { get; }
    public string Name { get; } = run_prefix + Guid.NewGuid().ToString("N");
    public string DirectoryPath => Path.Combine(Root, Name);

    private ReplayWorkerStorage(string root, FileStream lease, long maximumBytes, Func<long> freeBytes)
    {
        Root = root;
        this.lease = lease;
        this.maximumBytes = maximumBytes;
        this.freeBytes = freeBytes;
    }

    public static ReplayWorkerStorage Acquire(CancellationToken token, string? root = null,
        long maximumBytes = MaximumBytes, Func<long>? freeBytes = null) =>
        Acquire(token, TimeSpan.FromSeconds(30), root, maximumBytes, freeBytes);

    internal static ReplayWorkerStorage Acquire(CancellationToken token, TimeSpan leaseWait, string? root = null,
        long maximumBytes = MaximumBytes, Func<long>? freeBytes = null)
    {
        bool defaultLocation = root is null;
        root = Path.GetFullPath(root ?? Path.Combine(Path.GetTempPath(), "AimMod", "replay-worker"));
        RejectLinks(root);
        Directory.CreateDirectory(root);
        freeBytes ??= () => new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
        string leasePath = Path.Combine(root, ".lease");
        RejectLinks(leasePath);
        FileStream lease;
        var waiting = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                break;
            }
            catch (IOException) when (waiting.Elapsed < leaseWait)
            {
                token.WaitHandle.WaitOne(50);
            }
            catch (IOException)
            {
                throw new ReplayAnalysisException("analysis_busy", "Another replay analysis is still using the scratch storage. Try again shortly.");
            }
        }

        var storage = new ReplayWorkerStorage(root, lease, maximumBytes, freeBytes);
        try
        {
            if (defaultLocation)
                tryCleanupLegacyRuns(Path.Combine(Path.GetTempPath(), "of-test-headless"), DateTime.UtcNow.AddDays(-1));
            // A single cross-process lease prevents pruning another live worker.
            // A crash releases the OS handle, so the next worker removes its files.
            foreach (string directory in Directory.EnumerateDirectories(root, run_prefix + "*"))
            {
                token.ThrowIfCancellationRequested();
                if (Guid.TryParseExact(Path.GetFileName(directory)[run_prefix.Length..], "N", out _))
                    DeleteOwnedTree(directory);
            }
            storage.ThrowIfLimitExceeded();
            Directory.CreateDirectory(storage.DirectoryPath);
            return storage;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>Removes temp and profile directories, which name the user, from error text.</summary>
    internal static string RedactLocalPaths(string message)
    {
        foreach ((string? path, string replacement) in new[]
                 {
                     (Path.GetTempPath(), "<temp>"),
                     (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<home>"),
                 })
        {
            string trimmed = string.IsNullOrEmpty(path) ? string.Empty : Path.TrimEndingDirectorySeparator(path);
            if (trimmed.Length > 3)
                message = message.Replace(trimmed, replacement, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        return message;
    }

    // Legacy cleanup is housekeeping and must never block a new analysis.
    private static void tryCleanupLegacyRuns(string root, DateTime olderThanUtc)
    {
        try { CleanupLegacyRuns(root, olderThanUtc); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine("[AimMod] Legacy replay scratch cleanup was skipped.");
        }
    }

    internal static void CleanupLegacyRuns(string root, DateTime olderThanUtc)
    {
        const string prefix = "aimmod-replay-analysis-";
        if (!Directory.Exists(root)) return;
        RejectLinks(root);
        foreach (string directory in Directory.EnumerateDirectories(root, prefix + "*"))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory)[prefix.Length..], "D", out _) ||
                Directory.GetLastWriteTimeUtc(directory) >= olderThanUtc) continue;
            try
            {
                // Old workers predate leases. Keep recent directories and any tree
                // containing recent writes; locked files are left for another run.
                if (LatestWrite(directory) < olderThanUtc) DeleteOwnedTree(directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("[AimMod] A legacy replay scratch directory could not be cleaned yet.");
            }
        }
    }

    private static DateTime LatestWrite(string directory)
    {
        RejectLinks(directory);
        DateTime latest = Directory.GetLastWriteTimeUtc(directory);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLinks(path);
            DateTime modified = Directory.Exists(path) ? LatestWrite(path) : File.GetLastWriteTimeUtc(path);
            if (modified > latest) latest = modified;
        }
        return latest;
    }

    public void ThrowIfLimitExceeded()
    {
        if (Volatile.Read(ref limitExceeded) != 0 || Measure(Root) > maximumBytes || freeBytes() < MinimumFreeBytes)
            throw new ReplayAnalysisException("analysis_storage_limit", "Replay analysis paused because its temporary storage limit was reached or disk space is low.");
    }

    public IDisposable Watch(Action stop) => new StorageWatch(this, stop);

    private sealed class StorageWatch : IDisposable
    {
        private readonly Timer timer;
        private int checking;

        public StorageWatch(ReplayWorkerStorage storage, Action stop)
        {
            timer = new Timer(_ =>
            {
                // A slow scan must not overlap the next tick, and nothing may escape the
                // timer callback because an unhandled exception there ends the worker.
                if (Interlocked.Exchange(ref checking, 1) != 0)
                    return;

                try { storage.ThrowIfLimitExceeded(); }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Interlocked.Exchange(ref storage.limitExceeded, 1);
                    try { stop(); }
                    catch (Exception stopError) when (stopError is not OutOfMemoryException) { }
                }
                finally { Volatile.Write(ref checking, 0); }
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        public void Dispose() => timer.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try { DeleteOwnedTree(DirectoryPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Do not turn a completed analysis into a failure. The next acquisition
            // must successfully remove these files before another host may start.
            Console.Error.WriteLine("[AimMod] Replay scratch cleanup deferred until the next analysis.");
        }
        finally { lease.Dispose(); }
    }

    internal static long Measure(string directory)
    {
        RejectLinks(directory);
        return measureTree(directory);
    }

    // The headless host creates and removes Realm sidecars while it runs, so entries that
    // vanish during the scan are skipped instead of being reported as a storage failure.
    private static long measureTree(string directory)
    {
        long total = 0;
        IEnumerable<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(directory).EnumerateFileSystemInfos(); }
        catch (DirectoryNotFoundException) { return 0; }

        try
        {
            foreach (FileSystemInfo entry in entries)
            {
                FileAttributes attributes;
                try { attributes = entry.Attributes; }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { continue; }

                if ((int)attributes == -1)
                    continue;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Replay scratch storage cannot use symbolic links or junctions.");

                long size;
                if ((attributes & FileAttributes.Directory) != 0)
                    size = measureTree(entry.FullName);
                else
                {
                    try { size = ((FileInfo)entry).Length; }
                    catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { continue; }
                }

                total = size > long.MaxValue - total ? long.MaxValue : total + size;
            }
        }
        catch (DirectoryNotFoundException)
        {
        }

        return total;
    }

    internal static void DeleteOwnedTree(string directory)
    {
        if (!Directory.Exists(directory)) return;
        RejectLinks(directory);
        // Explicit recursion rejects links instead of following user-controlled junctions.
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLinks(path);
            if (Directory.Exists(path)) DeleteOwnedTree(path);
            else
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        Directory.Delete(directory);
    }

    private static void RejectLinks(string path)
    {
        for (string? candidate = Path.GetFullPath(path); candidate is not null; candidate = Path.GetDirectoryName(candidate))
        {
            if ((File.Exists(candidate) || Directory.Exists(candidate)) &&
                (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Replay scratch storage cannot use symbolic links or junctions.");
        }
    }
}
