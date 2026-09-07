using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using Realms;

namespace AimMod.Osu.Worker;

internal static class ExternalTrainerSettingsReader
{
    public static async Task<ExternalTrainerSettingsResult> ReadAsync(string root, CancellationToken token)
    {
        string directory = Directory.CreateTempSubdirectory("aimmod-input-settings-").FullName;
        var factory = new RealmLazerLibrarySnapshotFactory();
        LazerLibrarySnapshot? snapshot = null;
        try
        {
            var location = new ExternalLazerLibraryValidator().Validate(new(root, directory));
            snapshot = await factory.CreateSnapshotAsync(location, token).ConfigureAwait(false);
            return await Task.Run(() =>
            {
                using var realm = Realm.GetInstance(new RealmConfiguration(snapshot.DatabasePath)
                { IsDynamic = true, IsReadOnly = true, SchemaVersion = RealmLazerLibrarySnapshotFactory.SupportedSchemaVersion });
                var bindings = new List<ExternalTrainerKeyBinding>();
                if (!realm.Schema.Any(s => s.Name == "KeyBinding")) return new ExternalTrainerSettingsResult(bindings);
                foreach (var binding in realm.DynamicApi.All("KeyBinding"))
                {
                    token.ThrowIfCancellationRequested();
                    if (binding.DynamicApi.Get<string?>("RulesetName") != "osu"
                        || binding.DynamicApi.Get<int?>("Variant") is > 0) continue;
                    bindings.Add(new(binding.DynamicApi.Get<int>("Action"), binding.DynamicApi.Get<string>("KeyCombination")));
                }
                return new ExternalTrainerSettingsResult(bindings);
            }, token).ConfigureAwait(false);
        }
        finally
        {
            if (snapshot is not null) await factory.DeleteSnapshotAsync(snapshot).ConfigureAwait(false);
            Directory.Delete(directory, false);
        }
    }
}
