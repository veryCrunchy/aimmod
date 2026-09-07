using osu.Framework.Graphics.UserInterface;
using AimMod.Desktop.Visuals;
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

public partial class StatisticsFilterDropdown<T> : BoundedShearedDropdown<T>
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
    [osu.Framework.Allocation.Resolved] private osu.Framework.Platform.GameHost popupHost { get; set; } = null!;
    private AimModPopupLayer? popupLayer;
    public BoundedShearedDropdown(LocalisableString label) : base(label)
    {
        Menu.MaxHeight = 240;
        if (Header is AimModDropdownHeader<T> header) header.Prefix = label.ToString();
        Menu.StateChanged += state =>
        {
            popupLayer?.Dispose(); popupLayer = null;
            if (state == MenuState.Open) popupLayer = new AimModPopupLayer(this, action => popupHost.UpdateThread.Scheduler.Add(action));
        };
    }
    protected override DropdownHeader CreateHeader() => new AimModDropdownHeader<T>();
    protected override DropdownMenu CreateMenu() => new AimModDropdownMenu<T>();
    protected override void Dispose(bool isDisposing)
    { popupLayer?.Dispose(); popupLayer = null; base.Dispose(isDisposing); }
    protected override void Update()
    {
        base.Update();
        Drawable viewport = this;
        while (viewport.Parent is {} parent) viewport = parent;
        float bottom = ToLocalSpace(viewport.ToScreenSpace(new osuTK.Vector2(0, viewport.DrawHeight))).Y;
        Menu.MaxHeight = Math.Clamp(bottom - DrawHeight - 24, 1, 240);
    }
}
