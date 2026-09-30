using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Visuals;

/// <summary>One search field with an optional result count beneath it.</summary>
public partial class AimModSearchBox : Container
{
    private readonly AimModTextBox input;
    private readonly TruncatingSpriteText status;
    public event Action? Committed;
    public Bindable<string> Current => input.Current;
    public string SearchHint { get => input.PlaceholderText.ToString(); set => input.PlaceholderText = value; }
    public LocalisableString PlaceholderText { get => input.PlaceholderText; set => input.PlaceholderText = value; }
    public LocalisableString StatusText { get => status.Text; set { if (status.Text != value) status.Text = value; } }

    /// <summary>Raised once per edit burst after a short pause, or immediately (once) on Enter.</summary>
    public event Action<string>? QueryChanged
    {
        add => input.QueryChanged += value;
        remove => input.QueryChanged -= value;
    }

    /// <summary>Raised when Down is pressed while typing so the page can move focus into its results.</summary>
    public event Action? MoveToResults
    {
        add => input.MoveToResults += value;
        remove => input.MoveToResults -= value;
    }

    public bool IsTextFocused => input.HasFocus;

    internal AimModTextBox TextBox => input;

    public AimModSearchBox()
    {
        Height = AimModVisualStyle.ControlHeight;
        Children = [input = new AimModTextBox { RelativeSizeAxes = Axes.X, PlaceholderText = "Search maps", FocusOnSearchShortcut = true },
            status = new TruncatingSpriteText { Y = 42, RelativeSizeAxes = Axes.X, Font = new FontUsage(size:11), Colour = AimModPalette.Muted }];
        input.OnCommit += (_, _) => Committed?.Invoke();
    }

    public void FocusSearch() => input.FocusSearch();
}
