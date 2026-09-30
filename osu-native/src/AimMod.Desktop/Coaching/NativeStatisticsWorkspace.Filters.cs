using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;

namespace AimMod.Desktop.Coaching;

public partial class NativeStatisticsWorkspace
{
    private const float popover_width = 340;

    /// <summary>
    /// Less common filters live in a popover below the Filters button. Rows are placed at fixed positions, not in a
    /// flow, so the popup layer can raise an open dropdown's row above its neighbours without reordering them.
    /// </summary>
    private Container createFilterPopover()
    {
        var popover = new StatisticsFilterBar
        {
            Anchor = Anchor.TopRight,
            Origin = Anchor.TopRight,
            Y = toolbar_y + AimModVisualStyle.ControlHeight + 6,
            Width = popover_width,
            Height = 330,
        };
        popover.AddRange(new Drawable[]
        {
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Masking = true,
                CornerRadius = AimModVisualStyle.CardRadius,
                BorderThickness = 1,
                BorderColour = AimModPalette.Border,
                EdgeEffect = new osu.Framework.Graphics.Effects.EdgeEffectParameters
                {
                    Type = osu.Framework.Graphics.Effects.EdgeEffectType.Shadow,
                    Colour = Colour4.Black.Opacity(0.45f),
                    Radius = 18,
                    Offset = new(0, 6),
                },
                Child = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
            },
            // A click inside the panel must not reach the dismiss layer behind it.
            new PopoverInputBlocker { RelativeSizeAxes = Axes.Both },
            popoverRow(16, "Saved", new StatisticsSegmentedControl<StatisticsScoreSource>(scoreSource,
            [
                (StatisticsScoreSource.All, "All plays"), (StatisticsScoreSource.Local, "On this PC"), (StatisticsScoreSource.Online, "Submitted online"),
            ], AimModVisualStyle.CompactControlHeight)),
            popoverRow(86, "Result", new StatisticsSegmentedControl<StatisticsResultFilter>(resultFilter,
            [
                (StatisticsResultFilter.All, "Any result"), (StatisticsResultFilter.MissFree, "Miss-free only"),
            ], AimModVisualStyle.CompactControlHeight)),
            popoverRow(156, "Stars", new AimModStarRatingFilter
            {
                RelativeSizeAxes = Axes.X,
                Height = 48,
                LowerBound = minimumStars,
                UpperBound = maximumStars,
                DefaultStringLowerBound = "0",
                DefaultStringUpperBound = "10+",
            }),
            // Mods sit above Stars in depth so the open menu covers the slider below it.
            new Container
            {
                RelativeSizeAxes = Axes.X,
                Y = 226,
                Height = AimModVisualStyle.ControlHeight,
                Depth = -2,
                Padding = new MarginPadding { Horizontal = 16 },
                Child = modDropdown,
            },
            new Container
            {
                Anchor = Anchor.BottomLeft,
                Origin = Anchor.BottomLeft,
                RelativeSizeAxes = Axes.X,
                Height = AimModVisualStyle.ControlHeight,
                Y = -16,
                Padding = new MarginPadding { Horizontal = 16 },
                Children = new Drawable[]
                {
                    new AimModButton("Reset", resetPopoverFilters),
                    new AimModButton("Done", closeFilters, primary: true) { Anchor = Anchor.TopRight, Origin = Anchor.TopRight },
                },
            },
        });
        return popover;
    }

    private sealed partial class PopoverInputBlocker : Container
    {
        protected override bool OnMouseDown(osu.Framework.Input.Events.MouseDownEvent e) => true;

        protected override bool OnClick(osu.Framework.Input.Events.ClickEvent e) => true;
    }

    private static Drawable popoverRow(float y, string label, Drawable control) => new Container
    {
        RelativeSizeAxes = Axes.X,
        Y = y,
        Height = 62,
        Padding = new MarginPadding { Horizontal = 16 },
        Children = new[]
        {
            StatisticsChartFormat.Text(label, 12, AimModPalette.Muted, "SemiBold"),
            control.With(d => d.Y = 20),
        },
    };

    private void toggleFilters()
    {
        if (filtersOpen) closeFilters();
        else openFilters();
    }

    private void openFilters()
    {
        filtersOpen = true;
        filterLayer.FadeIn(AimModVisualStyle.FastTransition);
        filtersButton.SetSelected(true);
    }

    private void closeFilters()
    {
        filtersOpen = false;
        filterLayer.FadeOut(AimModVisualStyle.FastTransition);
        filtersButton.SetSelected(false);
    }

    private void resetPopoverFilters()
    {
        modFilter.Value = ScoreMods.Any;
        scoreSource.Value = StatisticsScoreSource.All;
        minimumStars.SetDefault();
        maximumStars.SetDefault();
        resultFilter.Value = StatisticsResultFilter.All;
    }

    /// <summary>Shows each active filter as a removable chip and keeps the Filters button count current.</summary>
    private void updateChips()
    {
        chips.Clear();
        if (search.Current.Value.Trim() is { Length: > 0 } text)
            chips.Add(new StatisticsFilterChip($"\"{text}\"", () => search.Current.Value = string.Empty));
        if (timeRange.Value != StatisticsTimeRange.All)
            chips.Add(new StatisticsFilterChip(timeRange.Value switch
            {
                StatisticsTimeRange.Days30 => "Last 30 days",
                StatisticsTimeRange.Days90 => "Last 90 days",
                _ => "Last year",
            }, () => timeRange.Value = StatisticsTimeRange.All));
        if (scoreSource.Value != StatisticsScoreSource.All)
            chips.Add(new StatisticsFilterChip(scoreSource.Value == StatisticsScoreSource.Local ? "On this PC" : "Submitted online",
                () => scoreSource.Value = StatisticsScoreSource.All));
        if (resultFilter.Value == StatisticsResultFilter.MissFree)
            chips.Add(new StatisticsFilterChip("Miss-free", () => resultFilter.Value = StatisticsResultFilter.All));
        if (!minimumStars.IsDefault || !maximumStars.IsDefault)
            chips.Add(new StatisticsFilterChip(starLabel(), () => { minimumStars.SetDefault(); maximumStars.SetDefault(); }));
        if (modFilter.Value != ScoreMods.Any)
            chips.Add(new StatisticsFilterChip(modDropdown.Items.Contains(modFilter.Value) ? modLabel() : "Mods", () => modFilter.Value = ScoreMods.Any));

        clearButton.Alpha = filtersActive ? 1 : 0;
        int count = popoverFilterCount;
        filtersButton.SetCaption(count == 0 ? "Filters" : $"Filters · {count}");
        mainLayoutWidth = -1;
        layoutWidth = -1;
    }

    private string modLabel() => modDropdown.GetItemLabel(modFilter.Value);

    private string starLabel()
    {
        bool upper = !maximumStars.IsDefault;
        bool lower = !minimumStars.IsDefault;
        if (lower && upper) return $"{minimumStars.Value:0.#}–{maximumStars.Value:0.#} stars";
        return lower ? $"{minimumStars.Value:0.#}+ stars" : $"Up to {maximumStars.Value:0.#} stars";
    }
}
