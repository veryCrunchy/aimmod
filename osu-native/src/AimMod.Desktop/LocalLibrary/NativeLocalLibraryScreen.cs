using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Pooling;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.UserInterface;
using osu.Framework.Threading;
using osu.Framework.Allocation;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Framework.Graphics.Colour;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Drawables;
using AimMod.Desktop.Visuals;

namespace AimMod.Desktop.LocalLibrary;

public enum NativeLocalLibraryMode
{
    Beatmaps,
    Replays,
}

public partial class NativeLocalLibraryScreen : CompositeDrawable
{
    private const int page_size = 60;

    private readonly LocalLibraryController controller;
    private readonly ILocalLibrarySourceChanged? sourceChanges;
    private readonly NativeLocalLibraryMode mode;
    private readonly Action<LocalReplay>? openReplay;
    private readonly BindableList<LocalLibraryRow> rows = new();
    [Cached]
    private readonly Bindable<Guid?> selectedRowId = new();
    private readonly OsuTextBox searchBox;
    private readonly TruncatingSpriteText resultStatus;
    private readonly LoadMoreButton loadMoreButton;
    private readonly AimModLoadingOverlay loadingOverlay;
    private readonly RangeSlider starSlider;
    private readonly OsuDropdown<LocalLibrarySort> sortDropdown;
    private readonly Bindable<LocalLibrarySort> sortMode;
    private readonly BindableDouble minimumStars = new(0) { MinValue = 0, MaxValue = 10, Default = 0 };
    private readonly BindableDouble maximumStars = new(10) { MinValue = 0, MaxValue = 10, Default = 10 };

    private ScheduledDelegate? scheduledQuery;
    private long displayedRevision;

    public NativeLocalLibraryScreen(
        ILocalLibrarySource source,
        NativeLocalLibraryMode mode,
        Action<LocalReplay>? openReplay = null)
    {
        this.mode = mode;
        this.openReplay = openReplay;
        sortMode = new Bindable<LocalLibrarySort>(mode == NativeLocalLibraryMode.Beatmaps
            ? LocalLibrarySort.RecentlyAdded
            : LocalLibrarySort.RecentlyPlayed);
        controller = new LocalLibraryController(source, mode);
        controller.StateChanged += stateChanged;
        sourceChanges = source as ILocalLibrarySourceChanged;
        if (sourceChanges is not null)
            sourceChanges.SourceChanged += sourceChanged;
        RelativeSizeAxes = Axes.Both;

        InternalChildren = new Drawable[]
        {
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = 190, Bottom = 52 },
                Masking = true,
                Depth = 10,
                Child = new LocalLibraryVirtualisedList
                {
                    RelativeSizeAxes = Axes.Both,
                    RowData = { BindTarget = rows },
                },
            },
            new Box
            {
                RelativeSizeAxes = Axes.X,
                Height = 190,
                Colour = AimModPalette.Canvas,
                Depth = 5,
            },
            new AimModSectionHeader(
                mode == NativeLocalLibraryMode.Beatmaps ? "Beatmaps" : "Replays",
                mode == NativeLocalLibraryMode.Beatmaps
                    ? "Your lazer library, grouped by beatmap set and difficulty."
                    : "Saved local plays. Select one to watch and analyse it inside AimMod.",
                mode == NativeLocalLibraryMode.Beatmaps ? "local library" : "play history"),
            resultStatus = new TruncatingSpriteText
            {
                Y = 164,
                Text = "Loading library...",
                Font = new FontUsage(size: 12, weight: "SemiBold"),
                Colour = AimModPalette.Muted,
            },
            new SpriteText
            {
                Y = 72,
                Text = mode == NativeLocalLibraryMode.Beatmaps ? "SEARCH INSTALLED MAPS" : "SEARCH LOCAL PLAYS",
                Font = new FontUsage(size: 10, weight: "Bold"),
                Colour = AimModPalette.Cyan,
            },
            searchBox = new OsuTextBox
            {
                RelativeSizeAxes = Axes.X,
                Width = 0.43f,
                Height = 46,
                Y = 91,
                PlaceholderText = mode == NativeLocalLibraryMode.Beatmaps
                    ? "Search beatmaps, artists, mappers, or difficulties"
                    : "Search replays, players, maps, or mods",
            },
            starSlider = new RangeSlider
            {
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                Position = new(-292, 72),
                Size = new(350, 65),
                Label = "Star rating",
                LowerBound = minimumStars,
                UpperBound = maximumStars,
                DefaultStringLowerBound = "0",
                DefaultStringUpperBound = "10+",
                TooltipSuffix = "stars",
                NubWidth = 30,
            },
            new SpriteText
            {
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                Position = new(0, 72),
                Text = "SORT",
                Font = new FontUsage(size: 10, weight: "Bold"),
                Colour = AimModPalette.Cyan,
            },
            sortDropdown = new OsuDropdown<LocalLibrarySort>
            {
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                Position = new(0, 91),
                Width = 180,
                Items = mode == NativeLocalLibraryMode.Beatmaps
                    ? new[]
                    {
                        LocalLibrarySort.RecentlyAdded,
                        LocalLibrarySort.Title,
                        LocalLibrarySort.StarRating,
                    }
                    : new[]
                    {
                        LocalLibrarySort.RecentlyPlayed,
                        LocalLibrarySort.Accuracy,
                        LocalLibrarySort.Score,
                    },
                Current = sortMode,
            },
            loadMoreButton = new LoadMoreButton(loadNextPage)
            {
                Anchor = Anchor.BottomCentre,
                Origin = Anchor.BottomCentre,
            },
            loadingOverlay = new AimModLoadingOverlay(),
        };
    }

    protected override void Update()
    {
        base.Update();

        float width = Math.Max(640, DrawWidth);
        const float gap = 24;
        const float sortWidth = 180;
        float contentWidth = width - sortWidth - gap * 2;
        float searchWidth = Math.Clamp(contentWidth * 0.48f, 198, 620);
        float sliderWidth = Math.Clamp(contentWidth - searchWidth, 180, 420);

        searchBox.Width = searchWidth;
        starSlider.Anchor = Anchor.TopLeft;
        starSlider.Origin = Anchor.TopLeft;
        starSlider.Position = new(searchWidth + gap, 72);
        starSlider.Size = new(sliderWidth, 65);
        sortDropdown.Anchor = Anchor.TopLeft;
        sortDropdown.Origin = Anchor.TopLeft;
        sortDropdown.Position = new(width - sortWidth, 91);
        sortDropdown.Width = sortWidth;
        resultStatus.MaxWidth = width;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        searchBox.OnCommit += (_, _) => resetQuery();
        minimumStars.BindValueChanged(_ => scheduleQuery());
        maximumStars.BindValueChanged(_ => scheduleQuery());
        sortMode.BindValueChanged(_ => resetQuery());
        resetQuery();
    }

    private void scheduleQuery()
    {
        scheduledQuery?.Cancel();
        scheduledQuery = Scheduler.AddDelayed(resetQuery, 220);
    }

    private void resetQuery()
    {
        controller.Cancel();
        loadNextPage(reset: true);
    }

    private void loadNextPage() => loadNextPage(reset: false);

    private void loadNextPage(bool reset)
    {
        LocalLibraryLoadState state = controller.State;
        if (state.IsLoading && !reset)
            return;

        int offset = reset ? 0 : state.ItemCount;
        var query = new LocalLibraryQuery(
            SearchText: searchBox.Current.Value,
            RulesetShortName: "osu",
            MinimumStars: minimumStars.IsDefault ? null : minimumStars.Value,
            MaximumStars: maximumStars.IsDefault ? null : maximumStars.Value,
            Sort: sortMode.Value,
            Offset: offset,
            Limit: page_size);

        _ = controller.LoadAsync(query, append: offset > 0);
    }

    private void stateChanged(object? sender, LocalLibraryLoadStateChangedEventArgs e)
    {
        if (!IsDisposed)
            Schedule(() => applyState(e.State));
    }

    private void sourceChanged()
    {
        if (!IsDisposed)
            Schedule(resetQuery);
    }

    private void applyState(LocalLibraryLoadState state)
    {
        if (state.Revision <= displayedRevision)
            return;

        displayedRevision = state.Revision;
        IEnumerable<LocalLibraryRow> nextRows = mode == NativeLocalLibraryMode.Beatmaps
            ? state.BeatmapSets.Select(LocalLibraryRow.FromBeatmapSet)
            : state.Replays.Select(replay => LocalLibraryRow.FromReplay(replay, openReplay));

        rows.Clear();
        rows.AddRange(nextRows);

        switch (state.Status)
        {
            case LocalLibraryLoadStatus.Loading:
                resultStatus.Text = state.ItemCount == 0 ? "Searching library..." : $"Loading more after {state.ItemCount:N0} results...";
                loadMoreButton.SetState(false, "Loading...");
                if (state.ItemCount == 0)
                    loadingOverlay.ShowLoading(
                        mode == NativeLocalLibraryMode.Beatmaps ? "Loading beatmaps" : "Loading replays",
                        "Reading your local osu!lazer library");
                break;

            case LocalLibraryLoadStatus.Empty:
                resultStatus.Text = mode == NativeLocalLibraryMode.Beatmaps ? "No beatmaps found" : "No replays found";
                loadMoreButton.SetState(false, "No results", visible: false);
                loadingOverlay.HideLoading();
                break;

            case LocalLibraryLoadStatus.Ready:
                resultStatus.Text = $"Showing {state.ItemCount:N0} of {state.Total:N0}  //  {sortDescription(sortMode.Value)}";
                loadMoreButton.SetState(state.HasMore, state.HasMore ? "Load more" : "All loaded");
                loadingOverlay.HideLoading();
                break;

            case LocalLibraryLoadStatus.Error:
                resultStatus.Text = $"Could not load the library: {state.ErrorMessage}";
                loadMoreButton.SetState(true, "Try again");
                loadingOverlay.HideLoading();
                break;
        }
    }

    private static string sortDescription(LocalLibrarySort sort) => sort switch
    {
        LocalLibrarySort.Title => "title A-Z",
        LocalLibrarySort.StarRating => "highest star rating",
        LocalLibrarySort.Accuracy => "highest accuracy",
        LocalLibrarySort.Score => "highest score",
        LocalLibrarySort.RecentlyPlayed => "recently played",
        _ => "recently added",
    };

    protected override void Dispose(bool isDisposing)
    {
        scheduledQuery?.Cancel();
        controller.StateChanged -= stateChanged;
        if (sourceChanges is not null)
            sourceChanges.SourceChanged -= sourceChanged;
        controller.Dispose();
        base.Dispose(isDisposing);
    }

    private partial class LocalLibraryVirtualisedList : VirtualisedListContainer<LocalLibraryRow, DrawableLocalLibraryRow>
    {
        public LocalLibraryVirtualisedList()
            : base(DrawableLocalLibraryRow.RowHeight, initialPoolSize: 24)
        {
        }

        protected override ScrollContainer<Drawable> CreateScrollContainer() => new OsuScrollContainer();
    }

    private partial class DrawableLocalLibraryRow : PoolableDrawable, IHasCurrentValue<LocalLibraryRow>
    {
        public const float RowHeight = 120;

        private readonly BindableWithCurrent<LocalLibraryRow> current = new();
        private Container card = null!;
        private Container artwork = null!;
        private FillFlowContainer content = null!;
        private TruncatingSpriteText title = null!;
        private TruncatingSpriteText subtitle = null!;
        private TruncatingSpriteText detail = null!;
        private SpriteText metric = null!;
        private SpriteText actionHint = null!;
        private Container starRating = null!;
        private Box fallbackBackground = null!;
        private Box hoverLayer = null!;
        private Box selectionLayer = null!;
        private Box accent = null!;
        private Box metricPanel = null!;
        private CircularContainer playButton = null!;
        private FillFlowContainer<Drawable> difficultyChips = null!;
        private bool currentIsReplay;

        [Resolved]
        private OsuColour colours { get; set; } = null!;

        [Resolved]
        private Bindable<Guid?> selection { get; set; } = null!;

        public Bindable<LocalLibraryRow> Current
        {
            get => current.Current;
            set => current.Current = value;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            RelativeSizeAxes = Axes.Both;

            InternalChildren = new Drawable[]
            {
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Bottom = 8 },
                    Child = card = new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Masking = true,
                        CornerRadius = 56,
                        BorderThickness = 1,
                        BorderColour = AimModPalette.Border,
                        Children = new Drawable[]
                        {
                            fallbackBackground = new Box
                            {
                                RelativeSizeAxes = Axes.Both,
                                Colour = ColourInfo.GradientHorizontal(AimModPalette.PanelRaised, AimModPalette.Panel),
                            },
                            artwork = new Container { RelativeSizeAxes = Axes.Both },
                            new Box
                            {
                                RelativeSizeAxes = Axes.Both,
                                Colour = ColourInfo.GradientHorizontal(AimModPalette.Canvas, AimModPalette.Panel),
                                Alpha = 0.78f,
                            },
                            metricPanel = new Box
                            {
                                Anchor = Anchor.TopRight,
                                Origin = Anchor.TopRight,
                                RelativeSizeAxes = Axes.Y,
                                Width = 260,
                                X = 72,
                                Shear = new(-0.1f, 0),
                                Colour = AimModPalette.Canvas,
                                Alpha = 0.58f,
                            },
                            selectionLayer = new Box
                            {
                                RelativeSizeAxes = Axes.Both,
                                Colour = AimModPalette.Pink,
                                Alpha = 0,
                            },
                            hoverLayer = new Box
                            {
                                RelativeSizeAxes = Axes.Both,
                                Colour = Colour4.White,
                                Alpha = 0,
                            },
                            accent = new Box
                            {
                                RelativeSizeAxes = Axes.Y,
                                Width = 4,
                                Colour = AimModPalette.Pink,
                            },
                            playButton = new CircularContainer
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Margin = new MarginPadding { Left = 22 },
                                Size = new(42),
                                Masking = true,
                                BorderThickness = 2,
                                BorderColour = AimModPalette.Text,
                                Children = new Drawable[]
                                {
                                    new Box
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        Colour = AimModPalette.Canvas,
                                        Alpha = 0.64f,
                                    },
                                    new SpriteIcon
                                    {
                                        Anchor = Anchor.Centre,
                                        Origin = Anchor.Centre,
                                        Icon = FontAwesome.Solid.Play,
                                        Size = new(13),
                                        X = 1,
                                        Colour = AimModPalette.Text,
                                    },
                                },
                            },
                            content = new FillFlowContainer
                            {
                                AutoSizeAxes = Axes.Both,
                                Margin = new MarginPadding { Left = 24, Top = 12 },
                                Direction = FillDirection.Vertical,
                                Spacing = new(2),
                                Children = new Drawable[]
                                {
                                    title = rowText(18, AimModPalette.Text, "Bold"),
                                    subtitle = rowText(13, AimModPalette.Text, "SemiBold"),
                                    detail = rowText(11, AimModPalette.Muted),
                                },
                            },
                            difficultyChips = new FillFlowContainer<Drawable>
                            {
                                Anchor = Anchor.BottomLeft,
                                Origin = Anchor.BottomLeft,
                                AutoSizeAxes = Axes.Both,
                                Margin = new MarginPadding { Left = 24, Bottom = 12 },
                                Direction = FillDirection.Horizontal,
                                Spacing = new(6),
                            },
                            starRating = new Container
                            {
                                Anchor = Anchor.TopRight,
                                Origin = Anchor.TopRight,
                                AutoSizeAxes = Axes.Both,
                                Margin = new MarginPadding { Top = 14, Right = 22 },
                            },
                            metric = rowText(13, AimModPalette.Text, "SemiBold").With(drawable =>
                            {
                                drawable.Anchor = Anchor.BottomRight;
                                drawable.Origin = Anchor.BottomRight;
                                drawable.Margin = new MarginPadding { Bottom = 30, Right = 22 };
                            }),
                            actionHint = rowText(11, AimModPalette.Muted, "SemiBold").With(drawable =>
                            {
                                drawable.Anchor = Anchor.BottomRight;
                                drawable.Origin = Anchor.BottomRight;
                                drawable.Margin = new MarginPadding { Bottom = 12, Right = 22 };
                            }),
                        },
                    },
                },
            };

            current.BindValueChanged(value => updateRow(value.NewValue), true);
            selection.BindValueChanged(_ => updateSelection(), true);
        }

        private void updateRow(LocalLibraryRow row)
        {
            currentIsReplay = row.IsReplay;
            title.Text = row.Title;
            subtitle.Text = row.Subtitle;
            detail.Text = row.Detail;
            metric.Text = row.Metric;
            actionHint.Text = row.ActionHint;
            Colour4 difficultyColour = colours.ForStarDifficulty(row.StarRating);
            metric.Colour = difficultyColour;
            accent.Colour = difficultyColour;
            fallbackBackground.Colour = ColourInfo.GradientHorizontal(difficultyColour, AimModPalette.Panel);
            fallbackBackground.Alpha = 0.28f;
            artwork.Child = string.IsNullOrWhiteSpace(row.ArtworkPath) ? null : new AimModLocalArtwork(row.ArtworkPath);
            playButton.Alpha = row.IsReplay ? 1 : 0;
            content.Margin = new MarginPadding { Left = row.IsReplay ? 80 : 24, Top = 12 };
            difficultyChips.Margin = new MarginPadding { Left = row.IsReplay ? 80 : 24, Bottom = 12 };
            starRating.Child = new StarRatingDisplay(new StarDifficulty(row.StarRating, 0), StarRatingDisplaySize.Regular);
            difficultyChips.Clear();
            foreach (DifficultyChip chip in row.Difficulties.Take(6))
                difficultyChips.Add(new DifficultyPill(chip));
            if (row.Difficulties.Count > 6)
                difficultyChips.Add(new AimModPill($"+{row.Difficulties.Count - 6}", AimModPillTone.Neutral));

            // Beatmap sets are full-width artwork banners, not oversized filter chips.
            // Replay rows retain their existing circular silhouette until that route gets its own polish pass.
            card.CornerRadius = row.IsReplay ? 56 : 8;
            metricPanel.Width = row.IsReplay ? 260 : 250;
            metricPanel.X = row.IsReplay ? 72 : 44;
            metricPanel.Shear = new(row.IsReplay ? -0.1f : -0.055f, 0);
            updateSelection();
            updateTextBounds();
        }

        protected override void Update()
        {
            base.Update();
            updateTextBounds();
        }

        protected override bool OnClick(ClickEvent e)
        {
            selection.Value = current.Value.Id;
            Action? action = current.Value.Action;
            action?.Invoke();
            return true;
        }

        protected override bool OnHover(HoverEvent e)
        {
            hoverLayer.FadeTo(0.055f, 90);
            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            hoverLayer.FadeOut(110);
            base.OnHoverLost(e);
        }

        private void updateSelection()
        {
            bool selected = selection.Value == current.Value.Id;
            card.BorderColour = selected ? AimModPalette.Pink : AimModPalette.Border;
            card.BorderThickness = selected ? 2 : 1;
            selectionLayer.FadeTo(selected ? 0.085f : 0, 100);
        }

        private void updateTextBounds()
        {
            float reservedRight = currentIsReplay ? 340 : 315;
            float textLeft = currentIsReplay ? 80 : 24;
            float available = Math.Max(120, DrawWidth - textLeft - reservedRight);
            title.MaxWidth = available;
            subtitle.MaxWidth = available;
            detail.MaxWidth = available;
        }

        private static TruncatingSpriteText rowText(float size, Colour4 colour, string weight = "Regular") => new()
        {
            Font = new FontUsage(size: size, weight: weight),
            Colour = colour,
            MaxWidth = 120,
        };

        private partial class DifficultyPill : CircularContainer
        {
            public DifficultyPill(DifficultyChip chip)
            {
                AutoSizeAxes = Axes.Both;
                Masking = true;
                Colour4 colour = AimModVisualStyle.DifficultyColour(chip.Stars);
                Children = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = colour,
                        Alpha = 0.2f,
                    },
                    new FillFlowContainer
                    {
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Horizontal,
                        Spacing = new(6),
                        Padding = new MarginPadding { Horizontal = 10, Vertical = 4 },
                        Children = new Drawable[]
                        {
                            new TruncatingSpriteText
                            {
                                Text = chip.Name,
                                Font = new FontUsage(size: 10, weight: "SemiBold"),
                                Colour = AimModPalette.Text,
                                MaxWidth = 145,
                            },
                            new SpriteText
                            {
                                Text = $"{chip.Stars:0.00}*",
                                Font = new FontUsage(size: 10, weight: "Bold"),
                                Colour = colour,
                            },
                        },
                    },
                };
            }
        }
    }

    private partial class LoadMoreButton : ClickableContainer
    {
        private readonly Action action;
        private readonly SpriteText label;
        private bool enabled;

        public LoadMoreButton(Action action)
        {
            this.action = action;
            Size = new(180, 42);
            Masking = true;
            CornerRadius = 8;

            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = AimModPalette.PanelHover,
                },
                label = new SpriteText
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Font = new FontUsage(size: 14, weight: "SemiBold"),
                    Colour = AimModPalette.Text,
                },
            };
        }

        public void SetState(bool isEnabled, string text, bool visible = true)
        {
            enabled = isEnabled;
            label.Text = text;
            this.FadeTo(visible ? (isEnabled ? 1 : 0.55f) : 0, 100);
        }

        protected override bool OnClick(ClickEvent e)
        {
            if (enabled)
                action();

            return true;
        }
    }

    private sealed record LocalLibraryRow(
        Guid Id,
        string Title,
        string Subtitle,
        string Detail,
        string Metric,
        string ActionHint,
        double StarRating,
        IReadOnlyList<DifficultyChip> Difficulties,
        string ArtworkPath,
        bool IsReplay,
        Action? Action)
    {
        public static LocalLibraryRow FromBeatmapSet(LocalBeatmapSet set)
        {
            double minimumStars = set.Difficulties.Min(difficulty => difficulty.StarRating);
            double maximumStars = set.Difficulties.Max(difficulty => difficulty.StarRating);
            string starRange = Math.Abs(maximumStars - minimumStars) < 0.005
                ? $"{maximumStars:0.00} stars"
                : $"{minimumStars:0.00} to {maximumStars:0.00} stars";

            return new LocalLibraryRow(
                set.SetId,
                set.Title,
                set.Artist,
                set.LocalReplayCount switch
                {
                    1 => $"mapped by {set.Creator}  ·  {set.Difficulties.Count} difficulties  ·  1 replay",
                    > 1 => $"mapped by {set.Creator}  ·  {set.Difficulties.Count} difficulties  ·  {set.LocalReplayCount:N0} replays",
                    _ => $"mapped by {set.Creator}  ·  {set.Difficulties.Count} difficulties",
                },
                starRange,
                set.LastPlayed is { } lastPlayed ? $"played {lastPlayed:yyyy-MM-dd}" : "installed locally",
                maximumStars,
                set.Difficulties
                   .OrderBy(difficulty => difficulty.StarRating)
                   .Select(difficulty => new DifficultyChip(difficulty.Name, difficulty.StarRating))
                   .ToArray(),
                set.BackgroundPath,
                false,
                null);
        }

        public static LocalLibraryRow FromReplay(LocalReplay replay, Action<LocalReplay>? openReplay)
        {
            string mods = replay.Mods.Count == 0 ? "No Mod" : string.Join(',', replay.Mods);
            return new LocalLibraryRow(
                replay.ScoreId,
                replay.Title,
                $"{replay.Artist}  ·  {replay.Difficulty}",
                $"{replay.Player}  ·  {replay.PlayedAt:yyyy-MM-dd HH:mm}  ·  {replay.TotalScore:N0}  ·  {mods}",
                $"{replay.Accuracy:P2}  ·  {replay.MissCount} misses",
                replay.HasReplayFile ? "open replay" : "replay file unavailable",
                replay.StarRating,
                new[] { new DifficultyChip(replay.Difficulty, replay.StarRating) },
                replay.BackgroundPath,
                true,
                replay.HasReplayFile && openReplay is not null ? () => openReplay(replay) : null);
        }
    }

    private sealed record DifficultyChip(string Name, double Stars);
}
