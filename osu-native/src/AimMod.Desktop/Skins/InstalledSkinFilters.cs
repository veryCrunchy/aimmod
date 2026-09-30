using System.ComponentModel;

namespace AimMod.Desktop.Skins;

public enum InstalledSkinSort
{
    [Description("Name")]
    Name,

    [Description("Recently added")]
    RecentlyAdded,

    [Description("Source")]
    Source,
}

public enum InstalledSkinSourceFilter
{
    [Description("All sources")]
    All,

    [Description("osu!lazer")]
    Lazer,

    [Description("osu!stable")]
    Stable,
}

public static class InstalledSkinFilters
{
    /// <summary>Filters and orders a loaded skin list by its readable name.</summary>
    public static IReadOnlyList<InstalledLazerSkin> Apply(
        IEnumerable<InstalledLazerSkin> skins,
        InstalledSkinSourceFilter source,
        InstalledSkinSort sort)
    {
        IEnumerable<InstalledLazerSkin> filtered = source switch
        {
            InstalledSkinSourceFilter.Lazer => skins.Where(skin => skin.Origin == InstalledSkinOrigin.Lazer),
            InstalledSkinSourceFilter.Stable => skins.Where(skin => skin.Origin == InstalledSkinOrigin.Stable),
            _ => skins,
        };
        IOrderedEnumerable<InstalledLazerSkin> ordered = sort switch
        {
            InstalledSkinSort.RecentlyAdded => filtered.OrderByDescending(skin => skin.AddedAt ?? DateTimeOffset.MinValue),
            InstalledSkinSort.Source => filtered.OrderBy(skin => skin.IsBuiltIn ? 0 : skin.Origin == InstalledSkinOrigin.Lazer ? 1 : 2),
            _ => filtered.OrderBy(_ => 0),
        };
        return ordered.ThenBy(skin => skin.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
