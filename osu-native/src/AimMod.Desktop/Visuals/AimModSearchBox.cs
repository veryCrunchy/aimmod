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
    public LocalisableString StatusText { get => status.Text; set => status.Text = value; }
    public AimModSearchBox()
    {
        Height = AimModVisualStyle.ControlHeight;
        Children = [input = new AimModTextBox { RelativeSizeAxes = Axes.X, PlaceholderText = "Search maps" },
            status = new TruncatingSpriteText { Y = 42, RelativeSizeAxes = Axes.X, Font = new FontUsage(size:11), Colour = AimModPalette.Muted }];
        input.OnCommit += (_, _) => Committed?.Invoke();
    }
}
