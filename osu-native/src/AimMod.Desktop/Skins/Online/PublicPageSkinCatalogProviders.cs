using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AimMod.Desktop.Skins.Online;

public sealed class OsuSkinsNetCatalogProvider : PublicPageSkinCatalogProvider
{
    private static readonly string[] page_hosts = ["osuskins.net", "www.osuskins.net"];

    public OsuSkinsNetCatalogProvider(ISecureSkinHttpClient http)
        : base(http)
    {
    }

    public override string Id => "osuskins-net";
    public override string DisplayName => "osuskins.net";
    public override Uri HomePage { get; } = new("https://osuskins.net/");

    public override async Task<OnlineSkinCatalogPage> SearchAsync(OnlineSkinCatalogQuery query, CancellationToken cancellationToken = default)
    {
        query = query.Normalize();
        Uri source = buildSearchUri(query);
        try
        {
            string html = await getHtml(source, page_hosts, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<OnlineSkinCatalogEntry> entries = OsuSkinsNetHtmlParser.ParseListing(html, source, query.IncludeSensitive);
            return new OnlineSkinCatalogPage(
                OnlineSkinCatalogStatus.Success,
                entries.Take(query.PageSize).ToArray(),
                query.Page,
                query.PageSize,
                entries.Count >= query.PageSize);
        }
        catch (Exception error) when (error is SkinHttpException or JsonException or FormatException or RegexMatchTimeoutException)
        {
            return OnlineSkinCatalogPage.Unavailable(query, $"osuskins.net could not be read: {error.Message}");
        }
    }

    public override async Task<OnlineSkinCatalogEntry?> GetDetailsAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!CatalogId.IsSafe(id))
            return null;
        Uri source = new(HomePage, $"skin/{id}");
        try
        {
            string html = await getHtml(source, page_hosts, cancellationToken).ConfigureAwait(false);
            return OsuSkinsNetHtmlParser.ParseDetails(html, source, id);
        }
        catch (Exception error) when (error is SkinHttpException or JsonException or FormatException or RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static Uri buildSearchUri(OnlineSkinCatalogQuery query)
    {
        string sort = query.Sort switch
        {
            OnlineSkinSort.Downloads => "downloads",
            OnlineSkinSort.Views => "views",
            OnlineSkinSort.Name => "name",
            OnlineSkinSort.Random => "random",
            _ => "date",
        };
        int mode = query.Ruleset switch
        {
            OnlineSkinRuleset.Standard => 1,
            OnlineSkinRuleset.Mania => 2,
            OnlineSkinRuleset.Taiko => 3,
            OnlineSkinRuleset.Catch => 4,
            _ => 0,
        };
        var queryParts = new List<string>
        {
            $"p={query.Page}",
            $"sortby={sort}",
            $"order={(query.Descending ? "desc" : "asc")}",
        };
        if (query.SearchText.Length > 0)
            queryParts.Add("q=" + Uri.EscapeDataString(query.SearchText));
        if (mode > 0)
            queryParts.Add($"mode%5B%5D={mode}");
        return new Uri("https://osuskins.net/?" + string.Join('&', queryParts));
    }
}

public sealed class OsuckNetSkinCatalogProvider : PublicPageSkinCatalogProvider
{
    private static readonly string[] page_hosts = ["skins.osuck.net"];

    public OsuckNetSkinCatalogProvider(ISecureSkinHttpClient http)
        : base(http)
    {
    }

    public override string Id => "skins-osuck-net";
    public override string DisplayName => "skins.osuck.net";
    public override Uri HomePage { get; } = new("https://skins.osuck.net/");

    public override async Task<OnlineSkinCatalogPage> SearchAsync(OnlineSkinCatalogQuery query, CancellationToken cancellationToken = default)
    {
        query = query.Normalize();
        Uri source = new(HomePage, $"skins?l=en&s={query.Page}");
        try
        {
            string html = await getHtml(source, page_hosts, cancellationToken).ConfigureAwait(false);
            IEnumerable<OnlineSkinCatalogEntry> entries = OsuckNetHtmlParser.ParseListing(html, source, query.IncludeSensitive);
            if (query.Ruleset != OnlineSkinRuleset.Any)
                entries = entries.Where(entry => entry.SupportedRulesets.Count == 0 || entry.SupportedRulesets.Contains(query.Ruleset));
            if (query.SearchText.Length > 0)
                entries = entries.Where(entry => entry.Name.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase)
                                                 || entry.Creator.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase));
            OnlineSkinCatalogEntry[] page = sort(entries, query).Take(query.PageSize).ToArray();
            return new OnlineSkinCatalogPage(OnlineSkinCatalogStatus.Success, page, query.Page, query.PageSize, page.Length >= query.PageSize);
        }
        catch (Exception error) when (error is SkinHttpException or JsonException or FormatException or RegexMatchTimeoutException)
        {
            return OnlineSkinCatalogPage.Unavailable(query, $"skins.osuck.net could not be read: {error.Message}");
        }
    }

    public override async Task<OnlineSkinCatalogEntry?> GetDetailsAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out int numericId) || numericId <= 0)
            return null;
        Uri source = new(HomePage, $"skins/{numericId}");
        try
        {
            string html = await getHtml(source, page_hosts, cancellationToken).ConfigureAwait(false);
            return OsuckNetHtmlParser.ParseDetails(html, source, id);
        }
        catch (Exception error) when (error is SkinHttpException or JsonException or FormatException or RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static IEnumerable<OnlineSkinCatalogEntry> sort(IEnumerable<OnlineSkinCatalogEntry> entries, OnlineSkinCatalogQuery query)
    {
        Func<OnlineSkinCatalogEntry, object?> key = query.Sort switch
        {
            OnlineSkinSort.Downloads => entry => entry.DownloadCount,
            OnlineSkinSort.Views => entry => entry.ViewCount,
            OnlineSkinSort.Name => entry => entry.Name,
            _ => entry => entry.PublishedAt,
        };
        return query.Descending ? entries.OrderByDescending(key) : entries.OrderBy(key);
    }
}

public abstract class PublicPageSkinCatalogProvider : IOnlineSkinCatalogProvider
{
    private static readonly string[] html_types = ["text/html", "application/xhtml+xml"];
    private readonly ISecureSkinHttpClient http;

    protected PublicPageSkinCatalogProvider(ISecureSkinHttpClient http)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract Uri HomePage { get; }
    public abstract Task<OnlineSkinCatalogPage> SearchAsync(OnlineSkinCatalogQuery query, CancellationToken cancellationToken = default);
    public abstract Task<OnlineSkinCatalogEntry?> GetDetailsAsync(string id, CancellationToken cancellationToken = default);

    protected async Task<string> getHtml(Uri uri, IReadOnlyCollection<string> hosts, CancellationToken cancellationToken)
    {
        var options = new SkinHttpFetchOptions(hosts, html_types, 2 * 1024 * 1024, TimeSpan.FromSeconds(15));
        SkinHttpPayload payload = await http.GetBytesAsync(uri, options, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload.Bytes);
    }
}

internal static class OsuSkinsNetHtmlParser
{
    public static IReadOnlyList<OnlineSkinCatalogEntry> ParseListing(string html, Uri source, bool includeSensitive)
    {
        var entries = new List<OnlineSkinCatalogEntry>();
        foreach (JsonElement root in SkinHtml.ReadJsonLd(html))
        {
            if (!SkinHtml.JsonTypeIs(root, "ItemList") || !root.TryGetProperty("itemListElement", out JsonElement items))
                continue;
            foreach (JsonElement listItem in items.EnumerateArray())
            {
                if (!listItem.TryGetProperty("item", out JsonElement item))
                    continue;
                string? detailsText = SkinHtml.JsonString(item, "url");
                string? name = SkinHtml.JsonString(item, "name") ?? SkinHtml.JsonString(item, "headline");
                string? imageText = SkinHtml.JsonString(item, "image");
                if (!Uri.TryCreate(detailsText, UriKind.Absolute, out Uri? details)
                    || !string.Equals(details.Host, "osuskins.net", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(name))
                    continue;
                string id = details.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
                if (!CatalogId.IsSafe(id))
                    continue;
                IReadOnlyList<Uri> previews = SkinHtml.SafePreviewUris([imageText], "cdn.osuskins.net");
                entries.Add(new OnlineSkinCatalogEntry(
                    "osuskins-net",
                    id,
                    SkinHtml.Clean(name),
                    "Unknown creator",
                    details,
                    previews,
                    attribution(details)));
            }
        }
        return entries;
    }

    public static OnlineSkinCatalogEntry? ParseDetails(string html, Uri source, string id)
    {
        JsonElement article = SkinHtml.ReadJsonLd(html).FirstOrDefault(root => SkinHtml.JsonTypeIs(root, "Article"));
        if (article.ValueKind == JsonValueKind.Undefined)
            return null;

        string name = SkinHtml.Clean(SkinHtml.JsonString(article, "headline") ?? id);
        string creator = readAuthor(article) ?? "Unknown creator";
        string description = SkinHtml.JsonString(article, "description") ?? string.Empty;
        var previews = new List<string?> { SkinHtml.JsonString(article, "image") };
        previews.AddRange(SkinHtml.AttributeValues(html, "data-src"));
        IReadOnlyList<Uri> previewUris = SkinHtml.SafePreviewUris(previews, "cdn.osuskins.net");
        IReadOnlyList<OnlineSkinRuleset> rulesets = SkinHtml.ReadRulesets(description);
        long? views = readInteraction(article, "ViewAction");
        long? downloads = readInteraction(article, "DownloadAction");
        DateTimeOffset? published = DateTimeOffset.TryParse(SkinHtml.JsonString(article, "datePublished"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset date)
            ? date
            : null;
        long? fileSize = SkinHtml.ReadHumanSize(description);
        bool sensitive = SkinHtml.HasSensitiveMarker(html);
        string? formAction = SkinHtml.FindFormAction(html, "downloadForm");
        OnlineSkinDownloadTarget? download = formAction is null
            ? null
            : new OnlineSkinDownloadTarget(
                new Uri(source, WebUtility.HtmlDecode(formAction)),
                OnlineSkinDownloadKind.FormPost,
                ["osuskins.net", "www.osuskins.net"],
                BrowserHandoffUri: source);
        return new OnlineSkinCatalogEntry(
            "osuskins-net", id, name, creator, source, previewUris, attribution(source), download,
            rulesets, sensitive, downloads, views, fileSize, published);
    }

    private static string? readAuthor(JsonElement article)
    {
        if (!article.TryGetProperty("author", out JsonElement author))
            return null;
        if (author.ValueKind == JsonValueKind.Array)
            author = author.EnumerateArray().FirstOrDefault();
        return author.ValueKind == JsonValueKind.Object ? SkinHtml.JsonString(author, "name") : null;
    }

    private static long? readInteraction(JsonElement article, string action)
    {
        if (!article.TryGetProperty("interactionStatistic", out JsonElement statistics) || statistics.ValueKind != JsonValueKind.Array)
            return null;
        foreach (JsonElement statistic in statistics.EnumerateArray())
        {
            string type = SkinHtml.JsonString(statistic, "interactionType") ?? string.Empty;
            if (!type.EndsWith(action, StringComparison.OrdinalIgnoreCase))
                continue;
            string? count = SkinHtml.JsonString(statistic, "userInteractionCount");
            if (long.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out long value))
                return value;
        }
        return null;
    }

    private static OnlineSkinSourceAttribution attribution(Uri source) => new(
        "osuskins-net", "osuskins.net", source,
        "Catalog metadata and screenshots are attributed to osuskins.net and the listed skin creators.");
}

internal static class OsuckNetHtmlParser
{
    public static IReadOnlyList<OnlineSkinCatalogEntry> ParseListing(string html, Uri source, bool includeSensitive)
    {
        var entries = new Dictionary<string, OnlineSkinCatalogEntry>(StringComparer.Ordinal);
        foreach (Match match in SkinHtml.SkinLinkMatches(html))
        {
            string id = match.Groups["id"].Value;
            if (entries.ContainsKey(id))
                continue;
            string fragment = SkinHtml.EnclosingFragment(html, match.Index, 3_000);
            bool sensitive = SkinHtml.HasSensitiveMarker(fragment);
            if (sensitive && !includeSensitive)
                continue;
            string name = SkinHtml.ReadHeading(fragment) ?? SkinHtml.ReadImageAlt(fragment) ?? $"Skin {id}";
            name = Regex.Replace(name, @"\s+osu skin.*$", string.Empty, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            string creator = SkinHtml.ReadDataText(fragment, "creator") ?? "Unknown creator";
            IReadOnlyList<Uri> previews = SkinHtml.SafePreviewUris(SkinHtml.ImageSources(fragment), "skins.osuck.net");
            Uri details = new(source, $"/skins/{id}");
            entries[id] = new OnlineSkinCatalogEntry(
                "skins-osuck-net", id, SkinHtml.Clean(name), SkinHtml.Clean(creator), details, previews,
                attribution(details), Rulesets: SkinHtml.ReadRulesets(SkinHtml.Clean(fragment)), IsSensitive: sensitive);
        }
        return entries.Values.ToArray();
    }

    public static OnlineSkinCatalogEntry? ParseDetails(string html, Uri source, string id)
    {
        string? heading = SkinHtml.ReadHeading(html);
        if (heading is null)
            return null;
        string name = Regex.Replace(heading, @"\s+osu skin.*$", string.Empty, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        string creator = SkinHtml.ReadDataText(html, "creator") ?? SkinHtml.ReadCreatorHeading(html) ?? "Unknown creator";
        bool sensitive = SkinHtml.HasSensitiveMarker(html);
        IReadOnlyList<Uri> previews = SkinHtml.SafePreviewUris(
            SkinHtml.ImageSources(html).Concat(SkinHtml.MetaImageSources(html)),
            "skins.osuck.net");
        string? link = SkinHtml.FindDownloadLink(html);
        OnlineSkinDownloadTarget? target = link is null ? null : SkinDownloadTargetClassifier.Classify(new Uri(source, WebUtility.HtmlDecode(link)));
        string plain = SkinHtml.Clean(html);
        return new OnlineSkinCatalogEntry(
            "skins-osuck-net", id, SkinHtml.Clean(name), SkinHtml.Clean(creator), source, previews, attribution(source), target,
            SkinHtml.ReadRulesets(plain), sensitive, FileSizeBytes: SkinHtml.ReadHumanSize(plain));
    }

    private static OnlineSkinSourceAttribution attribution(Uri source) => new(
        "skins-osuck-net", "skins.osuck.net", source,
        "Catalog metadata and screenshots are attributed to skins.osuck.net and the listed skin creators.");
}

internal static class SkinDownloadTargetClassifier
{
    private static readonly string[] direct_hosts = ["osuskins.net", "www.osuskins.net", "cdn.osuskins.net", "skins.osuck.net"];

    public static OnlineSkinDownloadTarget Classify(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443
            || !string.IsNullOrEmpty(uri.UserInfo))
            return new OnlineSkinDownloadTarget(uri, OnlineSkinDownloadKind.External, []);
        string host = uri.Host.TrimEnd('.').ToLowerInvariant();
        if (host is "mega.nz" or "www.mega.nz")
            return new OnlineSkinDownloadTarget(uri, OnlineSkinDownloadKind.Mega, [host]);
        if (host is "drive.google.com" or "drive.usercontent.google.com" or "docs.google.com")
            return new OnlineSkinDownloadTarget(uri, OnlineSkinDownloadKind.GoogleDrive, GoogleDriveSkinDownloadResolver.AllowedHosts);
        if (direct_hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            return new OnlineSkinDownloadTarget(uri, OnlineSkinDownloadKind.DirectHttps, direct_hosts);
        return new OnlineSkinDownloadTarget(uri, OnlineSkinDownloadKind.External, [host]);
    }
}

internal static class CatalogId
{
    public static bool IsSafe(string? id) => !string.IsNullOrWhiteSpace(id)
                                             && id.Length <= 80
                                             && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}

internal static class SkinHtml
{
    private static readonly Regex json_ld = new(@"<script\b[^>]*type\s*=\s*['""]application/ld\+json['""][^>]*>(?<json>.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));
    private static readonly Regex attributes = new(@"(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*(?<quote>['""])(?<value>.*?)\k<quote>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));
    private static readonly Regex tags = new(@"<[^>]+>", RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));
    private static readonly Regex whitespace = new(@"\s+", RegexOptions.None, TimeSpan.FromMilliseconds(100));
    private static readonly Regex skin_links = new(@"<a\b[^>]*href\s*=\s*['""](?:https://skins\.osuck\.net)?/skins/(?<id>[0-9]+)(?:\?[^'""]*)?['""][^>]*>", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
    private static readonly Regex heading = new(@"<h[1-3]\b[^>]*>(?<text>.*?)</h[1-3]>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));
    private static readonly Regex image = new(@"<img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));
    private static readonly Regex form = new(@"<form\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));
    private static readonly Regex anchor = new(@"<a\b(?<attrs>[^>]*)>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));
    private static readonly Regex meta = new(@"<meta\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));

    public static IEnumerable<JsonElement> ReadJsonLd(string html)
    {
        foreach (Match match in json_ld.Matches(html ?? string.Empty))
        {
            using JsonDocument document = JsonDocument.Parse(WebUtility.HtmlDecode(match.Groups["json"].Value));
            yield return document.RootElement.Clone();
        }
    }

    public static bool JsonTypeIs(JsonElement element, string type) =>
        string.Equals(JsonString(element, "@type"), type, StringComparison.OrdinalIgnoreCase);

    public static string? JsonString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out JsonElement value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    public static IEnumerable<Match> SkinLinkMatches(string html) => skin_links.Matches(html ?? string.Empty).Cast<Match>();

    public static string EnclosingFragment(string html, int index, int maximumLength)
    {
        int article = html.LastIndexOf("<article", index, StringComparison.OrdinalIgnoreCase);
        int listItem = html.LastIndexOf("<li", index, StringComparison.OrdinalIgnoreCase);
        int start = Math.Max(0, Math.Max(article, listItem));
        string closingTag = article >= listItem ? "</article>" : "</li>";
        int end = html.IndexOf(closingTag, index, StringComparison.OrdinalIgnoreCase);
        int length = end >= 0
            ? Math.Min(maximumLength, end + closingTag.Length - start)
            : Math.Min(maximumLength, html.Length - start);
        return html.Substring(start, length);
    }

    public static string? ReadHeading(string html)
    {
        Match match = heading.Match(html ?? string.Empty);
        return match.Success ? Clean(match.Groups["text"].Value) : null;
    }

    public static string? ReadImageAlt(string html)
    {
        foreach (Match match in image.Matches(html ?? string.Empty))
        {
            string? alt = ReadAttribute(match.Value, "alt");
            if (!string.IsNullOrWhiteSpace(alt))
                return Clean(alt);
        }
        return null;
    }

    public static string? ReadDataText(string html, string name)
    {
        var pattern = new Regex($@"<[^>]*data-{Regex.Escape(name)}(?:=['""][^'""]*['""])?[^>]*>(?<text>.*?)</[^>]+>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(100));
        Match match = pattern.Match(html ?? string.Empty);
        return match.Success ? Clean(match.Groups["text"].Value) : null;
    }

    public static string? ReadCreatorHeading(string html)
    {
        var pattern = new Regex(@"(?:Creators?|Uploader)\s*</[^>]+>\s*<h[1-6][^>]*>(?<text>.*?)</h[1-6]>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(100));
        Match match = pattern.Match(html ?? string.Empty);
        return match.Success ? Clean(match.Groups["text"].Value) : null;
    }

    public static IEnumerable<string?> ImageSources(string html)
    {
        foreach (Match match in image.Matches(html ?? string.Empty))
            yield return ReadAttribute(match.Value, "src") ?? ReadAttribute(match.Value, "data-src");
    }

    public static IEnumerable<string?> MetaImageSources(string html)
    {
        foreach (Match match in meta.Matches(html ?? string.Empty))
        {
            string? property = ReadAttribute(match.Groups["attrs"].Value, "property");
            if (string.Equals(property, "og:image", StringComparison.OrdinalIgnoreCase))
                yield return ReadAttribute(match.Groups["attrs"].Value, "content");
        }
    }

    public static IEnumerable<string?> AttributeValues(string html, string attribute)
    {
        foreach (Match match in attributes.Matches(html ?? string.Empty))
        {
            if (string.Equals(match.Groups["name"].Value, attribute, StringComparison.OrdinalIgnoreCase))
                yield return WebUtility.HtmlDecode(match.Groups["value"].Value);
        }
    }

    public static IReadOnlyList<Uri> SafePreviewUris(IEnumerable<string?> candidates, params string[] allowedHosts)
    {
        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => Uri.TryCreate(WebUtility.HtmlDecode(candidate), UriKind.Absolute, out Uri? uri) ? uri : null)
            .Where(uri => uri is not null
                          && uri.Scheme == Uri.UriSchemeHttps
                          && uri.Port == 443
                          && string.IsNullOrEmpty(uri.UserInfo)
                          && allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            .Cast<Uri>()
            .DistinctBy(uri => uri.AbsoluteUri, StringComparer.Ordinal)
            .Take(16)
            .ToArray();
    }

    public static string? FindFormAction(string html, string id)
    {
        foreach (Match match in form.Matches(html ?? string.Empty))
        {
            string attrs = match.Groups["attrs"].Value;
            if (string.Equals(ReadAttribute(attrs, "id"), id, StringComparison.OrdinalIgnoreCase))
                return ReadAttribute(attrs, "action");
        }
        return null;
    }

    public static string? FindDownloadLink(string html)
    {
        foreach (Match match in anchor.Matches(html ?? string.Empty))
        {
            string? href = ReadAttribute(match.Groups["attrs"].Value, "href");
            string text = Clean(match.Groups["text"].Value);
            if (href is not null && (href.Contains("download", StringComparison.OrdinalIgnoreCase)
                                     || text.Contains("download", StringComparison.OrdinalIgnoreCase)
                                     || href.Contains("mega.nz", StringComparison.OrdinalIgnoreCase)
                                     || href.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase)))
                return href;
        }
        return null;
    }

    public static bool HasSensitiveMarker(string html) =>
        html.Contains("sensitive content", StringComparison.OrdinalIgnoreCase)
        || html.Contains("data-sensitive=\"true\"", StringComparison.OrdinalIgnoreCase)
        || html.Contains("data-nsfw=\"true\"", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<OnlineSkinRuleset> ReadRulesets(string text)
    {
        var result = new List<OnlineSkinRuleset>();
        if (text.Contains("Standard", StringComparison.OrdinalIgnoreCase) || text.Contains("osu!", StringComparison.OrdinalIgnoreCase))
            result.Add(OnlineSkinRuleset.Standard);
        if (text.Contains("Mania", StringComparison.OrdinalIgnoreCase))
            result.Add(OnlineSkinRuleset.Mania);
        if (text.Contains("Taiko", StringComparison.OrdinalIgnoreCase))
            result.Add(OnlineSkinRuleset.Taiko);
        if (text.Contains("Catch", StringComparison.OrdinalIgnoreCase) || text.Contains("ctb", StringComparison.OrdinalIgnoreCase))
            result.Add(OnlineSkinRuleset.Catch);
        return result.Distinct().ToArray();
    }

    public static long? ReadHumanSize(string text)
    {
        Match match = Regex.Match(text ?? string.Empty, @"(?<value>[0-9]+(?:\.[0-9]+)?)\s*(?<unit>KB|MB|GB)\b", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        if (!match.Success || !double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            return null;
        double multiplier = match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "KB" => 1024,
            "MB" => 1024 * 1024,
            "GB" => 1024d * 1024 * 1024,
            _ => 1,
        };
        double bytes = value * multiplier;
        return bytes is > 0 and <= long.MaxValue ? (long)Math.Round(bytes) : null;
    }

    public static string Clean(string value) => whitespace.Replace(WebUtility.HtmlDecode(tags.Replace(value ?? string.Empty, " ")), " ").Trim();

    private static string? ReadAttribute(string html, string name)
    {
        foreach (Match match in attributes.Matches(html ?? string.Empty))
        {
            if (string.Equals(match.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase))
                return WebUtility.HtmlDecode(match.Groups["value"].Value);
        }
        return null;
    }
}
