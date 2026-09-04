namespace AimMod.Desktop.Discovery;

public sealed record OsuStableInstallation(
    string CanonicalPath,
    string SongsPath,
    string SkinsPath,
    IReadOnlyList<string> Problems)
{
    public bool IsComplete => Problems.Count == 0;
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
            validateFile(root, "osu!.db", problems);
            string songs = resolveSongsDirectory(root, environment.CurrentUserName, problems);
            string skins = canonicalOptionalDirectory(root, "Skins");
            installations.Add(new OsuStableInstallation(root, songs, skins, problems));
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

        string resolveSongsDirectory(string root, string? currentUserName, ICollection<string> problems)
        {
            string configured = fileSystem.EnumerateFiles(root, "osu!.*.cfg")
                .OrderByDescending(path => matchesCurrentUser(path, currentUserName))
                .ThenByDescending(fileSystem.GetLastWriteTimeUtc)
                .Select(readBeatmapDirectory)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
                ?? "Songs";
            string candidate = Path.IsPathFullyQualified(configured)
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

        string? readBeatmapDirectory(string path)
        {
            try
            {
                foreach (string line in fileSystem.ReadAllText(path, 2 * 1024 * 1024).Split('\n'))
                {
                    string[] pair = line.Trim().Split('=', 2);
                    if (pair.Length == 2 && string.Equals(pair[0].Trim(), "BeatmapDirectory", StringComparison.OrdinalIgnoreCase))
                        return pair[1].Trim().Trim('"');
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
            }
            return null;
        }

        static bool matchesCurrentUser(string path, string? currentUserName) =>
            !string.IsNullOrWhiteSpace(currentUserName)
            && string.Equals(Path.GetFileName(path), $"osu!.{currentUserName}.cfg", StringComparison.OrdinalIgnoreCase);

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
}
