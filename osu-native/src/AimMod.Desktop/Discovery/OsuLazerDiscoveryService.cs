namespace AimMod.Desktop.Discovery;

public sealed class OsuLazerDiscoveryService
{
    public const string DataRootEnvironmentVariable = "AIMMOD_OSU_LAZER_DATA_DIR";
    public const int MaximumStorageConfigurationBytes = 64 * 1024;

    private readonly IOsuDiscoveryFileSystem fileSystem;

    public OsuLazerDiscoveryService(IOsuDiscoveryFileSystem fileSystem)
    {
        this.fileSystem = fileSystem;
    }

    public OsuLazerDiscoveryResult Discover(OsuHostPlatform platform, OsuDiscoveryEnvironment environment)
    {
        List<DataRootCandidate> directCandidates = buildConventionalCandidates(platform, environment);
        var candidates = new List<DataRootCandidate>(directCandidates);
        var rejected = new List<RejectedOsuDataRoot>();

        foreach (DataRootCandidate candidate in directCandidates)
        {
            StorageRedirectParseResult redirect = inspectStorageRedirect(platform, candidate, rejected);
            if (redirect.FullPath is not null)
                candidates.Add(new DataRootCandidate(redirect.FullPath, OsuDataRootSource.StorageRedirect));
        }

        var discoveredByPath = new Dictionary<string, MutableDataRoot>(OsuDiscoveryPath.Comparer(platform));

        foreach (DataRootCandidate candidate in candidates.OrderBy(candidate => sourcePriority(candidate.Source)))
        {
            string? canonicalRoot = fileSystem.CanonicalizeExisting(candidate.Path);
            if (canonicalRoot is null)
            {
                rejected.Add(new RejectedOsuDataRoot(candidate.Path, candidate.Source, "The directory does not exist or could not be resolved safely."));
                continue;
            }

            DiscoveryEntry rootEntry = fileSystem.Inspect(canonicalRoot);
            if (rootEntry.Kind != DiscoveryEntryKind.Directory)
            {
                rejected.Add(new RejectedOsuDataRoot(candidate.Path, candidate.Source, "The resolved path is not a directory."));
                continue;
            }

            RootValidation validation = validateRoot(platform, canonicalRoot);
            if (!validation.HasAnyMarker)
            {
                rejected.Add(new RejectedOsuDataRoot(candidate.Path, candidate.Source, "No osu!lazer data markers were found."));
                continue;
            }

            if (discoveredByPath.TryGetValue(canonicalRoot, out MutableDataRoot? existing))
            {
                existing.Sources.Add(candidate.Source);
                continue;
            }

            discoveredByPath.Add(canonicalRoot, new MutableDataRoot(
                canonicalRoot,
                new HashSet<OsuDataRootSource> { candidate.Source },
                validation.HasClientRealm,
                validation.HasFileStore,
                validation.HasGameConfiguration,
                validation.Problems));
        }

        OsuLazerDataRoot[] roots = discoveredByPath.Values
                                                       .Select(root => root.ToImmutable())
                                                       .OrderByDescending(root => root.IsComplete)
                                                       .ThenBy(root => root.CanonicalPath, OsuDiscoveryPath.Comparer(platform))
                                                       .ToArray();
        return new OsuLazerDiscoveryResult(roots, rejected);
    }

    public static StorageRedirectParseResult ParseStorageConfiguration(OsuHostPlatform platform, string contents)
    {
        string? fullPath = null;

        foreach (string rawLine in contents.TrimStart('\uFEFF').Split('\n'))
        {
            string line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line[0] is '#' or ';' || !line.Contains('='))
                continue;

            string[] pair = line.Split('=', 2);
            if (!string.Equals(pair[0].Trim(), "FullPath", StringComparison.OrdinalIgnoreCase))
                continue;

            if (fullPath is not null)
                return new StorageRedirectParseResult(null, "storage.ini contains more than one FullPath value.");

            string value = pair[1].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1].Trim();

            if (!OsuDiscoveryPath.IsAbsolute(platform, value))
                return new StorageRedirectParseResult(null, "storage.ini FullPath must be an absolute path.");

            fullPath = value;
        }

        return new StorageRedirectParseResult(fullPath, null);
    }

    private StorageRedirectParseResult inspectStorageRedirect(
        OsuHostPlatform platform,
        DataRootCandidate candidate,
        ICollection<RejectedOsuDataRoot> rejected)
    {
        string storagePath = OsuDiscoveryPath.Combine(platform, candidate.Path, "storage.ini");
        DiscoveryEntry entry = fileSystem.Inspect(storagePath);
        if (entry.Kind == DiscoveryEntryKind.Missing)
            return new StorageRedirectParseResult(null, null);

        if (entry.Kind != DiscoveryEntryKind.File)
            return reject("storage.ini is not a regular file.");

        if (entry.IsSymbolicLink)
            return reject("storage.ini is a symbolic link and was not followed.");

        if (entry.Length is <= 0 or > MaximumStorageConfigurationBytes)
            return reject("storage.ini is empty or larger than 64 KiB.");

        try
        {
            StorageRedirectParseResult parsed = ParseStorageConfiguration(platform, fileSystem.ReadAllText(storagePath, MaximumStorageConfigurationBytes));
            if (parsed.Error is not null)
                rejected.Add(new RejectedOsuDataRoot(storagePath, OsuDataRootSource.StorageRedirect, parsed.Error));
            return parsed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return reject($"storage.ini could not be read: {error.Message}");
        }

        StorageRedirectParseResult reject(string reason)
        {
            rejected.Add(new RejectedOsuDataRoot(storagePath, OsuDataRootSource.StorageRedirect, reason));
            return new StorageRedirectParseResult(null, reason);
        }
    }

    private RootValidation validateRoot(OsuHostPlatform platform, string canonicalRoot)
    {
        MarkerValidation database = validateMarker(platform, canonicalRoot, "client.realm", DiscoveryEntryKind.File, requireNonEmpty: true);
        MarkerValidation files = validateMarker(platform, canonicalRoot, "files", DiscoveryEntryKind.Directory, requireNonEmpty: false);
        MarkerValidation configuration = validateMarker(platform, canonicalRoot, "game.ini", DiscoveryEntryKind.File, requireNonEmpty: true);
        string[] problems = new[] { database, files, configuration }
                            .Where(marker => !marker.Valid)
                            .Select(marker => marker.Problem!)
                            .ToArray();

        return new RootValidation(
            database.Valid,
            files.Valid,
            configuration.Valid,
            database.Present || files.Present || configuration.Present,
            problems);
    }

    private MarkerValidation validateMarker(
        OsuHostPlatform platform,
        string canonicalRoot,
        string name,
        DiscoveryEntryKind expectedKind,
        bool requireNonEmpty)
    {
        string markerPath = OsuDiscoveryPath.Combine(platform, canonicalRoot, name);
        DiscoveryEntry entry = fileSystem.Inspect(markerPath);
        if (entry.Kind == DiscoveryEntryKind.Missing)
            return new MarkerValidation(false, false, $"{name} is missing.");

        if (entry.Kind != expectedKind)
            return new MarkerValidation(true, false, $"{name} has the wrong file type.");

        string? canonicalMarker = fileSystem.CanonicalizeExisting(markerPath);
        if (canonicalMarker is null || !OsuDiscoveryPath.IsWithin(platform, canonicalMarker, canonicalRoot))
            return new MarkerValidation(true, false, $"{name} resolves outside the data root.");

        if (requireNonEmpty && entry.Length <= 0)
            return new MarkerValidation(true, false, $"{name} is empty.");

        return new MarkerValidation(true, true, null);
    }

    private static List<DataRootCandidate> buildConventionalCandidates(OsuHostPlatform platform, OsuDiscoveryEnvironment environment)
    {
        var candidates = new List<DataRootCandidate>();
        add(environment.ExplicitDataRoot, OsuDataRootSource.EnvironmentOverride);

        switch (platform)
        {
            case OsuHostPlatform.Linux:
                if (!string.IsNullOrWhiteSpace(environment.XdgDataHome))
                    add(OsuDiscoveryPath.Combine(platform, environment.XdgDataHome, "osu"), OsuDataRootSource.ConventionalLocation);
                if (!string.IsNullOrWhiteSpace(environment.HomeDirectory))
                {
                    add(OsuDiscoveryPath.Combine(platform, environment.HomeDirectory, ".local", "share", "osu"), OsuDataRootSource.ConventionalLocation);
                    add(OsuDiscoveryPath.Combine(platform, environment.HomeDirectory, ".var", "app", "sh.ppy.osu", "data", "osu"), OsuDataRootSource.ConventionalLocation);
                }
                break;

            case OsuHostPlatform.Windows:
                if (!string.IsNullOrWhiteSpace(environment.AppData))
                    add(OsuDiscoveryPath.Combine(platform, environment.AppData, "osu"), OsuDataRootSource.ConventionalLocation);
                break;

            case OsuHostPlatform.MacOS:
                if (!string.IsNullOrWhiteSpace(environment.HomeDirectory))
                    add(OsuDiscoveryPath.Combine(platform, environment.HomeDirectory, "Library", "Application Support", "osu"), OsuDataRootSource.ConventionalLocation);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(platform), platform, null);
        }

        return candidates;

        void add(string? path, OsuDataRootSource source)
        {
            if (!string.IsNullOrWhiteSpace(path))
                candidates.Add(new DataRootCandidate(path.Trim(), source));
        }
    }

    private static int sourcePriority(OsuDataRootSource source) => source switch
    {
        OsuDataRootSource.EnvironmentOverride => 0,
        OsuDataRootSource.StorageRedirect => 1,
        _ => 2,
    };

    private sealed record DataRootCandidate(string Path, OsuDataRootSource Source);

    private sealed record MarkerValidation(bool Present, bool Valid, string? Problem);

    private sealed record RootValidation(
        bool HasClientRealm,
        bool HasFileStore,
        bool HasGameConfiguration,
        bool HasAnyMarker,
        IReadOnlyList<string> Problems);

    private sealed record MutableDataRoot(
        string CanonicalPath,
        HashSet<OsuDataRootSource> Sources,
        bool HasClientRealm,
        bool HasFileStore,
        bool HasGameConfiguration,
        IReadOnlyList<string> Problems)
    {
        public OsuLazerDataRoot ToImmutable() => new(
            CanonicalPath,
            Sources.OrderBy(sourcePriority).ToArray(),
            HasClientRealm,
            HasFileStore,
            HasGameConfiguration,
            Problems);
    }
}
