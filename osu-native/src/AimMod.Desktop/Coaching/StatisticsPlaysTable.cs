using osu.Game.Graphics.Sprites;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;

namespace AimMod.Desktop.Coaching;

/// <summary>Column positions shared by the header and every row, so values line up.</summary>
public static class StatisticsTableColumns
{
    public enum Column
    {
        Map,
        Mods,
        Stars,
        Accuracy,
        Misses,
        Pp,
        Played,
    }

    public const float NarrowWidth = 600;
    private const float gap = 12;

    private static readonly (Column Column, float Width)[] fixedColumns =
    [
        (Column.Mods, 74), (Column.Stars, 64), (Column.Accuracy, 70), (Column.Misses, 52), (Column.Pp, 56), (Column.Played, 74),
    ];

    public static bool IsNarrow(float width) => width < NarrowWidth;

    /// <summary>Left edge and width of each visible column; narrow tables fold mods and date into the map cell.</summary>
    public static IReadOnlyDictionary<Column, (float X, float Width)> Layout(float width)
    {
        bool narrow = IsNarrow(width);
        var visible = fixedColumns.Where(column => !narrow || column.Column is not (Column.Mods or Column.Played)).ToArray();
        const float left = 14;
        const float right = 14;
        float fixedWidth = visible.Sum(column => column.Width + gap);
        float mapWidth = Math.Max(80, width - left - right - fixedWidth);
        var result = new Dictionary<Column, (float, float)> { [Column.Map] = (left, mapWidth) };
        float x = left + mapWidth + gap;
        foreach ((Column column, float columnWidth) in visible)
        {
            result[column] = (x, columnWidth);
            x += columnWidth + gap;
        }

        return result;
    }

    public static bool RightAligned(Column column) => column is Column.Accuracy or Column.Misses or Column.Pp or Column.Played;
}

/// <summary>Column titles. Sortable columns show the active order and change it on click.</summary>
public sealed partial class StatisticsPlaysHeader : CompositeDrawable
{
    private readonly Bindable<StatisticsRunSort> sort = new();
    private readonly Dictionary<StatisticsTableColumns.Column, Drawable> cells = [];
    private float laidOutWidth = -1;

    public StatisticsPlaysHeader(Bindable<StatisticsRunSort> source)
    {
        sort.BindTo(source);
        RelativeSizeAxes = Axes.X;
        Height = 30;
        foreach (StatisticsTableColumns.Column column in Enum.GetValues<StatisticsTableColumns.Column>())
        {
            StatisticsRunSort? order = column switch
            {
                StatisticsTableColumns.Column.Played => StatisticsRunSort.Recent,
                StatisticsTableColumns.Column.Stars => StatisticsRunSort.StarRating,
                StatisticsTableColumns.Column.Accuracy => StatisticsRunSort.Accuracy,
                StatisticsTableColumns.Column.Pp => StatisticsRunSort.PerformancePoints,
                _ => null,
            };
            Drawable cell = order is { } value
                ? new SortCell(title(column), value, sort, StatisticsTableColumns.RightAligned(column))
                : StatisticsChartFormat.Text(title(column), 11, AimModPalette.Muted, "SemiBold").With(t =>
                {
                    t.Anchor = Anchor.CentreLeft;
                    t.Origin = StatisticsTableColumns.RightAligned(column) ? Anchor.CentreRight : Anchor.CentreLeft;
                });
            cells[column] = cell;
            AddInternal(cell);
        }
    }

    private static string title(StatisticsTableColumns.Column column) => column switch
    {
        StatisticsTableColumns.Column.Map => "Map",
        StatisticsTableColumns.Column.Mods => "Mods",
        StatisticsTableColumns.Column.Stars => "Stars",
        StatisticsTableColumns.Column.Accuracy => "Accuracy",
        StatisticsTableColumns.Column.Misses => "Misses",
        StatisticsTableColumns.Column.Pp => "PP",
        _ => "Played",
    };

    protected override void Update()
    {
        base.Update();
        if (DrawWidth == laidOutWidth) return;
        laidOutWidth = DrawWidth;
        var layout = StatisticsTableColumns.Layout(DrawWidth);
        foreach ((StatisticsTableColumns.Column column, Drawable cell) in cells)
        {
            if (!layout.TryGetValue(column, out var slot))
            {
                cell.Alpha = 0;
                continue;
            }

            cell.Alpha = 1;
            if (cell is SortCell sortCell)
            {
                sortCell.X = slot.X;
                sortCell.Width = slot.Width;
            }
            else
            {
                cell.X = StatisticsTableColumns.RightAligned(column) ? slot.X + slot.Width : slot.X;
            }
        }
    }

    private sealed partial class SortCell : ClickableContainer
    {
        private readonly SpriteText text;
        private readonly SpriteIcon icon;
        private readonly StatisticsRunSort order;
        private readonly Bindable<StatisticsRunSort> sort = new();

        public SortCell(string label, StatisticsRunSort order, Bindable<StatisticsRunSort> source, bool rightAligned)
        {
            this.order = order;
            sort.BindTo(source);
            RelativeSizeAxes = Axes.Y;
            Action = () => sort.Value = order;
            Anchor anchor = rightAligned ? Anchor.CentreRight : Anchor.CentreLeft;
            Child = new FillFlowContainer
            {
                Anchor = anchor,
                Origin = anchor,
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Horizontal,
                Spacing = new(4),
                Children =
                [
                    text = StatisticsChartFormat.Text(label, 11, AimModPalette.Muted, "SemiBold").With(t => { t.Anchor = Anchor.CentreLeft; t.Origin = Anchor.CentreLeft; }),
                    icon = new SpriteIcon { Icon = FontAwesome.Solid.CaretDown, Size = new(9), Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft },
                ],
            };
            sort.BindValueChanged(_ => style(), true);
        }

        private void style()
        {
            bool active = sort.Value == order;
            text.Colour = active ? AimModPalette.Accent : IsHovered ? AimModPalette.Text : AimModPalette.Muted;
            icon.Colour = text.Colour;
            icon.Alpha = active || IsHovered ? 1 : 0;
        }

        protected override bool OnHover(HoverEvent e)
        {
            style();
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            base.OnHoverLost(e);
            style();
        }
    }
}

/// <summary>One play in the table: map, mods, stars, accuracy, misses, PP and when it was played.</summary>
public sealed partial class StatisticsPlayRow : ClickableContainer
{
    private readonly Box background;
    private readonly Box selectionBar;
    private readonly TruncatingSpriteText title;
    private readonly TruncatingSpriteText subtitle;
    private readonly Dictionary<StatisticsTableColumns.Column, Drawable> cells = [];
    private readonly string artist;
    private readonly string compactDetail;
    private float laidOutWidth = -1;
    private bool selected;

    public LocalReplay Replay { get; }

    public const string MissingPpTooltip = "No PP: unranked map, failed play or not calculated";

    public StatisticsPlayRow(LocalReplay replay, Action action, DateTimeOffset now)
    {
        Replay = replay;
        Action = action;
        RelativeSizeAxes = Axes.X;
        Height = 46;
        Masking = true;
        CornerRadius = AimModVisualStyle.ControlRadius;
        string mods = replay.Mods.Count == 0 ? "No mod" : string.Join(" ", replay.Mods.Take(4));
        artist = replay.Artist;
        compactDetail = $"{replay.Artist}  ·  {mods}  ·  {StatisticsChartFormat.Relative(replay.PlayedAt, now)}";
        Children =
        [
            background = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
            selectionBar = new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = AimModPalette.Accent, Alpha = 0 },
        ];

        var map = new FillFlowContainer
        {
            Anchor = Anchor.CentreLeft,
            Origin = Anchor.CentreLeft,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(2),
            Children =
            [
                title = StatisticsChartFormat.Truncating($"{replay.Title} [{replay.Difficulty}]", 13, AimModPalette.Text, "SemiBold"),
                subtitle = StatisticsChartFormat.Truncating(replay.Artist, 11, AimModPalette.Muted),
            ],
        };
        add(StatisticsTableColumns.Column.Map, map);
        add(StatisticsTableColumns.Column.Mods, StatisticsChartFormat.Truncating(mods, 12, replay.Mods.Count == 0 ? AimModPalette.Muted : AimModPalette.Text, "SemiBold"));
        add(StatisticsTableColumns.Column.Stars, double.IsFinite(replay.StarRating)
            ? new AimModDifficultyPill(replay.StarRating)
            : new StatisticsHintText(StatisticsChartFormat.Missing, "Star rating unknown for this score", 13, AimModPalette.Muted));
        add(StatisticsTableColumns.Column.Accuracy, StatisticsChartFormat.Text(
            StatisticsChartFormat.Value(StatisticsMetric.Accuracy, StatisticsInsightsBuilder.Value(replay, StatisticsMetric.Accuracy)), 13, AimModPalette.Text, "SemiBold"));
        add(StatisticsTableColumns.Column.Misses, StatisticsChartFormat.Text(
            Math.Max(0, replay.MissCount).ToString("N0"), 13, replay.MissCount == 0 ? AimModPalette.Success : AimModPalette.Text, "SemiBold"));
        add(StatisticsTableColumns.Column.Pp, replay.PerformancePoints is { } pp && pp >= 0
            ? StatisticsChartFormat.Text($"{pp:0}pp", 13, AimModPalette.Text, "SemiBold")
            : new StatisticsHintText(StatisticsChartFormat.Missing, MissingPpTooltip, 13, AimModPalette.Muted, "SemiBold"));
        add(StatisticsTableColumns.Column.Played, new StatisticsHintText(
            StatisticsChartFormat.Relative(replay.PlayedAt, now), StatisticsChartFormat.DateTime(replay.PlayedAt), 12, AimModPalette.Muted));
    }

    private void add(StatisticsTableColumns.Column column, Drawable cell)
    {
        cells[column] = cell;
        Add(cell);
    }

    public void SetSelected(bool value)
    {
        if (selected == value) return;
        selected = value;
        style();
    }

    private void style()
    {
        background.FadeColour(selected ? AimModPalette.AccentMuted : IsHovered ? AimModPalette.PanelHover : AimModPalette.Panel, AimModVisualStyle.FastTransition);
        selectionBar.FadeTo(selected ? 1 : 0, AimModVisualStyle.FastTransition);
    }

    protected override void Update()
    {
        base.Update();
        if (DrawWidth == laidOutWidth) return;
        laidOutWidth = DrawWidth;
        bool narrow = StatisticsTableColumns.IsNarrow(DrawWidth);
        subtitle.Text = narrow ? compactDetail : artist;
        var layout = StatisticsTableColumns.Layout(DrawWidth);
        foreach ((StatisticsTableColumns.Column column, Drawable cell) in cells)
        {
            if (!layout.TryGetValue(column, out var slot))
            {
                cell.Alpha = 0;
                continue;
            }

            cell.Alpha = 1;
            bool right = StatisticsTableColumns.RightAligned(column);
            cell.Anchor = right ? Anchor.CentreLeft : Anchor.CentreLeft;
            cell.Origin = right ? Anchor.CentreRight : Anchor.CentreLeft;
            cell.X = right ? slot.X + slot.Width : slot.X;
            if (column == StatisticsTableColumns.Column.Map)
            {
                title.MaxWidth = slot.Width;
                subtitle.MaxWidth = slot.Width;
            }
            else if (cell is TruncatingSpriteText text)
            {
                text.MaxWidth = slot.Width;
            }
        }
    }

    protected override bool OnHover(HoverEvent e)
    {
        style();
        return true;
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        base.OnHoverLost(e);
        style();
    }
}
