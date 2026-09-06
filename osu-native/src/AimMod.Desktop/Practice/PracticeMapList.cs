using AimMod.Desktop.Visuals;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Pooling;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Practice;

internal sealed record PracticeMapListRow(string Key, string Title, string Difficulty, string Summary,
    string? Image, Action<string> Select);

internal partial class PracticeMapList : VirtualisedListContainer<PracticeMapListRow, PracticeMapListDrawable>
{
    public PracticeMapList() : base(106, initialPoolSize: 12) { }
    protected override ScrollContainer<Drawable> CreateScrollContainer() => new AimModScrollContainer();

    public void SetRows(IReadOnlyList<PracticeMapListRow> rows)
    {
        if (RowData.SequenceEqual(rows)) return;
        RowData.Clear();
        RowData.AddRange(rows);
    }
}

internal partial class PracticeMapListDrawable : PoolableDrawable, IHasCurrentValue<PracticeMapListRow>
{
    private readonly BindableWithCurrent<PracticeMapListRow> current = new();
    private readonly Container artwork = new() { Width = 80, RelativeSizeAxes = Axes.Y, Masking = true };
    private readonly Container labels = new() { RelativeSizeAxes = Axes.Both };
    private readonly TruncatingSpriteText title = text(15, AimModPalette.Text);
    private readonly TruncatingSpriteText difficulty = text(12, AimModPalette.Cyan);
    private readonly TruncatingSpriteText summary = text(11, AimModPalette.Muted);
    private string? loadedImage;
    public Bindable<PracticeMapListRow> Current { get => current.Current; set => current.Current = value; }

    public PracticeMapListDrawable()
    {
        RelativeSizeAxes = Axes.Both;
        title.Y = 10; difficulty.Y = 34; summary.Y = 56;
        labels.Children = [title, difficulty, summary];
        InternalChild = new Container
        {
            RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Right = 10, Bottom = 10 },
            Child = new ClickableContainer
            {
                RelativeSizeAxes = Axes.Both, Masking = true, CornerRadius = 4,
                Action = () => current.Value?.Select(current.Value.Key),
                Children = [new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised }, artwork, labels],
            },
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        current.BindValueChanged(change =>
        {
            var row = change.NewValue;
            if (row is null) return;
            title.Text = row.Title; difficulty.Text = row.Difficulty; summary.Text = row.Summary;
            labels.Padding = new MarginPadding { Left = string.IsNullOrWhiteSpace(row.Image) ? 12 : 92, Right = 10 };
            if (loadedImage == row.Image) return;
            loadedImage = row.Image;
            artwork.Clear();
            if (!string.IsNullOrWhiteSpace(row.Image))
            {
                string path = row.Image;
                artwork.Add(new DelayedLoadWrapper(() => new AimModLocalArtwork(path), 150) { RelativeSizeAxes = Axes.Both });
            }
        }, true);
    }

    private static TruncatingSpriteText text(float size, Colour4 colour) => new()
    { RelativeSizeAxes = Axes.X, Font = new FontUsage("Torus", size), Colour = colour };
}
