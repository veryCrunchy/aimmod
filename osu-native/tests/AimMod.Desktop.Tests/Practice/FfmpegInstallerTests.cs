using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using AimMod.Desktop.Practice;
using NUnit.Framework;

namespace AimMod.Desktop.Tests.Practice;

[TestFixture]
public sealed class FfmpegInstallerTests
{
    private string root = null!;
    [SetUp] public void SetUp() => root = Path.Combine(Path.GetTempPath(), "aimmod-tool-test-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void TearDown() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private static byte[] archive(bool executable = true)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (string name in executable ? new[] { "build/bin/ffmpeg.exe", "build/LICENSE", "build/README.txt", "../../escaped.txt" } : new[] { "build/LICENSE" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write("synthetic fixture");
            }
        return output.ToArray();
    }

    [Test]
    public async Task InstallsOnceAndReusesPrivateCopyAcrossConcurrentRequests()
    {
        byte[] bytes = archive();
        var handler = new Handler(bytes);
        using var client = new HttpClient(handler);
        var installer = new FfmpegInstaller(client, Path.Combine(root, "version"), Convert.ToHexString(SHA256.HashData(bytes)));
        string[] paths = await Task.WhenAll(installer.EnsureAsync(default), installer.EnsureAsync(default));
        Assert.That(paths[0], Is.EqualTo(paths[1]));
        Assert.That(File.ReadAllText(paths[0]), Is.EqualTo("synthetic fixture"));
        Assert.That(File.Exists(Path.Combine(root, "version", "LICENSE")), Is.True);
        Assert.That(handler.Calls, Is.EqualTo(1));
        Assert.That(Directory.GetDirectories(root), Has.Length.EqualTo(1));
        Assert.That(Directory.GetFiles(root, "escaped.txt", SearchOption.AllDirectories), Is.Empty);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void InvalidDownloadsNeverPublishAnExecutable(bool badHash)
    {
        byte[] bytes = archive(badHash);
        using var client = new HttpClient(new Handler(bytes));
        var installer = new FfmpegInstaller(client, Path.Combine(root, "version"), badHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes)));
        Assert.ThrowsAsync<FfmpegSetupException>(() => installer.EnsureAsync(default));
        Assert.That(Directory.GetFiles(root, "ffmpeg.exe", SearchOption.AllDirectories), Is.Empty);
        Assert.That(Directory.GetDirectories(root), Is.Empty);
    }

    [Test]
    public async Task CancelledAndFailedRequestsCanBeRetried()
    {
        byte[] bytes = archive();
        var handler = new Handler(bytes) { Fail = true };
        using var client = new HttpClient(handler);
        var installer = new FfmpegInstaller(client, Path.Combine(root, "version"), Convert.ToHexString(SHA256.HashData(bytes)));
        Assert.CatchAsync<OperationCanceledException>(() => installer.EnsureAsync(new CancellationToken(true)));
        Assert.That(handler.Calls, Is.Zero);
        Assert.ThrowsAsync<FfmpegSetupException>(() => installer.EnsureAsync(default));
        handler.Fail = false;
        Assert.That(File.Exists(await installer.EnsureAsync(default)), Is.True);
    }

    [Test, Explicit("Downloads the real pinned FFmpeg release into a temporary directory.")]
    public async Task RealDownloadWorksWithoutPathInstallation()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        string executable = await new FfmpegInstaller(client, Path.Combine(root, "version"), FfmpegInstaller.ArchiveHash).EnsureAsync(default);
        string output = Path.Combine(root, "tone.ogg");
        var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:a", "libvorbis", output }) start.ArgumentList.Add(arg);
        var result = await new PracticeProcessRunner().RunAsync(start, TimeSpan.FromSeconds(30), default);
        Assert.That(result.ExitCode, Is.Zero, result.StandardError);
        Assert.That(new FileInfo(output).Length, Is.GreaterThan(64));
        string practice = Path.Combine(root, "practice.ogg");
        var slicer = new WindowsFfmpegAudioSlicer(executable, new PracticeProcessRunner(), TimeSpan.FromSeconds(30));
        await slicer.SliceAsync(new PracticeAudioSliceRequest(output, 0, 800, "practice.ogg", 3), practice);
        Assert.That(new FileInfo(practice).Length, Is.GreaterThan(64));
    }

    private sealed class Handler(byte[] bytes) : HttpMessageHandler
    {
        internal int Calls;
        internal bool Fail;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
