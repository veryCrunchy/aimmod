using AimMod.Desktop.Skins.Online;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class OnlineSkinCatalogParserTests
{
    [Test]
    public void ExplicitArchiveLinkCanBeDownloadedWithoutFormSubmission()
    {
        OnlineSkinCatalogEntry? entry = OsuSkinsNetHtmlParser.ParseDetails(
            CatalogFixtures.OsuSkinsDetails + "<a href=\"https://cdn.osuskins.net/files/skin.osk\">HD package</a>",
            new Uri("https://osuskins.net/skin/abc123"), "abc123");
        Assert.That(entry?.Download?.Kind, Is.EqualTo(OnlineSkinDownloadKind.DirectHttps));
    }

    [Test]
    public void CatalogMergesOnlySameArchiveAndVariantNotSimilarNames()
    {
        Uri uri = new("https://cdn.osuskins.net/files/skin.osk");
        var item = new OnlineSkinCatalogEntry("one", "1", "Same name", "Creator", uri, [],
            new OnlineSkinSourceAttribution("one", "One", uri, ""), SkinDownloadTargetClassifier.Classify(uri));
        var page = new OnlineSkinCatalogPage(OnlineSkinCatalogStatus.Success,
            [item, item with { ProviderId = "two", Id = "2" }, item with { Variant = "HD" },
                item with { Id = "3", Download = SkinDownloadTargetClassifier.Classify(new Uri("https://cdn.osuskins.net/files/other.osk")) }], 1, 30, false);
        var result = new OnlineSkinCatalogSearchResult([new OnlineSkinProviderResult("one", "One", uri, page)]);
        Assert.That(result.Items, Has.Count.EqualTo(3));
    }

    [Test]
    public void ParsesOsuSkinsListingJsonLdWithoutDependingOnLiveMarkup()
    {
        IReadOnlyList<OnlineSkinCatalogEntry> entries = OsuSkinsNetHtmlParser.ParseListing(
            CatalogFixtures.OsuSkinsListing,
            new Uri("https://osuskins.net/?p=1"),
            includeSensitive: false);

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(entries[0].Id, Is.EqualTo("abc123"));
            Assert.That(entries[0].Name, Is.EqualTo("Clean Skin"));
            Assert.That(entries[0].PreviewUris.Single().Host, Is.EqualTo("cdn.osuskins.net"));
            Assert.That(entries[0].Attribution.ProviderName, Is.EqualTo("osuskins.net"));
        });
    }

    [Test]
    public void ParsesOsuSkinsDetailsAndModelsPostDownloadAsBrowserHandoff()
    {
        OnlineSkinCatalogEntry? entry = OsuSkinsNetHtmlParser.ParseDetails(
            CatalogFixtures.OsuSkinsDetails,
            new Uri("https://osuskins.net/skin/abc123"),
            "abc123");

        Assert.That(entry, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(entry!.Creator, Is.EqualTo("Skin Author"));
            Assert.That(entry.DownloadCount, Is.EqualTo(1200));
            Assert.That(entry.ViewCount, Is.EqualTo(3400));
            Assert.That(entry.FileSizeBytes, Is.EqualTo(25L * 1024 * 1024));
            Assert.That(entry.SupportedRulesets, Does.Contain(OnlineSkinRuleset.Standard));
            Assert.That(entry.SupportedRulesets, Does.Contain(OnlineSkinRuleset.Mania));
            Assert.That(entry.Download?.Kind, Is.EqualTo(OnlineSkinDownloadKind.FormPost));
            Assert.That(entry.PreviewUris, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void OsuckListingHonoursSensitiveContentFilter()
    {
        IReadOnlyList<OnlineSkinCatalogEntry> safe = OsuckNetHtmlParser.ParseListing(
            CatalogFixtures.OsuckListing,
            new Uri("https://skins.osuck.net/skins"),
            includeSensitive: false);
        IReadOnlyList<OnlineSkinCatalogEntry> all = OsuckNetHtmlParser.ParseListing(
            CatalogFixtures.OsuckListing,
            new Uri("https://skins.osuck.net/skins"),
            includeSensitive: true);

        Assert.Multiple(() =>
        {
            Assert.That(safe.Select(entry => entry.Id), Is.EqualTo(new[] { "183" }));
            Assert.That(all, Has.Count.EqualTo(2));
            Assert.That(all.Single(entry => entry.Id == "184").IsSensitive, Is.True);
        });
    }

    [TestCase("https://drive.google.com/file/d/0123456789abcdef/view", OnlineSkinDownloadKind.GoogleDrive)]
    [TestCase("https://mega.nz/file/abc#key", OnlineSkinDownloadKind.Mega)]
    [TestCase("https://skins.osuck.net/skins/183/download", OnlineSkinDownloadKind.DirectHttps)]
    [TestCase("https://unknown.example/skin.osk", OnlineSkinDownloadKind.External)]
    public void OsuckDetailsClassifyDownloadTargets(string download, OnlineSkinDownloadKind expected)
    {
        string fixture = CatalogFixtures.OsuckDetails.Replace("$DOWNLOAD$", download, StringComparison.Ordinal);

        OnlineSkinCatalogEntry? entry = OsuckNetHtmlParser.ParseDetails(fixture, new Uri("https://skins.osuck.net/skins/183"), "183");

        Assert.That(entry?.Download?.Kind, Is.EqualTo(expected));
    }
}

internal static class CatalogFixtures
{
    public const string OsuSkinsListing = """
        <html><script type="application/ld+json">
        {"@type":"ItemList","itemListElement":[{"item":{"@type":"Article","name":"Clean Skin","url":"https://osuskins.net/skin/abc123","image":"https://cdn.osuskins.net/screenshots/headers/abc123.webp"}}]}
        </script></html>
        """;

    public const string OsuSkinsDetails = """
        <html><script type="application/ld+json">
        {"@type":"Article","headline":"Clean Skin","description":"Created by Skin Author, supports Standard, Mania modes, 25 MB file size.","image":"https://cdn.osuskins.net/screenshots/headers/abc123.webp","datePublished":"2024-01-02T03:04:05+00:00","author":[{"name":"Skin Author"}],"interactionStatistic":[{"interactionType":"https://schema.org/DownloadAction","userInteractionCount":"1200"},{"interactionType":"https://schema.org/ViewAction","userInteractionCount":"3400"}]}
        </script>
        <form id="downloadForm" method="POST" action="/skin/abc123/download"></form>
        <div data-src="https://cdn.osuskins.net/screenshots/gameplay/abc123.webp"></div>
        </html>
        """;

    public const string OsuckListing = """
        <article><a href="/skins/183"><img src="https://skins.osuck.net/files/183.webp" alt="Safe Skin osu skin"><h2>Safe Skin</h2><span data-creator>Garin</span></a></article>
        <article data-sensitive="true"><a href="/skins/184"><img src="https://skins.osuck.net/files/184.webp" alt="Sensitive Skin osu skin"><h2>Sensitive Skin</h2><span data-creator>Uploader</span><span>Sensitive content</span></a></article>
        """;

    public const string OsuckDetails = """
        <html><meta property="og:image" content="https://skins.osuck.net/files/183.webp"><h1>Stoof Pro Skin v1.0 osu skin</h1><span data-creator>Garin</span><p>osu! Standard, 33 MB</p><a href="$DOWNLOAD$">Download skin</a></html>
        """;
}
