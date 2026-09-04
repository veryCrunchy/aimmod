using AimMod.Desktop.Skins.Online;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class OnlineSkinCacheTests
{
    private string temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-skin-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, true);
    }

    [Test]
    public async Task CachePersistsDataAndEnforcesEntryCount()
    {
        var options = new OnlineSkinCacheOptions(MaximumBytes: 64, MaximumEntries: 2, MaximumEntryBytes: 32, MaximumAge: TimeSpan.FromDays(1));
        var cache = new OnlineSkinCatalogCache(temporaryDirectory, options);
        await cache.PutBytesAsync("one", new byte[] { 1 }, "fixture");
        await Task.Delay(10);
        await cache.PutBytesAsync("two", new byte[] { 2 }, "fixture");
        await Task.Delay(10);
        await cache.PutBytesAsync("three", new byte[] { 3 }, "fixture");

        byte[]? one = await cache.ReadBytesAsync("one");
        byte[]? two = await cache.ReadBytesAsync("two");
        byte[]? three = await new OnlineSkinCatalogCache(temporaryDirectory, options).ReadBytesAsync("three");

        Assert.Multiple(() =>
        {
            Assert.That(one, Is.Null);
            Assert.That(two, Is.EqualTo(new byte[] { 2 }));
            Assert.That(three, Is.EqualTo(new byte[] { 3 }));
            Assert.That(Directory.EnumerateFiles(Path.Combine(temporaryDirectory, "entries")).Count(), Is.EqualTo(2));
        });
    }

    [Test]
    public void CacheRejectsOversizedEntries()
    {
        var cache = new OnlineSkinCatalogCache(temporaryDirectory, new OnlineSkinCacheOptions(MaximumBytes: 8, MaximumEntries: 2, MaximumEntryBytes: 4));

        Assert.That(async () => await cache.PutBytesAsync("large", new byte[5], "fixture"), Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}
