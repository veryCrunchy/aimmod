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

public partial class ScoreModFilterDropdown : BoundedShearedDropdown<string> {
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

// Long score-derived lists must scroll within the window, including when resized.
public partial class BoundedShearedDropdown<T> : ShearedDropdown<T>
{
    public BoundedShearedDropdown(LocalisableString label) : base(label)
    {
        Menu.MaxHeight = 240;
    }

    protected override DropdownMenu CreateMenu() => new BoundedMenu();

    private partial class BoundedMenu : ShearedDropdownMenu
    {
        public BoundedMenu()
        {
            // Tall sheared menus drift sideways and clip labels at the window edge.
            Shear = osuTK.Vector2.Zero;
            Padding = new MarginPadding();
        }

        protected override DrawableDropdownMenuItem CreateDrawableDropdownMenuItem(osu.Framework.Graphics.UserInterface.MenuItem item)
            => new StraightMenuItem(item)
            {
                BackgroundColourHover = HoverColour,
                BackgroundColourSelected = SelectionColour,
            };

        private partial class StraightMenuItem : ShearedMenuItem
        {
            public StraightMenuItem(osu.Framework.Graphics.UserInterface.MenuItem item) : base(item)
            {
                Foreground.Shear = osuTK.Vector2.Zero;
            }
        }
    }

    protected override void Update()
    {
        base.Update();
        Drawable viewport = this;
        while (viewport.Parent is { } parent) viewport = parent;
        float bottom = ToLocalSpace(viewport.ToScreenSpace(new osuTK.Vector2(0, viewport.DrawHeight))).Y;
        Menu.MaxHeight = Math.Clamp(bottom - DrawHeight - 24, 1, 240);
    }
}
