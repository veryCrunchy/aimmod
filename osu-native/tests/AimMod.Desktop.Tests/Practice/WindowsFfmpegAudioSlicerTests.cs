using System.Diagnostics;
using System.IO.Compression;
using System.Globalization;
using AimMod.Desktop.Practice;
using NUnit.Framework;

namespace AimMod.Desktop.Tests.Practice;

[TestFixture]
public sealed class WindowsFfmpegAudioSlicerTests
{
    private string directory = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aimmod-ffmpeg-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }

    [Test]
    public void PathResolutionWinsBeforeWingetFallback()
    {
        string pathDirectory = Path.Combine(directory, "path");
        string wingetDirectory = Path.Combine(directory, "Microsoft", "WinGet", "Packages", "Gyan.FFmpeg_test", "bin");
        Directory.CreateDirectory(pathDirectory);
        Directory.CreateDirectory(wingetDirectory);
        string filename = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string expected = Path.Combine(pathDirectory, filename);
        File.WriteAllText(expected, string.Empty);
        File.WriteAllText(Path.Combine(wingetDirectory, "ffmpeg.exe"), string.Empty);

        Assert.That(FfmpegExecutableLocator.Find(pathDirectory, directory), Is.EqualTo(expected));
    }

    [Test]
    public void FindsCurrentWingetPackageWhenPathIsStale()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("WinGet fallback is Windows-specific.");
        string bin = Path.Combine(directory, "Microsoft", "WinGet", "Packages", "Gyan.FFmpeg_Microsoft.Winget.Source_test", "ffmpeg-9.0.1", "bin");
        Directory.CreateDirectory(bin);
        string expected = Path.Combine(bin, "ffmpeg.exe");
        File.WriteAllText(expected, string.Empty);

        Assert.That(FfmpegExecutableLocator.Find(string.Empty, directory), Is.EqualTo(expected));
    }

    [Test]
    public async Task UsesArgumentListAndValidatesProducedAudio()
    {
        string source = Path.Combine(directory, "source audio;name.ogg");
        string destination = Path.Combine(directory, "out audio.ogg");
        File.WriteAllBytes(source, new byte[128]);
        ProcessStartInfo? observed = null;
        var runner = new FakeRunner(startInfo =>
        {
            observed = startInfo;
            File.WriteAllBytes(destination, new byte[128]);
            return new PracticeProcessResult(0, string.Empty);
        });
        var slicer = new WindowsFfmpegAudioSlicer("C:\\tools\\ffmpeg.exe", runner, TimeSpan.FromSeconds(1));

        var request = new PracticeAudioSliceRequest(source, 1_250, 4_750, Path.GetFileName(destination), 3);
        await slicer.SliceAsync(request, destination);

        Assert.That(observed, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(observed!.UseShellExecute, Is.False);
            Assert.That(observed.ArgumentList, Does.Contain(source));
            Assert.That(observed.ArgumentList, Does.Contain("-filter_complex"));
            Assert.That(observed.ArgumentList, Does.Contain(WindowsFfmpegAudioSlicer.CreateRepeatFilter(request)));
            Assert.That(WindowsFfmpegAudioSlicer.CreateRepeatFilter(request), Does.Contain("atrim=start=1.25:end=4.75"));
            Assert.That(WindowsFfmpegAudioSlicer.CreateRepeatFilter(request), Does.Contain("aresample=48000:first_pts=0"));
            Assert.That(WindowsFfmpegAudioSlicer.CreateRepeatFilter(request), Does.Contain("atrim=duration=3.5"));
            Assert.That(WindowsFfmpegAudioSlicer.CreateRepeatFilter(request), Does.Contain("concat=n=3"));
            Assert.That(observed.ArgumentList, Does.Contain("libvorbis"));
            Assert.That(observed.ArgumentList[^1], Is.EqualTo(destination));
        });
    }

    [Test]
    public void SingleRoundFilterDoesNotCreateAConcatGraph()
    {
        string filter = WindowsFfmpegAudioSlicer.CreateRepeatFilter(
            new PracticeAudioSliceRequest("source.ogg", 500, 2_000, "out.ogg"));

        Assert.Multiple(() =>
        {
            Assert.That(filter, Is.EqualTo("[0:a]atrim=start=0.5:end=2,asetpts=PTS-STARTPTS,aresample=48000:first_pts=0,apad,atrim=duration=1.5[practice]"));
            Assert.That(filter, Does.Not.Contain("concat"));
        });
    }

    [Test]
    public async Task ProducesTimestampZeroOggAtThePlannedRepeatedDuration()
    {
        string? ffmpeg = FfmpegExecutableLocator.Find();
        if (ffmpeg is null)
            Assert.Ignore("FFmpeg is required for the synchronized audio integration test.");
        string ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (!File.Exists(ffprobe))
            Assert.Ignore("FFprobe is required for the synchronized audio integration test.");

        string source = Path.Combine(directory, "source.wav");
        string output = Path.Combine(directory, "practice-audio.ogg");
        await runTool(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=8",
            "-ar", "48000", "-ac", "2", source);
        var request = new PracticeAudioSliceRequest(source, 1_250, 3_750, Path.GetFileName(output), 4);

        await new WindowsFfmpegAudioSlicer().SliceAsync(request, output);

        string probe = await runTool(ffprobe, "-v", "error", "-show_entries", "format=start_time,duration",
            "-of", "default=noprint_wrappers=1:nokey=1", output);
        double[] values = probe.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                               .Select(value => double.Parse(value, CultureInfo.InvariantCulture))
                               .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(values[0], Is.EqualTo(0).Within(0.001), "The generated OGG should start at timestamp zero.");
            Assert.That(values[1] * 1000, Is.EqualTo(request.OutputDurationMs).Within(25),
                "Repeated audio should match the beatmap timeline without accumulated encoder delay.");
        });
    }

    [Test]
    public void RejectsSuccessfulProcessWithoutUsableOutput()
    {
        string source = Path.Combine(directory, "source.ogg");
        string destination = Path.Combine(directory, "output.ogg");
        File.WriteAllBytes(source, new byte[128]);
        var slicer = new WindowsFfmpegAudioSlicer("ffmpeg.exe",
            new FakeRunner(_ => new PracticeProcessResult(0, string.Empty)), TimeSpan.FromSeconds(1));

        Assert.That(async () => await slicer.SliceAsync(
            new PracticeAudioSliceRequest(source, 0, 1000, "output.ogg"), destination),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void ConcreteRunnerStopsAProcessAtTheTimeout()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("This verifies the Windows practice-map process boundary.");
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 5" })
            startInfo.ArgumentList.Add(argument);

        Assert.That(async () => await new PracticeProcessRunner().RunAsync(startInfo, TimeSpan.FromMilliseconds(100), CancellationToken.None),
            Throws.TypeOf<TimeoutException>());
    }

    [Test]
    public void HonoursCancellationBeforeStartingFfmpeg()
    {
        string source = Path.Combine(directory, "source.ogg");
        File.WriteAllBytes(source, new byte[128]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var slicer = new WindowsFfmpegAudioSlicer("ffmpeg.exe",
            new FakeRunner(_ => new PracticeProcessResult(0, string.Empty)), TimeSpan.FromSeconds(1));

        Assert.That(async () => await slicer.SliceAsync(
            new PracticeAudioSliceRequest(source, 0, 1000, "output.ogg"), Path.Combine(directory, "output.ogg"), cancellation.Token),
            Throws.TypeOf<OperationCanceledException>());
    }

    [Test]
    public void PackagesOnlyGeneratedMapAndAudioOutsideExportFolder()
    {
        string exportDirectory = Path.Combine(directory, "map");
        Directory.CreateDirectory(exportDirectory);
        string beatmap = Path.Combine(exportDirectory, "practice.osu");
        string audio = Path.Combine(exportDirectory, "practice.ogg");
        File.WriteAllText(beatmap, "osu file format v14");
        File.WriteAllBytes(audio, new byte[128]);
        string archive = Path.Combine(directory, "practice.osz");

        PracticeMapPackageService.Create(new PracticeMapExportResult(exportDirectory, beatmap, audio), archive);

        using ZipArchive zip = ZipFile.OpenRead(archive);
        Assert.That(zip.Entries.Select(entry => entry.FullName), Is.EquivalentTo(new[] { "practice.osu", "practice.ogg" }));
    }

    private sealed class FakeRunner(Func<ProcessStartInfo, PracticeProcessResult> run) : IPracticeProcessRunner
    {
        public Task<PracticeProcessResult> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(run(startInfo));
        }
    }

    private static async Task<string> runTool(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new IOException($"Could not start {Path.GetFileName(executable)}.");
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidDataException($"{Path.GetFileName(executable)} failed: {error}");
        return output;
    }
}
