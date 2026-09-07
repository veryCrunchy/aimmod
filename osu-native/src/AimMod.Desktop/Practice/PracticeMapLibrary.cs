using System.Text.Json;

namespace AimMod.Desktop.Practice;

public sealed record SavedPracticeMap(
    string Id, string Title, string Difficulty, PracticeDrillType Scenario,
    DateTimeOffset CreatedAt, double SourceStartMs, double SourceEndMs,
    double DurationMs, int Repetitions, int ObjectCount, bool Favourite = false, double PlaybackRate = 1, PracticeTracking? Tracking = null, bool Automatic = false, Guid RevisionScoreId = default, DateTimeOffset? RetiredAt = null, bool PayloadRemoved = false);

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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,SemaphoreSlim> gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim ioGate;

    public PracticeMapLibrary(string root) { this.root = Path.GetFullPath(root); ioGate = gates.GetOrAdd(this.root, _ => new(1,1)); }

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
                if (!File.Exists(metadata) || new FileInfo(metadata).Length > 512_000) continue;
                SavedPracticeMap? entry = JsonSerializer.Deserialize<SavedPracticeMap>(File.ReadAllText(metadata));
                if (entry?.Id == id && (entry.PayloadRemoved || File.Exists(ArchivePath(id)))) maps.Add(entry);
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
        var matches = maps.Where(map => !map.PayloadRemoved && (!favourites || map.Favourite) && (scenario is null || map.Scenario == scenario || map.Tracking?.Difficulties.Any(d=>d.Skill == scenario) == true)
            && (map.Title.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                || map.Difficulty.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                || map.Tracking?.Difficulties.Any(d=>d.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)) == true));
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
        if (!map.PayloadRemoved && !File.Exists(ArchivePath(map.Id))) throw new FileNotFoundException("The practice package is missing.");
        string target = Path.Combine(directory, "practice.json");
        string temporary = target + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(map));
        File.Move(temporary, target, true);
    }

    public PracticeProgress LoadProgress(string id)
    {
        string path = Path.Combine(ownedDirectory(id), "progress.json");
        if (!File.Exists(path)) return PracticeProgress.Empty;
        if (new FileInfo(path).Length > 64_000_000) throw new InvalidDataException("Practice history is too large.");
        return JsonSerializer.Deserialize<PracticeProgress>(File.ReadAllText(path)) ?? PracticeProgress.Empty;
    }
    public IReadOnlyList<PracticeSetProgress> RefreshProgress(IEnumerable<AimMod.Desktop.LocalLibrary.LocalReplay> history, int accountId)
    {
        var runs=history.ToArray(); var result=new List<PracticeSetProgress>();
        foreach (var map in List().Where(m=>m.Tracking is not null && (m.Tracking.AccountId == 0 || m.Tracking.AccountId == accountId)))
        {
            var previous=LoadProgress(map.Id); var next=PracticeProgressTracker.Reconcile(map,previous,runs,accountId);
            if (!previous.Attempts.SequenceEqual(next.Attempts))
            {
                string path=Path.Combine(ownedDirectory(map.Id),"progress.json");
                File.WriteAllText(path+".tmp",JsonSerializer.Serialize(next)); File.Move(path+".tmp",path,true);
            }
            result.Add(new(map,next));
        }
        return result;
    }

    public void RetireAutomatic(SavedPracticeMap map, DateTimeOffset now)
    {
        if (!map.Automatic || map.Favourite || map.RetiredAt is not null) return;
        Save(map with { RetiredAt = now });
    }

    public void PruneRetiredPayload(SavedPracticeMap map)
    {
        if (!map.Automatic || map.Favourite || map.RetiredAt is null) return;
        string directory = ownedDirectory(map.Id);
        checkTree(directory);
        // Keep identities and recorded progress even after generated audio is removed.
        Save(map with { PayloadRemoved = true });
        string generated = Path.GetFullPath(Path.Combine(directory, "map"));
        if (!generated.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid generated directory.");
        if (Directory.Exists(generated)) Directory.Delete(generated, true);
        if (File.Exists(ArchivePath(map.Id))) File.Delete(ArchivePath(map.Id));
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
