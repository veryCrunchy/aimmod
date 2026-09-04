using System.Collections.Concurrent;
using System.Globalization;
using AimMod.Desktop.Skins.Online;

namespace AimMod.Desktop;

public sealed record AimModDeepLink(int? BeatmapSetId, string? ProviderId, string? SourceId)
{
    public static bool TryParse(string? value, out AimModDeepLink? link)
    {
        link = null;
        const string prefix = "aimmod-osu://";
        if (value is null || value.Length > 256 || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        // Validate the original text: URI normalization must not hide traversal or escapes.
        string[] parts = value[prefix.Length..].Split('/');
        if (parts is ["beatmapsets", var setId] && positiveId(setId, out int id))
            link = new(id, null, null);
        else if (parts is ["skins", "osuskins", var skinId] && skinId.Length == 7 && skinId.All(char.IsAsciiLetterOrDigit))
            link = new(null, "osuskins-net", skinId);
        else if (parts is ["skins", "osuck", var osuckId] && CatalogId.IsSafe(osuckId))
            link = new(null, "skins-osuck-net", osuckId);
        return link is not null;
    }

    private static bool positiveId(string value, out int id)
    {
        id = 0;
        return value.Length > 0 && value[0] != '0'
               && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }
}

internal sealed class AimModLinkInbox
{
    private readonly ConcurrentQueue<AimModDeepLink> pending = new();

    public bool Accept(IReadOnlyList<string>? arguments)
    {
        if (arguments is null || arguments.Count != 1 || !AimModDeepLink.TryParse(arguments[0], out var link))
            return false;
        pending.Enqueue(link!);
        return true;
    }

    public bool TryTake(out AimModDeepLink? link) => pending.TryDequeue(out link);
}
