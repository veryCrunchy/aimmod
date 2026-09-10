using AimMod.Desktop.Creator;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class TimestampThumbnailTests
{
    [TestCase("https://www.twitch.tv/videos/12345", "12345")]
    [TestCase("https://twitch.tv/videos/12345?t=1h", "12345")]
    public void AcceptsOnlyValidatedVideoLocations(string url, string id)
        => Assert.That(TimestampThumbnailService.VideoId(url), Is.EqualTo(id));

    [TestCase("https://twitch.tv.attacker.invalid/videos/12345")]
    [TestCase("https://twitch.tv/videos/script")]
    [TestCase("https://twitch.tv:444/videos/12345")]
    [TestCase("file:///video.mp4")]
    public void RejectsUnsupportedLocations(string url)
        => Assert.That(() => TimestampThumbnailService.VideoId(url), Throws.Exception);

    [Test]
    public void CacheCleanupBoundsCountAndExpiresOldFrames()
    {
        string root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "thumbnail-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (int i = 0; i < 102; i++) File.WriteAllBytes(Path.Combine(root, i + ".jpg"), [1]);
            string old = Path.Combine(root, "old.jpg"); File.WriteAllBytes(old, [1]); File.WriteAllText(old + ".json", "{}");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));
            TimestampThumbnailService.Prune(root, DateTime.UtcNow);
            Assert.That(Directory.GetFiles(root, "*.jpg"), Has.Length.EqualTo(100));
            Assert.That(File.Exists(old), Is.False); Assert.That(File.Exists(old + ".json"), Is.False);
        }
        finally { Directory.Delete(root, true); }
    }

    [Test, Explicit("Fetches one public VOD frame configured by the operator; no full video download.")]
    public async Task CaptureSelectedPublicVodFrame()
    {
        string? url = Environment.GetEnvironmentVariable("AIMMOD_PREVIEW_TEST_URL");
        if (url is null) Assert.Ignore("No VOD selected.");
        double seconds = double.Parse(Environment.GetEnvironmentVariable("AIMMOD_PREVIEW_TEST_SECONDS")!, System.Globalization.CultureInfo.InvariantCulture);
        string root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "timestamp-preview");
        var result = await new TimestampThumbnailService(root).GetAsync(url!, seconds, default);
        Assert.That(Math.Abs(result.Seconds - seconds), Is.LessThanOrEqualTo(1.5));
        Assert.That(new FileInfo(result.Path).Length, Is.GreaterThan(1000));
        TestContext.AddTestAttachment(result.Path);
    }
}
