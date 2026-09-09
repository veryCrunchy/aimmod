using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using Realms;

namespace AimMod.Osu.Worker;

internal static class ExternalTrainerSettingsReader
{
    public static Task<ExternalTrainerSettingsResult> ReadAsync(string root, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        string database = new ExternalLazerLibraryValidator().ValidateReadOnlyDatabase(root);
        // Read only the small key-binding table. A full database copy for every launch
        // consumes unnecessary disk space. Read-only dynamic access never migrates the source.
        using var realm = Realm.GetInstance(new RealmConfiguration(database)
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
    }, token);
}
