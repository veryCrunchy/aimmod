using AimMod.Desktop.PpTargets;
using AimMod.Desktop.ScoreHistory;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Framework.Threading;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.LocalLibrary;

/// <summary>
/// Compact, set-first view of the installed osu! library. All displayed values come from
/// the local index, matching local scores, or the exact PP calculator.
/// </summary>
public partial class NativeInstalledBeatmapBrowser : CompositeDrawable
{
    private const int page_size = 80;
    private const double detail_debounce_ms = 150;
    private const int history_pages = 5;
    internal const float SingleRowToolbarWidth = 980;
    internal const float SideInspectorWidth = 900;
    internal static readonly int[] AccuracyPoints = [95, 97, 98, 99, 100];

    private readonly ILocalLibrarySource source;
    private readonly Func<IPpTargetExactCalculationService?> exactCalculator;
    private readonly Func<IAccountScoreHistoryService?> onlineScoreHistory;
    private readonly LocalLibraryController controller;
    private readonly Container toolbar;
    private readonly AimModSearchBox searchBox;
    private readonly AimModButton filtersButton;
    private readonly SpriteText filtersCaption;
    private readonly SpriteIcon filtersIcon;
    private readonly Container filtersBadge;
    private readonly SpriteText filtersBadgeText;
    private readonly PrettyDropdown<LocalLibrarySort> sortDropdown;
    private readonly Container filterPopover;
    private readonly ClickableContainer popoverDismiss;
    private readonly FillFlowContainer<Drawable> activeChips;
    private readonly AimModResetButton resetButton;
    private readonly Bindable<LocalLibrarySort> sort = new(LocalLibrarySort.RecentlyAdded);
    private readonly Bindable<BpmFilter> bpmFilter = new(BpmFilter.Any);
    private readonly Bindable<LengthFilter> lengthFilter = new(LengthFilter.Any);
    private readonly Bindable<PlayedFilter> playedFilter = new(PlayedFilter.Everything);
    private readonly BindableDouble minimumStars = new(0) { MinValue = 0, MaxValue = 10, Default = 0 };
    private readonly BindableDouble maximumStars = new(10) { MinValue = 0, MaxValue = 10, Default = 10 };
    private readonly AimModStarRatingFilter stars;
    private readonly TruncatingSpriteText status;
    private readonly FillFlowContainer<Drawable> setRows;
    private readonly OsuScrollContainer listScroll;
    private readonly BeatmapInspector inspector;
    private readonly AimModInlineStatus loadStatus;
    private readonly Container rightRail;
    private readonly Container listPanel;
    private readonly AimModButton backToResults;
    private readonly ILocalLibrarySourceChanged? sourceChanges;

    private ScheduledDelegate? scheduledQuery;
    private ScheduledDelegate? scheduledDetails;
    private CancellationTokenSource? detailCancellation;
    private CancellationTokenSource? historyCancellation;
    private (Guid Set, Guid Difficulty)? detailsFor;
    private readonly List<Guid> displayedSetIds = [];
    private AimModResetButton? loadMoreButton;
    private AimModLayout.ChangeTracker<(float, float)> layoutTracker;
    private AimModLayout.ChangeTracker<(string, int, int)> progressTracker;
    private AimModLayout.ChangeTracker<float> statusWidthTracker;
    private LocalBeatmapSet? selectedSet;
    private LocalBeatmapDifficulty? selectedDifficulty;
    private long displayedRevision;
    private bool libraryLoaded;
    private bool sideInspector = true;
    private bool detailsOpen;
    private LocalLibraryQuery? activeQuery;
    private Func<LocalBeatmapSet, bool>? activeFilter;
    private InstalledBeatmapHistory history = InstalledBeatmapHistory.Empty;

    public NativeInstalledBeatmapBrowser(
        ILocalLibrarySource source,
        Func<IPpTargetExactCalculationService?>? exactCalculator = null,
        Func<IAccountScoreHistoryService?>? onlineScoreHistory = null,
        Func<int, CancellationToken, Task>? openBeatmap = null,
        Action<string>? openPractice = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.exactCalculator = exactCalculator ?? (() => null);
        this.onlineScoreHistory = onlineScoreHistory ?? (() => null);
        controller = new LocalLibraryController(source, NativeLocalLibraryMode.Beatmaps);
        controller.StateChanged += stateChanged;
        sourceChanges = source as ILocalLibrarySourceChanged;
        if (sourceChanges is not null)
            sourceChanges.SourceChanged += sourceChanged;

        RelativeSizeAxes = Axes.Both;
        // The filter button carries a count badge, so it uses custom content inside the shared button.
        filtersButton = new AimModButton("Filters", () => setPopover(filterPopover!.Alpha == 0));
        filtersButton.SetVisualContent(new FillFlowContainer
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Horizontal,
            Spacing = new(7, 0),
            Children = new Drawable[]
            {
                filtersIcon = new SpriteIcon { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Icon = FontAwesome.Solid.Filter, Size = new(11), Colour = AimModPalette.Muted },
                filtersCaption = new SpriteText { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Text = "Filters", Font = new FontUsage(size: 14, weight: "SemiBold"), Colour = AimModPalette.Text },
                filtersBadge = new CircularContainer
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Size = new(18),
                    Masking = true,
                    Alpha = 0,
                    Children = new Drawable[]
                    {
                        new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Accent },
                        filtersBadgeText = new SpriteText { Anchor = Anchor.Centre, Origin = Anchor.Centre, Font = new FontUsage(size: 11, weight: "Bold"), Colour = AimModPalette.Canvas },
                    },
                },
            },
        }, AimModVisualStyle.ControlHeight);
        filtersButton.Width = 112;
        InternalChildren = new Drawable[]
        {
            listPanel = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Masking = true,
                Child = listScroll = new AimModScrollContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Child = new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Spacing = new(AimModVisualStyle.RelatedSpacing),
                        Padding = new MarginPadding { Right = AimModVisualStyle.RelatedSpacing, Bottom = AimModVisualStyle.SectionSpacing },
                        Children = new Drawable[]
                        {
                            loadStatus = new AimModInlineStatus(),
                            setRows = new FillFlowContainer<Drawable>
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Spacing = new(0, 6),
                            },
                        },
                    },
                },
            },
            rightRail = new Container
            {
                Masking = true,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas, Depth = 100 },
                    backToResults = new AimModButton("Back to results", closeDetails) { Height = AimModVisualStyle.CompactControlHeight, Alpha = 0 },
                    inspector = new BeatmapInspector(openBeatmap, openPractice, set => selectSet(set, true), retryDetails),
                },
            },
            // The toolbar is drawn ahead of the list and inspector so its popups are never covered.
            toolbar = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Depth = -20,
                Children = new Drawable[]
                {
                    popoverDismiss = new ClickableContainer { RelativeSizeAxes = Axes.Both, Alpha = 0, Action = () => setPopover(false) },
                    searchBox = new AimModSearchBox
                    {
                        Height = AimModVisualStyle.ControlHeight,
                        PlaceholderText = "Search title, artist, mapper or difficulty",
                    },
                    stars = new AimModStarRatingFilter
                    {
                        Size = new(264, AimModVisualStyle.ControlHeight),
                        LowerBound = minimumStars,
                        UpperBound = maximumStars,
                        DefaultStringLowerBound = "0",
                        DefaultStringUpperBound = "10+",
                    },
                    filtersButton,
                    sortDropdown = new PrettyDropdown<LocalLibrarySort>("Sort", formatSort)
                    {
                        Width = 206,
                        Items = new[] { LocalLibrarySort.RecentlyAdded, LocalLibrarySort.RecentlyPlayed, LocalLibrarySort.Title, LocalLibrarySort.StarRating },
                        Current = sort,
                    },
                    status = new TruncatingSpriteText
                    {
                        Font = AimModVisualStyle.BodyStrongFont,
                        Colour = AimModPalette.Text,
                        Text = "Reading installed beatmaps...",
                    },
                    new Container
                    {
                        Name = "active filters",
                        AutoSizeAxes = Axes.Both,
                        Child = activeChips = new FillFlowContainer<Drawable>
                        {
                            AutoSizeAxes = Axes.Both,
                            Direction = FillDirection.Horizontal,
                            Spacing = new(6),
                        },
                    },
                    resetButton = new AimModResetButton(clearFilters, "Reset all") { Height = 26, Width = 84, Alpha = 0 },
                    filterPopover = new Container
                    {
                        Width = 520,
                        AutoSizeAxes = Axes.Y,
                        Alpha = 0,
                        Masking = true,
                        CornerRadius = AimModVisualStyle.CardRadius,
                        BorderThickness = 1,
                        BorderColour = AimModPalette.Border,
                        EdgeEffect = new osu.Framework.Graphics.Effects.EdgeEffectParameters
                        {
                            Type = osu.Framework.Graphics.Effects.EdgeEffectType.Shadow,
                            Colour = Colour4.Black.Opacity(0.45f),
                            Radius = 18,
                        },
                        Children = new Drawable[]
                        {
                            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                            new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Spacing = new(AimModVisualStyle.RelatedSpacing),
                                Padding = new MarginPadding(16),
                                Children = new Drawable[]
                                {
                                    new MapBrowserChoiceGroup<BpmFilter>("BPM", bpmFilter, formatBpmChoice),
                                    new MapBrowserChoiceGroup<LengthFilter>("Length", lengthFilter, formatLengthChoice),
                                    new MapBrowserChoiceGroup<PlayedFilter>("Played", playedFilter, formatPlayedChoice),
                                },
                            },
                        },
                    },
                },
            },
        };
    }

    private void setPopover(bool open)
    {
        filterPopover.Alpha = open ? 1 : 0;
        popoverDismiss.Alpha = open ? 1 : 0;
        updateFilterSummary();
    }

    private void clearFilters()
    {
        searchBox.Current.Value = string.Empty;
        minimumStars.Value = 0; maximumStars.Value = 10;
        bpmFilter.Value = BpmFilter.Any; lengthFilter.Value = LengthFilter.Any;
        playedFilter.Value = PlayedFilter.Everything;
        scheduleQuery();
    }

    internal void ApplyFiltersForTesting(string search, double minimum, double maximum, BpmFilter bpm = BpmFilter.Any)
    {
        searchBox.Current.Value = search;
        minimumStars.Value = minimum;
        maximumStars.Value = maximum;
        bpmFilter.Value = bpm;
        resetQuery();
    }

    internal void SetFilterPopoverForTesting(bool open) => setPopover(open);

    protected override void LoadComplete()
    {
        base.LoadComplete();
        searchBox.QueryChanged += _ => resetQuery();
        searchBox.MoveToResults += () => moveSelection(1);
        minimumStars.BindValueChanged(_ => scheduleQuery());
        maximumStars.BindValueChanged(_ => scheduleQuery());
        sort.BindValueChanged(_ => resetQuery());
        bpmFilter.BindValueChanged(_ => resetQuery());
        lengthFilter.BindValueChanged(_ => resetQuery());
        playedFilter.BindValueChanged(_ => resetQuery());
        resetQuery();
        loadHistory();
    }

    /// <summary>Two toolbar rows below this width; one row above it.</summary>
    internal static bool UsesCompactToolbar(float width) => width < SingleRowToolbarWidth;

    /// <summary>Side-by-side inspector at or above this width; details open over the list below it.</summary>
    internal static bool UsesSideInspector(float width) => width >= SideInspectorWidth;

    protected override void Update()
    {
        base.Update();

        if (loadStatus.IsShowing && !loadStatus.IsError && controller.Progress is { } progress
            && progressTracker.Update((progress.State, progress.Completed, progress.Total)))
        {
            loadStatus.SetLoadingText(progress.Total > 0
                ? $"{progress.State}  {progress.Completed:N0} / {progress.Total:N0}"
                : progress.State);
        }

        if (layoutTracker.Update((DrawWidth, DrawHeight)))
            applyLayout();
        if (statusWidthTracker.Update(status.DrawWidth))
            positionChips();
    }

    private void applyLayout()
    {
        const float gap = AimModVisualStyle.RelatedSpacing;
        float width = Math.Max(0, DrawWidth);
        bool compact = UsesCompactToolbar(width);
        float summaryY;

        if (compact)
        {
            searchBox.Position = new(0, 0);
            searchBox.Width = Math.Max(0, width - filtersButton.Width - gap);
            filtersButton.Position = new(width - filtersButton.Width, 0);
            float sortWidth = Math.Min(206, Math.Max(150, width * 0.34f));
            stars.Position = new(0, AimModVisualStyle.ControlHeight + gap);
            stars.Width = Math.Max(0, width - sortWidth - gap);
            sortDropdown.Position = new(width - sortWidth, AimModVisualStyle.ControlHeight + gap);
            sortDropdown.Width = sortWidth;
            summaryY = (AimModVisualStyle.ControlHeight + gap) * 2;
        }
        else
        {
            const float starsWidth = 264;
            float sortWidth = sortDropdown.Width = 206;
            float right = width;
            sortDropdown.Position = new(right - sortWidth, 0);
            right -= sortWidth + gap;
            filtersButton.Position = new(right - filtersButton.Width, 0);
            right -= filtersButton.Width + gap;
            stars.Position = new(right - starsWidth, 0);
            stars.Width = starsWidth;
            right -= starsWidth + gap;
            searchBox.Position = new(0, 0);
            searchBox.Width = Math.Max(0, right);
            summaryY = AimModVisualStyle.ControlHeight + gap;
        }

        status.Position = new(0, summaryY + 4);
        status.MaxWidth = Math.Max(120, width * 0.4f);
        activeChips.Parent!.Position = new(0, summaryY);
        resetButton.Position = new(width - resetButton.Width, summaryY);
        positionChips();
        filterPopover.Width = Math.Min(520, width);
        filterPopover.Position = new(Math.Max(0, filtersButton.X + filtersButton.Width - filterPopover.Width), filtersButton.Y + filtersButton.Height + 6);

        float top = summaryY + 26 + 14;
        sideInspector = UsesSideInspector(width);
        if (sideInspector)
        {
            float railWidth = Math.Clamp(width * 0.32f, 330, 470);
            rightRail.Position = new(width - railWidth, top);
            rightRail.Size = new(railWidth, Math.Max(0, DrawHeight - top));
            rightRail.Padding = new MarginPadding { Left = AimModVisualStyle.SectionSpacing };
            rightRail.Alpha = 1;
            backToResults.Alpha = 0;
            inspector.Padding = new MarginPadding();
            listPanel.Padding = new MarginPadding { Top = top, Right = railWidth };
            listPanel.Alpha = 1;
        }
        else
        {
            rightRail.Position = new(0, top);
            rightRail.Size = new(width, Math.Max(0, DrawHeight - top));
            rightRail.Padding = new MarginPadding();
            backToResults.Alpha = 1;
            inspector.Padding = new MarginPadding { Top = AimModVisualStyle.CompactControlHeight + AimModVisualStyle.RelatedSpacing };
            listPanel.Padding = new MarginPadding { Top = top };
            applyDetailsVisibility();
        }
    }

    private void applyDetailsVisibility()
    {
        if (sideInspector)
            return;
        rightRail.Alpha = detailsOpen ? 1 : 0;
        listPanel.Alpha = detailsOpen ? 0 : 1;
    }

    private void closeDetails()
    {
        detailsOpen = false;
        applyDetailsVisibility();
        if (selectedSet is not null && setRows.OfType<BeatmapSetRow>().FirstOrDefault(row => row.SetId == selectedSet.SetId) is { } row)
            listScroll.ScrollIntoView(row);
    }

    internal bool DetailsOpenForTesting => detailsOpen;

    public override bool HandleNonPositionalInput => true;

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (e.ControlPressed || e.AltPressed || !isVisible()
            || GetContainingInputManager()?.FocusedDrawable is osu.Framework.Graphics.UserInterface.TextBox)
            return base.OnKeyDown(e);

        switch (e.Key)
        {
            case osuTK.Input.Key.Down:
                return moveSelection(1);

            case osuTK.Input.Key.Up:
                return moveSelection(-1);

            case osuTK.Input.Key.Escape when filterPopover.Alpha > 0:
                setPopover(false);
                return true;

            case osuTK.Input.Key.Escape when detailsOpen && !sideInspector:
                closeDetails();
                return true;
        }

        return base.OnKeyDown(e);
    }

    private bool isVisible()
    {
        for (Drawable? drawable = this; drawable is not null; drawable = drawable.Parent)
        {
            if (drawable.Alpha <= 0 || !drawable.IsPresent)
                return false;
        }
        return true;
    }

    private bool moveSelection(int direction)
    {
        BeatmapSetRow[] rows = setRows.OfType<BeatmapSetRow>().ToArray();
        if (rows.Length == 0)
            return false;
        int current = Array.FindIndex(rows, row => row.SetId == selectedSet?.SetId);
        int next = Math.Clamp(current < 0 ? 0 : current + direction, 0, rows.Length - 1);
        GetContainingFocusManager()?.ChangeFocus(null);
        selectSet(rows[next].Set);
        listScroll.ScrollIntoView(rows[next]);
        return true;
    }

    private void scheduleQuery()
    {
        scheduledQuery?.Cancel();
        scheduledQuery = Scheduler.AddDelayed(resetQuery, 220);
    }

    private void resetQuery()
    {
        controller.Cancel();
        var query = new LocalLibraryQuery(
            searchBox.Current.Value,
            "osu",
            minimumStars.IsDefault ? null : minimumStars.Value,
            maximumStars.IsDefault ? null : maximumStars.Value,
            sort.Value,
            0,
            page_size);
        activeQuery = query;
        BpmFilter bpm = bpmFilter.Value;
        LengthFilter length = lengthFilter.Value;
        PlayedFilter played = playedFilter.Value;
        activeFilter = bpm == BpmFilter.Any && length == LengthFilter.Any && played == PlayedFilter.Everything
            ? null : set => matchesFilters(set, bpm, length, played);
        updateFilterSummary();
        _ = controller.LoadAsync(query, beatmapFilter: activeFilter);
    }

    private void updateFilterSummary()
    {
        var chips = new List<(string Text, Action Remove)>();
        if (!string.IsNullOrWhiteSpace(searchBox.Current.Value))
            chips.Add(($"“{searchBox.Current.Value.Trim()}”", () => searchBox.Current.Value = string.Empty));
        if (!minimumStars.IsDefault || !maximumStars.IsDefault)
            chips.Add(($"{minimumStars.Value:0.#} – {(maximumStars.IsDefault ? "10+" : maximumStars.Value.ToString("0.#"))} stars", () => { minimumStars.Value = 0; maximumStars.Value = 10; }));
        if (bpmFilter.Value != BpmFilter.Any)
            chips.Add((formatBpm(bpmFilter.Value), () => bpmFilter.Value = BpmFilter.Any));
        if (lengthFilter.Value != LengthFilter.Any)
            chips.Add((formatLength(lengthFilter.Value), () => lengthFilter.Value = LengthFilter.Any));
        if (playedFilter.Value != PlayedFilter.Everything)
            chips.Add((formatPlayed(playedFilter.Value), () => playedFilter.Value = PlayedFilter.Everything));

        string[] current = activeChips.OfType<MapBrowserFilterChip>().Select(chip => chip.Text).ToArray();
        if (!current.SequenceEqual(chips.Select(chip => chip.Text)))
        {
            activeChips.Clear();
            foreach ((string text, Action remove) in chips)
                activeChips.Add(new MapBrowserFilterChip(text, remove));
        }

        int popoverFilters = (bpmFilter.Value != BpmFilter.Any ? 1 : 0) + (lengthFilter.Value != LengthFilter.Any ? 1 : 0)
                             + (playedFilter.Value != PlayedFilter.Everything ? 1 : 0);
        filtersBadge.Alpha = popoverFilters > 0 ? 1 : 0;
        filtersBadgeText.Text = popoverFilters.ToString();
        bool highlighted = popoverFilters > 0 || filterPopover.Alpha > 0;
        filtersButton.SetSelected(highlighted);
        filtersCaption.Colour = highlighted ? AimModPalette.Accent : AimModPalette.Text;
        filtersIcon.Colour = highlighted ? AimModPalette.Accent : AimModPalette.Muted;
        resetButton.Alpha = chips.Count > 0 ? 1 : 0;
        positionChips();
    }

    private void positionChips()
    {
        if (activeChips.Parent is not Container chipHost)
            return;
        float statusWidth = Math.Min(status.MaxWidth, status.DrawWidth);
        chipHost.X = activeChips.Count == 0 ? 0 : statusWidth + 16;
    }

    private void loadMore()
    {
        var state = controller.State;
        if (activeQuery is null || state.IsLoading || !state.HasMore) return;
        _ = controller.LoadAsync(activeQuery with { Offset = state.BeatmapSets.Count }, append: true, beatmapFilter: activeFilter);
    }

    private void stateChanged(object? sender, LocalLibraryLoadStateChangedEventArgs e)
    {
        if (!IsDisposed)
            Schedule(() => applyState(e.State));
    }

    private void sourceChanged()
    {
        if (IsDisposed)
            return;
        Schedule(() =>
        {
            resetQuery();
            loadHistory();
        });
    }

    private void loadHistory()
    {
        historyCancellation?.Cancel();
        historyCancellation?.Dispose();
        historyCancellation = new CancellationTokenSource();
        CancellationToken token = historyCancellation.Token;
        _ = Task.Run(async () =>
        {
            // A bounded window of recent plays is enough for row status and "your usual".
            var replays = new List<LocalReplay>();
            for (int page = 0; page < history_pages; page++)
            {
                LocalLibraryPage<LocalReplay> result = await source.SearchReplaysAsync(
                    new LocalLibraryQuery(RulesetShortName: "osu", Sort: LocalLibrarySort.RecentlyPlayed, Offset: replays.Count, Limit: 200), token).ConfigureAwait(false);
                replays.AddRange(result.Items);
                if (!result.HasMore || result.Items.Count == 0)
                    break;
            }
            return new InstalledBeatmapHistory(replays);
        }, token).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully && !token.IsCancellationRequested && !IsDisposed)
                Schedule(() => { if (!token.IsCancellationRequested) applyHistory(task.Result); });
        }, TaskScheduler.Default);
    }

    private void applyHistory(InstalledBeatmapHistory value)
    {
        history = value;
        foreach (BeatmapSetRow row in setRows.OfType<BeatmapSetRow>())
            row.SetHistory(history.BySet.GetValueOrDefault(row.SetId));
        inspector.SetHistory(history, controller.State.BeatmapSets);
    }

    private void applyState(LocalLibraryLoadState state)
    {
        if (state.Revision <= displayedRevision)
            return;
        displayedRevision = state.Revision;

        if (state.Status == LocalLibraryLoadStatus.Loading)
        {
            bool appending = state.BeatmapSets.Count > 0;
            status.Text = appending ? $"Loading more after {state.BeatmapSets.Count:N0} sets..." : libraryLoaded ? "Updating results..." : "Reading installed beatmaps...";
            loadMoreButton?.SetLoading(true);
            if (!appending)
            {
                progressTracker.Reset();
                if (!libraryLoaded)
                    loadStatus.ShowLoading("Reading your installed beatmaps...", cancelQuery);
            }
            return;
        }

        if (state.Status == LocalLibraryLoadStatus.Error)
        {
            libraryLoaded = state.BeatmapSets.Count > 0;
            status.Text = "Library unavailable";
            loadStatus.ShowError("AimMod could not read your installed beatmaps. Check that the osu! drive is connected, then retry.",
                state.ErrorMessage, state.BeatmapSets.Count > 0 ? loadMore : resetQuery);
            if (state.BeatmapSets.Count == 0)
            {
                clearRows();
                setRows.Add(new EmptyState(FontAwesome.Solid.ExclamationTriangle, "Could not read the local library", "Check the osu! data location, then retry."));
            }
            loadMoreButton?.SetLoading(false);
            return;
        }

        loadStatus.Dismiss();
        libraryLoaded = true;

        LocalBeatmapSet[] visibleSets = state.BeatmapSets.ToArray();
        if (state.Status == LocalLibraryLoadStatus.Empty || visibleSets.Length == 0)
        {
            bool filtered = resetButton.Alpha > 0;
            status.Text = filtered ? "No matching sets" : state.ErrorMessage ?? "No installed beatmaps";
            clearRows();
            setRows.Add(filtered
                ? new EmptyState(FontAwesome.Solid.Filter, "No beatmaps match these filters", "Remove a filter above, or reset them all.", "Reset filters", clearFilters)
                : new EmptyState(FontAwesome.Solid.Music, "No installed beatmaps yet", "Find maps on the Online tab, then they appear here."));
            // With nothing to select, a "select a beatmap" hint beside the empty list would mislead.
            inspector.ClearSelection(showHint: false);
            selectedSet = null;
            selectedDifficulty = null;
            detailsFor = null;
            scheduledDetails?.Cancel();
            detailCancellation?.Cancel();
            positionChips();
            return;
        }

        status.Text = state.ErrorMessage ?? (visibleSets.Length < state.Total
            ? $"{visibleSets.Length:N0} of {state.Total:N0} sets"
            : state.Total == 1 ? "1 set" : $"{state.Total:N0} sets");

        Guid[] incoming = visibleSets.Select(set => set.SetId).ToArray();
        if (ListDiff.IsAppend(displayedSetIds, incoming))
        {
            // Load more only adds rows, so the reader keeps their place in the list.
            for (int index = displayedSetIds.Count; index < visibleSets.Length; index++)
                setRows.Add(createRow(visibleSets[index]));
        }
        else
        {
            clearRows();
            foreach (LocalBeatmapSet set in visibleSets)
                setRows.Add(createRow(set));
            listScroll.ScrollToStart(false);
        }
        displayedSetIds.Clear();
        displayedSetIds.AddRange(incoming);

        if (loadMoreButton is not null)
        {
            setRows.Remove(loadMoreButton, true);
            loadMoreButton = null;
        }
        if (state.HasMore)
            setRows.Add(loadMoreButton = new AimModResetButton(loadMore, $"Show {Math.Min(page_size, state.Total - visibleSets.Length):N0} more") { Width = 160, Height = 36, Margin = new MarginPadding { Top = 6 } });

        inspector.SetHistory(history, visibleSets);
        LocalBeatmapSet? current = selectedSet is null ? null : visibleSets.FirstOrDefault(set => set.SetId == selectedSet.SetId);
        if (current is null)
            selectSet(visibleSets[0]);
        else
        {
            foreach (BeatmapSetRow row in setRows.OfType<BeatmapSetRow>())
                row.SetSelection(current.SetId);
        }
    }

    private BeatmapSetRow createRow(LocalBeatmapSet set)
    {
        var row = new BeatmapSetRow(set, value => selectSet(value, true));
        row.SetHistory(history.BySet.GetValueOrDefault(set.SetId));
        return row;
    }

    private void clearRows()
    {
        setRows.Clear();
        displayedSetIds.Clear();
        loadMoreButton = null;
    }

    private void cancelQuery()
    {
        scheduledQuery?.Cancel();
        controller.Cancel();
        status.Text = "Loading cancelled";
        loadStatus.ShowMessage("Loading was cancelled.", resetQuery);
        loadMoreButton?.SetLoading(false);
    }

    private static bool matchesFilters(LocalBeatmapSet set, BpmFilter bpm, LengthFilter length, PlayedFilter played)
    {
        bool playedMatches = played switch {
            PlayedFilter.Played => set.LastPlayed is not null || set.LocalReplayCount is > 0,
            PlayedFilter.Unplayed => set.LastPlayed is null && set.LocalReplayCount is not > 0,
            _ => true,
        };
        return playedMatches && set.Difficulties.Any(d =>
            (bpm switch {
                BpmFilter.Below160 => d.Bpm < 160,
                BpmFilter.From160To200 => d.Bpm is >= 160 and <= 200,
                BpmFilter.Above200 => d.Bpm > 200,
                _ => true,
            }) && (length switch {
                LengthFilter.Short => d.LengthMilliseconds < 120_000,
                LengthFilter.Medium => d.LengthMilliseconds is >= 120_000 and <= 240_000,
                LengthFilter.Long => d.LengthMilliseconds > 240_000,
                _ => true,
            }));
    }

    private void selectSet(LocalBeatmapSet set) => selectSet(set, false);

    private void selectSet(LocalBeatmapSet set, bool reveal)
    {
        LocalBeatmapDifficulty difficulty = selectedSet?.SetId == set.SetId && selectedDifficulty is not null
            ? set.Difficulties.FirstOrDefault(item => item.BeatmapId == selectedDifficulty.BeatmapId) ?? defaultDifficulty(set)
            : defaultDifficulty(set);
        selectDifficulty(set, difficulty);
        if (reveal && !sideInspector)
        {
            detailsOpen = true;
            applyDetailsVisibility();
        }
    }

    /// <summary>Start from the hardest difficulty the player has played, else the one nearest their usual stars.</summary>
    private LocalBeatmapDifficulty defaultDifficulty(LocalBeatmapSet set)
    {
        LocalBeatmapDifficulty? played = set.Difficulties.Where(item => history.ByDifficulty.ContainsKey(item.BeatmapId)).MaxBy(item => item.StarRating);
        if (played is not null)
            return played;
        if (history.UsualStars is { } usual)
            return set.Difficulties.MinBy(item => Math.Abs(item.StarRating - usual))!;
        return set.Difficulties.OrderBy(item => item.StarRating).ElementAt((set.Difficulties.Count - 1) / 2);
    }

    private void selectDifficulty(LocalBeatmapSet set, LocalBeatmapDifficulty difficulty)
    {
        selectedSet = set;
        selectedDifficulty = difficulty;
        foreach (BeatmapSetRow row in setRows.Children.OfType<BeatmapSetRow>())
            row.SetSelection(set.SetId);
        if (detailsFor == (set.SetId, difficulty.BeatmapId))
            return;
        detailsFor = (set.SetId, difficulty.BeatmapId);
        inspector.ShowSelection(set, difficulty, controller.State.BeatmapSets, selectDifficulty, canCalculatePp(difficulty));
        // Moving through rows quickly should not start an exact PP calculation per row.
        scheduledDetails?.Cancel();
        detailCancellation?.Cancel();
        scheduledDetails = Scheduler.AddDelayed(() => loadDetails(set, difficulty), detail_debounce_ms);
    }

    private bool canCalculatePp(LocalBeatmapDifficulty difficulty) =>
        exactCalculator() is not null && difficulty.OnlineId > 0;

    private void retryDetails()
    {
        if (selectedSet is null || selectedDifficulty is null)
            return;
        detailsFor = null;
        selectDifficulty(selectedSet, selectedDifficulty);
    }

    private void loadDetails(LocalBeatmapSet set, LocalBeatmapDifficulty difficulty)
    {
        detailCancellation?.Cancel();
        detailCancellation?.Dispose();
        detailCancellation = new CancellationTokenSource();
        CancellationToken token = detailCancellation.Token;

        _ = Task.Run(() => loadDetailsAsync(set, difficulty, token), token).ContinueWith(task =>
        {
            if (task.IsFaulted && !token.IsCancellationRequested && !IsDisposed)
            {
                Exception error = task.Exception!.GetBaseException();
                Schedule(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        inspector.ShowScores([], unavailableOnlineHistory(difficulty.OnlineId, OsuBestScoresFetchStatus.InvalidResponse), error);
                        inspector.ShowPp(new Dictionary<int, double>(), error);
                    }
                });
            }
        }, TaskScheduler.Default);
    }

    private async Task loadDetailsAsync(LocalBeatmapSet set, LocalBeatmapDifficulty difficulty, CancellationToken token)
    {
        Task<OnlineBeatmapScoreHistoryResult> onlineTask = loadOnlineHistory(difficulty.OnlineId, token);
        Task<IReadOnlyDictionary<int, double>> ppTask = calculatePp(difficulty, token);
        IReadOnlyList<LocalReplay> matching = Array.Empty<LocalReplay>();
        Exception? replayError = null;
        try
        {
            LocalLibraryPage<LocalReplay> page = await source.SearchReplaysAsync(new LocalLibraryQuery(
                SearchText: set.Title,
                RulesetShortName: "osu",
                Sort: LocalLibrarySort.RecentlyPlayed,
                Limit: 200), token).AsTask().WaitAsync(TimeSpan.FromSeconds(15), token);
            matching = page.Items.Where(replay => replay.BeatmapId == difficulty.BeatmapId).OrderBy(replay => replay.PlayedAt).ToArray();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            replayError = error;
        }

        // Scores usually arrive long before exact PP, so they are shown as soon as they are ready.
        OnlineBeatmapScoreHistoryResult online = await onlineTask.ConfigureAwait(false);
        IReadOnlyList<ScoreHistoryEntry> plays = ScoreHistoryMerger.Merge(matching, online.Scores);
        if (!token.IsCancellationRequested && !IsDisposed)
            Schedule(() => { if (!token.IsCancellationRequested) inspector.ShowScores(plays, online, replayError); });

        IReadOnlyDictionary<int, double> ppAtAccuracy = new Dictionary<int, double>();
        Exception? ppError = null;
        try
        {
            ppAtAccuracy = await ppTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            ppError = new TimeoutException("Exact PP calculation exceeded 45 seconds.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ppError = error;
        }
        if (!token.IsCancellationRequested && !IsDisposed)
            Schedule(() => { if (!token.IsCancellationRequested) inspector.ShowPp(ppAtAccuracy, ppError); });
    }

    private async Task<IReadOnlyDictionary<int, double>> calculatePp(LocalBeatmapDifficulty difficulty, CancellationToken token)
    {
        IPpTargetExactCalculationService? calculator = exactCalculator();
        if (calculator is null || difficulty.OnlineId <= 0)
            return new Dictionary<int, double>();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        if (calculator is PpTargetExactCalculationService exact)
        {
            return await exact.CalculateAccuracyCurveAsync(
                difficulty.OnlineId,
                difficulty.BeatmapHash,
                Array.Empty<string>(),
                AccuracyPoints,
                timeout.Token).ConfigureAwait(false);
        }

        // The generic service returns one estimate per beatmap, so each accuracy point is its own request.
        var values = new Dictionary<int, double>();
        foreach (int accuracy in AccuracyPoints)
        {
            IReadOnlyDictionary<int, PpTargetEstimate> result = await calculator.CalculateAsync(
                new[] { new PpTargetExactRequest(difficulty.OnlineId, difficulty.BeatmapHash, Array.Empty<string>(), accuracy / 100d, 1) }, timeout.Token).ConfigureAwait(false);
            if (result.TryGetValue(difficulty.OnlineId, out PpTargetEstimate? estimate))
                values[accuracy] = accuracy == 100 ? estimate.RealisticMaximumPp : estimate.ExpectedPp;
        }
        return values;
    }

    private async Task<OnlineBeatmapScoreHistoryResult> loadOnlineHistory(int beatmapId, CancellationToken cancellationToken)
    {
        if (beatmapId <= 0)
            return unavailableOnlineHistory(beatmapId, OsuBestScoresFetchStatus.InvalidResponse);
        IAccountScoreHistoryService? service = onlineScoreHistory();
        if (service is null)
            return unavailableOnlineHistory(beatmapId, OsuBestScoresFetchStatus.SessionUnavailable);
        try
        {
            return await service.FetchBeatmapAsync(beatmapId, cancellationToken).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return unavailableOnlineHistory(beatmapId, OsuBestScoresFetchStatus.NetworkError);
        }
        catch
        {
            return unavailableOnlineHistory(beatmapId, OsuBestScoresFetchStatus.InvalidResponse);
        }
    }

    private static OnlineBeatmapScoreHistoryResult unavailableOnlineHistory(int beatmapId, OsuBestScoresFetchStatus status) =>
        new(beatmapId, [], new OnlineScoreCoverage(status, false, null, "exact beatmap submissions", null, false));

    protected override void Dispose(bool isDisposing)
    {
        scheduledQuery?.Cancel();
        scheduledDetails?.Cancel();
        detailCancellation?.Cancel();
        detailCancellation?.Dispose();
        historyCancellation?.Cancel();
        historyCancellation?.Dispose();
        controller.StateChanged -= stateChanged;
        if (sourceChanges is not null)
            sourceChanges.SourceChanged -= sourceChanged;
        controller.Dispose();
        base.Dispose(isDisposing);
    }

    private sealed partial class EmptyState : CompositeDrawable
    {
        public EmptyState(IconUsage icon, string title, string detail, string? actionText = null, Action? action = null)
        {
            RelativeSizeAxes = Axes.X;
            Height = actionText is null ? 160 : 200;
            var content = new FillFlowContainer
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Vertical,
                Spacing = new(8),
                Children = new Drawable[]
                {
                    new SpriteIcon { Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre, Icon = icon, Size = new(24), Colour = AimModPalette.Cyan },
                    new SpriteText { Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre, Text = title, Font = new FontUsage(size: 16, weight: "Bold"), Colour = AimModPalette.Text },
                    new SpriteText { Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre, Text = detail, Font = AimModVisualStyle.BodyFont, Colour = AimModPalette.Muted },
                },
            };
            if (actionText is not null && action is not null)
                content.Add(new AimModButton(actionText, action) { Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre, Margin = new MarginPadding { Top = 8 } });
            InternalChild = content;
        }
    }

    internal enum BpmFilter
    {
        Any,
        Below160,
        From160To200,
        Above200,
    }

    internal enum LengthFilter
    {
        Any,
        Short,
        Medium,
        Long,
    }

    internal enum PlayedFilter
    {
        Everything,
        Played,
        Unplayed,
    }

    private sealed partial class PrettyDropdown<T> : AimMod.Desktop.Coaching.BoundedShearedDropdown<T>
        where T : struct, Enum
    {
        private readonly Func<T, string> formatter;

        public PrettyDropdown(string label, Func<T, string> formatter) : base(label)
        {
            this.formatter = formatter;
        }

        protected override LocalisableString GenerateItemText(T item) => formatter(item);
    }

    private static string formatSort(LocalLibrarySort value) => value switch
    {
        LocalLibrarySort.Title => "Title A–Z",
        LocalLibrarySort.StarRating => "Hardest first",
        LocalLibrarySort.RecentlyPlayed => "Recently played",
        _ => "Recently added",
    };

    private static string formatBpm(BpmFilter value) => value switch
    {
        BpmFilter.Below160 => "Under 160 BPM",
        BpmFilter.From160To200 => "160–200 BPM",
        BpmFilter.Above200 => "Over 200 BPM",
        _ => "Any BPM",
    };

    private static string formatBpmChoice(BpmFilter value) => value switch
    {
        BpmFilter.Below160 => "< 160",
        BpmFilter.From160To200 => "160–200",
        BpmFilter.Above200 => "> 200",
        _ => "Any",
    };

    private static string formatLength(LengthFilter value) => value switch
    {
        LengthFilter.Short => "Under 2 min",
        LengthFilter.Medium => "2–4 min",
        LengthFilter.Long => "Over 4 min",
        _ => "Any length",
    };

    private static string formatLengthChoice(LengthFilter value) => value switch
    {
        LengthFilter.Short => "< 2 min",
        LengthFilter.Medium => "2–4 min",
        LengthFilter.Long => "> 4 min",
        _ => "Any",
    };

    private static string formatPlayed(PlayedFilter value) => value switch
    {
        PlayedFilter.Played => "Played",
        PlayedFilter.Unplayed => "Unplayed",
        _ => "All maps",
    };

    private static string formatPlayedChoice(PlayedFilter value) => value switch
    {
        PlayedFilter.Played => "Played",
        PlayedFilter.Unplayed => "Unplayed",
        _ => "Any",
    };
}
