using System.Globalization;
using System.Text;

namespace AimMod.Osu.Runtime;

public sealed class LazerSessionMonitor : IAsyncDisposable
{
    private const int default_maximum_file_bytes = 1024 * 1024;
    private const int maximum_username_characters = 256;
    private const int maximum_token_characters = 16 * 1024;
    private static readonly TimeSpan default_reconciliation_interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan minimum_access_token_lifetime = TimeSpan.FromSeconds(30);

    private readonly string gameIniPath;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan debounceDelay;
    private readonly TimeSpan reconciliationInterval;
    private readonly int maximumFileBytes;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object stateLock = new();
    private readonly HashSet<LazerAccessTokenLease> activeLeases = [];

    private FileSystemWatcher? watcher;
    private CancellationTokenSource? debounceCancellation;
    private CancellationTokenSource? reconciliationCancellation;
    private Task? debounceTask;
    private Task? reconciliationTask;
    private SessionSnapshot snapshot = SessionSnapshot.Unavailable;
    private LazerSessionState current = new(LazerSessionStatus.Unavailable, null, 0);
    private bool hasLoaded;
    private bool disposed;

    private LazerSessionMonitor(
        string gameIniPath,
        TimeProvider timeProvider,
        TimeSpan debounceDelay,
        TimeSpan reconciliationInterval,
        int maximumFileBytes)
    {
        this.gameIniPath = Path.GetFullPath(gameIniPath);
        this.timeProvider = timeProvider;
        this.debounceDelay = debounceDelay;
        this.reconciliationInterval = reconciliationInterval;
        this.maximumFileBytes = maximumFileBytes;
    }

    public LazerSessionState Current
    {
        get
        {
            lock (stateLock)
                return current;
        }
    }

    public event Action<LazerSessionState>? StateChanged;

    public static Task<LazerSessionMonitor> CreateAsync(string gameIniPath, CancellationToken cancellationToken = default) =>
        CreateAsync(
            gameIniPath,
            TimeProvider.System,
            TimeSpan.FromMilliseconds(150),
            default_reconciliation_interval,
            default_maximum_file_bytes,
            cancellationToken);

    internal static Task<LazerSessionMonitor> CreateAsync(
        string gameIniPath,
        TimeProvider timeProvider,
        TimeSpan debounceDelay,
        int maximumFileBytes,
        CancellationToken cancellationToken = default) =>
        CreateAsync(
            gameIniPath,
            timeProvider,
            debounceDelay,
            default_reconciliation_interval,
            maximumFileBytes,
            cancellationToken);

    internal static async Task<LazerSessionMonitor> CreateAsync(
        string gameIniPath,
        TimeProvider timeProvider,
        TimeSpan debounceDelay,
        TimeSpan reconciliationInterval,
        int maximumFileBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameIniPath);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (debounceDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounceDelay));
        if (reconciliationInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(reconciliationInterval));
        if (maximumFileBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));

        var monitor = new LazerSessionMonitor(gameIniPath, timeProvider, debounceDelay, reconciliationInterval, maximumFileBytes);

        try
        {
            monitor.tryStartWatcher();
            await monitor.RefreshAsync(cancellationToken);
            return monitor;
        }
        catch
        {
            await monitor.DisposeAsync();
            throw;
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        throwIfDisposed();
        tryStartWatcher();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await refreshGate.WaitAsync(linkedCancellation.Token);

        try
        {
            SessionSnapshot next = await readSnapshotAsync(linkedCancellation.Token);
            applySnapshot(next);
        }
        finally
        {
            refreshGate.Release();
        }
    }

    internal LazerAccessTokenLease? TryLeaseAccessToken()
    {
        lock (stateLock)
        {
            if (disposed || snapshot.Status != LazerSessionStatus.SignedIn || snapshot.AccessToken is null || snapshot.ExpiresAt is null)
                return null;
            if (snapshot.ExpiresAt <= timeProvider.GetUtcNow() + minimum_access_token_lifetime)
                return null;

            var lease = new LazerAccessTokenLease(
                snapshot.AccessToken,
                snapshot.ExpiresAt.Value - minimum_access_token_lifetime,
                current.Revision,
                timeProvider,
                isCurrentRevision,
                releaseLease);
            activeLeases.Add(lease);
            return lease;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? pendingDebounce;
        Task? pendingReconciliation;

        lock (stateLock)
        {
            if (disposed)
                return;

            disposed = true;
            lifetime.Cancel();
            watcher?.Dispose();
            watcher = null;
            debounceCancellation?.Cancel();
            reconciliationCancellation?.Cancel();
            pendingDebounce = debounceTask;
            pendingReconciliation = reconciliationTask;
            revokeActiveLeases();
            snapshot = SessionSnapshot.Unavailable;
            current = new LazerSessionState(LazerSessionStatus.Unavailable, null, current.Revision + 1);
        }

        await ignoreCancellationAsync(pendingDebounce);
        await ignoreCancellationAsync(pendingReconciliation);
        await refreshGate.WaitAsync();
        refreshGate.Release();

        debounceCancellation?.Dispose();
        reconciliationCancellation?.Dispose();
        refreshGate.Dispose();
        lifetime.Dispose();
    }

    private async Task<SessionSnapshot> readSnapshotAsync(CancellationToken cancellationToken)
    {
        byte[]? bytes = null;

        try
        {
            await using var stream = new FileStream(
                gameIniPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (stream.Length > maximumFileBytes)
                return SessionSnapshot.Unavailable;

            int initialBufferLength = (int)Math.Min(
                (long)maximumFileBytes + 1,
                Math.Max(4096L, stream.Length + 1));
            bytes = new byte[initialBufferLength];
            int bytesRead = 0;

            while (true)
            {
                if (bytesRead == bytes.Length)
                {
                    if (bytesRead > maximumFileBytes)
                        return SessionSnapshot.Unavailable;

                    int nextLength = (int)Math.Min((long)maximumFileBytes + 1, Math.Max((long)bytes.Length * 2, bytes.Length + 1L));
                    byte[] expanded = new byte[nextLength];
                    Buffer.BlockCopy(bytes, 0, expanded, 0, bytesRead);
                    Array.Clear(bytes);
                    bytes = expanded;
                }

                int read = await stream.ReadAsync(bytes.AsMemory(bytesRead, bytes.Length - bytesRead), cancellationToken);
                if (read == 0)
                    break;
                bytesRead += read;
            }

            if (bytesRead > maximumFileBytes)
                return SessionSnapshot.Unavailable;

            string contents = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes, 0, bytesRead);
            return parse(contents, timeProvider.GetUtcNow());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return SessionSnapshot.Unavailable;
        }
        finally
        {
            if (bytes is not null)
                Array.Clear(bytes);
        }
    }

    private static SessionSnapshot parse(string contents, DateTimeOffset now)
    {
        string? username = null;
        string? tokenValue = null;
        bool savePassword = true;

        foreach (string rawLine in contents.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] is '#' or ';' || line[0] == '[' && line[^1] == ']')
                continue;

            int separator = line.IndexOf('=');
            if (separator < 1)
                return SessionSnapshot.Unavailable;

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();

            if (key.Equals("Username", StringComparison.OrdinalIgnoreCase))
                username = value;
            else if (key.Equals("Token", StringComparison.OrdinalIgnoreCase))
                tokenValue = value;
            else if (key.Equals("SavePassword", StringComparison.OrdinalIgnoreCase) && !bool.TryParse(value, out savePassword))
                return SessionSnapshot.Unavailable;
        }

        if (username?.Length > maximum_username_characters || tokenValue?.Length > maximum_token_characters)
            return SessionSnapshot.Unavailable;

        username = string.IsNullOrWhiteSpace(username) ? null : username;

        if (!savePassword || string.IsNullOrEmpty(tokenValue))
            return new SessionSnapshot(LazerSessionStatus.SignedOut, username, null, null);

        int firstSeparator = tokenValue.IndexOf('|');
        int secondSeparator = firstSeparator < 0 ? -1 : tokenValue.IndexOf('|', firstSeparator + 1);
        if (firstSeparator <= 0 || secondSeparator <= firstSeparator + 1 || tokenValue.IndexOf('|', secondSeparator + 1) >= 0 ||
            !long.TryParse(tokenValue.AsSpan(firstSeparator + 1, secondSeparator - firstSeparator - 1), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out long expiryUnixSeconds))
        {
            return SessionSnapshot.Unavailable;
        }

        DateTimeOffset expiresAt;
        try
        {
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiryUnixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return SessionSnapshot.Unavailable;
        }

        if (username is null)
            return SessionSnapshot.Unavailable;

        // Keep the same safety window as lazer's OAuthToken.IsValid. A token in its final
        // thirty seconds is left for lazer to refresh and is never sent by AimMod.
        if (expiresAt > now + minimum_access_token_lifetime)
            return new SessionSnapshot(LazerSessionStatus.SignedIn, username, tokenValue[..firstSeparator], expiresAt);

        bool hasRefreshToken = secondSeparator < tokenValue.Length - 1;
        return new SessionSnapshot(hasRefreshToken ? LazerSessionStatus.Remembered : LazerSessionStatus.SignedOut, username, null, null);
    }

    private void applySnapshot(SessionSnapshot next)
    {
        LazerSessionState? changedState = null;

        lock (stateLock)
        {
            if (disposed)
                return;

            bool changed = !hasLoaded || !snapshot.Equals(next);
            hasLoaded = true;

            if (changed)
                revokeActiveLeases();

            snapshot = next;

            reconciliationCancellation?.Cancel();
            reconciliationCancellation?.Dispose();
            reconciliationCancellation = null;
            reconciliationTask = null;

            scheduleReconciliation(next);

            if (changed)
            {
                current = new LazerSessionState(next.Status, next.Username, current.Revision + 1);
                changedState = current;
            }
        }

        if (changedState is not null)
            notifyStateChanged(changedState);
    }

    private void scheduleReconciliation(SessionSnapshot next)
    {
        TimeSpan delay = reconciliationInterval;

        if (next.Status == LazerSessionStatus.SignedIn && next.ExpiresAt is not null)
        {
            TimeSpan untilSafetyWindow = next.ExpiresAt.Value - timeProvider.GetUtcNow() - minimum_access_token_lifetime
                                         + TimeSpan.FromMilliseconds(10);
            if (untilSafetyWindow < delay)
                delay = untilSafetyWindow;
        }

        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        reconciliationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        CancellationToken cancellationToken = reconciliationCancellation.Token;
        reconciliationTask = refreshAfterDelayAsync(delay, cancellationToken);
    }

    private void scheduleFileRefresh()
    {
        lock (stateLock)
        {
            if (disposed)
                return;

            debounceCancellation?.Cancel();
            debounceCancellation?.Dispose();
            debounceCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            debounceTask = refreshAfterDelayAsync(debounceDelay, debounceCancellation.Token);
        }
    }

    private async Task refreshAfterDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, timeProvider, cancellationToken);
            await RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void tryStartWatcher()
    {
        lock (stateLock)
        {
            if (disposed || watcher is not null)
                return;

            string? directory = Path.GetDirectoryName(gameIniPath);
            if (directory is null || !Directory.Exists(directory))
                return;

            watcher = new FileSystemWatcher(directory, Path.GetFileName(gameIniPath))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
            };
            watcher.Changed += onFileChanged;
            watcher.Created += onFileChanged;
            watcher.Deleted += onFileChanged;
            watcher.Renamed += onFileChanged;
            watcher.Error += onWatcherError;
            watcher.EnableRaisingEvents = true;
        }
    }

    private void onFileChanged(object sender, FileSystemEventArgs eventArgs) => scheduleFileRefresh();

    private void onWatcherError(object sender, ErrorEventArgs eventArgs) => scheduleFileRefresh();

    private bool isCurrentRevision(long revision)
    {
        lock (stateLock)
            return !disposed && current.Revision == revision && snapshot.Status == LazerSessionStatus.SignedIn;
    }

    private void releaseLease(LazerAccessTokenLease lease)
    {
        lock (stateLock)
            activeLeases.Remove(lease);
    }

    private void revokeActiveLeases()
    {
        foreach (LazerAccessTokenLease lease in activeLeases)
            lease.Revoke();

        activeLeases.Clear();
    }

    private void notifyStateChanged(LazerSessionState state)
    {
        foreach (Action<LazerSessionState> subscriber in StateChanged?.GetInvocationList().Cast<Action<LazerSessionState>>() ?? [])
        {
            try
            {
                subscriber(state);
            }
            catch
            {
                // Session monitoring must not fail because a UI observer failed.
            }
        }
    }

    private void throwIfDisposed()
    {
        lock (stateLock)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(LazerSessionMonitor));
        }
    }

    private static async Task ignoreCancellationAsync(Task? task)
    {
        if (task is null)
            return;

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed record SessionSnapshot(LazerSessionStatus Status, string? Username, string? AccessToken, DateTimeOffset? ExpiresAt)
    {
        public static SessionSnapshot Unavailable { get; } = new(LazerSessionStatus.Unavailable, null, null, null);

        public override string ToString() => $"{nameof(SessionSnapshot)} {{ Status = {Status}, Username = {Username}, ExpiresAt = {ExpiresAt} }}";
    }
}

internal sealed class LazerAccessTokenLease : IDisposable
{
    private readonly DateTimeOffset usableUntil;
    private readonly long revision;
    private readonly TimeProvider timeProvider;
    private readonly Func<long, bool> isCurrentRevision;
    private readonly Action<LazerAccessTokenLease> onDisposed;
    private string? accessToken;

    internal LazerAccessTokenLease(
        string accessToken,
        DateTimeOffset usableUntil,
        long revision,
        TimeProvider timeProvider,
        Func<long, bool> isCurrentRevision,
        Action<LazerAccessTokenLease> onDisposed)
    {
        this.accessToken = accessToken;
        this.usableUntil = usableUntil;
        this.revision = revision;
        this.timeProvider = timeProvider;
        this.isCurrentRevision = isCurrentRevision;
        this.onDisposed = onDisposed;
    }

    internal bool TryGetAccessToken(out string token)
    {
        string? currentToken = Volatile.Read(ref accessToken);
        if (currentToken is null || usableUntil <= timeProvider.GetUtcNow() || !isCurrentRevision(revision))
        {
            token = string.Empty;
            return false;
        }

        token = currentToken;
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref accessToken, null) is not null)
            onDisposed(this);
    }

    internal void Revoke() => Interlocked.Exchange(ref accessToken, null);

    public override string ToString() => nameof(LazerAccessTokenLease);
}
