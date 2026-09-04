using System.Security.Cryptography;
using AimMod.Desktop.Skins;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class ExternalLazerInstalledSkinsTests
{
    private string temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-installed-skins-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(temporaryDirectory, "files"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, recursive: true);
    }

    [Test]
    public async Task ResolvesOnlyCataloguedPreviewThroughHashedStoreLayout()
    {
        byte[] image = "real preview bytes"u8.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
        string directory = Path.Combine(temporaryDirectory, "files", hash[..1], hash[..2]);
        Directory.CreateDirectory(directory);
        string storedPath = Path.Combine(directory, hash);
        await File.WriteAllBytesAsync(storedPath, image);
        Guid skinId = Guid.NewGuid();
        var source = new ExternalLazerInstalledSkinSource(
            temporaryDirectory,
            (request, _) => Task.FromResult(new ExternalLazerSkinCatalogSearchResult(
                new[] { new ExternalLazerSkinSummary(skinId, "Skin", "Creator", "", false, 2, hash, "menu-background.jpg") },
                1,
                request.Offset,
                request.Limit)));

        InstalledLazerSkinPage result = await source.SearchAsync(limit: 20);

        Assert.That(result.Items.Single().PreviewPath, Is.EqualTo(storedPath));
        Assert.That(result.Items.Single().HasPreview, Is.True);
    }

    [Test]
    public void PreviewAvailabilityRequiresExistingAbsoluteFile()
    {
        var summary = new ExternalLazerSkinSummary(Guid.NewGuid(), "Skin", "Creator", "", false, 2);
        string missingPath = Path.Combine(temporaryDirectory, "missing-preview.png");

        Assert.Multiple(() =>
        {
            Assert.That(new InstalledLazerSkin(summary, "preview.png").HasPreview, Is.False);
            Assert.That(new InstalledLazerSkin(summary, missingPath).HasPreview, Is.False);
        });
    }

    [Test]
    public async Task MissingHashedPreviewProducesUnavailableState()
    {
        Guid skinId = Guid.NewGuid();
        string hash = new('a', 64);
        var source = new ExternalLazerInstalledSkinSource(
            temporaryDirectory,
            (request, _) => Task.FromResult(new ExternalLazerSkinCatalogSearchResult(
                new[] { new ExternalLazerSkinSummary(skinId, "Skin", "Creator", "", false, 2, hash, "menu-background.jpg") },
                1,
                request.Offset,
                request.Limit)));

        InstalledLazerSkinPage result = await source.SearchAsync(limit: 20);

        Assert.Multiple(() =>
        {
            Assert.That(result.Items.Single().PreviewPath, Is.Empty);
            Assert.That(result.Items.Single().HasPreview, Is.False);
        });
    }

    [Test]
    public async Task MappingStoreRoundTripsLatestExternalToLocalSkinMapping()
    {
        string path = Path.Combine(temporaryDirectory, "mapping.json");
        var store = new ExternalSkinMappingStore(path);
        Guid external = Guid.NewGuid();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        await store.SaveAsync(new ExternalSkinMapping(external, new string('a', 64), first));
        await store.SaveAsync(new ExternalSkinMapping(external, new string('b', 64), second));

        ExternalSkinMapping mapping = store.Load()[external];
        Assert.Multiple(() =>
        {
            Assert.That(mapping.ContentHash, Is.EqualTo(new string('b', 64)));
            Assert.That(mapping.LocalSkinId, Is.EqualTo(second));
        });
    }
}
