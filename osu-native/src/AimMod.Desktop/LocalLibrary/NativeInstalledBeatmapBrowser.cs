using AimMod.Desktop.PpTargets;
using AimMod.Desktop.ScoreHistory;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using System.Diagnostics;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Framework.Threading;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.LocalLibrary;

/// <summary>
/// Dense, set-first view of the installed osu! library. All displayed values come from
/// the local lazer index, matching local scores, or the exact PP calculator.
/// </summary>
public partial class NativeInstalledBeatmapBrowser : CompositeDrawable
{
    private const int page_size = 80;
    private const float toolbar_height = 126;

    private readonly ILocalLibrarySource source;
    private readonly Func<IPpTargetExactCalculationService?> exactCalculator;
    private readonly Func<IAccountScoreHistoryService?> onlineScoreHistory;
    private readonly LocalLibraryController controller;
    private readonly OsuTextBox searchBox;
    private readonly Container searchSurface;
    private readonly Container starsSurface;
    private readonly Container sortSurface;
    private readonly PrettyDropdown<LocalLibrarySort> sortDropdown;
    private readonly PrettyDropdown<BpmFilter> bpmDropdown;
    private readonly PrettyDropdown<LengthFilter> lengthDropdown;
    private readonly PrettyDropdown<PlayedFilter> playedDropdown;
    private readonly Bindable<LocalLibrarySort> sort = new(LocalLibrarySort.RecentlyAdded);
    private readonly Bindable<BpmFilter> bpmFilter = new(BpmFilter.Any);
    private readonly Bindable<LengthFilter> lengthFilter = new(LengthFilter.Any);
    private readonly Bindable<PlayedFilter> playedFilter = new(PlayedFilter.Everything);
    private readonly BindableDouble minimumStars = new(0) { MinValue = 0, MaxValue = 10, Default = 0 };
    private readonly BindableDouble maximumStars = new(10) { MinValue = 0, MaxValue = 10, Default = 10 };
    private readonly RangeSlider stars;
    private readonly TruncatingSpriteText status;
    private readonly FillFlowContainer<Drawable> setRows;
    private readonly OsuScrollContainer listScroll;
    private readonly BeatmapInspector inspector;
    private readonly AimModLoadingOverlay loading;
    private readonly Container rightRail;
    private readonly Container listPanel;
    private readonly ILocalLibrarySourceChanged? sourceChanges;

    private ScheduledDelegate? scheduledQuery;
    private CancellationTokenSource? detailCancellation;
    private LocalBeatmapSet? selectedSet;
    private LocalBeatmapDifficulty? selectedDifficulty;
    private long displayedRevision;

    public NativeInstalledBeatmapBrowser(
        ILocalLibrarySource source,
        Func<IPpTargetExactCalculationService?>? exactCalculator = null,
        Func<IAccountScoreHistoryService?>? onlineScoreHistory = null)
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
        InternalChildren = new Drawable[]
        {
            new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = toolbar_height,
                Depth = -20,
                Children = new Drawable[]
                {
                    searchSurface = filterSurface(0, 0, 480, 38),
                    searchBox = new OsuTextBox
                    {
                        Width = 0.52f,
                        Height = 38,
                        PlaceholderText = "Search beatmaps, artists, mappers, or difficulties",
                    },
                    starsSurface = filterSurface(490, 51, 260, 46),
                    stars = new RangeSlider
                    {
                        Position = new(490, 52),
                        Size = new(260, 48),
                        Label = "Stars",
                        LowerBound = minimumStars,
                        UpperBound = maximumStars,
                        DefaultStringLowerBound = "0",
                        DefaultStringUpperBound = "10+",
                        TooltipSuffix = "stars",
                        NubWidth = 28,
                    },
                    sortSurface = filterSurface(0, 51, 178, 38, Anchor.TopRight),
                    sortDropdown = new PrettyDropdown<LocalLibrarySort>(formatSort)
                    {
                        Anchor = Anchor.TopRight,
                        Origin = Anchor.TopRight,
                        Y = 51,
                        Width = 178,
                        Items = new[] { LocalLibrarySort.RecentlyAdded, LocalLibrarySort.Title, LocalLibrarySort.StarRating },
                        Current = sort,
                    },
                    bpmDropdown = new PrettyDropdown<BpmFilter>(formatBpm)
                    {
                        Position = new(0, 51),
                        Width = 150,
                        Items = Enum.GetValues<BpmFilter>(),
                        Current = bpmFilter,
                    },
                    lengthDropdown = new PrettyDropdown<LengthFilter>(formatLength)
                    {
                        Position = new(160, 51),
                        Width = 150,
                        Items = Enum.GetValues<LengthFilter>(),
                        Current = lengthFilter,
                    },
                    playedDropdown = new PrettyDropdown<PlayedFilter>(formatPlayed)
                    {
                        Position = new(320, 51),
                        Width = 160,
                        Items = Enum.GetValues<PlayedFilter>(),
                        Current = playedFilter,
                    },
                    filterSurface(0, 51, 150, 38),
                    filterSurface(160, 51, 150, 38),
                    filterSurface(320, 51, 160, 38),
                    filterLabel("BPM", 4),
                    filterLabel("LENGTH", 164),
                    filterLabel("PLAYED", 324),
                    status = new TruncatingSpriteText
                    {
                        Y = 107,
                        Font = new FontUsage(size: 11, weight: "SemiBold"),
                        Colour = AimModPalette.Muted,
                        Text = "Reading installed beatmaps...",
                    },
                },
            },
            listPanel = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = toolbar_height },
                Masking = true,
                Child = listScroll = new OsuScrollContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Child = setRows = new FillFlowContainer<Drawable>
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Spacing = new(7),
                        Padding = new MarginPadding { Right = 10, Bottom = 24 },
                    },
                },
            },
            rightRail = new Container
            {
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                RelativeSizeAxes = Axes.Y,
                Width = 350,
                Padding = new MarginPadding { Left = 14, Top = 8 },
                Masking = true,
                Child = inspector = new BeatmapInspector(),
            },
            loading = new AimModLoadingOverlay(),
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        searchBox.OnCommit += (_, _) => resetQuery();
        minimumStars.BindValueChanged(_ => scheduleQuery());
        maximumStars.BindValueChanged(_ => scheduleQuery());
        sort.BindValueChanged(_ => resetQuery());
        bpmFilter.BindValueChanged(_ => resetQuery());
        lengthFilter.BindValueChanged(_ => resetQuery());
        playedFilter.BindValueChanged(_ => resetQuery());
        resetQuery();
    }

    protected override void Update()
    {
        base.Update();
        bool compact = DrawWidth < 1_280;
        float railWidth = compact ? 0 : Math.Clamp(DrawWidth * 0.27f, 350, 390);
        float contentWidth = DrawWidth - railWidth;
        rightRail.Width = railWidth;
        rightRail.Alpha = compact ? 0 : 1;
        rightRail.AlwaysPresent = !compact;
        listPanel.Padding = new MarginPadding { Top = toolbar_height, Right = railWidth };
        searchBox.Width = Math.Max(260, contentWidth - 230);
        searchSurface.Width = searchBox.Width;
        stars.Width = Math.Clamp(contentWidth - 680, 170, 300);
        starsSurface.Width = stars.Width;
        sortDropdown.Position = new(-railWidth, 51);
        sortSurface.Position = new(-railWidth, 51);
        status.MaxWidth = Math.Max(200, DrawWidth - railWidth - 340);
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
        _ = controller.LoadAsync(query);
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

        if (state.Status == LocalLibraryLoadStatus.Loading)
        {
            loading.ShowLoading("Loading installed beatmaps", "Reading sets and difficulties from osu!lazer");
            return;
        }

        loading.HideLoading();
        setRows.Clear();
        if (state.Status == LocalLibraryLoadStatus.Error)
        {
            status.Text = $"Library unavailable: {state.ErrorMessage}";
            setRows.Add(new EmptyState(FontAwesome.Solid.ExclamationTriangle, "Could not read the local library", "Check the osu!lazer data location and retry the search."));
            return;
        }

        if (state.Status == LocalLibraryLoadStatus.Empty)
        {
            status.Text = "No installed beatmaps match these filters";
            setRows.Add(new EmptyState(FontAwesome.Solid.Search, "No beatmaps found", "Change the search or star range to see more of your library."));
            inspector.ClearSelection();
            return;
        }

        LocalBeatmapSet[] visibleSets = state.BeatmapSets.Where(matchesFilters).ToArray();
        if (visibleSets.Length == 0)
        {
            status.Text = "No installed beatmaps match these filters";
            setRows.Add(new EmptyState(FontAwesome.Solid.Filter, "No beatmaps match", "Broaden the BPM, length, or played filters to see more sets."));
            inspector.ClearSelection();
            return;
        }

        status.Text = $"Showing {visibleSets.Length:N0} matching sets from {state.Total:N0} installed";
        foreach ((LocalBeatmapSet set, int index) in visibleSets.Select((value, index) => (value, index)))
            setRows.Add(new BeatmapSetRow(index + 1, set, selectSet, selectDifficulty));

        LocalBeatmapSet next = selectedSet is not null
            ? visibleSets.FirstOrDefault(set => set.SetId == selectedSet.SetId) ?? visibleSets[0]
            : visibleSets[0];
        selectSet(next);
    }

    private bool matchesFilters(LocalBeatmapSet set)
    {
        bool bpmMatches = bpmFilter.Value switch
        {
            BpmFilter.Below160 => set.Difficulties.Any(d => d.Bpm < 160),
            BpmFilter.From160To200 => set.Difficulties.Any(d => d.Bpm is >= 160 and <= 200),
            BpmFilter.Above200 => set.Difficulties.Any(d => d.Bpm > 200),
            _ => true,
        };
        bool lengthMatches = lengthFilter.Value switch
        {
            LengthFilter.Short => set.Difficulties.Any(d => d.LengthMilliseconds < 120_000),
            LengthFilter.Medium => set.Difficulties.Any(d => d.LengthMilliseconds is >= 120_000 and <= 240_000),
            LengthFilter.Long => set.Difficulties.Any(d => d.LengthMilliseconds > 240_000),
            _ => true,
        };
        bool playedMatches = playedFilter.Value switch
        {
            PlayedFilter.Played => set.LastPlayed is not null || set.LocalReplayCount is > 0,
            PlayedFilter.Unplayed => set.LastPlayed is null && set.LocalReplayCount is not > 0,
            _ => true,
        };
        return bpmMatches && lengthMatches && playedMatches;
    }

    private void selectSet(LocalBeatmapSet set)
    {
        selectedSet = set;
        LocalBeatmapDifficulty difficulty = selectedDifficulty is not null
            ? set.Difficulties.FirstOrDefault(item => item.BeatmapId == selectedDifficulty.BeatmapId) ?? set.Difficulties.OrderBy(item => item.StarRating).First()
            : set.Difficulties.OrderBy(item => item.StarRating).First();
        selectDifficulty(set, difficulty);
    }

    private void selectDifficulty(LocalBeatmapSet set, LocalBeatmapDifficulty difficulty)
    {
        selectedSet = set;
        selectedDifficulty = difficulty;
        foreach (BeatmapSetRow row in setRows.Children.OfType<BeatmapSetRow>())
            row.SetSelection(set.SetId, difficulty.BeatmapId);
        inspector.ShowSelection(set, difficulty, controller.State.BeatmapSets);
        loadDetails(set, difficulty);
    }

    private void loadDetails(LocalBeatmapSet set, LocalBeatmapDifficulty difficulty)
    {
        detailCancellation?.Cancel();
        detailCancellation?.Dispose();
        detailCancellation = new CancellationTokenSource();
        CancellationToken token = detailCancellation.Token;

        _ = Task.Run(async () =>
        {
            Task<OnlineBeatmapScoreHistoryResult> onlineTask = loadOnlineHistory(difficulty.OnlineId, token);
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

            IReadOnlyDictionary<int, double> ppAtAccuracy = new Dictionary<int, double>();
            Exception? ppError = null;
            IPpTargetExactCalculationService? calculator = exactCalculator();
            if (calculator is not null && (difficulty.OnlineId > 0 || !string.IsNullOrWhiteSpace(difficulty.BeatmapHash)))
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(45));
                    if (calculator is PpTargetExactCalculationService exact)
                    {
                        ppAtAccuracy = await exact.CalculateAccuracyCurveAsync(
                            difficulty.OnlineId,
                            difficulty.BeatmapHash,
                            Array.Empty<string>(),
                            new[] { 95, 98, 99, 100 },
                            timeout.Token);
                    }
                    else
                    {
                        var values = new Dictionary<int, double>();
                        foreach (int accuracy in new[] { 95, 98, 99, 100 })
                        {
                            IReadOnlyDictionary<int, PpTargetEstimate> result = await calculator.CalculateAsync(
                                new[] { new PpTargetExactRequest(difficulty.OnlineId, difficulty.BeatmapHash, Array.Empty<string>(), accuracy / 100d, 1) }, timeout.Token);
                            if (result.TryGetValue(difficulty.OnlineId, out PpTargetEstimate? estimate))
                                values[accuracy] = accuracy == 100 ? estimate.RealisticMaximumPp : estimate.ExpectedPp;
                        }
                        ppAtAccuracy = values;
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    ppError = new TimeoutException("Exact PP calculation exceeded 45 seconds.");
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    ppError = error;
                }
            }

            OnlineBeatmapScoreHistoryResult online = await onlineTask.ConfigureAwait(false);
            IReadOnlyList<ScoreHistoryEntry> plays = ScoreHistoryMerger.Merge(matching, online.Scores);
            if (!token.IsCancellationRequested && !IsDisposed)
                Schedule(() => inspector.ShowDetails(plays, online, ppAtAccuracy, replayError, ppError));
        }, token);
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
        detailCancellation?.Cancel();
        detailCancellation?.Dispose();
        controller.StateChanged -= stateChanged;
        if (sourceChanges is not null)
            sourceChanges.SourceChanged -= sourceChanged;
        controller.Dispose();
        base.Dispose(isDisposing);
    }

    private sealed partial class BeatmapSetRow : ClickableContainer
    {
        private readonly LocalBeatmapSet set;
        private readonly Action<LocalBeatmapSet> selectSet;
        private readonly FillFlowContainer<Drawable> difficultyPills;
        private readonly FillFlowContainer<Drawable> difficultyRows;
        private readonly OsuScrollContainer difficultyScroll;
        private readonly Box selectedLayer;
        private bool expanded;

        public BeatmapSetRow(int index, LocalBeatmapSet set, Action<LocalBeatmapSet> selectSet, Action<LocalBeatmapSet, LocalBeatmapDifficulty> selectDifficulty)
        {
            this.set = set;
            this.selectSet = selectSet;
            RelativeSizeAxes = Axes.X;
            Height = 112;
            Masking = true;
            CornerRadius = 6;
            BorderThickness = 1;
            BorderColour = AimModPalette.Border;

            double minStars = set.Difficulties.Min(d => d.StarRating);
            double maxStars = set.Difficulties.Max(d => d.StarRating);
            LocalBeatmapDifficulty representative = set.Difficulties.OrderBy(d => d.StarRating).ElementAt(set.Difficulties.Count / 2);

            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                selectedLayer = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Pink, Alpha = 0 },
                new SpriteText { Text = index.ToString(), Position = new(12, 14), Font = new FontUsage(size: 14, weight: "Bold"), Colour = AimModPalette.Text },
                new Container
                {
                    Position = new(36, 7),
                    Size = new(126, 98),
                    Masking = true,
                    CornerRadius = 5,
                    Child = new AimModLocalArtwork(set.BackgroundPath) { RelativeSizeAxes = Axes.Both },
                },
                new TruncatingSpriteText { Text = set.Title, Position = new(178, 12), Font = new FontUsage(size: 17, weight: "Bold"), Colour = AimModPalette.Text, MaxWidth = 390 },
                new TruncatingSpriteText { Text = set.Artist, Position = new(178, 37), Font = new FontUsage(size: 12), Colour = AimModPalette.Text, MaxWidth = 390 },
                new TruncatingSpriteText { Text = $"mapped by {set.Creator}", Position = new(178, 56), Font = new FontUsage(size: 11), Colour = AimModPalette.Cyan, MaxWidth = 390 },
                new SpriteText { Text = $"{representative.Bpm:0} BPM   {formatDuration(representative.LengthMilliseconds)}", Position = new(585, 17), Font = new FontUsage(size: 11), Colour = AimModPalette.Muted },
                new SpriteText { Text = $"Added {relativeDate(set.DateAdded)}", Position = new(585, 39), Font = new FontUsage(size: 11), Colour = AimModPalette.Muted },
                new SpriteText { Text = set.LocalReplayCount is > 0 ? $"{set.LocalReplayCount:N0} local plays" : "No local plays", Position = new(585, 61), Font = new FontUsage(size: 11, weight: "SemiBold"), Colour = set.LocalReplayCount is > 0 ? AimModPalette.Text : AimModPalette.Muted },
                new AimModDifficultyPill(maxStars) { Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Margin = new MarginPadding { Top = 12, Right = 12 } },
                difficultyPills = new FillFlowContainer<Drawable>
                {
                    RelativeSizeAxes = Axes.X,
                    Width = 0.75f,
                    AutoSizeAxes = Axes.Y,
                    Position = new(178, 76),
                    Masking = true,
                    Direction = FillDirection.Horizontal,
                    Spacing = new(6),
                },
                new DifficultyTableHeader { Y = 112 },
                difficultyScroll = new OsuScrollContainer
                {
                    RelativeSizeAxes = Axes.X,
                    Y = 144,
                    Height = 216,
                    Masking = true,
                    Child = difficultyRows = new FillFlowContainer<Drawable>
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                    },
                },
            };

            foreach (LocalBeatmapDifficulty difficulty in set.Difficulties.OrderBy(d => d.StarRating).Take(6))
                difficultyPills.Add(new DifficultyChoice(difficulty, () => { expanded = true; updateExpanded(); selectDifficulty(set, difficulty); }));
            foreach (LocalBeatmapDifficulty difficulty in set.Difficulties.OrderBy(d => d.StarRating))
                difficultyRows.Add(new DifficultyTableRow(difficulty, () => selectDifficulty(set, difficulty)));
        }

        public void SetSelection(Guid setId, Guid difficultyId)
        {
            bool selected = set.SetId == setId;
            selectedLayer.Alpha = selected ? 0.055f : 0;
            if (!selected && expanded)
            {
                expanded = false;
                updateExpanded();
            }
            if (selected && !expanded)
            {
                expanded = true;
                updateExpanded();
            }
            foreach (DifficultyChoice choice in difficultyPills.Children.OfType<DifficultyChoice>())
                choice.Selected = selected && choice.BeatmapId == difficultyId;
            foreach (DifficultyTableRow row in difficultyRows.Children.OfType<DifficultyTableRow>())
                row.Selected = selected && row.BeatmapId == difficultyId;
        }

        private void updateExpanded()
        {
            Height = expanded ? 360 : 112;
            difficultyRows.Alpha = expanded ? 1 : 0;
            difficultyScroll.Alpha = expanded ? 1 : 0;
        }

        protected override bool OnClick(ClickEvent e)
        {
            selectSet(set);
            return true;
        }
    }

    private sealed partial class DifficultyTableHeader : CompositeDrawable
    {
        public DifficultyTableHeader()
        {
            RelativeSizeAxes = Axes.X;
            Height = 32;
            Alpha = 0.75f;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                label("DIFFICULTY", 40),
                label("STARS", 230),
                label("AR", 290),
                label("OD", 337),
                label("CS", 384),
                label("HP", 431),
                label("BPM", 478),
                label("LENGTH", 538),
                label("YOUR SCORES", 605),
            };
        }

        private static Drawable label(string value, float x) => new SpriteText
        {
            Text = value,
            Position = new(x, 10),
            Font = new FontUsage(size: 8, weight: "Bold"),
            Colour = AimModPalette.Muted,
        };
    }

    private sealed partial class DifficultyChoice : ClickableContainer
    {
        private readonly Box background;
        private readonly Action action;
        public Guid BeatmapId { get; }

        public DifficultyChoice(LocalBeatmapDifficulty difficulty, Action action)
        {
            this.action = action;
            BeatmapId = difficulty.BeatmapId;
            AutoSizeAxes = Axes.Both;
            Masking = true;
            CornerRadius = 4;
            Children = new Drawable[]
            {
                background = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Horizontal,
                    Spacing = new(7),
                    Padding = new MarginPadding { Horizontal = 9, Vertical = 5 },
                    Children = new Drawable[]
                    {
                        new SpriteText { Text = $"{difficulty.StarRating:0.00}", Font = new FontUsage(size: 10, weight: "Bold"), Colour = AimModVisualStyle.DifficultyColour(difficulty.StarRating) },
                        new TruncatingSpriteText { Text = difficulty.Name, Font = new FontUsage(size: 10), Colour = AimModPalette.Text, MaxWidth = 82 },
                    },
                },
            };
        }

        public bool Selected { set { background.Colour = value ? AimModPalette.PinkDark : AimModPalette.PanelRaised; BorderThickness = value ? 1 : 0; BorderColour = AimModPalette.Pink; } }
        protected override bool OnClick(ClickEvent e) { action(); return true; }
    }

    private sealed partial class DifficultyTableRow : ClickableContainer
    {
        private readonly Box background;
        private readonly Action action;
        public Guid BeatmapId { get; }

        public DifficultyTableRow(LocalBeatmapDifficulty difficulty, Action action)
        {
            this.action = action;
            BeatmapId = difficulty.BeatmapId;
            RelativeSizeAxes = Axes.X;
            Height = 36;
            Children = new Drawable[]
            {
                background = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas, Alpha = 0.72f },
                new SpriteIcon { Position = new(16, 12), Size = new(11), Icon = FontAwesome.Solid.Play, Colour = AimModPalette.Muted },
                cell(difficulty.Name, 40, 185, true),
                cell($"{difficulty.StarRating:0.00}", 230, 55),
                cell($"{difficulty.ApproachRate:0.#}", 290, 42),
                cell($"{difficulty.OverallDifficulty:0.#}", 337, 42),
                cell($"{difficulty.CircleSize:0.#}", 384, 42),
                cell($"{difficulty.DrainRate:0.#}", 431, 42),
                cell($"{difficulty.Bpm:0}", 478, 55),
                cell(formatDuration(difficulty.LengthMilliseconds), 538, 62),
                cell(difficulty.LocalScoreCount is { } count ? $"{count:N0} scores" : "No scores", 605, 90),
            };
        }

        public bool Selected { set => background.Colour = value ? AimModPalette.PinkDark : AimModPalette.Canvas; }
        protected override bool OnClick(ClickEvent e) { action(); return true; }

        private static Drawable cell(string value, float x, float width, bool bold = false) => new TruncatingSpriteText
        {
            Text = value,
            Position = new(x, 10),
            MaxWidth = width,
            Font = new FontUsage(size: 10, weight: bold ? "SemiBold" : "Regular"),
            Colour = AimModPalette.Text,
        };
    }

    private sealed partial class BeatmapInspector : CompositeDrawable
    {
        private readonly OsuScrollContainer scroll;
        private readonly FillFlowContainer<Drawable> content;
        private LocalBeatmapSet? set;
        private LocalBeatmapDifficulty? difficulty;
        private IReadOnlyList<LocalBeatmapSet> candidates = Array.Empty<LocalBeatmapSet>();

        public BeatmapInspector()
        {
            RelativeSizeAxes = Axes.Both;
            Masking = true;
            InternalChild = scroll = new OsuScrollContainer
            {
                RelativeSizeAxes = Axes.Both,
                Child = content = new FillFlowContainer<Drawable>
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new(8),
                    Padding = new MarginPadding { Right = 10, Bottom = 20 },
                },
            };
            ClearSelection();
        }

        public void ClearSelection()
        {
            set = null;
            difficulty = null;
            content.Clear();
            content.Add(new EmptyState(FontAwesome.Solid.MousePointer, "Select a difficulty", "Skill demand, exact PP, and local performance will appear here."));
        }

        public void ShowSelection(LocalBeatmapSet set, LocalBeatmapDifficulty difficulty, IReadOnlyList<LocalBeatmapSet> candidates)
        {
            this.set = set;
            this.difficulty = difficulty;
            this.candidates = candidates;
            content.Clear();
            content.Add(new SkillDemandCard(difficulty));
            content.Add(new LoadingCard("Calculating exact PP", "Resolving this beatmap difficulty and applying osu!standard performance rules."));
            content.Add(new LoadingCard("Reading recent performance", "Matching local scores to this exact difficulty."));
        }

        public void ShowDetails(
            IReadOnlyList<ScoreHistoryEntry> plays,
            OnlineBeatmapScoreHistoryResult online,
            IReadOnlyDictionary<int, double> pp,
            Exception? replayError,
            Exception? ppError)
        {
            if (set is null || difficulty is null)
                return;
            while (content.Count > 1)
                content.Remove(content.Last(), true);
            content.Add(new PersonalFitCard(plays, online));
            content.Add(new PpAccuracyCard(pp, ppError));
            content.Add(new NextMapsCard(set, difficulty, candidates));
            content.Add(new RecentPerformanceCard(plays, online, replayError));
            content.Add(new ActionBar(difficulty));
        }
    }

    private enum InspectorPanelStyle
    {
        Default,
        Raised,
    }

    private partial class InspectorPanel : Container
    {
        private readonly Container content;

        protected override Container<Drawable> Content => content;

        public InspectorPanel(InspectorPanelStyle style)
        {
            Masking = true;
            CornerRadius = 6;
            BorderThickness = 1;
            BorderColour = AimModPalette.Border;
            InternalChildren = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = style == InspectorPanelStyle.Raised ? AimModPalette.PanelRaised : AimModPalette.Panel,
                },
                content = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding(14),
                },
            };
        }
    }

    private sealed partial class InspectorHeading : InspectorPanel
    {
        public InspectorHeading(LocalBeatmapSet set, LocalBeatmapDifficulty difficulty)
            : base(InspectorPanelStyle.Raised)
        {
            RelativeSizeAxes = Axes.X;
            Height = 105;
            Children = new Drawable[]
            {
                new TruncatingSpriteText { Text = set.Title, Font = new FontUsage(size: 16, weight: "Bold"), Colour = AimModPalette.Text, MaxWidth = 285 },
                new TruncatingSpriteText { Text = difficulty.Name, Y = 25, Font = new FontUsage(size: 12), Colour = AimModPalette.Cyan, MaxWidth = 285 },
                new AimModDifficultyPill(difficulty.StarRating) { Y = 51 },
                new SpriteText { Text = $"AR {difficulty.ApproachRate:0.#}   OD {difficulty.OverallDifficulty:0.#}   CS {difficulty.CircleSize:0.#}   HP {difficulty.DrainRate:0.#}", Position = new(84, 57), Font = new FontUsage(size: 10), Colour = AimModPalette.Muted },
            };
        }
    }

    private sealed partial class SkillDemandCard : InspectorPanel
    {
        public SkillDemandCard(LocalBeatmapDifficulty difficulty)
            : base(InspectorPanelStyle.Default)
        {
            RelativeSizeAxes = Axes.X;
            Height = 170;
            Add(new SpriteText { Text = "SKILL DEMAND", Font = new FontUsage(size: 11, weight: "Bold"), Colour = AimModPalette.Text });
            (string name, double value)[] skills =
            {
                ("Aim", normalise(difficulty.StarRating, 1.5, 9)),
                ("Speed", normalise(difficulty.Bpm, 90, 260)),
                ("Stamina", normalise(difficulty.LengthMilliseconds, 45_000, 360_000)),
                ("Reading", normalise(difficulty.ApproachRate, 5, 10.5)),
                ("Precision", normalise((difficulty.OverallDifficulty + difficulty.CircleSize) / 2, 3, 9)),
            };
            for (int i = 0; i < skills.Length; i++)
                Add(new SkillBar(skills[i].name, skills[i].value) { Y = 28 + i * 25 });
        }

        private static double normalise(double value, double minimum, double maximum) => Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
    }

    private sealed partial class SkillBar : CompositeDrawable
    {
        public SkillBar(string label, double value)
        {
            RelativeSizeAxes = Axes.X;
            Height = 18;
            InternalChildren = new Drawable[]
            {
                new SpriteText { Text = label, Font = new FontUsage(size: 10), Colour = AimModPalette.Muted },
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Width = 0.62f,
                    Height = 5,
                    Position = new(78, 6),
                    Children = new Drawable[]
                    {
                        new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Border },
                        new Box { RelativeSizeAxes = Axes.Both, Width = (float)value, Colour = AimModPalette.Pink },
                    },
                },
                new SpriteText { Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Text = $"{value * 10:0.0}", Font = new FontUsage(size: 10, weight: "Bold"), Colour = AimModPalette.Text },
            };
        }
    }

    private sealed partial class PpAccuracyCard : InspectorPanel
    {
        public PpAccuracyCard(IReadOnlyDictionary<int, double> values, Exception? error)
            : base(InspectorPanelStyle.Default)
        {
            RelativeSizeAxes = Axes.X;
            Height = 120;
            Add(new SpriteText { Text = "PP AT ACCURACY", Font = new FontUsage(size: 11, weight: "Bold"), Colour = AimModPalette.Text });
            if (values.Count == 0)
            {
                Add(new TruncatingSpriteText { Text = error is null ? "Exact PP is unavailable for this local difficulty." : "Exact PP calculation failed for this difficulty.", Y = 34, MaxWidth = 285, Font = new FontUsage(size: 11), Colour = AimModPalette.Muted });
                return;
            }
            int[] accuracies = { 95, 98, 99, 100 };
            for (int i = 0; i < accuracies.Length; i++)
            {
                int accuracy = accuracies[i];
                float x = i * 74;
                Add(new SpriteText { Text = accuracy == 100 ? "SS" : $"{accuracy}%", Position = new(x, 32), Font = new FontUsage(size: 10), Colour = AimModPalette.Muted });
                Add(new SpriteText { Text = values.TryGetValue(accuracy, out double pp) ? $"{pp:0}pp" : "--", Position = new(x, 55), Font = new FontUsage(size: 18, weight: "Bold"), Colour = AimModPalette.Pink });
            }
            if (values.TryGetValue(98, out double expected) && values.TryGetValue(100, out double maximum))
                Add(new SpriteText { Text = $"EXPECTED  {expected:0}pp     REALISTIC MAX  {maximum:0}pp", Y = 87, Font = new FontUsage(size: 10, weight: "SemiBold"), Colour = AimModPalette.Cyan });
        }
    }

    private sealed partial class PersonalFitCard : InspectorPanel
    {
        public PersonalFitCard(IReadOnlyList<ScoreHistoryEntry> plays, OnlineBeatmapScoreHistoryResult online)
            : base(InspectorPanelStyle.Default)
        {
            RelativeSizeAxes = Axes.X;
            Height = 88;
            Add(new SpriteText { Text = "PERSONAL FIT", Font = new FontUsage(size: 11, weight: "Bold"), Colour = AimModPalette.Text });
            double fit = plays.Count == 0 ? 0 : Math.Clamp((plays.Average(play => play.Accuracy) - 0.80) / 0.20, 0, 1);
            Add(new Container
            {
                RelativeSizeAxes = Axes.X,
                Width = 0.78f,
                Height = 6,
                Y = 31,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Border },
                    new Box { RelativeSizeAxes = Axes.Both, Width = (float)fit, Colour = AimModPalette.Pink },
                },
            });
            Add(new SpriteText
            {
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                Y = 24,
                Text = plays.Count == 0 ? "NO PLAYS" : $"{fit:P0}",
                Font = new FontUsage(size: 12, weight: "Bold"),
                Colour = plays.Count == 0 ? AimModPalette.Muted : AimModPalette.Text,
            });
            Add(new TruncatingSpriteText
            {
                Text = coverageText(plays, online),
                Y = 45,
                MaxWidth = 285,
                Font = new FontUsage(size: 8),
                Colour = online.IsSuccess ? AimModPalette.Cyan : AimModPalette.Muted,
            });
        }
    }

    private sealed partial class NextMapsCard : InspectorPanel
    {
        public NextMapsCard(LocalBeatmapSet selectedSet, LocalBeatmapDifficulty selectedDifficulty, IReadOnlyList<LocalBeatmapSet> candidates)
            : base(InspectorPanelStyle.Default)
        {
            RelativeSizeAxes = Axes.X;
            LocalBeatmapSet[] next = candidates.Where(set => set.SetId != selectedSet.SetId)
                                               .OrderBy(set => set.Difficulties.Min(difficulty => Math.Abs(difficulty.StarRating - selectedDifficulty.StarRating)))
                                               .Take(3)
                                               .ToArray();
            Height = 38 + Math.Max(1, next.Length) * 40;
            Add(new SpriteText { Text = $"NEXT MAPS  {next.Length} / 3", Font = new FontUsage(size: 11, weight: "Bold"), Colour = AimModPalette.Text });
            if (next.Length == 0)
            {
                Add(new SpriteText { Text = "No nearby installed difficulties.", Y = 31, Font = new FontUsage(size: 10), Colour = AimModPalette.Muted });
                return;
            }

            for (int i = 0; i < next.Length; i++)
            {
                LocalBeatmapSet set = next[i];
                LocalBeatmapDifficulty nearest = set.Difficulties.MinBy(difficulty => Math.Abs(difficulty.StarRating - selectedDifficulty.StarRating))!;
                Add(new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 34,
                    Y = 27 + i * 40,
                    Children = new Drawable[]
                    {
                        new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas, Alpha = 0.55f },
                        new TruncatingSpriteText { Text = set.Title, Position = new(9, 5), MaxWidth = 205, Font = new FontUsage(size: 10, weight: "SemiBold"), Colour = AimModPalette.Text },
                        new TruncatingSpriteText { Text = nearest.Name, Position = new(9, 20), MaxWidth = 205, Font = new FontUsage(size: 8), Colour = AimModPalette.Cyan },
                        new SpriteText { Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, Margin = new MarginPadding { Right = 9 }, Text = $"{nearest.StarRating:0.00}*", Font = new FontUsage(size: 10, weight: "Bold"), Colour = AimModVisualStyle.DifficultyColour(nearest.StarRating) },
                    },
                });
            }
        }
    }

    private sealed partial class RecentPerformanceCard : InspectorPanel
    {
        public RecentPerformanceCard(
            IReadOnlyList<ScoreHistoryEntry> plays,
            OnlineBeatmapScoreHistoryResult online,
            Exception? error)
            : base(InspectorPanelStyle.Default)
        {
            RelativeSizeAxes = Axes.X;
            Height = 158;
            Add(new SpriteText { Text = "RECENT PERFORMANCE", Font = new FontUsage(size: 11, weight: "Bold"), Colour = AimModPalette.Text });
            Add(new TruncatingSpriteText { Text = coverageText(plays, online), Y = 20, MaxWidth = 285, Font = new FontUsage(size: 8), Colour = online.IsSuccess ? AimModPalette.Cyan : AimModPalette.Muted });
            if (plays.Count == 0)
            {
                Add(new TruncatingSpriteText { Text = emptyHistoryText(online, error), Y = 45, MaxWidth = 285, Font = new FontUsage(size: 11), Colour = AimModPalette.Muted });
                return;
            }
            ScoreHistoryEntry best = plays.OrderByDescending(play => play.PerformancePoints ?? play.Accuracy).First();
            Add(new SpriteText { Text = $"{best.Accuracy:P2}", Position = new(0, 43), Font = new FontUsage(size: 24, weight: "Bold"), Colour = AimModPalette.Cyan });
            Add(new SpriteText { Text = best.PerformancePoints is { } pp ? $"{pp:0}pp best" : $"{best.TotalScore:N0} best score", Position = new(136, 51), Font = new FontUsage(size: 12, weight: "SemiBold"), Colour = AimModPalette.Pink });
            Add(new PerformanceSparkline(plays) { Position = new(0, 84), RelativeSizeAxes = Axes.X, Height = 48 });
            ScoreHistoryEntry latest = plays[^1];
            string mods = latest.Mods.Count == 0 ? "NM" : string.Join(string.Empty, latest.Mods);
            Add(new TruncatingSpriteText { Text = $"Latest  {latest.PlayedAt.LocalDateTime:g}  {mods}  {latest.MissCount} miss", Y = 137, MaxWidth = 285, Font = new FontUsage(size: 9), Colour = AimModPalette.Muted });
        }
    }

    private sealed partial class PerformanceSparkline : CompositeDrawable
    {
        public PerformanceSparkline(IReadOnlyList<ScoreHistoryEntry> plays)
        {
            float[] values = plays.TakeLast(20).Select(play => (float)(play.Accuracy * 100)).ToArray();
            RelativeSizeAxes = Axes.Both;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas, Alpha = 0.55f },
                new LineGraph { RelativeSizeAxes = Axes.Both, Values = values, MinValue = Math.Max(0, values.Min() - 2), MaxValue = Math.Min(100, values.Max() + 2), LineColour = AimModPalette.Cyan },
            };
        }
    }

    private static string coverageText(IReadOnlyList<ScoreHistoryEntry> plays, OnlineBeatmapScoreHistoryResult online)
    {
        int local = plays.Count(play => play.IsLocal);
        int submitted = plays.Count(play => play.IsSubmitted);
        if (online.IsSuccess)
        {
            string source = online.Coverage.IsFromCache ? "osu! cached" : "osu! live";
            return $"{local:N0} local / {submitted:N0} submitted variants  |  {source}";
        }
        return $"{local:N0} local  |  {onlineStatusText(online.Coverage.Status)}";
    }

    private static string emptyHistoryText(OnlineBeatmapScoreHistoryResult online, Exception? localError)
    {
        if (localError is not null)
            return online.IsSuccess ? "No submitted scores; local history could not be read." : "Local history could not be read and osu! history is unavailable.";
        return online.IsSuccess ? "No submitted or local scores for this exact difficulty." : "No local scores. Connect osu! to check submitted score variants.";
    }

    private static string onlineStatusText(OsuBestScoresFetchStatus status) => status switch
    {
        OsuBestScoresFetchStatus.SignedOut => "osu! signed out",
        OsuBestScoresFetchStatus.TokenExpired => "osu! session expired",
        OsuBestScoresFetchStatus.Unauthorized => "osu! access denied",
        OsuBestScoresFetchStatus.SessionChanged => "osu! account changed",
        OsuBestScoresFetchStatus.NetworkError => "osu! network unavailable",
        OsuBestScoresFetchStatus.ServerError => "osu! service unavailable",
        OsuBestScoresFetchStatus.InvalidResponse => "online difficulty unavailable",
        _ => "osu! session unavailable",
    };

    private sealed partial class LoadingCard : InspectorPanel
    {
        public LoadingCard(string title, string detail)
            : base(InspectorPanelStyle.Default)
        {
            RelativeSizeAxes = Axes.X;
            Height = 92;
            Children = new Drawable[]
            {
                new SpriteIcon { Icon = FontAwesome.Solid.CircleNotch, Size = new(18), Colour = AimModPalette.Pink },
                new SpriteText { Text = title, Position = new(30, 0), Font = new FontUsage(size: 12, weight: "Bold"), Colour = AimModPalette.Text },
                new TruncatingSpriteText { Text = detail, Position = new(30, 25), MaxWidth = 250, Font = new FontUsage(size: 10), Colour = AimModPalette.Muted },
            };
        }
    }

    private sealed partial class ActionBar : FillFlowContainer
    {
        public ActionBar(LocalBeatmapDifficulty difficulty)
        {
            RelativeSizeAxes = Axes.X;
            Height = 48;
            Direction = FillDirection.Horizontal;
            Spacing = new(8);
            Children = new Drawable[]
            {
                new InspectorAction(FontAwesome.Solid.Play, "Open", true, difficulty.OnlineId > 0 ? () => openInOsu(difficulty.OnlineId) : null),
                new InspectorAction(FontAwesome.Solid.Bullseye, "Practice", false, null),
                new InspectorAction(FontAwesome.Regular.Heart, "Save", false, null),
            };
        }
    }

    private sealed partial class InspectorAction : ClickableContainer
    {
        private readonly Box background;
        private readonly Action? action;
        private readonly Colour4 normalColour;

        public InspectorAction(IconUsage icon, string label, bool primary, Action? action)
        {
            this.action = action;
            normalColour = primary ? AimModPalette.Pink : AimModPalette.Panel;
            Width = 88;
            Height = 42;
            Masking = true;
            CornerRadius = 5;
            BorderThickness = primary ? 0 : 1;
            BorderColour = AimModPalette.Border;
            Children = new Drawable[]
            {
                background = new Box { RelativeSizeAxes = Axes.Both, Colour = normalColour },
                new FillFlowContainer
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Horizontal,
                    Spacing = new(7),
                    Children = new Drawable[]
                    {
                        new SpriteIcon { Icon = icon, Size = new(12), Colour = AimModPalette.Text },
                        new SpriteText { Text = label, Font = new FontUsage(size: 11, weight: "SemiBold"), Colour = AimModPalette.Text },
                    },
                },
            };
            Alpha = action is null ? 0.48f : 1;
        }
        protected override bool OnClick(ClickEvent e) { action?.Invoke(); return true; }
        protected override bool OnHover(HoverEvent e) { if (action is not null) background.FadeColour(AimModPalette.PanelHover, 100); return true; }
        protected override void OnHoverLost(HoverLostEvent e) { background.FadeColour(normalColour, 100); base.OnHoverLost(e); }
    }

    private sealed partial class EmptyState : CompositeDrawable
    {
        public EmptyState(IconUsage icon, string title, string detail)
        {
            RelativeSizeAxes = Axes.X;
            Height = 180;
            InternalChild = new FillFlowContainer
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
                    new SpriteText { Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre, Text = detail, Font = new FontUsage(size: 11), Colour = AimModPalette.Muted },
                },
            };
        }
    }

    private enum BpmFilter
    {
        Any,
        Below160,
        From160To200,
        Above200,
    }

    private enum LengthFilter
    {
        Any,
        Short,
        Medium,
        Long,
    }

    private enum PlayedFilter
    {
        Everything,
        Played,
        Unplayed,
    }

    private sealed partial class PrettyDropdown<T> : OsuDropdown<T>
        where T : struct, Enum
    {
        private readonly Func<T, string> formatter;

        public PrettyDropdown(Func<T, string> formatter)
        {
            this.formatter = formatter;
        }

        protected override LocalisableString GenerateItemText(T item) => formatter(item);
    }

    private static string formatDuration(double milliseconds)
    {
        TimeSpan duration = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";
    }

    private static Drawable filterLabel(string value, float x) => new SpriteText
    {
        Text = value,
        Position = new(x, 40),
        Font = new FontUsage(size: 8, weight: "Bold"),
        Colour = AimModPalette.Cyan,
    };

    private static Container filterSurface(float x, float y, float width, float height, Anchor anchor = Anchor.TopLeft) => new()
    {
        Anchor = anchor,
        Origin = anchor,
        Position = new(x, y),
        Size = new(width, height),
        Masking = true,
        CornerRadius = 4,
        BorderThickness = 1,
        BorderColour = AimModPalette.Border,
        Depth = 5,
        Child = new Box
        {
            RelativeSizeAxes = Axes.Both,
            Colour = AimModPalette.Panel,
        },
    };

    private static string formatSort(LocalLibrarySort value) => value switch
    {
        LocalLibrarySort.Title => "Title A-Z",
        LocalLibrarySort.StarRating => "Highest stars",
        _ => "Recently added",
    };

    private static string formatBpm(BpmFilter value) => value switch
    {
        BpmFilter.Below160 => "Below 160 BPM",
        BpmFilter.From160To200 => "160-200 BPM",
        BpmFilter.Above200 => "Above 200 BPM",
        _ => "Any BPM",
    };

    private static string formatLength(LengthFilter value) => value switch
    {
        LengthFilter.Short => "Under 2 min",
        LengthFilter.Medium => "2-4 min",
        LengthFilter.Long => "Over 4 min",
        _ => "Any length",
    };

    private static string formatPlayed(PlayedFilter value) => value switch
    {
        PlayedFilter.Played => "Played",
        PlayedFilter.Unplayed => "Unplayed",
        _ => "All maps",
    };

    private static void openInOsu(int beatmapId)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"osu://b/{beatmapId}") { UseShellExecute = true });
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"[AimMod] Could not open beatmap {beatmapId} in osu!: {error.Message}");
        }
    }

    private static string relativeDate(DateTimeOffset date)
    {
        int days = Math.Max(0, (int)(DateTimeOffset.Now - date).TotalDays);
        return days switch { 0 => "today", 1 => "yesterday", < 30 => $"{days} days ago", _ => date.ToString("yyyy-MM-dd") };
    }
}
