namespace AimMod.Desktop.PpTargets;

/// <summary>A pattern class to find maps for, measured from each map's own objects.</summary>
public enum PpTargetSkillset { Any, Streams, Bursts, Jumps, SharpAngles }

/// <summary>
/// How much of a map is a given pattern, from the features PP targets measures for every calculated difficulty: the
/// same streams, bursts, jumps and direction changes as Home's skillsets and the statistics page.
/// </summary>
public static class PpTargetSkillsets
{
    public static readonly PpTargetSkillset[] Choices = Enum.GetValues<PpTargetSkillset>();

    public static string Label(PpTargetSkillset skillset) => skillset switch
    {
        PpTargetSkillset.Streams => "Streams",
        PpTargetSkillset.Bursts => "Bursts",
        PpTargetSkillset.Jumps => "Jumps",
        PpTargetSkillset.SharpAngles => "Sharp angles",
        _ => "Any skillset",
    };

    /// <summary>
    /// The share a map needs to count as this kind of map. Streams and bursts use the 10% at which PP targets already
    /// treats a map as demanding them; jumps and sharp angles appear in most maps, so they need to dominate.
    /// </summary>
    public static double Threshold(PpTargetSkillset skillset) => skillset switch
    {
        PpTargetSkillset.Streams => .10,
        PpTargetSkillset.Bursts => .10,
        PpTargetSkillset.Jumps => .40,
        PpTargetSkillset.SharpAngles => .30,
        _ => 0,
    };

    /// <summary>
    /// Streams and bursts: share of notes inside them. Jumps: share of moves between notes that are jumps. Sharp angles:
    /// share of turns of 60° or more. Null when the map has not been measured or its circle size is unknown.
    /// </summary>
    public static double? Share(PpPatternFeatures? features, PpTargetSkillset skillset) => features is null ? null : skillset switch
    {
        PpTargetSkillset.Streams => features.StreamFraction,
        PpTargetSkillset.Bursts => features.BurstFraction,
        PpTargetSkillset.Jumps => features.JumpFraction,
        PpTargetSkillset.SharpAngles => features.SharpTurnFraction,
        _ => null,
    };

    public static bool Matches(PpPatternFeatures? features, PpTargetSkillset skillset) =>
        skillset == PpTargetSkillset.Any || Share(features, skillset) is { } share && share >= Threshold(skillset);

    /// <summary>A short tag for a map row: the chosen skillset's share, or else the map's most prominent pattern.</summary>
    public static string? Tag(PpPatternFeatures? features, PpTargetSkillset chosen)
    {
        if (chosen != PpTargetSkillset.Any)
            return Share(features, chosen) is { } share ? $"{share:P0} {noun(chosen)}" : null;
        var prominent = Choices.Where(s => s != PpTargetSkillset.Any)
            .Select(s => (Skillset: s, Share: Share(features, s)))
            .Where(p => p.Share is { } share && share >= Threshold(p.Skillset))
            .OrderByDescending(p => p.Share!.Value / Threshold(p.Skillset))
            .FirstOrDefault();
        return prominent.Share is { } value ? $"{value:P0} {noun(prominent.Skillset)}" : null;
    }

    private static string noun(PpTargetSkillset skillset) => skillset switch
    {
        PpTargetSkillset.Streams => "streams",
        PpTargetSkillset.Bursts => "bursts",
        PpTargetSkillset.Jumps => "jumps",
        _ => "sharp angles",
    };
}
