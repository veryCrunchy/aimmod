using System.Text;
using AimMod.Desktop.Skins.Online;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class CreatorReleaseSkinCatalogTests
{
    [Test]
    public async Task BundledCreatorCatalogIsAvailableWithoutNetworkAndPreservesVariants()
    {
        var provider = new CreatorReleaseSkinCatalogProvider();
        var page = await provider.SearchAsync(new(Ruleset: OnlineSkinRuleset.Any, PageSize: 60));
        Assert.Multiple(() =>
        {
            Assert.That(page.Status, Is.EqualTo(OnlineSkinCatalogStatus.Success));
            Assert.That(page.Items.Count, Is.EqualTo(22));
            Assert.That(page.Items.Select(item => item.Id).Distinct().Count(), Is.EqualTo(page.Items.Count));
            Assert.That(page.Items.All(item => item.Download?.Kind == OnlineSkinDownloadKind.DirectHttps), Is.True);
            Assert.That(page.Items.All(item => item.Download!.Uri.Host == "github.com"), Is.True);
            Assert.That(page.Items.Count(item => item.Creator == "PopCat19" && item.Variant == "v1.2.1"), Is.EqualTo(5));
            Assert.That(page.Items.Count(item => item.Name.StartsWith("std::lite", StringComparison.Ordinal)), Is.EqualTo(7));
        });
        foreach (var item in page.Items)
            Assert.That(await provider.GetDetailsAsync(item.Id), Is.EqualTo(item));
    }

    [Test]
    public async Task BundledCatalogSupportsSearchModeSortingAndPagination()
    {
        var provider = new CreatorReleaseSkinCatalogProvider();
        var first = await provider.SearchAsync(new(Sort: OnlineSkinSort.Name, Descending: false, PageSize: 5));
        var second = await provider.SearchAsync(new(Sort: OnlineSkinSort.Name, Descending: false, PageSize: 5, Page: 2));
        var mania = await provider.SearchAsync(new(Ruleset: OnlineSkinRuleset.Mania));
        var search = await provider.SearchAsync(new(SearchText: "  MONOMAL  "));
        var beyond = await provider.SearchAsync(new(Page: int.MaxValue));
        Assert.Multiple(() =>
        {
            Assert.That(first.Items, Has.Count.EqualTo(5));
            Assert.That(first.HasMore, Is.True);
            Assert.That(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)), Is.Empty);
            Assert.That(first.Items.Select(item => item.Name), Is.Ordered);
            Assert.That(mania.Items, Has.Count.EqualTo(2));
            Assert.That(mania.Items.All(item => item.Creator == "yanorei32"), Is.True);
            Assert.That(search.Items, Has.Count.EqualTo(1));
            Assert.That(beyond.Items, Is.Empty);
            Assert.That(beyond.HasMore, Is.False);
        });
        Assert.That(await provider.GetDetailsAsync("unknown"), Is.Null);
    }

    [Test]
    public async Task RandomOrderStaysStableWhilePaging()
    {
        var provider = new CreatorReleaseSkinCatalogProvider();
        var first = await provider.SearchAsync(new(Sort: OnlineSkinSort.Random, PageSize: 10));
        var again = await provider.SearchAsync(new(Sort: OnlineSkinSort.Random, PageSize: 10));
        var second = await provider.SearchAsync(new(Sort: OnlineSkinSort.Random, PageSize: 10, Page: 2));
        Assert.That(first.Items.Select(item => item.Id), Is.EqualTo(again.Items.Select(item => item.Id)));
        Assert.That(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)), Is.Empty);
    }

    [Test]
    public void CatalogHonorsCancellation()
    {
        var provider = new CreatorReleaseSkinCatalogProvider();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await provider.SearchAsync(new(), cancel.Token));
        Assert.ThrowsAsync<OperationCanceledException>(async () => await provider.GetDetailsAsync("unknown", cancel.Token));
    }

    [TestCase("https://github.com.evil.example/creator/skin/releases/download/v1/skin.osk")]
    [TestCase("https://github.com/creator/skin/releases/download/../../../../../other/skin.osk")]
    [TestCase("https://user@github.com/creator/skin/releases/download/v1/skin.osk")]
    [TestCase("http://github.com/creator/skin/releases/download/v1/skin.osk")]
    [TestCase("https://github.com:444/creator/skin/releases/download/v1/skin.osk")]
    public void CreatorArchiveMustRemainOnItsOwnRepository(string url)
    {
        byte[] json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new[] { new
        {
            draft = false, prerelease = false, tag_name = "v1", published_at = "2026-01-01T00:00:00Z",
            assets = new[] { new { id = 1, name = "skin.osk", size = 1200, download_count = 10, browser_download_url = url } },
        } });
        Assert.That(CreatorReleaseSkinCatalogProvider.Parse("creator/skin", json), Is.Empty);
    }

    [Test]
    public void OnlyCreatorReleaseSkinArchivesAreOffered()
    {
        var json = Encoding.UTF8.GetBytes("""
            [{"draft":false,"prerelease":false,"tag_name":"v1","published_at":"2026-01-01T00:00:00Z","assets":[
            {"id":1,"name":"skin.osk","size":1200,"download_count":10,"browser_download_url":"https://github.com/creator/skin/releases/download/v1/skin.osk"},
            {"id":2,"name":"tool.exe","size":1200,"download_count":10,"browser_download_url":"https://github.com/creator/skin/releases/download/v1/tool.exe"},
            {"id":3,"name":"other.osk","size":1200,"download_count":10,"browser_download_url":"https://github.com/other/skin/releases/download/v1/other.osk"}
            ]}]
            """);
        var entries = CreatorReleaseSkinCatalogProvider.Parse("creator/skin", json);
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Download!.Kind, Is.EqualTo(OnlineSkinDownloadKind.DirectHttps));
        Assert.That(entries[0].Creator, Is.EqualTo("creator"));
        Assert.That(entries[0].Variant, Is.EqualTo("v1"));
    }

    [TestCase("https://download123.mediafire.com/token/skin.osk", true)]
    [TestCase("https://download123.mediafire.com.evil.example/skin.osk", false)]
    [TestCase("http://download123.mediafire.com/skin.osk", false)]
    [TestCase("https://user:password@download123.mediafire.com/skin.osk", false)]
    public void MediaFireRequiresThePublicDownloadControlAndApprovedHost(string url, bool valid)
    {
        string html = $"<a id=\"advertisement\" href=\"https://example.test/ad.osk\">Download</a><a id=\"downloadButton\" href=\"{url}\">Download</a>";
        Assert.That(MediaFireSkinDownloadResolver.DownloadLink(html) is not null, Is.EqualTo(valid));
        Assert.That(MediaFireSkinDownloadResolver.DownloadLink("<div>Human verification required</div>"), Is.Null);
    }

    [Test]
    public void GitHubPagesAreNotMisclassifiedAsDownloads()
    {
        Assert.That(SkinDownloadTargetClassifier.Classify(new Uri("https://github.com/creator/skin")).Kind, Is.EqualTo(OnlineSkinDownloadKind.External));
        Assert.That(SkinDownloadTargetClassifier.Classify(new Uri("https://github.com/creator/skin/releases/download/v1/skin.osk")).Kind, Is.EqualTo(OnlineSkinDownloadKind.DirectHttps));
    }
}
