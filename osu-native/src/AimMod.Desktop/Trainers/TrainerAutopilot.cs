using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;

namespace AimMod.Desktop.Trainers;

/// <summary>osu!'s Autopilot mod for the timing drills: the cursor follows the notes and the player only taps.</summary>
public static class TrainerAutopilot
{
    public static bool Supports(TrainerKind kind) => kind <= TrainerKind.Rhythm;

    public static bool IsActive(TrainerSettings settings) => settings.Autopilot && Supports(settings.Kind);

    /// <summary>
    /// Mods for the embedded player. Star targets keep using <see cref="TrainerReadingPatterns.Mods"/>,
    /// so turning Autopilot on generates the same drill.
    /// </summary>
    public static Mod[] GameplayMods(TrainerSettings settings)
    {
        Mod[] mods = TrainerReadingPatterns.Mods(settings);
        return IsActive(settings) ? [.. mods, new OsuModAutopilot()] : mods;
    }
}
