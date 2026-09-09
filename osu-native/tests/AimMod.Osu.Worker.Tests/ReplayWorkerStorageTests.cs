using NUnit.Framework;

namespace AimMod.Osu.Worker.Tests;

[TestFixture]
public sealed class ReplayWorkerStorageTests
{
    private string root = null!;
    [SetUp]
    public void Setup() => root = Directory.CreateTempSubdirectory("aimmod-storage-test-").FullName;
    [TearDown]
    public void Cleanup() => Directory.Delete(root, true);

    private ReplayWorkerStorage acquire(long maximum = ReplayWorkerStorage.MaximumBytes, Func<long>? free = null,
        CancellationToken token = default) => ReplayWorkerStorage.Acquire(token, root, maximum,
            free ?? (() => long.MaxValue));

    [Test]
    public void RemovesScratchOnSuccessAndException()
    {
        string path;
        using (var storage = acquire())
        {
            path = storage.DirectoryPath;
            File.WriteAllText(Path.Combine(path, "online.db"), "synthetic scratch");
        }
        Assert.That(Directory.Exists(path), Is.False);
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var storage = acquire();
            File.WriteAllText(Path.Combine(storage.DirectoryPath, "scratch"), "partial");
            throw new InvalidOperationException();
        });
        Assert.That(Directory.GetDirectories(root), Is.Empty);
    }

    [Test]
    public void RecoversCrashedRunsWithoutTouchingUnrecognisedData()
    {
        string abandoned = Directory.CreateDirectory(Path.Combine(root, "run-" + Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(abandoned, "scratch"), "abandoned");
        string unrelated = Directory.CreateDirectory(Path.Combine(root, "run-important")).FullName;
        using var storage = acquire();
        Assert.That(Directory.Exists(abandoned), Is.False);
        Assert.That(Directory.Exists(unrelated), Is.True);
    }

    [Test]
    public async Task LiveLeasePreventsConcurrentCleanupAndHonoursCancellation()
    {
        using var active = acquire();
        string file = Path.Combine(active.DirectoryPath, "live");
        File.WriteAllText(file, "still in use");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Task.Run(() => Assert.Throws<OperationCanceledException>(() => acquire(token: cancel.Token)));
        Assert.That(File.ReadAllText(file), Is.EqualTo("still in use"));
    }

    [Test]
    public void LowDiskSpaceRefusesNewRunAndReleasesLease()
    {
        Assert.Throws<ReplayAnalysisException>(() => acquire(free: () => 0));
        Assert.That(Directory.GetDirectories(root), Is.Empty);
        using var recovered = acquire();
    }

    [Test]
    public void LockedCrashArtifactPreventsStartingMoreRuns()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Tests Windows file sharing semantics.");
        string abandoned = Directory.CreateDirectory(Path.Combine(root, "run-" + Guid.NewGuid().ToString("N"))).FullName;
        string file = Path.Combine(abandoned, "locked.db");
        using (var locked = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => acquire());
            Assert.That(Directory.GetDirectories(root), Has.Length.EqualTo(1));
        }
        using var recovered = acquire();
        Assert.That(Directory.Exists(abandoned), Is.False);
    }

    [Test]
    public void LinkedScratchDoesNotDeleteTheTarget()
    {
        string outside = Directory.CreateTempSubdirectory("aimmod-storage-external-test-").FullName;
        string link = Path.Combine(root, "run-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(Path.Combine(outside, "keep"), "keep");
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (UnauthorizedAccessException) { Assert.Ignore("Symbolic links are unavailable."); }
            catch (IOException error) when ((error.HResult & 0xffff) == 1314)
            {
                Assert.Ignore("Creating symbolic links requires a Windows privilege unavailable to this test process.");
            }
            Assert.Throws<IOException>(() => acquire());
            Assert.That(File.ReadAllText(Path.Combine(outside, "keep")), Is.EqualTo("keep"));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(outside, true);
        }
    }

    [Test]
    public void ByteLimitIncludesUnremovedFiles()
    {
        File.WriteAllBytes(Path.Combine(root, "unexpected.bin"), new byte[65]);
        Assert.Throws<ReplayAnalysisException>(() => acquire(maximum: 64));
        Assert.That(Directory.GetDirectories(root), Is.Empty);
    }

    [Test]
    public void WatchStopsAHostThatExceedsTheLimit()
    {
        using var storage = acquire(maximum: 64);
        using var stopped = new ManualResetEventSlim();
        using var watch = storage.Watch(stopped.Set);
        File.WriteAllBytes(Path.Combine(storage.DirectoryPath, "growing.bin"), new byte[65]);
        Assert.That(stopped.Wait(TimeSpan.FromSeconds(5)), Is.True);
        Assert.Throws<ReplayAnalysisException>(storage.ThrowIfLimitExceeded);
    }

    [Test]
    public void LegacyCleanupKeepsRecentAndUnrelatedFolders()
    {
        string old = makeLegacy("aimmod-replay-analysis-" + Guid.NewGuid(), DateTime.UtcNow.AddDays(-2));
        string recent = makeLegacy("aimmod-replay-analysis-" + Guid.NewGuid(), DateTime.UtcNow);
        string unknown = makeLegacy("aimmod-replay-analysis-not-a-guid", DateTime.UtcNow.AddDays(-2));
        ReplayWorkerStorage.CleanupLegacyRuns(root, DateTime.UtcNow.AddDays(-1));
        Assert.That(Directory.Exists(old), Is.False);
        Assert.That(Directory.Exists(recent), Is.True);
        Assert.That(Directory.Exists(unknown), Is.True);
    }

    private string makeLegacy(string name, DateTime date)
    {
        string path = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        string file = Path.Combine(path, "online.db");
        File.WriteAllText(file, "synthetic");
        File.SetLastWriteTimeUtc(file, date);
        Directory.SetLastWriteTimeUtc(path, date);
        return path;
    }
}
