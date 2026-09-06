using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Localisation;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Overlays;

namespace AimMod.Desktop.Coaching;

public partial class StatisticsFilterBar : Container
{
    // Scope osu's native song-select control theme to this toolbar.
    [Cached]
    private readonly OverlayColourProvider colours = new(OverlayColourScheme.Blue);
}

public partial class StatisticsFilterDropdown<T> : ShearedDropdown<T>
    where T : struct, Enum
{
    public StatisticsFilterDropdown(string label, Bindable<T> current)
        : base(label)
    {
        RelativeSizeAxes = Axes.X;
        Items = Enum.GetValues<T>();
        Current = current;
    }

    protected override LocalisableString GenerateItemText(T item) => item.ToString() switch
    {
        "Days30" => "Last 30 days",
        "Days90" => "Last 90 days",
        "Year" => "Last year",
        "NoMod" => "No Mod",
        "HardRock" => "Hard Rock",
        "DoubleTime" => "Double Time",
        "PerformancePoints" => "Performance points",
        "StarRating" => "Star rating",
        "BelowFour" => "Below 4 stars",
        "FourToFive" => "4 - 5 stars",
        "FiveToSix" => "5 - 6 stars",
        "SixToSeven" => "6 - 7 stars",
        "SevenPlus" => "7+ stars",
        "MissFree" => "Miss-free",
        _ => item.ToString(),
    };
}

public partial class ScoreModFilterDropdown : ShearedDropdown<string> {
    private IReadOnlyDictionary<string,string> labels = new Dictionary<string,string>();
    public ScoreModFilterDropdown(Bindable<string> current) : base("Mods") { RelativeSizeAxes=Axes.X; Current=current; SetScores([]); }
    public void SetScores(IEnumerable<AimMod.Desktop.LocalLibrary.LocalReplay> scores) {
        SetChoices(AimMod.Desktop.LocalLibrary.ScoreMods.Choices(scores));
    }
    public void SetChoices(IReadOnlyList<AimMod.Desktop.LocalLibrary.ScoreModChoice> choices) {
        labels=choices.ToDictionary(c=>c.Key,c=>c.Label);
        Items=choices.Select(c=>c.Key).ToArray();
        if (!labels.ContainsKey(Current.Value)) Current.Value=AimMod.Desktop.LocalLibrary.ScoreMods.Any;
    }
    protected override LocalisableString GenerateItemText(string item) => labels.GetValueOrDefault(item,item);
}
