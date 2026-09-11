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
        if (realm.Schema.Any(s => s.Name == "KeyBinding"))
        foreach (var binding in realm.DynamicApi.All("KeyBinding"))
        {
            token.ThrowIfCancellationRequested();
            if (binding.DynamicApi.Get<string?>("RulesetName") != "osu"
                || binding.DynamicApi.Get<int?>("Variant") is > 0) continue;
            bindings.Add(new(binding.DynamicApi.Get<int>("Action"), binding.DynamicApi.Get<string>("KeyCombination")));
        }
        var gameplay = new Dictionary<string, string>();
        string[] allowed = ["SnakingInSliders", "SnakingOutSliders", "HitAnimations", "ShowCursorTrail", "ShowCursorRipples", "PlayfieldBorderStyle"];
        if (realm.Schema.Any(s => s.Name == "RulesetSetting"))
        foreach (var setting in realm.DynamicApi.All("RulesetSetting"))
        {
            token.ThrowIfCancellationRequested();
            if (setting.DynamicApi.Get<string>("RulesetName") != "osu" || setting.DynamicApi.Get<int>("Variant") != 0) continue;
            string key = setting.DynamicApi.Get<string>("Key");
            if (allowed.Contains(key) && setting.DynamicApi.Get<string>("Value") is { Length: <= 64 } value) gameplay[key] = value;
        }
        return new ExternalTrainerSettingsResult(bindings, gameplay);
    }, token);
}
