using System.Text.Json;

namespace AimMod.Desktop.Hub;

public sealed record HubSharingPreferences(
    OsuHubVisibility Visibility = OsuHubVisibility.Private,
    bool UploadReplayFile = false,
    bool UploadAnalysis = false)
{
    public static HubSharingPreferences Default { get; } = new();

    public HubSharingPreferences Normalised() => this with
    {
        Visibility = Enum.IsDefined(Visibility) ? Visibility : OsuHubVisibility.Private,
    };
}

public interface IHubSharingPreferenceStore
{
    HubSharingPreferences Load();
    Task SaveAsync(HubSharingPreferences preferences, CancellationToken cancellationToken = default);
}

public sealed class FileHubSharingPreferenceStore : IHubSharingPreferenceStore
{
    private const int current_version = 1;
    private static readonly JsonSerializerOptions json_options = new(JsonSerializerDefaults.Web);
    private readonly string path;

    public FileHubSharingPreferenceStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The Hub sharing preference path must be absolute.", nameof(path));
        this.path = path;
    }

    public HubSharingPreferences Load()
    {
        try
        {
            if (!File.Exists(path))
                return HubSharingPreferences.Default;
            using FileStream stream = File.OpenRead(path);
            PreferenceDocument? document = JsonSerializer.Deserialize<PreferenceDocument>(stream, json_options);
            return document?.Version == current_version && document.Preferences is not null
                ? document.Preferences.Normalised()
                : HubSharingPreferences.Default;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return HubSharingPreferences.Default;
        }
    }

    public async Task SaveAsync(HubSharingPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, new PreferenceDocument(current_version, preferences.Normalised()), json_options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record PreferenceDocument(int Version, HubSharingPreferences Preferences);
}
