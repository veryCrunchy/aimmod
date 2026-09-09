namespace AimMod.Desktop.Practice;

public sealed record PracticeSectionRange(double StartMs, double EndMs);

public static class PracticeSectionQueue
{
    public static IReadOnlyList<PracticeSectionChoice> Remaining(IEnumerable<PracticeSectionChoice> choices,
        IReadOnlyList<PracticeSectionRange> prepared) => choices
        .Where(c => c.Section.SourceEndTimeMs > c.Section.SourceStartTimeMs)
        .Where(c => !prepared.Any(p =>
            Math.Max(0, Math.Min(p.EndMs, c.Section.SourceEndTimeMs) - Math.Max(p.StartMs, c.Section.SourceStartTimeMs))
            >= (c.Section.SourceEndTimeMs - c.Section.SourceStartTimeMs) * .5))
        .OrderBy(c => c.Section.SourceStartTimeMs).ThenByDescending(c => c.Section.WeaknessScore)
        .DistinctBy(c => (c.Section.FirstObjectIndex, c.Section.LastObjectIndex)).ToArray();
}
