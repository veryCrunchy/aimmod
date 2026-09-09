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
            catch (IOException) when (waiting.Elapsed < TimeSpan.FromSeconds(30))
            {
                token.WaitHandle.WaitOne(50);
            }
        }

        var storage = new ReplayWorkerStorage(root, lease, maximumBytes, freeBytes);
        try
        {
            if (defaultLocation)
                CleanupLegacyRuns(Path.Combine(Path.GetTempPath(), "of-test-headless"), DateTime.UtcNow.AddDays(-1));
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
        public StorageWatch(ReplayWorkerStorage storage, Action stop)
        {
            timer = new Timer(_ =>
            {
                try { storage.ThrowIfLimitExceeded(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ReplayAnalysisException)
                {
                    Interlocked.Exchange(ref storage.limitExceeded, 1);
                    try { stop(); }
                    catch (ObjectDisposedException) { }
                }
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

    private static long Measure(string directory)
    {
        RejectLinks(directory);
        long total = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLinks(path);
            total = checked(total + (Directory.Exists(path) ? Measure(path) : new FileInfo(path).Length));
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
