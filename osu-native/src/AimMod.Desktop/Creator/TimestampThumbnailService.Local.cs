using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AimMod.Desktop.Practice;

namespace AimMod.Desktop.Creator;

public sealed partial class TimestampThumbnailService
{
    internal static string LocalCacheIdentity(string location)
    {
        LocalFootagePlayer.ValidateFile(location);
        var file = new FileInfo(location);
        return $"local-v1:{file.FullName}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
    }

    private async Task<TimestampThumbnail> localFrame(string location, double seconds, CancellationToken token)
    {
        string identity = LocalCacheIdentity(location);
        await gate.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            Prune(cacheDirectory, DateTime.UtcNow);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + ":" + seconds.ToString("F3", CultureInfo.InvariantCulture))));
            string output = Path.Combine(cacheDirectory, key + ".jpg");
            if (File.Exists(output)) return new(output, seconds);
            string? executable = FfmpegExecutableLocator.Find();
            if (executable is null && OperatingSystem.IsWindows())
            {
                string cached = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AimMod", "tools", "ffmpeg", FfmpegInstaller.Version, "ffmpeg.exe");
                if (File.Exists(cached)) executable = cached;
            }
            if (executable is null) throw new InvalidOperationException("Install FFmpeg to preview local recordings. You can still open the video at this time.");
            temporary = output + ".tmp";
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-protocol_whitelist", "file,pipe", "-ss", seconds.ToString("R", CultureInfo.InvariantCulture),
                "-i", location, "-map", "0:v:0", "-frames:v", "1", "-an", "-sn", "-vf", "scale=960:540:force_original_aspect_ratio=decrease", "-q:v", "3", "-f", "image2", "-update", "1", temporary })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The local preview could not start.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var stop = deadline.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            // Drain without retaining diagnostics, which can contain private paths.
            Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            Task stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(stderr, stdout).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || !File.Exists(temporary) || new FileInfo(temporary).Length is < 1000 or > 2 * 1024 * 1024)
                throw new InvalidOperationException("No video frame was found at this time. Check the recording's duration and timestamp.");
            File.Move(temporary, output, true);
            return new(output, seconds);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException("The local preview took too long. Open the recording to check this moment.");
        }
        finally
        {
            if (temporary is not null) File.Delete(temporary);
            gate.Release();
        }
    }
}
