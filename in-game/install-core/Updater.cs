using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AimMod.InGame;

sealed record UpdatePreferences(
    [property: JsonPropertyName("autoUpdate")] bool AutoUpdate = true,
    [property: JsonPropertyName("channel")] string Channel = "stable",
    // Override for the feed location (HTTPS only), e.g. a Hub mirror.
    [property: JsonPropertyName("feedUrl")] string? FeedUrl = null);

// %LOCALAPPDATA%\AimMod\KovaaksNative\update-settings.json
sealed class UpdateSettings
{
    public const string DefaultFeed = "https://github.com/verycrunchy/aimmod/releases/download/aimmod-ingame-{channel}/aimmod-ingame-{channel}.json";
    const int Limit = 4096;
    readonly object gate = new();
    readonly string path;
    UpdatePreferences current = new();
    // Without saved preferences the channel follows the installed package, so a beta install keeps getting betas.
    public UpdateSettings(string directory, string? installedChannel = null)
    {
        path = Path.Combine(directory, "update-settings.json");
        if (installedChannel is "beta") current = current with { Channel = "beta" };
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length <= Limit) current = Validate(JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllBytes(path)) ?? new());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ReleaseFormatException)
        {
            // An unreadable preference never turns updates on by itself.
            current = new(AutoUpdate: false);
        }
    }
    static UpdatePreferences Validate(UpdatePreferences value)
    {
        if (value.Channel is not ("stable" or "beta")) throw new ReleaseFormatException("Invalid channel.");
        if (value.FeedUrl is not null && !UpdateFeed.IsHttps(value.FeedUrl.Replace("{channel}", "stable"))) throw new ReleaseFormatException("The feed must use HTTPS.");
        return value;
    }
    public UpdatePreferences Current { get { lock (gate) return current; } }
    public string FeedUrl(UpdatePreferences value) => (value.FeedUrl ?? DefaultFeed).Replace("{channel}", value.Channel);
    public UpdatePreferences ApplyJson(ReadOnlyMemory<byte> json)
    {
        if (json.Length is 0 or > 1024) throw new JsonException("Invalid size.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Expected an object.");
        bool? auto = null; string? channel = null; var count = 0;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            switch (property.Name)
            {
                case "autoUpdate" when auto is null && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False: auto = property.Value.GetBoolean(); break;
                case "channel" when channel is null && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is "stable" or "beta": channel = property.Value.GetString(); break;
                default: throw new JsonException("Unknown, duplicate or invalid setting.");
            }
            count++;
        }
        if (count == 0) throw new JsonException("No settings supplied.");
        lock (gate)
        {
            var next = current with { AutoUpdate = auto ?? current.AutoUpdate, Channel = channel ?? current.Channel };
            AtomicFile.WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(next, new JsonSerializerOptions { WriteIndented = true }));
            current = next;
            return next;
        }
    }
}

sealed record StagedUpdate(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("folder")] string Folder,
    [property: JsonPropertyName("manifestSha256")] string ManifestSha256,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("stagedAt")] string StagedAt);

enum UpdateState { Disabled, Unmanaged, Idle, Checking, UpToDate, Downloading, Ready, NeedsNewerGame, Failed }

sealed record UpdateCheck(UpdateState State, string? Version = null, string? Notes = null, string? Message = null);

// Checks the feed, downloads and verifies a newer package and stages
// it under updates\staged. Nothing here runs or installs what it downloads;
// PackageApplier applies a staged update after the game has closed.
sealed class Updater(string stateRoot, HttpMessageHandler? handler = null)
{
    readonly HttpClient http = CreateClient(handler);
    public string Root => stateRoot;
    string StagedPointer => Path.Combine(stateRoot, "staged.json");

    static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        // .NET never follows an HTTPS -> HTTP redirect; the final URL is checked too.
        handler ??= new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5, AutomaticDecompression = System.Net.DecompressionMethods.None, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AimMod-InGame-Updater", "1"));
        return client;
    }

    public StagedUpdate? Staged()
    {
        try
        {
            if (!File.Exists(StagedPointer) || new FileInfo(StagedPointer).Length > 64 * 1024) return null;
            var staged = JsonSerializer.Deserialize<StagedUpdate>(File.ReadAllBytes(StagedPointer));
            if (staged is null || !SemanticVersion.TryParse(staged.Version, out _)) return null;
            var folder = Path.GetFullPath(Path.Combine(stateRoot, "staged", staged.Folder));
            return Path.GetDirectoryName(folder) == Path.GetFullPath(Path.Combine(stateRoot, "staged")) && Directory.Exists(folder) ? staged : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public string StagedFolder(StagedUpdate staged) => Path.Combine(stateRoot, "staged", staged.Folder);
    // Re-verified (manifest hash and every file) just before it is applied.
    public VerifiedPackage OpenStaged(StagedUpdate staged) => VerifiedPackage.Open(StagedFolder(staged), staged.ManifestSha256);
    public void ClearStaged()
    {
        try { File.Delete(StagedPointer); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        CleanStaging(keep: null);
    }
    void CleanStaging(string? keep)
    {
        var folder = Path.Combine(stateRoot, "staged");
        if (!Directory.Exists(folder)) return;
        foreach (var dir in Directory.GetDirectories(folder))
            if (!string.Equals(Path.GetFileName(dir), keep, StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(dir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    async Task<byte[]> Fetch(string url, int limit, CancellationToken token)
    {
        if (!UpdateFeed.IsHttps(url)) throw new ReleaseFormatException("Updates are only downloaded over HTTPS.");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps) throw new ReleaseFormatException("The update server redirected away from HTTPS.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new ReleaseFormatException("The update file is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > limit) throw new ReleaseFormatException("The update file is too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public async Task<UpdateCheck> Check(UpdatePreferences preferences, string feedUrl, InstallManifest? installed, long? steamBuildId, CancellationToken token)
    {
        if (installed is null || !installed.Managed) return new(UpdateState.Unmanaged, Message: "This install was not made from an AimMod release.");
        var current = SemanticVersion.Parse(installed.Version);
        var feed = UpdateFeed.Parse(await FetchFeed(feedUrl, token));
        if (feed.Channel != preferences.Channel) throw new ReleaseFormatException("The update feed is for another channel.");
        var version = feed.SemVer;
        if (version <= current) { ClearStaged(); return new(UpdateState.UpToDate, current.ToString()); }
        if (feed.MinimumSteamBuildId > 0 && steamBuildId is long build && build < feed.MinimumSteamBuildId)
            return new(UpdateState.NeedsNewerGame, feed.Version, feed.Notes, "This update needs a newer KovaaK's. Update the game in Steam.");
        if (Staged() is { } staged && staged.Version == feed.Version && Sha256Hex.Same(staged.ManifestSha256, feed.ManifestSha256))
            return new(UpdateState.Ready, feed.Version, feed.Notes);
        await Download(feed, token);
        return new(UpdateState.Ready, feed.Version, feed.Notes);
    }

    // The raw feed (HTTPS only, at most UpdateFeed.MaximumBytes).
    public Task<byte[]> FetchFeed(string feedUrl, CancellationToken token) => Fetch(feedUrl, UpdateFeed.MaximumBytes, token);

    async Task Download(UpdateFeed feed, CancellationToken token)
    {
        var stagingRoot = Path.Combine(stateRoot, "staged");
        Directory.CreateDirectory(stagingRoot);
        var name = feed.Version + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var folder = Path.Combine(stagingRoot, name);
        var package = await DownloadPackage(feed, folder, Path.Combine(stateRoot, name + ".zip.part"), null, token);
        var staged = new StagedUpdate(feed.Version, feed.Channel, name, package.ManifestSha256, feed.Notes, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        AtomicFile.WriteBytes(StagedPointer, JsonSerializer.SerializeToUtf8Bytes(staged), durable: true);
        CleanStaging(keep: name);
    }

    // Downloads the feed's package to zipPath (size-capped, SHA-256 compared
    // with the feed before anything is extracted), extracts the files its
    // hash-pinned manifest lists into folder and verifies every one. The zip is
    // always deleted; the folder is deleted on any failure. progress gets the
    // bytes downloaded so far.
    public async Task<VerifiedPackage> DownloadPackage(UpdateFeed feed, string folder, string zipPath, IProgress<long>? progress, CancellationToken token)
    {
        if (!UpdateFeed.IsHttps(feed.Package.Url)) throw new ReleaseFormatException("Updates are only downloaded over HTTPS.");
        try
        {
            using (var response = await http.GetAsync(feed.Package.Url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps) throw new ReleaseFormatException("The update server redirected away from HTTPS.");
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(token);
                await using var target = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var chunk = new byte[1 << 16]; long total = 0; int read;
                while ((read = await source.ReadAsync(chunk, token)) > 0)
                {
                    total += read;
                    if (total > feed.Package.Size) throw new ReleaseFormatException("The update package is larger than announced.");
                    sha.AppendData(chunk, 0, read);
                    await target.WriteAsync(chunk.AsMemory(0, read), token);
                    progress?.Report(total);
                }
                if (total != feed.Package.Size || !Sha256Hex.Same(Convert.ToHexString(sha.GetHashAndReset()), feed.Package.Sha256))
                    throw new ReleaseFormatException("The update package does not match the update feed.");
            }
            ExtractVerified(zipPath, folder, feed.ManifestSha256);
            // The manifest hash and every file are checked again here.
            var package = VerifiedPackage.Open(folder, feed.ManifestSha256);
            // The beta feed also carries stable releases.
            if (package.Manifest.Version != feed.Version || (package.Manifest.Channel != feed.Channel && package.Manifest.Channel != "stable")) throw new ReleaseFormatException("The package version does not match the feed.");
            return package;
        }
        catch
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        finally { try { File.Delete(zipPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    // Extracts only the release manifest, the helper scripts and the files
    // the (hash-pinned) manifest lists, each capped at
    // its declared size. Anything else in the zip is ignored.
    internal static readonly string[] TopLevel = ["aimmod-release.json", "Install-AimMod.cmd", "Repair-AimMod.cmd", "Uninstall-AimMod.cmd", "README.txt"];
    internal static void ExtractVerified(string zipPath, string folder, string manifestSha256)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        ZipArchiveEntry? Find(string name) => archive.Entries.SingleOrDefault(e => e.FullName.Replace('\\', '/').Equals(name, StringComparison.OrdinalIgnoreCase));
        var manifestEntry = Find("aimmod-release.json") ?? throw new ReleaseFormatException("The package has no release manifest.");
        if (manifestEntry.Length > ReleaseManifest.MaximumBytes) throw new ReleaseFormatException("Release manifest has an invalid size.");
        Directory.CreateDirectory(folder);
        void Copy(ZipArchiveEntry entry, string destination, long limit)
        {
            if (entry.Length > limit) throw new ReleaseFormatException("A package file is larger than announced.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var chunk = new byte[1 << 16]; long total = 0; int read;
            while ((read = input.Read(chunk)) > 0)
            {
                total += read;
                if (total > limit) throw new ReleaseFormatException("A package file is larger than announced.");
                output.Write(chunk, 0, read);
            }
        }
        var manifestPath = Path.Combine(folder, "aimmod-release.json");
        Copy(manifestEntry, manifestPath, ReleaseManifest.MaximumBytes);
        var bytes = File.ReadAllBytes(manifestPath);
        if (!Sha256Hex.Same(Sha256Hex.Of(bytes), manifestSha256)) throw new ReleaseFormatException("The release manifest does not match the update feed.");
        var manifest = ReleaseManifest.Parse(bytes);
        foreach (var name in TopLevel.Skip(1))
            if (Find(name) is { } entry) Copy(entry, Path.Combine(folder, name), 256 * 1024);
        var files = Path.Combine(folder, "files");
        foreach (var file in manifest.Files)
        {
            var entry = Find("files/" + file.Path) ?? throw new ReleaseFormatException($"The package is missing {file.Path}.");
            Copy(entry, ReleasePaths.Combine(files, file.Path), file.Size);
        }
    }
}
