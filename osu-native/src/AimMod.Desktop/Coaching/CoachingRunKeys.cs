using System.Runtime.CompilerServices;
using AimMod.Desktop.LocalLibrary;

namespace AimMod.Desktop.Coaching;

/// <summary>
/// Memoises the mod-derived keys of immutable score records. <see cref="ScoreMods"/> parses the mod JSON on
/// every call, which dominates coaching builds that group or compare thousands of plays repeatedly.
/// </summary>
internal static class CoachingRunKeys
{
    private static readonly string[] assisted_mods = ["AT", "CN", "RX", "AP", "AUTOPLAY", "CINEMA", "RELAX", "AUTOPILOT"];
    private static readonly ConditionalWeakTable<LocalReplay, Keys> keys = new();

    public static string[] Acronyms(LocalReplay run)
    {
        Keys entry = get(run);
        return entry.Acronyms ??= ScoreMods.Acronyms(run);
    }

    public static string Configuration(LocalReplay run)
    {
        Keys entry = get(run);
        return entry.Configuration ??= ScoreMods.Configuration(run);
    }

    public static string SetupKey(LocalReplay run)
    {
        Keys entry = get(run);
        return entry.SetupKey ??= ScoreMods.SetupKey(run);
    }

    public static bool IsManualPlay(LocalReplay run)
    {
        Keys entry = get(run);
        if (entry.Manual is { } manual)
            return manual;
        bool value = !Acronyms(run).Intersect(assisted_mods).Any();
        entry.Manual = value;
        return value;
    }

    /// <summary>Equivalent to <see cref="ScoreMods.Matches"/> using the memoised keys.</summary>
    public static bool Matches(LocalReplay run, string selection) => selection switch
    {
        ScoreMods.Any or "" => true,
        "NM" => Acronyms(run).Length == 0,
        _ when selection.StartsWith("mod:", StringComparison.Ordinal) => Acronyms(run).Contains(selection[4..]),
        _ when selection.StartsWith("setup:", StringComparison.Ordinal) => Configuration(run) == selection[6..],
        _ => false,
    };

    /// <summary>Case-insensitive distinct mod acronyms as stored on the score, for set comparisons.</summary>
    public static HashSet<string> ModSet(LocalReplay run)
    {
        Keys entry = get(run);
        return entry.ModSet ??= (run.Mods ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static Keys get(LocalReplay run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return keys.GetValue(run, static _ => new Keys());
    }

    private sealed class Keys
    {
        public string[]? Acronyms;
        public string? Configuration;
        public string? SetupKey;
        public bool? Manual;
        public HashSet<string>? ModSet;
    }
}
