using System.Diagnostics;

namespace AimMod.Desktop.Practice;

public sealed record PracticeProcessResult(int ExitCode, string StandardError);

public interface IPracticeProcessRunner
{
    Task<PracticeProcessResult> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class PracticeProcessRunner : IPracticeProcessRunner
{
    public async Task<PracticeProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new IOException("FFmpeg could not be started.");

        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw new TimeoutException("Audio preparation took too long.");
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }

        return new PracticeProcessResult(process.ExitCode, await error.ConfigureAwait(false));
    }
}

public sealed class WindowsFfmpegAudioSlicer : IPracticeAudioSlicer
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    private readonly string executablePath;
    private readonly IPracticeProcessRunner runner;
    private readonly TimeSpan timeout;

    public WindowsFfmpegAudioSlicer()
        : this(FfmpegExecutableLocator.Find() ?? throw new FileNotFoundException("FFmpeg is not installed or could not be found."),
            new PracticeProcessRunner(), DefaultTimeout)
    {
    }

    internal WindowsFfmpegAudioSlicer(string executablePath, IPracticeProcessRunner runner, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        this.executablePath = executablePath;
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.timeout = timeout > TimeSpan.Zero ? timeout : throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task SliceAsync(
        PracticeAudioSliceRequest request,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();

        string source = Path.GetFullPath(request.SourceAudioPath);
        string destination = Path.GetFullPath(destinationPath);
        if (!File.Exists(source))
            throw new FileNotFoundException("The source beatmap audio is unavailable.", source);
        if (request.SourceStartTimeMs < 0 || request.SourceEndTimeMs <= request.SourceStartTimeMs)
            throw new ArgumentOutOfRangeException(nameof(request), "The requested audio range is invalid.");
        if (request.RepeatCount is < 1 or > 24)
            throw new ArgumentOutOfRangeException(nameof(request), "The requested repetition count is invalid.");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        ProcessStartInfo startInfo = CreateStartInfo(executablePath, request, destination);
        PracticeProcessResult result = await runner.RunAsync(startInfo, timeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidDataException("FFmpeg could not prepare the practice audio.");
        if (!File.Exists(destination) || new FileInfo(destination).Length < 64)
            throw new InvalidDataException("FFmpeg did not produce usable practice audio.");
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executablePath,
        PracticeAudioSliceRequest request,
        string destinationPath)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                     "-i", Path.GetFullPath(request.SourceAudioPath),
                     "-filter_complex", CreateRepeatFilter(request),
                     "-map", "[practice]", "-vn", "-map_metadata", "-1",
                     "-c:a", "libvorbis", "-ar", "48000", "-ac", "2", destinationPath,
                 })
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    internal static string CreateRepeatFilter(PracticeAudioSliceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RepeatCount is < 1 or > 24)
            throw new ArgumentOutOfRangeException(nameof(request));
        string start = (request.SourceStartTimeMs / 1000d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        string end = (request.SourceEndTimeMs / 1000d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        string duration = (request.CycleDurationMs / 1000d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        string trim = $"[0:a]atrim=start={start}:end={end},asetpts=PTS-STARTPTS,aresample=48000:first_pts=0,apad,atrim=duration={duration}";
        if (request.RepeatCount == 1)
            return trim + "[practice]";

        string outputs = string.Concat(Enumerable.Range(0, request.RepeatCount).Select(index => $"[round{index}]"));
        return $"{trim},asplit={request.RepeatCount}{outputs};{outputs}concat=n={request.RepeatCount}:v=0:a=1[practice]";
    }
}

public static class FfmpegExecutableLocator
{
    public static string? Find() => Find(
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static string? Find(string? path, string? localAppData)
    {
        string filename = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (string directory in (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.GetFullPath(Path.Combine(directory.Trim('"'), filename));
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { }
        }

        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(localAppData))
            return null;
        string packages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(packages))
            return null;

        try
        {
            return Directory.EnumerateFiles(packages, "ffmpeg.exe", SearchOption.AllDirectories)
                            .Where(candidate => candidate.Contains("Gyan.FFmpeg_", StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(File.GetLastWriteTimeUtc)
                            .FirstOrDefault();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
}
