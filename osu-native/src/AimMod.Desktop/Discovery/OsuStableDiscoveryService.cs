namespace AimMod.Desktop.Discovery;

public sealed record OsuStableInstallation(
    string CanonicalPath,
    string SongsPath,
    string SkinsPath,
    IReadOnlyList<string> Problems)
{
    public bool IsComplete => Problems.Count == 0;
    public string? RememberedUsername { get; init; }
    public int? RememberedUserId { get; init; }
}

public sealed record OsuStableDiscoveryResult(
    IReadOnlyList<OsuStableInstallation> Installations,
    IReadOnlyList<RejectedOsuDataRoot> RejectedCandidates)
{
    public IReadOnlyList<OsuStableInstallation> CompleteInstallations =>
        Installations.Where(installation => installation.IsComplete).ToArray();
}

public sealed class OsuStableDiscoveryService
{
    public const string InstallRootEnvironmentVariable = "AIMMOD_OSU_STABLE_DIR";

    private readonly IOsuDiscoveryFileSystem fileSystem;

    public OsuStableDiscoveryService(IOsuDiscoveryFileSystem fileSystem)
    {
        this.fileSystem = fileSystem;
    }

    public OsuStableDiscoveryResult Discover(OsuHostPlatform platform, OsuDiscoveryEnvironment environment)
    {
        var candidates = new List<string>();
        add(environment.ExplicitStableRoot);

        if (platform == OsuHostPlatform.Windows)
        {
            if (!string.IsNullOrWhiteSpace(environment.LocalAppData))
                add(OsuDiscoveryPath.Combine(platform, environment.LocalAppData, "osu!"));
        }
        else if (platform == OsuHostPlatform.Linux && !string.IsNullOrWhiteSpace(environment.HomeDirectory))
        {
            add(OsuDiscoveryPath.Combine(platform, environment.HomeDirectory, ".osu"));
            add(OsuDiscoveryPath.Combine(platform, environment.HomeDirectory, ".wine", "drive_c", "osu!"));
        }

        var installations = new List<OsuStableInstallation>();
        var rejected = new List<RejectedOsuDataRoot>();
        var seen = new HashSet<string>(OsuDiscoveryPath.Comparer(platform));

        foreach (string candidate in candidates)
        {
            string? root = fileSystem.CanonicalizeExisting(candidate);
            if (root is null || !seen.Add(root))
            {
                if (root is null)
                    rejected.Add(new RejectedOsuDataRoot(candidate, OsuDataRootSource.ConventionalLocation, "The osu!stable directory does not exist or could not be resolved safely."));
                continue;
            }

            var problems = new List<string>();
            if (fileSystem.Inspect(root).Kind != DiscoveryEntryKind.Directory)
            {
                rejected.Add(new RejectedOsuDataRoot(candidate, OsuDataRootSource.ConventionalLocation, "The resolved stable path is not a directory."));
                continue;
            }
            validateFile(root, "osu!.db", problems);
            StableConfiguration configuration = readConfiguration(root, environment.CurrentUserName);
            string songs = resolveSongsDirectory(root, configuration.BeatmapDirectory, problems);
            string skins = canonicalOptionalDirectory(root, "Skins");
            installations.Add(new OsuStableInstallation(root, songs, skins, problems)
            {
                RememberedUsername = configuration.Username,
                RememberedUserId = readUserId(root, configuration.Username),
            });
        }

        return new OsuStableDiscoveryResult(
            installations.OrderByDescending(installation => installation.IsComplete).ToArray(),
            rejected);

        void add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                candidates.Add(path.Trim());
        }

        void validateFile(string root, string name, ICollection<string> problems)
        {
            string path = OsuDiscoveryPath.Combine(platform, root, name);
            DiscoveryEntry entry = fileSystem.Inspect(path);
            if (entry.Kind != DiscoveryEntryKind.File || entry.Length <= 0)
                problems.Add($"{name} is missing or empty.");
        }

        string resolveSongsDirectory(string root, string? beatmapDirectory, ICollection<string> problems)
        {
            string configured = beatmapDirectory ?? "Songs";
            string candidate = OsuDiscoveryPath.IsAbsolute(platform, configured)
                ? configured
                : OsuDiscoveryPath.Combine(platform, root, configured);
            string? canonical = fileSystem.CanonicalizeExisting(candidate);
            if (canonical is null || fileSystem.Inspect(canonical).Kind != DiscoveryEntryKind.Directory)
            {
                problems.Add("The configured BeatmapDirectory is missing or could not be resolved safely.");
                return candidate;
            }
            return canonical;
        }

        StableConfiguration readConfiguration(string root, string? currentUserName)
        {
            // Select one OS user's configuration, so absent values never come from a stale account.
            string? path = fileSystem.EnumerateFiles(root, "osu!.*.cfg")
                .OrderByDescending(path => matchesCurrentUser(path, currentUserName))
                .ThenByDescending(fileSystem.GetLastWriteTimeUtc)
                .ThenBy(path => path, OsuDiscoveryPath.Comparer(platform))
                .FirstOrDefault();
            if (path is null)
                return new StableConfiguration(null, null);
            try
            {
                string? username = null;
                string? beatmapDirectory = null;
                foreach (string line in fileSystem.ReadAllText(path, 2 * 1024 * 1024).TrimStart('\uFEFF').Split('\n'))
                {
                    string[] pair = line.Trim().Split('=', 2);
                    if (pair.Length != 2)
                        continue;
                    string value = pair[1].Trim().Trim('"').Trim();
                    string? optionalValue = value.Length == 0 ? null : value;
                    if (string.Equals(pair[0].Trim(), "BeatmapDirectory", StringComparison.OrdinalIgnoreCase))
                        beatmapDirectory = optionalValue;
                    else if (string.Equals(pair[0].Trim(), "Username", StringComparison.OrdinalIgnoreCase))
                        username = optionalValue;
                }
                if (username is not null && (username.Length > 255 || username.Any(char.IsControl)))
                    username = null;
                return new StableConfiguration(username, beatmapDirectory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
            }
            return new StableConfiguration(null, null);
        }

        int? readUserId(string root, string? username)
        {
            if (username is null)
                return null;
            string path = OsuDiscoveryPath.Combine(platform, root, "presence.db");
            if (fileSystem.Inspect(path).Kind != DiscoveryEntryKind.File)
                return null;
            try
            {
                using var stream = new MemoryStream(fileSystem.ReadAllBytes(path, 32 * 1024 * 1024), writable: false);
                var database = OsuParsers.Decoders.DatabaseDecoder.DecodePresence(stream);
                int[] ids = database.Players.Where(player => player.UserId > 0
                        && string.Equals(player.Username, username, StringComparison.OrdinalIgnoreCase))
                    .Select(player => player.UserId).Distinct().Take(2).ToArray();
                return ids.Length == 1 ? ids[0] : null;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                or ArgumentException or OverflowException)
            {
                return null;
            }
        }

        static bool matchesCurrentUser(string path, string? currentUserName) =>
            !string.IsNullOrWhiteSpace(currentUserName)
            && string.Equals(path.Replace('\\', '/').Split('/')[^1], $"osu!.{currentUserName}.cfg", StringComparison.OrdinalIgnoreCase);

        string canonicalOptionalDirectory(string root, string name)
        {
            string path = OsuDiscoveryPath.Combine(platform, root, name);
            string? canonical = fileSystem.CanonicalizeExisting(path);
            return canonical is not null
                   && fileSystem.Inspect(canonical).Kind == DiscoveryEntryKind.Directory
                   && OsuDiscoveryPath.IsWithin(platform, canonical, root)
                ? canonical
                : string.Empty;
        }
    }

    private sealed record StableConfiguration(string? Username, string? BeatmapDirectory);
}
