using System.Text.Json;
using AimMod.InGame;

namespace AimMod.Setup;

// NotPublished: the channel has no release yet (the feed is 404).
enum FeedState { Loading, Ready, Unreachable, NotPublished, InstallerOutdated }

sealed record FeedStatus(FeedState State, UpdateFeed? Feed = null, string? Message = null, string? InstallerUrl = null, string? RequiredInstaller = null)
{
    public static readonly FeedStatus Loading = new(FeedState.Loading);
}

// Reads a channel feed for this installer. Before the strict parse, the
// schema and the installer fields are read on their own, so a feed of a later
// format, or one that needs a newer installer, still tells this (older)
// installer where to get the new one instead of failing as "invalid".
static class FeedCheck
{
    public const string Repository = "https://github.com/verycrunchy/aimmod";
    // The permanent link: the channel release always carries the newest installer.
    public static string DefaultInstallerUrl(string channel) => $"{Repository}/releases/download/aimmod-ingame-{channel}/AimMod-Setup.exe";

    public static FeedStatus Read(ReadOnlySpan<byte> bytes, SemanticVersion installer, string channel)
    {
        string? schema = null, minimum = null, url = null;
        if (bytes.Length is > 0 and <= UpdateFeed.MaximumBytes)
        {
            try
            {
                var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 8 });
                using var document = JsonDocument.ParseValue(ref reader);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    schema = Text(document.RootElement, "schema");
                    minimum = Text(document.RootElement, "minimumInstallerVersion");
                    url = Text(document.RootElement, "installerUrl");
                }
            }
            catch (JsonException) { }
        }
        var link = url is not null && UpdateFeed.IsHttps(url) ? url : DefaultInstallerUrl(channel);
        if (minimum is not null && SemanticVersion.TryParse(minimum, out var required) && required > installer)
            return new(FeedState.InstallerOutdated, InstallerUrl: link, RequiredInstaller: required.ToString(),
                Message: $"This AimMod release needs AimMod Setup {required} or newer. This is Setup {installer}.");
        if (schema is not null && schema.StartsWith(UpdateFeed.SchemaPrefix, StringComparison.Ordinal) && schema != UpdateFeed.SchemaName)
            return new(FeedState.InstallerOutdated, InstallerUrl: link,
                Message: $"AimMod releases now use a format this installer (Setup {installer}) cannot read.");
        UpdateFeed feed;
        try { feed = UpdateFeed.Parse(bytes); }
        catch (ReleaseFormatException ex) { return new(FeedState.Unreachable, Message: "The update feed could not be read: " + ex.Message); }
        if (feed.Channel != channel) return new(FeedState.Unreachable, Message: "The update feed is for another channel.");
        return new(FeedState.Ready, feed, InstallerUrl: link);
    }
    static string? Text(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
