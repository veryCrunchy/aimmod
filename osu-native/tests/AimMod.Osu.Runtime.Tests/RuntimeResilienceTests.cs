using System.Net.Http.Headers;
using NUnit.Framework;

namespace AimMod.Osu.Runtime.Tests;

[TestFixture]
public sealed class RuntimeResilienceTests
{
    private string temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-resilience-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, true);
    }

    [Test]
    public void SweepRemovesOnlyStaleManagedDirectoriesIncludingReadOnlyFiles()
    {
        string stale = Directory.CreateDirectory(Path.Combine(temporaryDirectory, AimModTempDirectories.SnapshotPrefix + "stale")).FullName;
        string staleFile = Path.Combine(stale, "snapshot.realm");
        File.WriteAllText(staleFile, "x");
        File.SetAttributes(staleFile, FileAttributes.ReadOnly);
        string staleAssets = Directory.CreateDirectory(Path.Combine(temporaryDirectory, AimModTempDirectories.AssetsPrefix + "stale")).FullName;
        string fresh = Directory.CreateDirectory(Path.Combine(temporaryDirectory, AimModTempDirectories.CatalogPrefix + "fresh")).FullName;
        string unrelated = Directory.CreateDirectory(Path.Combine(temporaryDirectory, "someone-elses-folder")).FullName;
        DateTime old = DateTime.UtcNow.AddHours(-2);
        Directory.SetLastWriteTimeUtc(stale, old);
        Directory.SetLastWriteTimeUtc(staleAssets, old);
        Directory.SetLastWriteTimeUtc(unrelated, old);

        int removed = AimModTempDirectories.SweepStale(root: temporaryDirectory);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.EqualTo(2));
            Assert.That(Directory.Exists(stale), Is.False);
            Assert.That(Directory.Exists(staleAssets), Is.False);
            Assert.That(Directory.Exists(fresh), Is.True);
            Assert.That(Directory.Exists(unrelated), Is.True);
        });
    }

    [Test]
    public void SweepToleratesDirectoriesThatAreInUse()
    {
        string busy = Directory.CreateDirectory(Path.Combine(temporaryDirectory, AimModTempDirectories.SkinsPrefix + "busy")).FullName;
        string busyFile = Path.Combine(busy, "held.bin");
        string idle = Directory.CreateDirectory(Path.Combine(temporaryDirectory, AimModTempDirectories.SkinsPrefix + "idle")).FullName;
        File.WriteAllText(busyFile, "x");
        DateTime old = DateTime.UtcNow.AddHours(-2);
        Directory.SetLastWriteTimeUtc(busy, old);
        Directory.SetLastWriteTimeUtc(idle, old);

        using (new FileStream(busyFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.DoesNotThrow(() => AimModTempDirectories.SweepStale(root: temporaryDirectory));
            Assert.That(Directory.Exists(idle), Is.False);
            if (OperatingSystem.IsWindows())
                Assert.That(Directory.Exists(busy), Is.True);
        }
    }

    [Test]
    public void SweepOfAMissingRootIsANoOp() =>
        Assert.That(AimModTempDirectories.SweepStale(root: Path.Combine(temporaryDirectory, "missing")), Is.Zero);

    [Test]
    public void TryDeleteNeverThrowsAndRefusesForeignDirectories()
    {
        string foreign = Directory.CreateDirectory(Path.Combine(temporaryDirectory, "foreign")).FullName;
        string owned = Directory.CreateDirectory(Path.Combine(temporaryDirectory, AimModTempDirectories.CatalogPrefix + "owned")).FullName;
        File.WriteAllText(Path.Combine(owned, "a.txt"), "x");

        Assert.Multiple(() =>
        {
            Assert.That(AimModTempDirectories.TryDelete(foreign, AimModTempDirectories.CatalogPrefix), Is.False);
            Assert.That(Directory.Exists(foreign), Is.True);
            Assert.That(AimModTempDirectories.TryDelete(owned, AimModTempDirectories.CatalogPrefix), Is.True);
            Assert.That(Directory.Exists(owned), Is.False);
            Assert.That(AimModTempDirectories.TryDelete(owned, AimModTempDirectories.CatalogPrefix), Is.True);
            Assert.That(AimModTempDirectories.TryDelete(null, AimModTempDirectories.CatalogPrefix), Is.True);
        });
        Assert.DoesNotThrow(() => AimModTempDirectories.TryDelete("\0invalid", AimModTempDirectories.CatalogPrefix));
    }

    [Test]
    public void TryDeleteReportsFailureInsteadOfThrowingWhenAFileIsLocked()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("Deleting an open file only fails on Windows.");

        string owned = Directory.CreateDirectory(Path.Combine(temporaryDirectory, AimModTempDirectories.SnapshotPrefix + "locked")).FullName;
        string file = Path.Combine(owned, "held.realm");
        File.WriteAllText(file, "x");

        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.That(AimModTempDirectories.TryDelete(owned, AimModTempDirectories.SnapshotPrefix), Is.False);

        Assert.That(AimModTempDirectories.TryDelete(owned, AimModTempDirectories.SnapshotPrefix), Is.True);
    }

    [Test]
    public void RetryAfterSupportsDeltaAndDateAndClampsToSaneBounds()
    {
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Multiple(() =>
        {
            Assert.That(HttpRequestPolicy.ParseRetryAfter(new RetryConditionHeaderValue(TimeSpan.FromSeconds(12)), now), Is.EqualTo(TimeSpan.FromSeconds(12)));
            Assert.That(HttpRequestPolicy.ParseRetryAfter(new RetryConditionHeaderValue(now.AddSeconds(40)), now), Is.EqualTo(TimeSpan.FromSeconds(40)));
            Assert.That(HttpRequestPolicy.ParseRetryAfter(null, now), Is.Null);
            Assert.That(HttpRequestPolicy.ClampCooldown(null), Is.EqualTo(HttpRequestPolicy.DefaultRateLimitCooldown));
            Assert.That(HttpRequestPolicy.ClampCooldown(TimeSpan.FromSeconds(-5)), Is.EqualTo(HttpRequestPolicy.MinimumRateLimitCooldown));
            Assert.That(HttpRequestPolicy.ClampCooldown(TimeSpan.FromDays(2)), Is.EqualTo(HttpRequestPolicy.MaximumRateLimitCooldown));
            Assert.That(HttpRequestPolicy.ClampCooldown(TimeSpan.FromSeconds(75)), Is.EqualTo(TimeSpan.FromSeconds(75)));
        });
    }

    [TestCase(System.Net.HttpStatusCode.InternalServerError, true)]
    [TestCase(System.Net.HttpStatusCode.BadGateway, true)]
    [TestCase(System.Net.HttpStatusCode.ServiceUnavailable, true)]
    [TestCase(System.Net.HttpStatusCode.GatewayTimeout, true)]
    [TestCase(System.Net.HttpStatusCode.NotImplemented, false)]
    [TestCase(System.Net.HttpStatusCode.TooManyRequests, false)]
    [TestCase(System.Net.HttpStatusCode.NotFound, false)]
    public void OnlyTransientServerErrorsAreRetried(System.Net.HttpStatusCode status, bool expected) =>
        Assert.That(HttpRequestPolicy.IsTransientServerError(status), Is.EqualTo(expected));

    [Test]
    public async Task PooledBodyReadsExactlyWhatWasSentAndRejectsOversizedBodies()
    {
        byte[] payload = Enumerable.Range(0, 70_000).Select(index => (byte)(index % 251)).ToArray();

        using (var exact = new ByteArrayContent(payload))
        using (PooledBody? body = await PooledBody.ReadAsync(exact, 100_000, CancellationToken.None))
            Assert.That(body!.Value.Span.ToArray(), Is.EqualTo(payload));

        using var unknownLength = new StreamContent(new ChunkedStream(payload, 1000));
        using (PooledBody? body = await PooledBody.ReadAsync(unknownLength, 70_000, CancellationToken.None))
            Assert.That(body!.Value.Span.ToArray(), Is.EqualTo(payload));

        using var tooLarge = new StreamContent(new ChunkedStream(payload, 1000));
        Assert.That(await PooledBody.ReadAsync(tooLarge, 69_999, CancellationToken.None), Is.Null);

        using var declaredTooLarge = new ByteArrayContent(payload);
        Assert.That(await PooledBody.ReadAsync(declaredTooLarge, 10, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task SessionLineWithoutASeparatorIsIgnoredInsteadOfMakingTheSessionUnavailable()
    {
        string gameIni = Path.Combine(temporaryDirectory, "game.ini");
        long expiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        await File.WriteAllTextAsync(gameIni, $"Username = crunchy\nthis line has no separator\nToken = access|{expiry}|refresh\n");

        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIni);

        Assert.Multiple(() =>
        {
            Assert.That(monitor.Current.Status, Is.EqualTo(LazerSessionStatus.SignedIn));
            Assert.That(monitor.Current.Username, Is.EqualTo("crunchy"));
        });
    }

    [Test]
    public async Task SessionFileIsNotReparsedWhileItsTimestampAndLengthAreUnchanged()
    {
        string gameIni = Path.Combine(temporaryDirectory, "game.ini");
        long expiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        await File.WriteAllTextAsync(gameIni, $"Username = crunchy\nToken = access|{expiry}|refresh\n");
        File.SetLastWriteTimeUtc(gameIni, DateTime.UtcNow.AddHours(-1));
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIni);

        await monitor.RefreshAsync();
        await monitor.RefreshAsync();
        int unchanged = monitor.ParseCount;

        await File.WriteAllTextAsync(gameIni, $"Username = second-user\nToken = access|{expiry}|refresh\n");
        File.SetLastWriteTimeUtc(gameIni, DateTime.UtcNow.AddHours(-1));
        await monitor.RefreshAsync();

        Assert.Multiple(() =>
        {
            Assert.That(unchanged, Is.EqualTo(1));
            Assert.That(monitor.ParseCount, Is.EqualTo(2));
            Assert.That(monitor.Current.Username, Is.EqualTo("second-user"));
        });
    }

    [Test]
    public async Task SessionReusedParseStillExpiresTheTokenOnTime()
    {
        string gameIni = Path.Combine(temporaryDirectory, "game.ini");
        long expiry = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        await File.WriteAllTextAsync(gameIni, $"Username = crunchy\nToken = access|{expiry}|refresh\n");
        File.SetLastWriteTimeUtc(gameIni, DateTime.UtcNow.AddHours(-1));
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(
            gameIni,
            clock,
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1),
            1024 * 1024);
        Assert.That(monitor.Current.Status, Is.EqualTo(LazerSessionStatus.SignedIn));

        clock.Advance(TimeSpan.FromMinutes(11));
        await monitor.RefreshAsync();

        Assert.Multiple(() =>
        {
            Assert.That(monitor.ParseCount, Is.EqualTo(1));
            Assert.That(monitor.Current.Status, Is.EqualTo(LazerSessionStatus.Remembered));
        });
    }

    [Test]
    public async Task PreferencesMonitorCreatesItsWatcherOnceTheDataDirectoryAppears()
    {
        string dataRoot = Path.Combine(temporaryDirectory, "late-root");
        await using LazerPreferencesMonitor monitor = await LazerPreferencesMonitor.CreateAsync(dataRoot, TimeSpan.FromHours(1));
        Directory.CreateDirectory(dataRoot);
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "framework.ini"), "VolumeMusic = 0.25\n");
        await monitor.RefreshAsync();
        Assert.That(monitor.Current.VolumeMusic, Is.EqualTo(0.25));

        await File.WriteAllTextAsync(Path.Combine(dataRoot, "framework.ini"), "VolumeMusic = 0.75\n");

        await waitUntilAsync(() => monitor.Current.VolumeMusic == 0.75);
        Assert.That(monitor.Current.VolumeMusic, Is.EqualTo(0.75));
    }

    [Test]
    public async Task PreferencesPollingKeepsWorkingAcrossFileChurn()
    {
        string dataRoot = Path.Combine(temporaryDirectory, "churn");
        Directory.CreateDirectory(dataRoot);
        string framework = Path.Combine(dataRoot, "framework.ini");
        await using LazerPreferencesMonitor monitor = await LazerPreferencesMonitor.CreateAsync(dataRoot, TimeSpan.FromMilliseconds(30));

        for (int index = 0; index < 5; index++)
        {
            File.Delete(framework);
            Directory.CreateDirectory(framework);
            await Task.Delay(40);
            Directory.Delete(framework);
            await File.WriteAllTextAsync(framework, $"VolumeMusic = 0.{index + 1}\n");
            await Task.Delay(40);
        }

        await waitUntilAsync(() => monitor.Current.VolumeMusic == 0.5);
        Assert.That(monitor.Current.VolumeMusic, Is.EqualTo(0.5));
    }

    [Test]
    public async Task PreferencesMonitorDisposesQuietlyAndRepeatedly()
    {
        LazerPreferencesMonitor monitor = await LazerPreferencesMonitor.CreateAsync(temporaryDirectory, TimeSpan.FromMilliseconds(20));
        await Task.Delay(60);

        await monitor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotThrowAsync(async () => await monitor.DisposeAsync());
        Assert.ThrowsAsync<ObjectDisposedException>(async () => await monitor.RefreshAsync());
    }

    private static async Task waitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
    }

    private sealed class ManualClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }

    private sealed class ChunkedStream(byte[] content, int chunk) : Stream
    {
        private int position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = Math.Min(Math.Min(chunk, buffer.Length), content.Length - position);
            content.AsSpan(position, count).CopyTo(buffer.Span);
            position += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
