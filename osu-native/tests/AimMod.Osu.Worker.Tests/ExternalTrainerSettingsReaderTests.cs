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
    public async Task ReadsOnlyOsuBindingsWithoutChangingDatabaseOrAccountConfiguration()
    {
        string root = Directory.CreateTempSubdirectory("trainer-settings-test-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "files"));
        string database = Path.Combine(root, "client.realm");
        var config = new RealmConfiguration(database) { SchemaVersion = 51, Schema = new[] { typeof(RealmKeyBinding), typeof(osu.Game.Configuration.RealmRulesetSetting) } };
        string accountConfig = Path.Combine(root, "game.ini");
        const string originalConfig = "Username = Synthetic Player\nToken = synthetic-access|2000000000|synthetic-refresh\nSavePassword = True\n";
        File.WriteAllText(accountConfig, originalConfig);
        try
        {
            using (var realm = Realm.GetInstance(config))
                realm.Write(() =>
                {
                    realm.Add(new RealmKeyBinding(OsuAction.LeftButton, new KeyBinding(InputKey.A, OsuAction.LeftButton).KeyCombination, "osu", 0));
                    realm.Add(new RealmKeyBinding(OsuAction.RightButton, new KeyBinding(InputKey.S, OsuAction.RightButton).KeyCombination, "osu", 0));
                    realm.Add(new RealmKeyBinding(OsuAction.LeftButton, new KeyBinding(InputKey.Q, OsuAction.LeftButton).KeyCombination, "taiko", 0));
                    realm.Add(new osu.Game.Configuration.RealmRulesetSetting { RulesetName = "osu", Variant = 0, Key = "SnakingInSliders", Value = "False" });
                    realm.Add(new osu.Game.Configuration.RealmRulesetSetting { RulesetName = "osu", Variant = 0, Key = "ShowCursorRipples", Value = "True" });
                    realm.Add(new osu.Game.Configuration.RealmRulesetSetting { RulesetName = "taiko", Variant = 0, Key = "HitAnimations", Value = "False" });
                    realm.Add(new osu.Game.Configuration.RealmRulesetSetting { RulesetName = "osu", Variant = 0, Key = "UnrelatedSetting", Value = "Not imported" });
                });
            byte[] before = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(database));
            var result = await ExternalTrainerSettingsReader.ReadAsync(root, CancellationToken.None);
            Assert.That(result.Bindings.Count, Is.EqualTo(2));
            Assert.That(result.Gameplay, Is.EquivalentTo(new Dictionary<string, string> { ["SnakingInSliders"] = "False", ["ShowCursorRipples"] = "True" }));
            Assert.That(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(database)), Is.EqualTo(before));
            Assert.That(File.ReadAllText(accountConfig), Is.EqualTo(originalConfig));
            Assert.That(result.Bindings.Select(b => b.Combination), Is.EquivalentTo(new[] {
                new KeyBinding(InputKey.A, OsuAction.LeftButton).KeyCombination.ToString(),
                new KeyBinding(InputKey.S, OsuAction.RightButton).KeyCombination.ToString() }));
        }
        finally { Realm.DeleteRealm(config); Directory.Delete(root, true); }
    }

    [Test]
    public void CancelledReadDoesNotOpenOrCreateAnything()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await ExternalTrainerSettingsReader.ReadAsync("not-a-library", cancellation.Token));
    }
}
