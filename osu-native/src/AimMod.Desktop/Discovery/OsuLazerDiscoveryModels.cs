namespace AimMod.Desktop.Discovery;

public enum OsuHostPlatform
{
    Linux,
    Windows,
    MacOS,
}

public enum OsuDataRootSource
{
    EnvironmentOverride,
    StorageRedirect,
    ConventionalLocation,
}

public enum DiscoveryEntryKind
{
    Missing,
    File,
    Directory,
}

public sealed record OsuDiscoveryEnvironment(
    string? HomeDirectory = null,
    string? XdgDataHome = null,
    string? AppData = null,
    string? ExplicitDataRoot = null);

public sealed record DiscoveryEntry(
    DiscoveryEntryKind Kind,
    long Length = 0,
    bool IsSymbolicLink = false);

public sealed record OsuLazerDataRoot(
    string CanonicalPath,
    IReadOnlyList<OsuDataRootSource> Sources,
    bool HasClientRealm,
    bool HasFileStore,
    bool HasGameConfiguration,
    IReadOnlyList<string> Problems)
{
    public bool IsComplete => HasClientRealm && HasFileStore && HasGameConfiguration && Problems.Count == 0;
}

public sealed record RejectedOsuDataRoot(string CandidatePath, OsuDataRootSource Source, string Reason);

public sealed record OsuLazerDiscoveryResult(
    IReadOnlyList<OsuLazerDataRoot> DataRoots,
    IReadOnlyList<RejectedOsuDataRoot> RejectedCandidates)
{
    public IReadOnlyList<OsuLazerDataRoot> CompleteDataRoots => DataRoots.Where(root => root.IsComplete).ToArray();
}

public sealed record StorageRedirectParseResult(string? FullPath, string? Error)
{
    public bool HasRedirect => FullPath is not null;
}

public interface IOsuDiscoveryFileSystem
{
    DiscoveryEntry Inspect(string path);

    string? CanonicalizeExisting(string path);

    string ReadAllText(string path, int maximumBytes);
}
