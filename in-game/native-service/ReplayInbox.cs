using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AimMod.InGame;

/// <summary>
/// Replays the player saved outside the library (a file someone sent them, or
/// one of their own exports), offered for import from two fixed folders:
/// Downloads and Documents\AimMod\Replays. Gameface has no file picker or
/// file drop, so the workspace lists these folders instead. Only bare file
/// names inside them are accepted; nothing else on disk can be named.
/// </summary>
sealed class ReplayInbox
{
    public const string Downloads = "downloads", Exports = "exports";
    const long MaxBytes = 8 * 1024 * 1024;
    const int MaxListed = 100;
    static readonly Regex NamePattern = new(@"^[^\\/:*?""<>|\x00-\x1f]{1,120}\.amreplay\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    static readonly Regex IdPattern = new(@"^[A-Za-z0-9_-]{1,100}\z", RegexOptions.CultureInvariant);
    readonly string output;
    readonly ReplayCatalog catalog;
    readonly IReadOnlyDictionary<string, string?> folders;

    public ReplayInbox(string output, ReplayCatalog catalog, string? downloads = null, string? exports = null)
    {
        this.output = output; this.catalog = catalog;
        folders = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [Downloads] = downloads ?? DownloadsFolder(),
            [Exports] = exports ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AimMod", "Replays"),
        };
    }

    public sealed record Entry(string Source, string Name, long Size, string ModifiedAt, string? Id, string? Scenario, string? RecordedAt, bool Supported, bool InLibrary);

    public IReadOnlyList<Entry> List()
    {
        var result = new List<Entry>();
        foreach (var (source, folder) in folders)
        {
            if (folder is null || !Directory.Exists(folder)) continue;
            IEnumerable<FileInfo> files;
            try { files = new DirectoryInfo(folder).EnumerateFiles("*.amreplay", SearchOption.TopDirectoryOnly).OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxListed).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                if (!Acceptable(file)) continue;
                string? id = null, scenario = null, recordedAt = null;
                try
                {
                    using var stream = file.OpenRead();
                    using var header = ReplayFormat2.ReadHeader(stream);
                    if (header is not null)
                    {
                        var root = header.RootElement;
                        id = root.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                        scenario = root.TryGetProperty("scenario", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                        recordedAt = root.TryGetProperty("recordedAt", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or EndOfStreamException) { }
                var supported = id is not null && IdPattern.IsMatch(id);
                result.Add(new(source, file.Name, file.Length, file.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"), supported ? id : null,
                    supported ? scenario : null, supported ? recordedAt : null, supported, supported && catalog.Resolve(id!) is not null));
            }
        }
        return result.OrderByDescending(e => e.ModifiedAt, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Imports one listed file. Returns the replay id, or an error code.</summary>
    public (string? Id, string? Scenario, string? Error) Import(string? source, string? name)
    {
        if (source is null || name is null || !folders.TryGetValue(source, out var folder) || folder is null) return (null, null, "unknown-source");
        if (!NamePattern.IsMatch(name) || Path.GetFileName(name) != name || name.Trim() != name || name.StartsWith('.')) return (null, null, "invalid-name");
        var root = Path.GetFullPath(folder);
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!string.Equals(Path.GetDirectoryName(path), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return (null, null, "invalid-name");
        var file = new FileInfo(path);
        if (!file.Exists) return (null, null, "missing");
        if (!Acceptable(file)) return (null, null, "unsupported-format");
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, null, "unreadable"); }
        return ReplayImport.Import(output, bytes);
    }

    static bool Acceptable(FileInfo file) =>
        file.Length is >= 24 and <= MaxBytes && !file.Attributes.HasFlag(FileAttributes.ReparsePoint);

    // The Downloads known folder (it can be moved); the profile's Downloads folder otherwise.
    static string? DownloadsFolder()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(ref id, 0, 0, out var pointer) == 0)
            {
                try { var path = Marshal.PtrToStringUni(pointer); if (!string.IsNullOrEmpty(path)) return path; }
                finally { Marshal.FreeCoTaskMem(pointer); }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "Downloads");
    }

    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out nint path);
}
