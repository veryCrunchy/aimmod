using AimMod.Osu.Worker;
using NUnit.Framework;
using osu.Framework.Input.Bindings;
using osu.Game.Input.Bindings;
using osu.Game.Rulesets.Osu;
using Realms;

namespace AimMod.Osu.Worker.Tests;

[TestFixture, NonParallelizable]
public class ExternalTrainerSettingsReaderTests
{
    [Test]
    public async Task ReadsOnlyOsuBindingsFromPrivateSnapshotWithoutChangingSource()
    {
        string root = Directory.CreateTempSubdirectory("trainer-settings-test-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "files"));
        string database = Path.Combine(root, "client.realm");
        var config = new RealmConfiguration(database) { SchemaVersion = 51, Schema = new[] { typeof(RealmKeyBinding) } };
        try
        {
            using (var realm = Realm.GetInstance(config))
                realm.Write(() =>
                {
                    realm.Add(new RealmKeyBinding(OsuAction.LeftButton, new KeyBinding(InputKey.A, OsuAction.LeftButton).KeyCombination, "osu", 0));
                    realm.Add(new RealmKeyBinding(OsuAction.RightButton, new KeyBinding(InputKey.S, OsuAction.RightButton).KeyCombination, "osu", 0));
                    realm.Add(new RealmKeyBinding(OsuAction.LeftButton, new KeyBinding(InputKey.Q, OsuAction.LeftButton).KeyCombination, "taiko", 0));
                });
            byte[] before = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(database));
            var result = await ExternalTrainerSettingsReader.ReadAsync(root, CancellationToken.None);
            Assert.That(result.Bindings.Count, Is.EqualTo(2));
            Assert.That(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(database)), Is.EqualTo(before));
        }
        finally { Realm.DeleteRealm(config); Directory.Delete(root, true); }
    }
}
