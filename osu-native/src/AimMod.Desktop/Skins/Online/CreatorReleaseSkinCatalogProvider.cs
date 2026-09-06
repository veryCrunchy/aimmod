using System.Globalization;
using System.Text.Json;

namespace AimMod.Desktop.Skins.Online;

public sealed class CreatorReleaseSkinCatalogProvider(ISecureSkinHttpClient http) : IOnlineSkinCatalogProvider
{
    internal static readonly string[] Repositories = ["kisaragi-hiu/osuskin-retome", "storycraft/osu-story-skin-edited", "Mathyzin/Mathyzin-Skins"];
    public string Id => "creator-releases";
    public string DisplayName => "Creator releases";
    public Uri HomePage => new("https://github.com/topics/osu-skin");

    public async Task<OnlineSkinCatalogPage> SearchAsync(OnlineSkinCatalogQuery query, CancellationToken cancellationToken = default)
    {
        query = query.Normalize();
        var items = new List<OnlineSkinCatalogEntry>();
        int unavailable = 0;
        foreach (string repository in Repositories)
        {
            try
            {
                var payload = await http.GetBytesAsync(new Uri($"https://api.github.com/repos/{repository}/releases?per_page=10"),
                    new(["api.github.com"], ["application/json"], 2 * 1024 * 1024, TimeSpan.FromSeconds(15)), cancellationToken).ConfigureAwait(false);
                items.AddRange(Parse(repository, payload.Bytes));
            }
            catch (Exception error) when (error is SkinHttpException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { unavailable++; }
        }
        IEnumerable<OnlineSkinCatalogEntry> filtered = items.Where(item =>
            (query.Ruleset == OnlineSkinRuleset.Any || item.SupportedRulesets.Contains(query.Ruleset)) &&
            (item.Name.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase) || item.Creator.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase)));
        filtered = query.Sort switch
        {
            OnlineSkinSort.Name => query.Descending ? filtered.OrderByDescending(item => item.Name) : filtered.OrderBy(item => item.Name),
            OnlineSkinSort.Downloads => query.Descending ? filtered.OrderByDescending(item => item.DownloadCount) : filtered.OrderBy(item => item.DownloadCount),
            _ => query.Descending ? filtered.OrderByDescending(item => item.PublishedAt) : filtered.OrderBy(item => item.PublishedAt),
        };
        var matching = filtered.ToArray();
        return new(unavailable == Repositories.Length ? OnlineSkinCatalogStatus.Unavailable : OnlineSkinCatalogStatus.Success,
            matching.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToArray(), query.Page, query.PageSize,
            matching.Length > query.Page * query.PageSize, unavailable > 0 ? "Some creator releases could not be checked." : null);
    }

    public async Task<OnlineSkinCatalogEntry?> GetDetailsAsync(string id, CancellationToken cancellationToken = default)
        => (await SearchAsync(new(Ruleset: OnlineSkinRuleset.Any, PageSize: 60), cancellationToken).ConfigureAwait(false)).Items.FirstOrDefault(item => item.Id == id);

    internal static IReadOnlyList<OnlineSkinCatalogEntry> Parse(string repository, byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var entries = new List<OnlineSkinCatalogEntry>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
            string version = release.GetProperty("tag_name").GetString()!;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString()!;
                string url = asset.GetProperty("browser_download_url").GetString()!;
                if (!name.EndsWith(".osk", StringComparison.OrdinalIgnoreCase) || !url.StartsWith($"https://github.com/{repository}/releases/download/", StringComparison.Ordinal)) continue;
                long size = asset.GetProperty("size").GetInt64();
                if (size <= 0 || size > 256L * 1024 * 1024) continue;
                var page = new Uri($"https://github.com/{repository}/releases/tag/{Uri.EscapeDataString(version)}");
                string language = name.Contains("1.0ja", StringComparison.Ordinal) ? "ja" : name.Contains("1.0tc", StringComparison.Ordinal) ? "tc" : "en";
                Uri[] previews = repository == "kisaragi-hiu/osuskin-retome" && version == "v1.0"
                    ? [new($"https://raw.githubusercontent.com/{repository}/v1.0/screenshots/1.0/{language}/mode-select.jpg")] : [];
                entries.Add(new(IdValue, asset.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture),
                    Path.GetFileNameWithoutExtension(name), repository.Split('/')[0], page, previews,
                    new(IdValue, "Creator releases", page, "Published by the skin creator. Original archive and attribution are preserved."),
                    SkinDownloadTargetClassifier.Classify(new Uri(url)), [OnlineSkinRuleset.Standard],
                    DownloadCount: asset.GetProperty("download_count").GetInt64(), FileSizeBytes: size,
                    PublishedAt: release.GetProperty("published_at").GetDateTimeOffset(), Variant: version));
            }
            if (entries.Count > 0) break;
        }
        return entries;
    }
    private const string IdValue = "creator-releases";
}
