using System.Diagnostics;
using AimMod.Desktop.Creator;
using AimMod.Desktop.Practice;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

public class LocalFootageTests
{
    [TestCase("vlc", "--start-time=93.25")]
    [TestCase("mpv", "--start=93.25")]
    [TestCase("ffplay", "93.25")]
    public void PlayerReceivesTimestampAndPathAsSeparateArguments(string player, string seek)
    {
        string file = Path.Combine(Path.GetTempPath(), "my recording & $(echo test).mkv");
        var start = LocalFootagePlayer.StartInfo(player, file, 93.25);
        Assert.That(start.UseShellExecute, Is.False);
        Assert.That(start.ArgumentList.Last(), Is.EqualTo(file));
        Assert.That(start.ArgumentList, Does.Contain(seek));
        if (player == "vlc") Assert.That(start.ArgumentList, Does.Contain("--no-one-instance"));
    }

    [Test]
    public void MissingLocalFilesGiveAnActionableError()
    {
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mkv");
        Assert.That(() => LocalFootagePlayer.ValidateFile(file), Throws.InvalidOperationException.With.Message.Contains("Update its file path"));
        Assert.Throws<InvalidOperationException>(() => LocalFootagePlayer.ValidateFile("https://www.twitch.tv/videos/12345"));
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalFootagePlayer.StartInfo("vlc", file, double.NaN));
    }

    [Test]
    public async Task LocalCacheChangesWhenFileIsReplaced()
    {
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4");
        try
        {
            await File.WriteAllTextAsync(file, "first");
            string first = TimestampThumbnailService.LocalCacheIdentity(file);
            await File.WriteAllTextAsync(file, "replacement");
            Assert.That(TimestampThumbnailService.LocalCacheIdentity(file), Is.Not.EqualTo(first));
        }
        finally { File.Delete(file); }
    }

    [Test, Explicit("Uses installed FFmpeg to create and seek a short synthetic local recording.")]
    public async Task PreviewSeeksLocalVideoAndRejectsPastEnd()
    {
        string? ffmpeg = FfmpegExecutableLocator.Find();
        if (ffmpeg is null) Assert.Ignore("FFmpeg is not installed.");
        string root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "local-footage");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "synthetic recording.mkv");
        var start = new ProcessStartInfo(ffmpeg!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=960x540:rate=10:duration=4", "-c:v", "libx264", "-preset", "ultrafast", file }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.That(process.ExitCode, Is.Zero, error);
        var service = new TimestampThumbnailService(Path.Combine(root, "cache"));
        var first = await service.GetAsync(file, 0, default);
        var later = await service.GetAsync(file, 2, default);
        Assert.That(await File.ReadAllBytesAsync(first.Path), Is.Not.EqualTo(await File.ReadAllBytesAsync(later.Path)));
        Assert.That((await service.GetAsync(file, 2, default)).Path, Is.EqualTo(later.Path));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await service.GetAsync(file, 20, default));
        Assert.That(Directory.GetFiles(Path.Combine(root, "cache"), "*.tmp"), Is.Empty);
        TestContext.AddTestAttachment(later.Path);
    }
}
