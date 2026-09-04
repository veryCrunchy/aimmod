using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;
using osu.Game.Database;
using osu.Game.Models;
using osu.Game.Skinning;
using Realms;

namespace AimMod.Osu.Worker.Tests;

[TestFixture]
[NonParallelizable]
public sealed class ExternalLazerSkinCatalogReaderTests
{
    private string temporaryDirectory = null!;
    private string realmPath = null!;
    private Guid skinId;
    private SynchronizationContext? originalSynchronizationContext;

    [SetUp]
    public void SetUp()
    {
        originalSynchronizationContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-skin-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(temporaryDirectory, "files"));
        realmPath = Path.Combine(temporaryDirectory, "skins.realm");

        using Realm realm = Realm.GetInstance(new RealmConfiguration(realmPath)
        {
            SchemaVersion = RealmLazerLibrarySnapshotFactory.SupportedSchemaVersion,
        });
        realm.Write(() =>
        {
            var skin = new SkinInfo("WhiteCat 3.0", "CK")
            {
                Hash = new string('a', 64),
            };
            skin.Files.Add(new RealmNamedFileUsage(
                new RealmFile { Hash = new string('b', 64) },
                "menu-background@2x.jpg"));
            skin.Files.Add(new RealmNamedFileUsage(
                new RealmFile { Hash = new string('c', 64) },
                "skin.ini"));
            skinId = skin.ID;
            realm.Add(skin);
            realm.Add(new SkinInfo("Deleted", "") { DeletePending = true });
        });
    }

    [Test]
    public async Task SkinManifestKeepsDifferentLogicalNamesWithTheSameStoredHash()
    {
        {
            using Realm realm = Realm.GetInstance(new RealmConfiguration(realmPath)
            {
                SchemaVersion = RealmLazerLibrarySnapshotFactory.SupportedSchemaVersion,
            });
            realm.Write(() =>
            {
                SkinInfo skin = realm.Find<SkinInfo>(skinId)!;
                skin.Files.Add(new RealmNamedFileUsage(
                    realm.Find<RealmFile>(new string('c', 64))!,
                    "normal-hitnormal.wav"));
            });
        }

        var reader = new DynamicRealmLazerLibraryManifestReader();
        LazerLibraryAssetManifest manifest = await reader.ReadManifestAsync(
            new LazerLibrarySnapshot(Guid.NewGuid(), realmPath, Path.Combine(temporaryDirectory, "files"), DateTimeOffset.UtcNow),
            new LazerLibraryAssetQuery(Array.Empty<string>(), Array.Empty<Guid>(), new[] { skinId }));

        Assert.Multiple(() =>
        {
            Assert.That(manifest.MissingSkins, Is.Empty);
            Assert.That(manifest.Files, Has.Count.EqualTo(3));
            Assert.That(manifest.Files.Count(file => file.Sha256Hash == new string('c', 64)), Is.EqualTo(2));
            Assert.That(manifest.Files.All(file => file.Kind == LazerLibraryAssetKind.Skin), Is.True);
        });
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalSynchronizationContext);
        }
    }

    [Test]
    public async Task ReadsMetadataAndPreferredPreviewWithoutOpeningSkinFiles()
    {
        var reader = new DynamicRealmLazerSkinCatalogReader();
        ExternalLazerSkinCatalogSearchResult result = await reader.ReadCatalogAsync(
            new LazerLibrarySnapshot(Guid.NewGuid(), realmPath, Path.Combine(temporaryDirectory, "files"), DateTimeOffset.UtcNow),
            new ExternalLazerSkinCatalogSearchRequest(temporaryDirectory, "white ck", Limit: 20));

        Assert.Multiple(() =>
        {
            Assert.That(result.Total, Is.EqualTo(1));
            Assert.That(result.Skins[0].Name, Is.EqualTo("WhiteCat 3.0"));
            Assert.That(result.Skins[0].Creator, Is.EqualTo("CK"));
            Assert.That(result.Skins[0].FileCount, Is.EqualTo(2));
            Assert.That(result.Skins[0].PreviewHash, Is.EqualTo(new string('b', 64)));
            Assert.That(result.Skins[0].PreviewLogicalName, Is.EqualTo("menu-background@2x.jpg"));
        });
    }

    [Test]
    public void RejectsUnboundedPages()
    {
        var reader = new DynamicRealmLazerSkinCatalogReader();
        ExternalLazerLibraryException exception = Assert.ThrowsAsync<ExternalLazerLibraryException>(async () =>
            await reader.ReadCatalogAsync(
                new LazerLibrarySnapshot(Guid.NewGuid(), realmPath, Path.Combine(temporaryDirectory, "files"), DateTimeOffset.UtcNow),
                new ExternalLazerSkinCatalogSearchRequest(temporaryDirectory, Limit: ExternalLazerSkinProtocol.MaximumPageSize + 1)))!;

        Assert.That(exception.Code, Is.EqualTo("skin_catalog_query_invalid"));
    }
}
