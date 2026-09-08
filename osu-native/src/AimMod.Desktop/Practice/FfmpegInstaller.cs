using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace AimMod.Desktop.Practice;

public sealed class FfmpegSetupException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>Installs a private, checksum-pinned Windows tool without changing PATH.</summary>
internal sealed class FfmpegInstaller
{
    internal const string Version = "9.0.1";
    internal const string DownloadUrl = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.1/ffmpeg-9.0.1-essentials_build.zip";
    internal const string ArchiveHash = "fec81ae03971d9dd4be3ebe02e263bd2ec1d789483f931bdba5f5715e65da2e9";
    private static readonly HttpClient client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly SemaphoreSlim gate = new(1, 1);
    private readonly HttpClient http;
    private readonly string directory;
    private readonly string hash;

    internal FfmpegInstaller(HttpClient http, string directory, string hash)
    {
        this.http = http;
        this.directory = directory;
        this.hash = hash;
    }

    internal static async Task<string> ResolveAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (FfmpegExecutableLocator.Find() is { } installed) return installed;
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
            throw new FfmpegSetupException("Install FFmpeg and try creating the practice set again.");
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AimMod", "tools", "ffmpeg", Version);
        return await new FfmpegInstaller(client, root, ArchiveHash).EnsureAsync(token).ConfigureAwait(false);
    }

    internal async Task<string> EnsureAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        string? staging = null;
        bool acquired = false;
        try
        {
            await gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            string executable = Path.Combine(directory, "ffmpeg.exe");
            if (File.Exists(executable) && new FileInfo(executable).Length > 0) return executable;
            string parent = Path.GetDirectoryName(directory)!;
            Directory.CreateDirectory(parent);
            staging = Path.Combine(parent, ".download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            string archive = Path.Combine(staging, "download.zip");
            using (var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                await using var output = File.Create(archive);
                await copyBoundedAsync(input, output, 160 * 1024 * 1024, deadline.Token).ConfigureAwait(false);
            }
            await using (var input = File.OpenRead(archive))
            {
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(input, deadline.Token).ConfigureAwait(false));
                if (!actual.Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("FFmpeg download checksum mismatch.");
            }
            using (var zip = ZipFile.OpenRead(archive))
            {
                // Fixed destinations prevent archive paths from escaping the staging directory.
                foreach (string name in new[] { "ffmpeg.exe", "LICENSE", "README.txt" })
                {
                    string suffix = name == "ffmpeg.exe" ? "/bin/ffmpeg.exe" : "/" + name;
                    var entry = zip.Entries.SingleOrDefault(e => e.FullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                    if (entry is null) throw new InvalidDataException("FFmpeg package is incomplete.");
                    await using var input = entry.Open();
                    await using var output = File.Create(Path.Combine(staging, name));
                    await copyBoundedAsync(input, output, name == "ffmpeg.exe" ? 200 * 1024 * 1024 : 1024 * 1024, deadline.Token).ConfigureAwait(false);
                }
            }
            if (new FileInfo(Path.Combine(staging, "ffmpeg.exe")).Length == 0) throw new InvalidDataException("FFmpeg executable is empty.");
            File.Delete(archive);
            deadline.Token.ThrowIfCancellationRequested();
            // Publish the executable last; interrupted installs never look complete.
            Directory.CreateDirectory(directory);
            foreach (string name in new[] { "LICENSE", "README.txt", "ffmpeg.exe" })
                File.Move(Path.Combine(staging, name), Path.Combine(directory, name), overwrite: true);
            return executable;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new FfmpegSetupException("The audio tool could not be downloaded. Check your connection and try creating the practice set again.", error);
        }
        finally
        {
            if (staging is not null)
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            if (acquired) gate.Release();
        }
    }

    private static async Task copyBoundedAsync(Stream input, Stream output, long limit, CancellationToken token)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            total += count;
            if (total > limit) throw new InvalidDataException("FFmpeg package exceeds its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
    }
}
