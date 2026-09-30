using AimMod.Osu.Runtime;

namespace AimMod.Osu.Worker;

/// <summary>
/// Reuses one private Realm snapshot while client.realm is unchanged (same length and
/// modification time) and the snapshot is young. Consumers lease the snapshot through the
/// normal create/delete calls; the copy is removed once it is stale and unleased, or when
/// the cache is disposed.
/// </summary>
internal sealed class CachedLazerLibrarySnapshotFactory : ILazerLibrarySnapshotFactory, IDisposable
{
    private static readonly object sharedLock = new();
    private static CachedLazerLibrarySnapshotFactory? shared;

    private readonly ILazerLibrarySnapshotFactory inner;
    private readonly TimeSpan maximumAge;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object stateLock = new();
    private readonly List<Entry> entries = new();
    private bool disposed;

    public CachedLazerLibrarySnapshotFactory(
        ILazerLibrarySnapshotFactory inner,
        TimeSpan maximumAge,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumAge, TimeSpan.Zero);
        this.inner = inner;
        this.maximumAge = maximumAge;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static CachedLazerLibrarySnapshotFactory Shared
    {
        get
        {
            lock (sharedLock)
                return shared ??= new CachedLazerLibrarySnapshotFactory(new RealmLazerLibrarySnapshotFactory(), TimeSpan.FromSeconds(30));
        }
    }

    public static void DisposeShared()
    {
        CachedLazerLibrarySnapshotFactory? toDispose;
        lock (sharedLock)
        {
            toDispose = shared;
            shared = null;
        }

        toDispose?.Dispose();
    }

    public async Task<LazerLibrarySnapshot> CreateSnapshotAsync(
        ValidatedExternalLazerLibraryLocation location,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);

        if (!tryReadKey(location.DatabasePath, out SnapshotKey key))
            return await inner.CreateSnapshotAsync(location, cancellationToken).ConfigureAwait(false);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            DateTimeOffset now = timeProvider.GetUtcNow();

            lock (stateLock)
            {
                Entry? current = entries.FirstOrDefault(entry => !entry.Retired);
                if (current is not null)
                {
                    if (current.Key == key && now - current.CreatedAt < maximumAge && File.Exists(current.Snapshot.DatabasePath))
                    {
                        current.Leases++;
                        return current.Snapshot;
                    }

                    current.Retired = true;
                }
            }

            removeUnleasedRetired();

            string directory = AimModTempDirectories.Create(AimModTempDirectories.SnapshotPrefix);
            LazerLibrarySnapshot snapshot;
            try
            {
                snapshot = await inner.CreateSnapshotAsync(location with { SnapshotDirectory = directory }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                AimModTempDirectories.TryDelete(directory, AimModTempDirectories.SnapshotPrefix);
                throw;
            }

            lock (stateLock)
                entries.Add(new Entry(key, snapshot, directory, now) { Leases = 1 });
            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DeleteSnapshotAsync(LazerLibrarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Entry? released = null;
        bool owned = false;
        lock (stateLock)
        {
            Entry? entry = entries.FirstOrDefault(candidate => candidate.Snapshot.SnapshotId == snapshot.SnapshotId);
            if (entry is not null)
            {
                owned = true;
                entry.Leases = Math.Max(0, entry.Leases - 1);
                if (entry.Retired && entry.Leases == 0)
                {
                    entries.Remove(entry);
                    released = entry;
                }
            }
        }

        if (!owned)
        {
            await inner.DeleteSnapshotAsync(snapshot).ConfigureAwait(false);
            return;
        }

        if (released is not null)
            await disposeEntryAsync(released).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Entry[] remaining;
        lock (stateLock)
        {
            if (disposed)
                return;

            disposed = true;
            remaining = entries.ToArray();
            entries.Clear();
        }

        foreach (Entry entry in remaining)
            disposeEntryAsync(entry).AsTask().GetAwaiter().GetResult();
    }

    private void removeUnleasedRetired()
    {
        Entry[] released;
        lock (stateLock)
        {
            released = entries.Where(entry => entry.Retired && entry.Leases == 0).ToArray();
            foreach (Entry entry in released)
                entries.Remove(entry);
        }

        foreach (Entry entry in released)
            disposeEntryAsync(entry).AsTask().GetAwaiter().GetResult();
    }

    private async ValueTask disposeEntryAsync(Entry entry)
    {
        try
        {
            await inner.DeleteSnapshotAsync(entry.Snapshot).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }

        AimModTempDirectories.TryDelete(entry.Directory, AimModTempDirectories.SnapshotPrefix);
    }

    private static bool tryReadKey(string databasePath, out SnapshotKey key)
    {
        try
        {
            var info = new FileInfo(databasePath);
            if (info.Exists)
            {
                key = new SnapshotKey(Path.GetFullPath(databasePath), info.LastWriteTimeUtc.Ticks, info.Length);
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        key = default;
        return false;
    }

    private readonly record struct SnapshotKey(string DatabasePath, long LastWriteTicks, long Length);

    private sealed class Entry(SnapshotKey key, LazerLibrarySnapshot snapshot, string directory, DateTimeOffset createdAt)
    {
        public SnapshotKey Key { get; } = key;
        public LazerLibrarySnapshot Snapshot { get; } = snapshot;
        public string Directory { get; } = directory;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public int Leases { get; set; }
        public bool Retired { get; set; }
    }
}
