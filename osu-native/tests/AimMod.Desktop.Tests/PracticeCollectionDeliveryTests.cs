using AimMod.Desktop.Practice;
using AimMod.Osu.Runtime;
using NUnit.Framework;
using Realms;
using Realms.Schema;

namespace AimMod.Desktop.Tests;

[TestFixture, NonParallelizable]
public class PracticeCollectionDeliveryTests
{
    private string root = null!;
    private const string first = "11111111111111111111111111111111", second = "22222222222222222222222222222222", other = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    [SetUp] public void SetUp() => root = Directory.CreateTempSubdirectory("aimmod-collection-test-").FullName;
    [TearDown] public void TearDown() => Directory.Delete(root, true);

    [Test]
    public void BeatmapFixtureUsesThePersistedOsuTableName()
    {
        var mapping = (MapToAttribute?)Attribute.GetCustomAttribute(typeof(osu.Game.Beatmaps.BeatmapInfo), typeof(MapToAttribute));
        Assert.That(mapping?.Mapping, Is.EqualTo("Beatmap"));
    }

    [Test]
    public async Task LazerCollectionMergePreservesOtherCollectionsAndOnlyRemovesOwnedRetiredHashes()
    {
        await Task.Run(() =>
        {
        var config = new RealmConfiguration(Path.Combine(root, "client.realm")) { IsDynamic = true, SchemaVersion = 51,
            Schema = new RealmSchema.Builder
            {
                new ObjectSchema.Builder("BeatmapCollection")
                {
                    Property.Primitive("ID", RealmValueType.Guid, isPrimaryKey: true), Property.Primitive("Name", RealmValueType.String),
                    Property.Primitive("LastModified", RealmValueType.Date), Property.PrimitiveList("BeatmapMD5Hashes", RealmValueType.String),
                },
                new ObjectSchema.Builder("Beatmap") { Property.Primitive("MD5Hash", RealmValueType.String) },
            } };
        using (var realm = Realm.GetInstance(config)) realm.Write(() =>
        {
            var collection = realm.DynamicApi.CreateObject("BeatmapCollection", Guid.NewGuid());
            collection.DynamicApi.Set("Name", "My favourites"); collection.DynamicApi.GetList<string>("BeatmapMD5Hashes").Add(other);
            var beatmap = realm.DynamicApi.CreateObject("Beatmap"); beatmap.DynamicApi.Set("MD5Hash", first);
        });
        var result = PracticeCollectionSync.Lazer(root, Path.Combine(root, "journal"), [first, second], []);
        Assert.That(result.InstalledHashes, Is.EqualTo(new[] { first }));
        PracticeCollectionSync.Lazer(root, Path.Combine(root, "journal"), [second], [first]);
        PracticeCollectionSync.Lazer(root, Path.Combine(root, "journal"), [second], [first]);
        using (var realm = Realm.GetInstance(config))
        {
            var collections = realm.DynamicApi.All("BeatmapCollection").ToArray();
            Assert.That(collections.Length, Is.EqualTo(2));
            Assert.That(collections.Single(c => c.DynamicApi.Get<string>("Name") == "My favourites").DynamicApi.GetList<string>("BeatmapMD5Hashes"), Is.EqualTo(new[] { other }));
            Assert.That(collections.Single(c => c.DynamicApi.Get<string>("Name") == PracticeCollectionSync.CollectionName).DynamicApi.GetList<string>("BeatmapMD5Hashes"), Is.EqualTo(new[] { second }));
            Assert.That(realm.DynamicApi.All("Beatmap").Count(), Is.EqualTo(1));
        }
        Realm.DeleteRealm(config);
        });
    }

    [Test]
    public void StableCollectionPreservesUnrelatedDataAndDefersWhileGameIsRunning()
    {
        string path = Path.Combine(root, "collection.db");
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(20260907); writer.Write(1); writer.Write((byte)11); writer.Write("My favourites");
            writer.Write(1); writer.Write((byte)11); writer.Write(other);
        }
        byte[] before = File.ReadAllBytes(path);
        Assert.Throws<IOException>(() => PracticeCollectionSync.Stable(root, Path.Combine(root, "journal"), [first], [], () => true));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
        PracticeCollectionSync.Stable(root, Path.Combine(root, "journal"), [first, second], [], () => false);
        PracticeCollectionSync.Stable(root, Path.Combine(root, "journal"), [second], [first], () => false);
        byte[] after = File.ReadAllBytes(path);
        PracticeCollectionSync.Stable(root, Path.Combine(root, "journal"), [second], [first], () => false);
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(after));
        using var reader = new BinaryReader(new MemoryStream(after));
        reader.ReadInt32(); Assert.That(reader.ReadInt32(), Is.EqualTo(2)); reader.ReadByte(); Assert.That(reader.ReadString(), Is.EqualTo("My favourites"));
        Assert.That(reader.ReadInt32(), Is.EqualTo(1)); reader.ReadByte(); Assert.That(reader.ReadString(), Is.EqualTo(other));
        reader.ReadByte(); Assert.That(reader.ReadString(), Is.EqualTo(PracticeCollectionSync.CollectionName));
        Assert.That(reader.ReadInt32(), Is.EqualTo(1)); reader.ReadByte(); Assert.That(reader.ReadString(), Is.EqualTo(second));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task DeliveryBackfillsExistingSetsAndDoesNotResendWhilePendingOrConfirmed(bool automatic)
    {
        var map = new SavedPracticeMap(Guid.NewGuid().ToString("N"), "Synthetic song", "Practice", PracticeDrillType.Mixed, DateTimeOffset.UtcNow,
            0, 1000, 15000, 6, 12, Automatic: automatic, Tracking: new("Synthetic player", 1, 0, "", Guid.Empty, "", false, [], [new("Drill", PracticeDrillType.Mixed, "", first, 0, 1000, true)]));
        Assert.That(AutomaticPracticeDelivery.ShouldDeliver(map, 1, false), Is.EqualTo(!automatic));
        Assert.That(AutomaticPracticeDelivery.ShouldDeliver(map, 1, true), Is.True);
        Assert.That(AutomaticPracticeDelivery.ShouldDeliver(map, 2, true), Is.False);
        Assert.That(AutomaticPracticeDelivery.ShouldDeliver(map with { RetiredAt = DateTimeOffset.UtcNow }, 1, true), Is.False);
        Assert.That(AutomaticPracticeDelivery.ShouldDeliver(map with { PayloadRemoved = true }, 1, true), Is.False);
        var service = new AutomaticPracticeDelivery(Path.Combine(root, "delivery.json"));
        int sends = 0; var now = DateTimeOffset.UtcNow;
        Task<LazerBeatmapInstallResult> send(SavedPracticeMap _, CancellationToken ct) { sends++; return Task.FromResult(new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.Sent)); }
        Assert.That((await service.DeliverAsync([map], new HashSet<string>(), send, now, default)).Pending, Is.EqualTo(1));
        await service.DeliverAsync([map], new HashSet<string>(), send, now.AddMinutes(1), default);
        await service.DeliverAsync([map], null, send, now.AddMinutes(20), default);
        Assert.That(sends, Is.EqualTo(1));
        Assert.That((await service.DeliverAsync([map], new HashSet<string> { first }, send, now.AddMinutes(21), default)).Confirmed, Is.EqualTo(1));
        await service.DeliverAsync([map], null, send, now.AddMinutes(30), default);
        Assert.That(sends, Is.EqualTo(1));
    }
}
