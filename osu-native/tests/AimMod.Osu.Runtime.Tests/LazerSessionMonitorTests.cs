using System.Text.Json;
using System.Reflection;
using NUnit.Framework;

namespace AimMod.Osu.Runtime.Tests;

[TestFixture]
public sealed class LazerSessionMonitorTests
{
    private string temporaryDirectory = null!;
    private string gameIniPath = null!;
    private ManualTimeProvider timeProvider = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-session-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        gameIniPath = Path.Combine(temporaryDirectory, "game.ini");
        timeProvider = new ManualTimeProvider(DateTimeOffset.FromUnixTimeSeconds(2_000_000_000));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, true);
    }

    [Test]
    public async Task MissingFileIsUnavailable()
    {
        await using LazerSessionMonitor monitor = await createMonitorAsync();

        Assert.Multiple(() =>
        {
            Assert.That(monitor.Current.Status, Is.EqualTo(LazerSessionStatus.Unavailable));
            Assert.That(monitor.Current.Username, Is.Null);
            Assert.That(monitor.Current.Revision, Is.EqualTo(1));
            Assert.That(monitor.TryLeaseAccessToken(), Is.Null);
        });
    }

    [Test]
    public async Task EmptyCredentialsAreSignedOut()
    {
        await File.WriteAllTextAsync(gameIniPath, "Username =\nToken =\n");

        await using LazerSessionMonitor monitor = await createMonitorAsync();

        Assert.That(monitor.Current, Is.EqualTo(new LazerSessionState(LazerSessionStatus.SignedOut, null, 1)));
    }

    [Test]
    public async Task LogoutMayRetainUsernameButIsSignedOut()
    {
        await File.WriteAllTextAsync(gameIniPath, "Username = crunchy\nToken =\n");

        await using LazerSessionMonitor monitor = await createMonitorAsync();

        Assert.Multiple(() =>
        {
            Assert.That(monitor.Current.Status, Is.EqualTo(LazerSessionStatus.SignedOut));
            Assert.That(monitor.Current.Username, Is.EqualTo("crunchy"));
            Assert.That(monitor.TryLeaseAccessToken(), Is.Null);
        });
    }

    [Test]
    public async Task SavePasswordFalseRejectsAStaleToken()
    {
        await File.WriteAllTextAsync(gameIniPath,
            tokenContents("crunchy", "stale-access", timeProvider.GetUtcNow().AddHours(1)) + "SavePassword = False\n");

        await using LazerSessionMonitor monitor = await createMonitorAsync();

        Assert.Multiple(() =>
        {
            Assert.That(monitor.Current.Status, Is.EqualTo(LazerSessionStatus.SignedOut));
            Assert.That(monitor.Current.Username, Is.EqualTo("crunchy"));
            Assert.That(monitor.TryLeaseAccessToken(), Is.Null);
            Assert.That(monitor.Current.ToString(), Does.Not.Contain("stale-access"));
        });
    }

    [Test]
    public async Task SignedInStateKeepsTokenOutOfPublicModelAndSerialisation()
    {
        const string secret = "access-secret-value";
        await writeSessionAsync("crunchy", secret, timeProvider.GetUtcNow().AddHours(1), "refresh-secret-value");

        await using LazerSessionMonitor monitor = await createMonitorAsync();
        using LazerAccessTokenLease? lease = monitor.TryLeaseAccessToken();
        string publicJson = JsonSerializer.Serialize(monitor.Current);
        string publicMonitorJson = JsonSerializer.Serialize(monitor);

        Assert.Multiple(() =>
        {
            Assert.That(monitor.Current.Status, Is.EqualTo(LazerSessionStatus.SignedIn));
            Assert.That(monitor.Current.Username, Is.EqualTo("crunchy"));
            Assert.That(publicJson, Does.Not.Contain(secret));
            Assert.That(publicJson, Does.Not.Contain("refresh-secret-value"));
            Assert.That(publicMonitorJson, Does.Not.Contain(secret));
            Assert.That(publicMonitorJson, Does.Not.Contain("refresh-secret-value"));
            Assert.That(monitor.Current.ToString(), Does.Not.Contain(secret));
            Assert.That(lease, Is.Not.Null);
            Assert.That(lease!.TryGetAccessToken(out string leasedToken), Is.True);
            Assert.That(leasedToken, Is.EqualTo(secret));
            Assert.That(lease.ToString(), Does.Not.Contain(secret));
            Assert.That(
                typeof(LazerSessionState).GetMembers(BindingFlags.Instance | BindingFlags.Public).Any(member => member.Name.Contains("Token", StringComparison.Ordinal)),
                Is.False);
            Assert.That(
                typeof(LazerSessionMonitor).GetMembers(BindingFlags.Instance | BindingFlags.Public).Any(member => member.Name.Contains("Token", StringComparison.Ordinal)),
                Is.False);
        });
    }

    [Test]
    public async Task ExpiredTokenRemainsRememberedAndIsNotRefreshed()
    {
        const string contents = "Username = crunchy\nToken = expired-access|1999999999|refresh-secret\n";
        await File.WriteAllTextAsync(gameIniPath, contents);

        await using LazerSessionMonitor monitor = await createMonitorAsync();

        Assert.Multiple(() =>
        {
            Assert.That(monitor.Current.Status, Is.EqualTo(LazerSessionStatus.Remembered));
            Assert.That(monitor.TryLeaseAccessToken(), Is.Null);
            Assert.That(File.ReadAllText(gameIniPath), Is.EqualTo(contents));
        });
    }

    [Test]
    public async Task TokenInLazersFinalThirtySecondSafetyWindowIsNeverLeased()
    {
        await writeSessionAsync("crunchy", "nearly-expired-access", timeProvider.GetUtcNow().AddSeconds(30));

        await using LazerSessionMonitor monitor = await createMonitorAsync();

        Assert.Multiple(() =>
        {
            Assert.That(monitor.Current.Status, Is.EqualTo(LazerSessionStatus.Remembered));
            Assert.That(monitor.Current.Username, Is.EqualTo("crunchy"));
            Assert.That(monitor.TryLeaseAccessToken(), Is.Null);
        });
    }

    [Test]
    public async Task ExistingLeaseStopsAtStartOfLazersSafetyWindow()
    {
        await writeSessionAsync("crunchy", "short-access", timeProvider.GetUtcNow().AddMinutes(1));
        await using LazerSessionMonitor monitor = await createMonitorAsync();
        using LazerAccessTokenLease? lease = monitor.TryLeaseAccessToken();

        timeProvider.Advance(TimeSpan.FromSeconds(31));

        Assert.Multiple(() =>
        {
            Assert.That(lease, Is.Not.Null);
            Assert.That(lease!.TryGetAccessToken(out _), Is.False);
        });
    }

    [Test]
    public async Task PeriodicReconciliationFindsSessionWhenWatcherCouldNotStart()
    {
        string lateDirectory = Path.Combine(temporaryDirectory, "late-lazer-root");
        gameIniPath = Path.Combine(lateDirectory, "game.ini");

        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(
            gameIniPath,
            TimeProvider.System,
            TimeSpan.FromHours(1),
            TimeSpan.FromMilliseconds(20),
            1024 * 1024);
        var connected = new TaskCompletionSource<LazerSessionState>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.StateChanged += state =>
        {
            if (state.Status == LazerSessionStatus.SignedIn)
                connected.TrySetResult(state);
        };

        Directory.CreateDirectory(lateDirectory);
        await File.WriteAllTextAsync(
            gameIniPath,
            tokenContents("rotated-user", "rotated-access", DateTimeOffset.UtcNow.AddHours(1)));

        LazerSessionState state = await connected.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Multiple(() =>
        {
            Assert.That(state.Username, Is.EqualTo("rotated-user"));
            Assert.That(state.Revision, Is.EqualTo(2));
            Assert.That(monitor.TryLeaseAccessToken(), Is.Not.Null);
        });
    }

    [Test]
    public async Task RefreshHandlesLogoutAndAccountSwapAndInvalidatesOldLease()
    {
        await writeSessionAsync("first", "first-access", timeProvider.GetUtcNow().AddHours(1));
        await using LazerSessionMonitor monitor = await createMonitorAsync();
        using LazerAccessTokenLease? firstLease = monitor.TryLeaseAccessToken();

        await writeSessionAsync("second", "second-access", timeProvider.GetUtcNow().AddHours(1));
        await monitor.RefreshAsync();
        using LazerAccessTokenLease? secondLease = monitor.TryLeaseAccessToken();

        Assert.Multiple(() =>
        {
            Assert.That(monitor.Current.Status, Is.EqualTo(LazerSessionStatus.SignedIn));
            Assert.That(monitor.Current.Username, Is.EqualTo("second"));
            Assert.That(monitor.Current.Revision, Is.GreaterThan(1));
            Assert.That(firstLease!.TryGetAccessToken(out _), Is.False);
            Assert.That(secondLease!.TryGetAccessToken(out string token), Is.True);
            Assert.That(token, Is.EqualTo("second-access"));
        });

        long revisionBeforeLogout = monitor.Current.Revision;
        await File.WriteAllTextAsync(gameIniPath, "Username = second\nToken =\n");
        await monitor.RefreshAsync();

        Assert.Multiple(() =>
        {
            Assert.That(monitor.Current.Status, Is.EqualTo(LazerSessionStatus.SignedOut));
            Assert.That(monitor.Current.Username, Is.EqualTo("second"));
            Assert.That(monitor.Current.Revision, Is.GreaterThan(revisionBeforeLogout));
            Assert.That(secondLease!.TryGetAccessToken(out _), Is.False);
            Assert.That(monitor.TryLeaseAccessToken(), Is.Null);
        });
    }

    [Test]
    public async Task ExpiryRemovesLeaseOnNextRefresh()
    {
        await writeSessionAsync("crunchy", "short-access", timeProvider.GetUtcNow().AddMinutes(1));
        await using LazerSessionMonitor monitor = await createMonitorAsync();
        using LazerAccessTokenLease? lease = monitor.TryLeaseAccessToken();

        timeProvider.Advance(TimeSpan.FromMinutes(2));

        Assert.That(lease!.TryGetAccessToken(out _), Is.False);
        await monitor.RefreshAsync();

        Assert.Multiple(() =>
        {
            Assert.That(monitor.Current.Status, Is.EqualTo(LazerSessionStatus.Remembered));
            Assert.That(monitor.Current.Revision, Is.EqualTo(2));
            Assert.That(monitor.TryLeaseAccessToken(), Is.Null);
        });
    }

    [Test]
    public async Task ExpiryPublishesRememberedStateWithoutAFileChange()
    {
        DateTimeOffset expiry = DateTimeOffset.UtcNow.AddSeconds(32);
        await writeSessionAsync("crunchy", "short-access", expiry);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(
            gameIniPath,
            TimeProvider.System,
            TimeSpan.Zero,
            1024 * 1024);
        var expired = new TaskCompletionSource<LazerSessionState>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.StateChanged += state =>
        {
            if (state.Status == LazerSessionStatus.Remembered)
                expired.TrySetResult(state);
        };

        LazerSessionState state = await expired.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(state.Username, Is.EqualTo("crunchy"));
            Assert.That(state.Revision, Is.EqualTo(2));
            Assert.That(monitor.TryLeaseAccessToken(), Is.Null);
        });
    }

    [Test]
    public async Task WatchesAtomicFileReplacement()
    {
        await writeSessionAsync("first", "first-access", timeProvider.GetUtcNow().AddHours(1));
        await using LazerSessionMonitor monitor = await createMonitorAsync(TimeSpan.FromMilliseconds(10));
        var changed = new TaskCompletionSource<LazerSessionState>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.StateChanged += state =>
        {
            if (state.Username == "second")
                changed.TrySetResult(state);
        };

        string replacement = Path.Combine(temporaryDirectory, "replacement.ini");
        await File.WriteAllTextAsync(replacement, tokenContents("second", "second-access", timeProvider.GetUtcNow().AddHours(1)));
        File.Move(replacement, gameIniPath, overwrite: true);

        LazerSessionState state = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(state.Status, Is.EqualTo(LazerSessionStatus.SignedIn));
            Assert.That(state.Username, Is.EqualTo("second"));
            Assert.That(state.Revision, Is.GreaterThan(1));
        });
    }

    [Test]
    public async Task RejectsOversizedAndMalformedInputWithoutLeakingIt()
    {
        await File.WriteAllTextAsync(gameIniPath, "Username = crunchy\nToken = secret-without-token-parts\n");
        await using LazerSessionMonitor malformedMonitor = await createMonitorAsync();

        Assert.Multiple(() =>
        {
            Assert.That(malformedMonitor.Current.Status, Is.EqualTo(LazerSessionStatus.Unavailable));
            Assert.That(malformedMonitor.Current.ToString(), Does.Not.Contain("secret-without-token-parts"));
            Assert.That(malformedMonitor.TryLeaseAccessToken(), Is.Null);
        });

        await malformedMonitor.DisposeAsync();
        await File.WriteAllTextAsync(gameIniPath, new string('x', 65));
        await using LazerSessionMonitor oversizedMonitor = await createMonitorAsync(maximumFileBytes: 64);

        Assert.That(oversizedMonitor.Current.Status, Is.EqualTo(LazerSessionStatus.Unavailable));
    }

    [Test]
    public async Task InvalidUtf8IsUnavailable()
    {
        await File.WriteAllBytesAsync(gameIniPath, [0xff, 0xfe, 0xfd]);

        await using LazerSessionMonitor monitor = await createMonitorAsync();

        Assert.That(monitor.Current.Status, Is.EqualTo(LazerSessionStatus.Unavailable));
    }

    [Test]
    public async Task DisposalClearsStateInvalidatesLeaseAndStopsRefresh()
    {
        await writeSessionAsync("crunchy", "access-secret", timeProvider.GetUtcNow().AddHours(1));
        LazerSessionMonitor monitor = await createMonitorAsync(TimeSpan.FromMilliseconds(10));
        using LazerAccessTokenLease? lease = monitor.TryLeaseAccessToken();

        await monitor.DisposeAsync();
        LazerSessionState disposedState = monitor.Current;
        await File.WriteAllTextAsync(gameIniPath, "Username = replacement\nToken =\n");
        await Task.Delay(100);

        Assert.Multiple(() =>
        {
            Assert.That(disposedState.Status, Is.EqualTo(LazerSessionStatus.Unavailable));
            Assert.That(monitor.Current, Is.EqualTo(disposedState));
            Assert.That(lease!.TryGetAccessToken(out _), Is.False);
            Assert.That(monitor.TryLeaseAccessToken(), Is.Null);
            Assert.ThrowsAsync<ObjectDisposedException>(async () => await monitor.RefreshAsync());
        });
    }

    private Task<LazerSessionMonitor> createMonitorAsync(TimeSpan? debounce = null, int maximumFileBytes = 1024 * 1024) =>
        LazerSessionMonitor.CreateAsync(gameIniPath, timeProvider, debounce ?? TimeSpan.Zero, maximumFileBytes);

    private Task writeSessionAsync(string username, string accessToken, DateTimeOffset expiry, string refreshToken = "refresh") =>
        File.WriteAllTextAsync(gameIniPath, tokenContents(username, accessToken, expiry, refreshToken));

    private static string tokenContents(string username, string accessToken, DateTimeOffset expiry, string refreshToken = "refresh") =>
        $"Username = {username}\nToken = {accessToken}|{expiry.ToUnixTimeSeconds()}|{refreshToken}\n";

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan amount) => utcNow += amount;
    }
}
