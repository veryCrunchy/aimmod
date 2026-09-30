using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using Realms;
using Realms.Exceptions;

namespace AimMod.Osu.Worker;

internal static class ExternalTrainerSettingsReader
{
    internal const int MaximumBindings = 1_024;
    internal const int MaximumCombinationLength = 256;

    public static Task<ExternalTrainerSettingsResult> ReadAsync(string root, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        string database;
        try { database = new ExternalLazerLibraryValidator().ValidateReadOnlyDatabase(root); }
        catch (ExternalLazerLibraryException exception) { throw new RuntimeCommandException(exception.Code, exception.Message); }

        try
        {
            return read(database, token);
        }
        catch (Exception exception) when (exception is RealmException or IOException or UnauthorizedAccessException or MissingMemberException or InvalidCastException)
        {
            throw new RuntimeCommandException("trainer_settings_unavailable", "The lazer control settings could not be read.");
        }
    }, token);

    private static ExternalTrainerSettingsResult read(string database, CancellationToken token)
    {
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
            if (binding.DynamicApi.Get<string?>("KeyCombination") is not { Length: > 0 and <= MaximumCombinationLength } combination) continue;
            if (bindings.Count == MaximumBindings) break;
            bindings.Add(new(binding.DynamicApi.Get<int>("Action"), combination));
        }
        var gameplay = new Dictionary<string, string>();
        string[] allowed = ["SnakingInSliders", "SnakingOutSliders", "HitAnimations", "ShowCursorTrail", "ShowCursorRipples", "PlayfieldBorderStyle"];
        if (realm.Schema.Any(s => s.Name == "RulesetSetting"))
        foreach (var setting in realm.DynamicApi.All("RulesetSetting"))
        {
            token.ThrowIfCancellationRequested();
            if (setting.DynamicApi.Get<string?>("RulesetName") != "osu" || setting.DynamicApi.Get<int>("Variant") != 0) continue;
            string? key = setting.DynamicApi.Get<string?>("Key");
            if (key is not null && allowed.Contains(key) && setting.DynamicApi.Get<string?>("Value") is { Length: <= 64 } value) gameplay[key] = value;
        }
        return new ExternalTrainerSettingsResult(bindings, gameplay);
    }
}
