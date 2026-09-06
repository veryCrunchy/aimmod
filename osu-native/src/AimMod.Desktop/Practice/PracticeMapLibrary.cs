using System.Text.Json;

namespace AimMod.Desktop.Practice;

public sealed record SavedPracticeMap(
    string Id, string Title, string Difficulty, PracticeDrillType Scenario,
    DateTimeOffset CreatedAt, double SourceStartMs, double SourceEndMs,
    double DurationMs, int Repetitions, int ObjectCount, bool Favourite = false, double PlaybackRate = 1);

public sealed record PracticeWorkspaceSettings(
    string Search = "", PracticeCandidateSort Sort = PracticeCandidateSort.WeakestFirst,
    PracticeEvidenceFilter Evidence = PracticeEvidenceFilter.AnyEvidence,
    double MinimumStars = 0, double MaximumStars = 10,
    int DurationSeconds = 60, int MinimumRounds = 6, int LeadInSeconds = 4,
    int PaddingSeconds = 3, int SpeedPercent = 100);

public enum PracticeLibrarySort { Newest, Title, Duration, Favourites }

/// <summary>Owns only generated practice packages, never the player's installed maps.</summary>
public sealed class PracticeMapLibrary
{
    private readonly string root;
    private readonly SemaphoreSlim ioGate = new(1, 1);

    public PracticeMapLibrary(string root) => this.root = Path.GetFullPath(root);

    public async Task<T> RunAsync<T>(Func<T> action, CancellationToken token = default)
    {
        await ioGate.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(action, token).ConfigureAwait(false); }
        finally { ioGate.Release(); }
    }

    public Task SaveSettingsAsync(PracticeWorkspaceSettings settings) => RunAsync(() => { SaveSettings(settings); return true; });
    public Task<IReadOnlyList<SavedPracticeMap>> ListAsync(CancellationToken token = default) => RunAsync(List, token);

    public PracticeWorkspaceSettings LoadSettings()
    {
        try
        {
            string path = Path.Combine(root, "workspace.json");
            return File.Exists(path) && new FileInfo(path).Length < 64_000
                ? JsonSerializer.Deserialize<PracticeWorkspaceSettings>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void SaveSettings(PracticeWorkspaceSettings settings)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "workspace.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(settings));
        File.Move(path + ".tmp", path, true);
    }

    public IReadOnlyList<SavedPracticeMap> List()
    {
        if (!Directory.Exists(root)) return [];
        var maps = new List<SavedPracticeMap>();
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                string id = Path.GetFileName(directory);
                if (!validId(id) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                string metadata = Path.Combine(directory, "practice.json");
                if (!File.Exists(metadata)) migrateLegacy(directory, id);
                if (!File.Exists(metadata) || new FileInfo(metadata).Length > 64_000) continue;
                SavedPracticeMap? entry = JsonSerializer.Deserialize<SavedPracticeMap>(File.ReadAllText(metadata));
                if (entry?.Id == id && File.Exists(ArchivePath(id))) maps.Add(entry);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
        }
        return maps.OrderByDescending(map => map.CreatedAt).ToArray();
    }

    private void migrateLegacy(string directory, string id)
    {
        string files = Path.Combine(directory, "map");
        if (!File.Exists(ArchivePath(id)) || !Directory.Exists(files)
            || (File.GetAttributes(files) & FileAttributes.ReparsePoint) != 0) return;
        string? path = Directory.EnumerateFiles(files, "*.osu").FirstOrDefault();
        if (path is null || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
        PracticeSourceBeatmap map = OsuPracticeBeatmapReader.Read(path);
        if (map.HitObjects.Count == 0) return;
        string version = map.Metadata.Version;
        PracticeDrillType type = version.Contains("Streams", StringComparison.OrdinalIgnoreCase) ? PracticeDrillType.Streams
            : version.Contains("Long jumps", StringComparison.OrdinalIgnoreCase) ? PracticeDrillType.LongJumps : PracticeDrillType.Mixed;
        var match = System.Text.RegularExpressions.Regex.Match(version, @"\bx(\d+)\b");
        int repetitions = match.Success && int.TryParse(match.Groups[1].Value, out int number) ? number : 1;
        Save(new SavedPracticeMap(id, map.Metadata.Title, version, type, Directory.GetCreationTimeUtc(directory),
            0, 0, map.HitObjects.Max(item => item.EndTimeMs), repetitions, map.HitObjects.Count));
    }

    public static IReadOnlyList<SavedPracticeMap> Search(IEnumerable<SavedPracticeMap> maps, string search,
        PracticeDrillType? scenario, bool favourites, PracticeLibrarySort sort)
    {
        var matches = maps.Where(map => (!favourites || map.Favourite) && (scenario is null || map.Scenario == scenario)
            && (map.Title.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                || map.Difficulty.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)));
        return (sort switch
        {
            PracticeLibrarySort.Title => matches.OrderBy(map => map.Title, StringComparer.OrdinalIgnoreCase),
            PracticeLibrarySort.Duration => matches.OrderBy(map => map.DurationMs),
            PracticeLibrarySort.Favourites => matches.OrderByDescending(map => map.Favourite).ThenByDescending(map => map.CreatedAt),
            _ => matches.OrderByDescending(map => map.CreatedAt),
        }).ToArray();
    }

    public void Save(SavedPracticeMap map)
    {
        string directory = ownedDirectory(map.Id);
        if (!File.Exists(ArchivePath(map.Id))) throw new FileNotFoundException("The practice package is missing.");
        string target = Path.Combine(directory, "practice.json");
        string temporary = target + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(map));
        File.Move(temporary, target, true);
    }

    public string ArchivePath(string id) => Path.Combine(ownedDirectory(id), "AimMod practice.osz");

    public void Delete(string id)
    {
        string directory = ownedDirectory(id);
        if (!File.Exists(Path.Combine(directory, "practice.json"))) throw new InvalidOperationException("Not a managed practice map.");
        // Refuse redirected descendants before recursively removing owned generated files.
        checkTree(directory);
        Directory.Delete(directory, true);
    }

    private static void checkTree(string directory)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The practice directory contains a redirected path.");
            if ((attributes & FileAttributes.Directory) != 0) checkTree(path);
        }
    }

    private string ownedDirectory(string id)
    {
        if (!validId(id)) throw new ArgumentException("Invalid practice identifier.", nameof(id));
        string directory = Path.GetFullPath(Path.Combine(root, id));
        if (!directory.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid practice directory.");
        if ((Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            || (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0))
            throw new IOException("The practice directory is redirected.");
        return directory;
    }

    private static bool validId(string id) => Guid.TryParseExact(id, "N", out _)
        || (id.Length == 48 && id[15] == '-' && Guid.TryParseExact(id[16..], "N", out _)
            && DateTime.TryParseExact(id[..15], "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _));
}

public sealed record PracticeSectionChoice(PracticeDrillType Scenario, PracticeSourceSection Section);
