namespace AimMod.Osu.Runtime.Contracts;

public sealed record ExternalTrainerSettingsRequest(string LibraryRoot);
public sealed record ExternalTrainerKeyBinding(int Action, string Combination);
public sealed record ExternalTrainerSettingsResult(IReadOnlyList<ExternalTrainerKeyBinding> Bindings);
