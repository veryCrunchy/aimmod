using System.Text;
using AimMod.Desktop.Skins.Online;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class CreatorReleaseSkinCatalogTests
{
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
