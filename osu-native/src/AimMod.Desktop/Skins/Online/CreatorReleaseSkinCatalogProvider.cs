using System.Globalization;
using System.Text.Json;

namespace AimMod.Desktop.Skins.Online;

public sealed class CreatorReleaseSkinCatalogProvider : IOnlineSkinCatalogProvider
{
    private static readonly Lazy<IReadOnlyList<OnlineSkinCatalogEntry>> entries = new(loadCatalog);
    private readonly int randomSeed = Random.Shared.Next();
    public string Id => "creator-releases";
    public string DisplayName => "Creator releases";
    public Uri HomePage => new("https://github.com/topics/osu-skin");

    public Task<OnlineSkinCatalogPage> SearchAsync(OnlineSkinCatalogQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        query = query.Normalize();
        IEnumerable<OnlineSkinCatalogEntry> filtered = entries.Value.Where(item =>
            (query.Ruleset == OnlineSkinRuleset.Any || item.SupportedRulesets.Contains(query.Ruleset)) &&
            (item.Name.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase) || item.Creator.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase)));
        filtered = query.Sort switch
        {
            OnlineSkinSort.Name => query.Descending ? filtered.OrderByDescending(item => item.Name) : filtered.OrderBy(item => item.Name),
            OnlineSkinSort.Downloads => query.Descending ? filtered.OrderByDescending(item => item.DownloadCount) : filtered.OrderBy(item => item.DownloadCount),
            OnlineSkinSort.Random => filtered.OrderBy(item => HashCode.Combine(randomSeed, item.Id)).ThenBy(item => item.Id),
            _ => query.Descending ? filtered.OrderByDescending(item => item.PublishedAt) : filtered.OrderBy(item => item.PublishedAt),
        };
        var matching = filtered.ToArray();
        long offset = ((long)query.Page - 1) * query.PageSize;
        return Task.FromResult(new OnlineSkinCatalogPage(OnlineSkinCatalogStatus.Success,
            matching.Skip((int)Math.Min(offset, matching.Length)).Take(query.PageSize).ToArray(), query.Page, query.PageSize,
            matching.LongLength > offset + query.PageSize));
    }

    public Task<OnlineSkinCatalogEntry?> GetDetailsAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(entries.Value.FirstOrDefault(item => item.Id == id));
    }

    private static IReadOnlyList<OnlineSkinCatalogEntry> loadCatalog()
    {
        // Reviewed, pinned creator releases stay searchable without GitHub API requests.
        using Stream stream = typeof(CreatorReleaseSkinCatalogProvider).Assembly.GetManifestResourceStream("AimMod.Resources.Skins.creator-releases.json")
            ?? throw new InvalidOperationException("Creator release catalog is missing.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateArray().SelectMany(source =>
            Parse(source.GetProperty("repository").GetString()!, System.Text.Encoding.UTF8.GetBytes(source.GetProperty("releases").GetRawText())))
            .DistinctBy(item => item.Id).ToArray();
    }

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
                if (!name.EndsWith(".osk", StringComparison.OrdinalIgnoreCase)
                    || !Uri.TryCreate(url, UriKind.Absolute, out Uri? archive)
                    || archive.Scheme != Uri.UriSchemeHttps || archive.Host != "github.com" || archive.Port != 443
                    || archive.UserInfo.Length > 0 || archive.Fragment.Length > 0
                    || !archive.AbsolutePath.StartsWith($"/{repository}/releases/download/", StringComparison.Ordinal)) continue;
                long size = asset.GetProperty("size").GetInt64();
                if (size <= 0 || size > 256L * 1024 * 1024) continue;
                var page = new Uri($"https://github.com/{repository}/releases/tag/{Uri.EscapeDataString(version)}");
                string language = name.Contains("1.0ja", StringComparison.Ordinal) ? "ja" : name.Contains("1.0tc", StringComparison.Ordinal) ? "tc" : "en";
                Uri[] previews = repository == "kisaragi-hiu/osuskin-retome" && version == "v1.0"
                    ? [new($"https://raw.githubusercontent.com/{repository}/v1.0/screenshots/1.0/{language}/mode-select.jpg")] : [];
                entries.Add(new(IdValue, asset.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture),
                    displayName(repository, name), repository.Split('/')[0], page, previews,
                    new(IdValue, "Creator releases", page, "Published by the skin creator. Original archive and attribution are preserved."),
                    SkinDownloadTargetClassifier.Classify(archive), repository == "yanorei32/yr32-skinbuilder"
                        ? [OnlineSkinRuleset.Standard, OnlineSkinRuleset.Mania] : [OnlineSkinRuleset.Standard],
                    DownloadCount: asset.GetProperty("download_count").GetInt64(), FileSizeBytes: size,
                    PublishedAt: release.GetProperty("published_at").GetDateTimeOffset(), Variant: version));
            }
            if (entries.Count > 0) break;
        }
        return entries;
    }

    private static string displayName(string repository, string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        return repository == "sineplusx/lite" ? $"std::lite (lazer) - {name}" : name;
    }
    private const string IdValue = "creator-releases";
}
