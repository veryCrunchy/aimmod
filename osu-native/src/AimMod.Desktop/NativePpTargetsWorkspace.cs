using System.Diagnostics;
using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Desktop.ScoreHistory;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Framework.Threading;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop;

public partial class NativePpTargetsWorkspace : CompositeDrawable
{
    private const int catalog_limit = 50;
    private const int local_set_limit = 1_000;

    private readonly ILocalLibrarySource source;
    private readonly ILocalLibrarySourceChanged? sourceChanges;
    private readonly Func<IOfficialBeatmapDiscoveryClient?> client;
    private readonly Func<OnlineBeatmapImportService?> importer;
    private readonly Func<IPpTargetExactCalculationService?> exactCalculator;
    private readonly Func<ILocalScorePpHydrationService?> localPpHydrator;
    private readonly Func<OfficialOsuApiClient?> officialApi;
    private readonly Func<IAccountScoreHistoryService?> accountHistory;
    private readonly PpTargetWorkspaceCache? workspaceCache;
    private readonly OsuTextBox search;
    private readonly TruncatingSpriteText status;
    private readonly TruncatingSpriteText profileSummary;
    private readonly TruncatingSpriteText resultCount;
    private readonly FillFlowContainer<Drawable> results;
    private readonly AimModLoadingOverlay loadingOverlay;
    private readonly Container refreshProgress;
    private readonly Box refreshProgressFill;
    private readonly SpriteText refreshText;
    private readonly Container filterHeader;
    private readonly Container filterBand;
    private readonly Container searchGroup;
    private readonly Container categoryGroup;
    private readonly Container lengthGroup;
    private readonly Container sortGroup;
    private readonly Container resultViewport;
    private readonly OsuScrollContainer resultScroll;
    private readonly PpTargetWorkspaceState workspaceState;
    private readonly RangeSlider starSlider;
    private readonly RangeSlider expectedPpSlider;
    private readonly RangeSlider maximumPpSlider;
    private readonly PpTargetDropdown<OfficialBeatmapCategory> categoryDropdown;
    private readonly PpTargetDropdown<TargetLength> lengthDropdown;
    private readonly PpTargetDropdown<TargetSort> sortDropdown;
    private readonly BindableDouble minimumStars = new(0) { MinValue = 0, MaxValue = 10, Default = 0 };
    private readonly BindableDouble maximumStars = new(10) { MinValue = 0, MaxValue = 10, Default = 10 };
    private readonly BindableDouble minimumExpectedPp = new(0) { MinValue = 0, MaxValue = 1_000, Default = 0 };
    private readonly BindableDouble maximumExpectedPp = new(1_000) { MinValue = 0, MaxValue = 1_000, Default = 1_000 };
    private readonly BindableDouble minimumMaximumPp = new(0) { MinValue = 0, MaxValue = 1_000, Default = 0 };
    private readonly BindableDouble maximumMaximumPp = new(1_000) { MinValue = 0, MaxValue = 1_000, Default = 1_000 };
    private readonly Bindable<OfficialBeatmapCategory> category = new(OfficialBeatmapCategory.Ranked);
    private readonly Bindable<TargetLength> length = new(TargetLength.Any);
    private readonly Bindable<TargetSort> sort = new(TargetSort.BestFit);

    private CancellationTokenSource? profileRefresh;
    private CancellationTokenSource? catalogSearch;
    private CancellationTokenSource? exactCalculation;
    private ScheduledDelegate? scheduledSearch;
    private PpTargetPreferenceProfile profile = PpTargetPreferenceProfile.Empty;
    private IReadOnlyList<OfficialBeatmapSet> catalog = Array.Empty<OfficialBeatmapSet>();
    private IReadOnlyList<LocalBeatmapSet> localSets = Array.Empty<LocalBeatmapSet>();
    private IReadOnlyDictionary<int, PpTargetEstimate> exactEstimates = new Dictionary<int, PpTargetEstimate>();
    private Dictionary<int, OfficialBeatmapSet> setsById = new();
    private int connectionAttempts;
    private int onlineBestCount;
    private string scoreDataStatus = string.Empty;
    private bool hasVisibleSnapshot;
    private bool suppressFilterEvents;

    public NativePpTargetsWorkspace(
        ILocalLibrarySource source,
        Func<IOfficialBeatmapDiscoveryClient?> client,
        Func<OnlineBeatmapImportService?> importer,
        Func<IPpTargetExactCalculationService?>? exactCalculator = null,
        Func<ILocalScorePpHydrationService?>? localPpHydrator = null,
        Func<OfficialOsuApiClient?>? officialApi = null,
        PpTargetWorkspaceCache? workspaceCache = null,
        Func<IAccountScoreHistoryService?>? accountHistory = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.importer = importer ?? throw new ArgumentNullException(nameof(importer));
        this.exactCalculator = exactCalculator ?? (() => null);
        this.localPpHydrator = localPpHydrator ?? (() => null);
        this.officialApi = officialApi ?? (() => null);
        this.workspaceCache = workspaceCache;
        this.accountHistory = accountHistory ?? (() => null);
        sourceChanges = source as ILocalLibrarySourceChanged;
        if (sourceChanges is not null)
            sourceChanges.SourceChanged += sourceChanged;

        RelativeSizeAxes = Axes.Both;
        InternalChildren = new Drawable[]
        {
            filterHeader = new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = 270,
                Depth = -20,
                Children = new Drawable[]
                {
                    new AimModSectionHeader(
                        "PP targets",
                        "Maps ranked against your local osu!standard history, preferred difficulty, and demonstrated PP range.",
                        "personal map finder"),
                    profileSummary = truncatingText("Building your preference profile...", 12, AimModPalette.Muted).With(drawable => drawable.Y = 65),
                    new ClickableContainer
                    {
                        Anchor = Anchor.TopRight,
                        Origin = Anchor.TopRight,
                        Position = new(0, 55),
                        Size = new(112, 30),
                        Action = reloadProfile,
                        Masking = true,
                        CornerRadius = AimModVisualStyle.ControlRadius,
                        Children = new Drawable[]
                        {
                            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                            new SpriteIcon
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Position = new(13, 0),
                                Size = new(12),
                                Icon = FontAwesome.Solid.Sync,
                                Colour = AimModPalette.Cyan,
                            },
                            refreshText = text("Refresh", 10, AimModPalette.Text, "SemiBold").With(drawable =>
                            {
                                drawable.Anchor = Anchor.CentreLeft;
                                drawable.Origin = Anchor.CentreLeft;
                                drawable.X = 34;
                            }),
                        },
                    },
                    refreshProgress = new Container
                    {
                        Position = new(0, 84),
                        RelativeSizeAxes = Axes.X,
                        Height = 3,
                        Alpha = 0,
                        Masking = true,
                        CornerRadius = 1.5f,
                        Children = new Drawable[]
                        {
                            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                            refreshProgressFill = new Box { RelativeSizeAxes = Axes.Both, Width = 0, Colour = AimModPalette.Pink },
                        },
                    },
                    filterBand = new Container
                    {
                        Position = new(0, 96),
                        RelativeSizeAxes = Axes.X,
                        Height = 112,
                        Children = new Drawable[]
                        {
                            new Container
                            {
                                RelativeSizeAxes = Axes.Both,
                                Masking = true,
                                CornerRadius = AimModVisualStyle.ControlRadius,
                                Children = new Drawable[]
                                {
                                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                                    new Box { RelativeSizeAxes = Axes.X, Height = 2, Colour = AimModPalette.Pink },
                                },
                            },
                            searchGroup = new Container
                            {
                                Children = new Drawable[]
                                {
                                    filterLabel("FIND A MAP"),
                                    search = new OsuTextBox
                                    {
                                        Position = new(0, 18),
                                        RelativeSizeAxes = Axes.X,
                                        Height = AimModVisualStyle.CompactControlHeight,
                                        PlaceholderText = "Title, artist, mapper, or source",
                                    },
                                },
                            },
                            starSlider = new RangeSlider
                            {
                                Label = "Star rating",
                                LowerBound = minimumStars,
                                UpperBound = maximumStars,
                                DefaultStringLowerBound = "0",
                                DefaultStringUpperBound = "10+",
                                TooltipSuffix = "stars",
                                NubWidth = 28,
                            },
                            expectedPpSlider = new RangeSlider
                            {
                                Label = "Expected PP",
                                LowerBound = minimumExpectedPp,
                                UpperBound = maximumExpectedPp,
                                DefaultStringLowerBound = "0",
                                DefaultStringUpperBound = "1000+",
                                TooltipSuffix = "pp",
                                NubWidth = 28,
                            },
                            maximumPpSlider = new RangeSlider
                            {
                                Label = "Realistic max PP",
                                LowerBound = minimumMaximumPp,
                                UpperBound = maximumMaximumPp,
                                DefaultStringLowerBound = "0",
                                DefaultStringUpperBound = "1000+",
                                TooltipSuffix = "pp",
                                NubWidth = 28,
                            },
                            categoryGroup = dropdownGroup("MAP STATUS", categoryDropdown = new PpTargetDropdown<OfficialBeatmapCategory>(CategoryLabel)
                            {
                                Items = new[] { OfficialBeatmapCategory.Ranked, OfficialBeatmapCategory.Loved, OfficialBeatmapCategory.Pending, OfficialBeatmapCategory.Any },
                                Current = category,
                            }),
                            lengthGroup = dropdownGroup("MAP LENGTH", lengthDropdown = new PpTargetDropdown<TargetLength>(LengthLabel)
                            {
                                Items = Enum.GetValues<TargetLength>(),
                                Current = length,
                            }),
                            sortGroup = dropdownGroup("SORT RESULTS", sortDropdown = new PpTargetDropdown<TargetSort>(SortLabel)
                            {
                                Items = Enum.GetValues<TargetSort>(),
                                Current = sort,
                            }),
                        },
                    },
                    status = truncatingText("Loading local history...", 10, AimModPalette.Muted, "SemiBold").With(drawable => drawable.Position = new(0, 245)),
                    resultCount = truncatingText(string.Empty, 11, AimModPalette.Muted, "SemiBold").With(drawable =>
                    {
                        drawable.Anchor = Anchor.TopRight;
                        drawable.Origin = Anchor.TopRight;
                        drawable.Position = new(0, 245);
                    }),
                },
            },
            resultViewport = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = 245 },
                Masking = true,
                Depth = 10,
                Children = new Drawable[]
                {
                    resultScroll = new OsuScrollContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        Child = results = new FillFlowContainer<Drawable>
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Spacing = new(AimModVisualStyle.RelatedSpacing),
                            Padding = new MarginPadding { Bottom = 32 },
                        },
                    },
                    workspaceState = new PpTargetWorkspaceState(),
                },
            },
            loadingOverlay = new AimModLoadingOverlay(),
        };
    }

    protected override void Update()
    {
        base.Update();

        float width = Math.Max(640, DrawWidth);
        bool compact = width < 1_120;
        float headerHeight = compact ? 310 : 250;
        filterHeader.Height = headerHeight;
        resultViewport.Padding = new MarginPadding { Top = headerHeight + 7 };
        workspaceState.Y = compact ? -28 : 0;

        if (compact)
        {
            float halfWidth = (width - 18) / 2;
            filterBand.Height = 174;
            placeGroup(searchGroup, 12, 8, halfWidth - 12, 54);
            placeSlider(starSlider, width - halfWidth + 6, 3, halfWidth - 18);
            placeSlider(expectedPpSlider, 12, 61, halfWidth - 24);
            placeSlider(maximumPpSlider, width - halfWidth + 6, 61, halfWidth - 18);

            float dropdownWidth = (width - 48) / 3;
            placeGroup(categoryGroup, 12, 119, dropdownWidth, 48);
            placeGroup(lengthGroup, 24 + dropdownWidth, 119, dropdownWidth, 48);
            placeGroup(sortGroup, 36 + dropdownWidth * 2, 119, dropdownWidth, 48);
            status.Position = new(0, 284);
            resultCount.Position = new(0, 284);
        }
        else
        {
            float searchWidth = Math.Clamp(width * 0.22f, 250, 310);
            float sliderWidth = Math.Max(190, (width - searchWidth - 78) / 3);
            float starX = 30 + searchWidth;
            float expectedX = starX + sliderWidth + 18;
            float maximumX = expectedX + sliderWidth + 18;
            filterBand.Height = 112;
            placeGroup(searchGroup, 12, 8, searchWidth, 54);
            placeSlider(starSlider, starX, 3, sliderWidth);
            placeSlider(expectedPpSlider, expectedX, 3, sliderWidth);
            placeSlider(maximumPpSlider, maximumX, 3, sliderWidth);

            float dropdownWidth = Math.Clamp((width - 48) / 3, 210, 320);
            placeGroup(categoryGroup, 12, 62, dropdownWidth, 44);
            placeGroup(lengthGroup, (width - dropdownWidth) / 2, 62, dropdownWidth, 44);
            placeGroup(sortGroup, width - dropdownWidth - 12, 62, dropdownWidth, 44);
            status.Position = new(0, 224);
            resultCount.Position = new(0, 224);
        }

        status.MaxWidth = width * 0.62f;
        resultCount.MaxWidth = width * 0.34f;
        profileSummary.MaxWidth = Math.Max(180, width - 140);
    }

    private static void placeSlider(RangeSlider slider, float x, float y, float width)
    {
        slider.Anchor = Anchor.TopLeft;
        slider.Origin = Anchor.TopLeft;
        slider.Position = new(x, y);
        slider.Size = new(width, 58);
    }

    private static void placeGroup(Container group, float x, float y, float width, float height)
    {
        group.Position = new(x, y);
        group.Size = new(width, height);
    }

    private static SpriteText filterLabel(string value) => text(value, 8, AimModPalette.Cyan, "Bold");

    private static Container dropdownGroup(string label, Drawable dropdown)
    {
        dropdown.Position = new(0, 15);
        dropdown.RelativeSizeAxes = Axes.X;
        dropdown.Width = 1;
        return new Container
        {
            Children = new[]
            {
                filterLabel(label),
                dropdown,
            },
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        PpTargetWorkspaceSnapshot? snapshot = workspaceCache?.Load();
        if (snapshot is not null)
            applySnapshot(snapshot);

        search.OnCommit += (_, _) => startCatalogSearch();
        minimumStars.BindValueChanged(_ => filterChanged(scheduleCatalogSearch));
        maximumStars.BindValueChanged(_ => filterChanged(scheduleCatalogSearch));
        minimumExpectedPp.BindValueChanged(_ => filterChanged(renderResults));
        maximumExpectedPp.BindValueChanged(_ => filterChanged(renderResults));
        minimumMaximumPp.BindValueChanged(_ => filterChanged(renderResults));
        maximumMaximumPp.BindValueChanged(_ => filterChanged(renderResults));
        category.BindValueChanged(_ => filterChanged(startCatalogSearch));
        length.BindValueChanged(_ => renderResults());
        sort.BindValueChanged(_ => renderResults());

        if (snapshot is null)
            reloadProfile();
        else if (!workspaceCache!.IsFresh(snapshot))
            reloadProfile();
        else
            status.Text = $"Ready from cache  /  updated {relativeAge(snapshot.CachedAt)}";
    }

    private void filterChanged(Action action)
    {
        if (!suppressFilterEvents)
            action();
    }

    private void applySnapshot(PpTargetWorkspaceSnapshot snapshot)
    {
        suppressFilterEvents = true;
        profile = snapshot.Profile ?? PpTargetPreferenceProfile.Empty;
        localSets = snapshot.LocalSets ?? [];
        catalog = snapshot.Catalog ?? [];
        exactEstimates = snapshot.ExactEstimates ?? new Dictionary<int, PpTargetEstimate>();
        onlineBestCount = snapshot.OnlineBestCount;
        scoreDataStatus = snapshot.ScoreDataStatus ?? string.Empty;
        search.Text = snapshot.SearchText ?? string.Empty;
        minimumStars.Value = Math.Clamp(snapshot.MinimumStars, 0, 10);
        maximumStars.Value = Math.Clamp(snapshot.MaximumStars, 0, 10);
        category.Value = snapshot.Category;
        suppressFilterEvents = false;
        setsById = catalog.GroupBy(set => set.BeatmapSetId).ToDictionary(group => group.Key, group => group.First());
        hasVisibleSnapshot = catalog.Count > 0;
        updateProfileSummary();
        renderResults();
    }

    private void reloadProfile()
    {
        replaceToken(ref profileRefresh);
        if (hasVisibleSnapshot)
            showRefresh("Refreshing score history", 0, 0);
        else
        {
            status.Text = "Loading local history...";
            loadingOverlay.ShowLoading("Building your PP profile", "Reading local osu!standard scores");
        }
        _ = loadProfileAsync(profileRefresh!.Token);
    }

    private async Task loadProfileAsync(CancellationToken cancellationToken)
    {
        try
        {
            StatisticsHistoryLoadResult history = await StatisticsHistoryLoader.LoadAsync(source, cancellationToken).ConfigureAwait(false);
            LocalScorePpHydrationResult? hydration = null;
            if (localPpHydrator() is { } hydrator)
            {
                if (!IsDisposed)
                    Schedule(() => showRefresh($"Calculating {history.Runs.Count:N0} local scores", 0, history.Runs.Count));
                var progress = new Progress<LocalScorePpHydrationProgress>(value =>
                {
                    if (!IsDisposed)
                        Schedule(() => showRefresh($"Calculating local performance {value.Completed:N0}/{value.Total:N0}", value.Completed, value.Total));
                });
                hydration = await hydrator.HydrateAsync(history.Runs, cancellationToken, progress).ConfigureAwait(false);
            }
            IReadOnlyList<LocalBeatmapSet> loadedSets = await loadLocalSets(cancellationToken).ConfigureAwait(false);
            if (!IsDisposed)
                Schedule(() => showRefresh("Refreshing submitted best scores", 0, 0));
            OnlineAccountScoreHistoryResult? online = await loadOnlineScores(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<LocalReplay> runs = ScoreHistoryMerger.MergeAsLocalReplays(
                hydration?.Runs ?? history.Runs,
                online?.Scores ?? []);
            PpTargetPreferenceProfile next = PpTargetPreferenceProfiler.Build(runs, loadedSets);
            if (!IsDisposed)
                Schedule(() => applyProfile(next, loadedSets, hydration, online));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!IsDisposed)
                Schedule(() =>
                {
                    loadingOverlay.HideLoading();
                    hideRefresh();
                    status.Text = $"Could not build the PP profile: {error.Message}";
                    if (!hasVisibleSnapshot)
                        workspaceState.ShowState(FontAwesome.Solid.ExclamationTriangle, "PP profile unavailable", "Refresh to try loading your local and submitted score history again.");
                });
        }
    }

    private async Task<OnlineAccountScoreHistoryResult?> loadOnlineScores(CancellationToken cancellationToken)
    {
        IAccountScoreHistoryService? service = accountHistory();
        if (service is null && officialApi() is { } api)
            service = new OfficialAccountScoreHistoryService(() => api);
        if (service is null)
            return null;
        return await service.FetchAccountAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<LocalBeatmapSet>> loadLocalSets(CancellationToken cancellationToken)
    {
        var sets = new List<LocalBeatmapSet>();
        int offset = 0;
        while (sets.Count < local_set_limit)
        {
            LocalLibraryPage<LocalBeatmapSet> page = await source.SearchBeatmapSetsAsync(new LocalLibraryQuery(
                RulesetShortName: "osu",
                Sort: LocalLibrarySort.RecentlyPlayed,
                Offset: offset,
                Limit: Math.Min(200, local_set_limit - sets.Count)), cancellationToken).ConfigureAwait(false);
            sets.AddRange(page.Items);
            if (!page.HasMore || page.Items.Count == 0)
                break;
            offset += page.Items.Count;
        }
        return sets.DistinctBy(set => set.SetId).ToArray();
    }

    private void applyProfile(
        PpTargetPreferenceProfile next,
        IReadOnlyList<LocalBeatmapSet> loadedSets,
        LocalScorePpHydrationResult? hydration,
        OnlineAccountScoreHistoryResult? online)
    {
        profile = next;
        localSets = loadedSets;
        exactEstimates = new Dictionary<int, PpTargetEstimate>();
        if (next.PreferredStarRange is { } stars)
        {
            suppressFilterEvents = true;
            minimumStars.Value = Math.Clamp(Math.Floor((stars.Minimum - 0.5) * 10) / 10, 0, 10);
            maximumStars.Value = Math.Clamp(Math.Ceiling((stars.Maximum + 0.8) * 10) / 10, 0, 10);
            suppressFilterEvents = false;
        }
        onlineBestCount = online?.Scores.Count ?? 0;
        updateProfileSummary();
        if (online is not null && !online.BestCoverage.IsSuccess && !online.RecentCoverage.IsSuccess)
            scoreDataStatus = onlineFailureMessage(online.BestCoverage.Status);
        else if (hydration is { UnavailableCount: > 0 })
            scoreDataStatus = $"{hydration.UnavailableCount:N0} local score{(hydration.UnavailableCount == 1 ? string.Empty : "s")} need complete beatmap or judgement data.";
        else
            scoreDataStatus = string.Empty;
        loadingOverlay.HideLoading();
        startCatalogSearch();
    }

    private void scheduleCatalogSearch()
    {
        scheduledSearch?.Cancel();
        scheduledSearch = Scheduler.AddDelayed(startCatalogSearch, 250);
    }

    private void startCatalogSearch()
    {
        scheduledSearch?.Cancel();
        scheduledSearch = null;
        replaceToken(ref catalogSearch);
        cancelToken(ref exactCalculation);
        IOfficialBeatmapDiscoveryClient? currentClient = client();
        if (currentClient is null)
        {
            connectionAttempts++;
            status.Text = connectionAttempts < 10 ? "Connecting to osu!lazer..." : "A signed-in osu!lazer session is required for map suggestions.";
            showRefresh("Waiting for the signed-in osu! session", 0, 0);
            if (!hasVisibleSnapshot)
            {
                workspaceState.ShowState(
                    connectionAttempts < 10 ? FontAwesome.Solid.Link : FontAwesome.Solid.SignInAlt,
                    connectionAttempts < 10 ? "Connecting to osu!" : "Sign in to osu!lazer",
                    connectionAttempts < 10
                        ? "Waiting for your osu!lazer session before searching for beatmaps."
                        : "Open osu!lazer and sign in, then refresh to build your recommendations.");
            }
            if (connectionAttempts < 10)
                scheduledSearch = Scheduler.AddDelayed(startCatalogSearch, 1000);
            else
                hideRefresh();
            return;
        }

        connectionAttempts = 0;
        status.Text = "Searching the osu! catalog...";
        showRefresh("Searching ranked osu!standard beatmaps", 0, 0);
        if (!hasVisibleSnapshot)
            workspaceState.ShowState(FontAwesome.Solid.Search, "Finding PP targets", "Searching osu!standard beatmaps that fit your performance profile.");
        _ = searchCatalogAsync(currentClient, catalogSearch!.Token);
    }

    private async Task searchCatalogAsync(IOfficialBeatmapDiscoveryClient currentClient, CancellationToken cancellationToken)
    {
        try
        {
            OfficialBeatmapSearchResult response = await currentClient.SearchAsync(new OfficialBeatmapSearchQuery(
                search.Current.Value,
                minimumStars.Value <= 0 ? null : minimumStars.Value,
                maximumStars.Value >= 10 ? null : maximumStars.Value,
                category.Value,
                OfficialBeatmapSort.Rating,
                Limit: catalog_limit), cancellationToken).ConfigureAwait(false);
            if (!IsDisposed)
                Schedule(() => applyCatalog(response));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!IsDisposed && !cancellationToken.IsCancellationRequested)
            {
                Schedule(() =>
                {
                    status.Text = $"Could not search the osu! catalog: {error.Message}";
                    hideRefresh();
                    if (!hasVisibleSnapshot)
                        workspaceState.ShowState(FontAwesome.Solid.ExclamationTriangle, "Beatmap search unavailable", "Check your connection and refresh to search again.");
                });
            }
        }
    }

    private void applyCatalog(OfficialBeatmapSearchResult response)
    {
        if (response.Status != OfficialBeatmapRequestStatus.Success)
        {
            if (!hasVisibleSnapshot)
            {
                catalog = Array.Empty<OfficialBeatmapSet>();
                setsById.Clear();
                results.Clear();
                resultCount.Text = string.Empty;
            }
            status.Text = failureMessage(response.Status);
            hideRefresh();
            if (!hasVisibleSnapshot)
                workspaceState.ShowState(FontAwesome.Solid.ExclamationTriangle, "Suggestions unavailable", failureMessage(response.Status));
            return;
        }

        catalog = response.BeatmapSets;
        exactEstimates = new Dictionary<int, PpTargetEstimate>();
        setsById = catalog.GroupBy(set => set.BeatmapSetId).ToDictionary(group => group.Key, group => group.First());
        hasVisibleSnapshot = catalog.Count > 0;
        status.Text = profile.PpSampleCount == 0
            ? "No complete PP results are available for recommendations."
            : scoreDataStatus.Length > 0
                ? scoreDataStatus
                : "Recommendations are based on your calculated and submitted PP results.";
        renderResults();
        saveSnapshot();
        startExactCalculations();
    }

    private void startExactCalculations()
    {
        IPpTargetExactCalculationService? calculator = exactCalculator();
        if (calculator is null || profile.TypicalAccuracy is null || catalog.Count == 0)
        {
            hideRefresh();
            return;
        }

        PpTargetRankingResult ranked = PpTargetRanker.Rank(profile, catalog, new PpTargetFilters(Limit: 50));
        Dictionary<int, LocalBeatmapDifficulty> installed = localSets.SelectMany(set => set.Difficulties)
            .Where(difficulty => difficulty.OnlineId > 0 && !string.IsNullOrWhiteSpace(difficulty.BeatmapHash))
            .GroupBy(difficulty => difficulty.OnlineId)
            .ToDictionary(group => group.Key, group => group.First());
        PpTargetExactRequest[] requests = ranked.Candidates
            .Select(candidate => new PpTargetExactRequest(
                candidate.BeatmapId,
                installed.GetValueOrDefault(candidate.BeatmapId)?.BeatmapHash,
                candidate.SuggestedMods,
                profile.TypicalAccuracy.Value,
                candidate.Attainability))
            .Take(50)
            .ToArray();
        if (requests.Length == 0)
        {
            hideRefresh();
            return;
        }

        replaceToken(ref exactCalculation);
        status.Text = $"Calculating PP for {requests.Length:N0} beatmap difficulties...";
        showRefresh("Calculating beatmap PP", 0, requests.Length);
        renderResults();
        _ = calculateExactAsync(calculator, requests, exactCalculation!.Token);
    }

    private async Task calculateExactAsync(
        IPpTargetExactCalculationService calculator,
        IReadOnlyList<PpTargetExactRequest> requests,
        CancellationToken cancellationToken)
    {
        try
        {
            var progress = new Progress<PpTargetExactCalculationProgress>(value =>
            {
                if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                    Schedule(() => showRefresh($"Calculating difficulty {value.Completed:N0} of {value.Total:N0}", value.Completed, value.Total));
            });
            IReadOnlyDictionary<int, PpTargetEstimate> calculated = await calculator.CalculateAsync(requests, cancellationToken, progress).ConfigureAwait(false);
            if (!IsDisposed && !cancellationToken.IsCancellationRequested)
            {
                Schedule(() =>
                {
                    exactEstimates = calculated;
                    status.Text = calculated.Count == 0
                        ? "PP values are unavailable for these difficulties."
                        : $"PP ready for {calculated.Count:N0} beatmap difficult{(calculated.Count == 1 ? "y" : "ies")}.";
                    renderResults();
                    hideRefresh();
                    saveSnapshot();
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!IsDisposed)
                Schedule(() =>
                {
                    status.Text = $"PP calculation failed: {error.Message}";
                    hideRefresh();
                });
        }
    }

    private void renderResults()
    {
        if (results is null || catalog.Count == 0)
            return;

        (int? minimumLength, int? maximumLength) = length.Value switch
        {
            TargetLength.Short => ((int?)null, 120),
            TargetLength.Medium => (121, 240),
            TargetLength.Long => (241, (int?)null),
            _ => ((int?)null, (int?)null),
        };
        string statusFilter = PpTargetStatus.FromCategory(category.Value);
        var filters = new PpTargetFilters(
            MinimumStars: minimumStars.Value <= 0 ? null : minimumStars.Value,
            MaximumStars: maximumStars.Value >= 10 ? null : maximumStars.Value,
            MinimumExpectedPp: minimumExpectedPp.Value <= 0 ? null : minimumExpectedPp.Value,
            MaximumExpectedPp: maximumExpectedPp.Value >= 1_000 ? null : maximumExpectedPp.Value,
            MinimumRealisticMaximumPp: minimumMaximumPp.Value <= 0 ? null : minimumMaximumPp.Value,
            MaximumRealisticMaximumPp: maximumMaximumPp.Value >= 1_000 ? null : maximumMaximumPp.Value,
            MinimumLengthSeconds: minimumLength,
            MaximumLengthSeconds: maximumLength,
            Statuses: string.IsNullOrEmpty(statusFilter) ? null : new[] { statusFilter },
            Limit: 200);
        PpTargetRankingResult ranked = PpTargetRanker.Rank(profile, catalog, filters, exactEstimates);
        IEnumerable<PpTargetCandidate> ordered = sort.Value switch
        {
            TargetSort.ExpectedPp => ranked.Candidates.OrderByDescending(candidate => candidate.Estimate?.ExpectedPp).ThenByDescending(candidate => candidate.RankScore),
            TargetSort.MaximumPp => ranked.Candidates.OrderByDescending(candidate => candidate.Estimate?.RealisticMaximumPp).ThenByDescending(candidate => candidate.RankScore),
            TargetSort.Stars => ranked.Candidates.OrderBy(candidate => candidate.StarRating).ThenByDescending(candidate => candidate.RankScore),
            _ => ranked.Candidates,
        };
        PpTargetCandidate[] visibleCandidates = ordered.ToArray();

        results.Clear();
        foreach (PpTargetCandidate candidate in visibleCandidates)
        {
            if (setsById.TryGetValue(candidate.BeatmapSetId, out OfficialBeatmapSet? set))
                results.Add(new PpTargetRow(candidate, set, importSet));
        }
        if (results.Count == 0)
            workspaceState.ShowState(FontAwesome.Solid.Filter, "No matching beatmaps", "Try widening the star, PP, status, or length filters.");
        else
            workspaceState.HideState();
        int calculatedCount = visibleCandidates.Count(candidate => candidate.Estimate is not null);
        resultCount.Text = exactCalculation is not null && !exactCalculation.IsCancellationRequested
            ? $"{visibleCandidates.Length:N0} matches  /  {calculatedCount:N0} PP values ready"
            : $"{visibleCandidates.Length:N0} matching difficulties from {catalog.Count:N0} sets";
    }

    private async Task<OnlineBeatmapImportResult> importSet(OfficialBeatmapSet set)
    {
        OnlineBeatmapImportService? current = importer();
        return current is null
            ? new OnlineBeatmapImportResult(OnlineBeatmapImportStatus.SessionUnavailable, set.BeatmapSetId)
            : await current.ImportAsync(set).ConfigureAwait(false);
    }

    private void updateProfileSummary()
    {
        string confidence = profile.Confidence.ToString().ToLowerInvariant();
        string mods = profile.CommonMods.Count == 0 ? "No dominant mods" : string.Join(", ", profile.CommonMods.Take(3).Select(item => item.Value));
        profileSummary.Text = profile.ValidRunCount == 0
            ? "No score history is available for PP recommendations."
            : $"{profile.ValidRunCount:N0} plays  /  {profile.PpSampleCount:N0} PP results  /  {onlineBestCount:N0} submitted  /  {confidence} confidence  /  {mods}";
    }

    private void showRefresh(string message, int completed, int total)
    {
        refreshText.Text = "Refreshing";
        refreshProgress.Alpha = 1;
        refreshProgressFill.Width = total > 0 ? Math.Clamp((float)completed / total, 0.02f, 1) : 0.18f;
        status.Text = message;
    }

    private void hideRefresh()
    {
        refreshText.Text = "Refresh";
        refreshProgress.Alpha = 0;
        refreshProgressFill.Width = 0;
    }

    private void saveSnapshot()
    {
        if (workspaceCache is null || catalog.Count == 0)
            return;

        var snapshot = new PpTargetWorkspaceSnapshot(
            DateTimeOffset.UtcNow,
            profile,
            localSets,
            catalog,
            exactEstimates,
            onlineBestCount,
            scoreDataStatus,
            search.Current.Value,
            minimumStars.Value,
            maximumStars.Value,
            category.Value);
        _ = workspaceCache.SaveAsync(snapshot);
    }

    private static void replaceToken(ref CancellationTokenSource? source)
    {
        cancelToken(ref source);
        source = new CancellationTokenSource();
    }

    private static void cancelToken(ref CancellationTokenSource? source)
    {
        source?.Cancel();
        source?.Dispose();
        source = null;
    }

    private void sourceChanged()
    {
        if (!IsDisposed)
            Schedule(reloadProfile);
    }

    protected override void Dispose(bool isDisposing)
    {
        cancelToken(ref profileRefresh);
        cancelToken(ref catalogSearch);
        cancelToken(ref exactCalculation);
        scheduledSearch?.Cancel();
        if (sourceChanges is not null)
            sourceChanges.SourceChanged -= sourceChanged;
        base.Dispose(isDisposing);
    }

    private static string relativeAge(DateTimeOffset cachedAt)
    {
        TimeSpan age = DateTimeOffset.UtcNow - cachedAt;
        if (age < TimeSpan.FromMinutes(1))
            return "just now";
        if (age < TimeSpan.FromHours(1))
            return $"{Math.Max(1, (int)age.TotalMinutes)}m ago";
        return $"{Math.Max(1, (int)age.TotalHours)}h ago";
    }

    internal static string BeatmapLaunchUri(int beatmapId)
    {
        if (beatmapId <= 0)
            throw new ArgumentOutOfRangeException(nameof(beatmapId));
        return $"osu://b/{beatmapId}";
    }

    private static string failureMessage(OfficialBeatmapRequestStatus requestStatus) => requestStatus switch
    {
        OfficialBeatmapRequestStatus.SignedOut => "Sign in to osu!lazer to load PP target suggestions.",
        OfficialBeatmapRequestStatus.TokenExpired => "The osu! session is refreshing. Try again shortly.",
        OfficialBeatmapRequestStatus.NetworkError => "AimMod could not reach the osu! catalog.",
        OfficialBeatmapRequestStatus.ServerError => "osu! could not complete the target search.",
        _ => "A usable osu!lazer session is required for PP target suggestions.",
    };

    private static OsuBestScoresFetchStatus mapProfileStatus(OsuProfileFetchStatus status) => status switch
    {
        OsuProfileFetchStatus.SignedOut => OsuBestScoresFetchStatus.SignedOut,
        OsuProfileFetchStatus.TokenExpired => OsuBestScoresFetchStatus.TokenExpired,
        OsuProfileFetchStatus.Unauthorized => OsuBestScoresFetchStatus.Unauthorized,
        OsuProfileFetchStatus.SessionChanged => OsuBestScoresFetchStatus.SessionChanged,
        OsuProfileFetchStatus.NetworkError => OsuBestScoresFetchStatus.NetworkError,
        OsuProfileFetchStatus.ServerError => OsuBestScoresFetchStatus.ServerError,
        OsuProfileFetchStatus.InvalidResponse => OsuBestScoresFetchStatus.InvalidResponse,
        _ => OsuBestScoresFetchStatus.SessionUnavailable,
    };

    private static string onlineFailureMessage(OsuBestScoresFetchStatus requestStatus) => requestStatus switch
    {
        OsuBestScoresFetchStatus.SignedOut => "Sign in to osu!lazer to include your submitted scores.",
        OsuBestScoresFetchStatus.TokenExpired => "Open osu!lazer to refresh your submitted scores.",
        OsuBestScoresFetchStatus.NetworkError => "Submitted scores could not be refreshed. Check your connection.",
        OsuBestScoresFetchStatus.ServerError => "osu! could not return your submitted scores right now.",
        _ => "Submitted scores could not be loaded for this session.",
    };

    internal static string CategoryLabel(OfficialBeatmapCategory value) => value switch
    {
        OfficialBeatmapCategory.Ranked => "Ranked maps",
        OfficialBeatmapCategory.Loved => "Loved maps",
        OfficialBeatmapCategory.Pending => "Pending maps",
        _ => "Any status",
    };

    internal static string LengthLabel(TargetLength value) => value switch
    {
        TargetLength.Short => "Under 2 minutes",
        TargetLength.Medium => "2 to 4 minutes",
        TargetLength.Long => "Over 4 minutes",
        _ => "Any length",
    };

    internal static string SortLabel(TargetSort value) => value switch
    {
        TargetSort.ExpectedPp => "Highest expected PP",
        TargetSort.MaximumPp => "Highest realistic max",
        TargetSort.Stars => "Lowest star rating",
        _ => "Best personal fit",
    };

    private static SpriteText text(string value, float size, Colour4 colour, string weight = "Regular") => new()
    {
        Text = value,
        Font = new FontUsage(size: size, weight: weight),
        Colour = colour,
    };

    private static TruncatingSpriteText truncatingText(string value, float size, Colour4 colour, string weight = "Regular") => new()
    {
        Text = value,
        Font = new FontUsage(size: size, weight: weight),
        Colour = colour,
    };

    internal enum TargetLength
    {
        Any,
        Short,
        Medium,
        Long,
    }

    internal enum TargetSort
    {
        BestFit,
        ExpectedPp,
        MaximumPp,
        Stars,
    }

    private sealed partial class PpTargetDropdown<T> : OsuDropdown<T>
        where T : struct, Enum
    {
        private readonly Func<T, string> formatter;

        public PpTargetDropdown(Func<T, string> formatter)
        {
            this.formatter = formatter;
        }

        protected override LocalisableString GenerateItemText(T item) => formatter(item);
    }

    private sealed partial class PpTargetWorkspaceState : Container
    {
        private readonly SpriteIcon icon;
        private readonly TruncatingSpriteText title;
        private readonly TruncatingSpriteText detail;

        public PpTargetWorkspaceState()
        {
            RelativeSizeAxes = Axes.Both;
            Depth = -10;
            Alpha = 0;
            Children = new Drawable[]
            {
                icon = new SpriteIcon
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.BottomCentre,
                    Position = new(0, -24),
                    Size = new(34),
                    Colour = AimModPalette.Pink,
                },
                title = new TruncatingSpriteText
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Position = new(0, 12),
                    Font = new FontUsage(size: 21, weight: "Bold"),
                    Colour = AimModPalette.Text,
                },
                detail = new TruncatingSpriteText
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Position = new(0, 43),
                    Font = new FontUsage(size: 12, weight: "SemiBold"),
                    Colour = AimModPalette.Muted,
                },
            };
        }

        public void ShowState(IconUsage stateIcon, string heading, string description)
        {
            icon.Icon = stateIcon;
            title.Text = heading;
            detail.Text = description;
            this.FadeIn(150, Easing.OutQuint);
        }

        public void HideState() => this.FadeOut(120, Easing.OutQuint);

        protected override void Update()
        {
            base.Update();
            float maxWidth = Math.Clamp(DrawWidth - 80, 260, 560);
            title.MaxWidth = maxWidth;
            detail.MaxWidth = maxWidth;
        }
    }

    private partial class PpTargetRow : AimModInteractiveSurface
    {
        private readonly OfficialBeatmapSet set;
        private readonly Func<OfficialBeatmapSet, Task<OnlineBeatmapImportResult>> import;
        private readonly SpriteText saveText;
        private readonly Box saveBackground;
        private readonly FillFlowContainer details;
        private readonly TruncatingSpriteText title;
        private readonly TruncatingSpriteText artist;
        private readonly TruncatingSpriteText mapDetails;
        private readonly TruncatingSpriteText mechanicsDetails;
        private readonly TruncatingSpriteText confidenceDetails;
        private readonly Container artwork;
        private readonly Container expectedMetric;
        private readonly Container maximumMetric;
        private bool importing;

        public PpTargetRow(PpTargetCandidate candidate, OfficialBeatmapSet set, Func<OfficialBeatmapSet, Task<OnlineBeatmapImportResult>> import)
        {
            this.set = set;
            this.import = import;
            RelativeSizeAxes = Axes.X;
            Height = 112;
            CornerRadius = AimModVisualStyle.ControlRadius;
            BackgroundColour = AimModPalette.Panel;

            Colour4 difficultyColour = AimModVisualStyle.DifficultyColour(candidate.StarRating);
            string expected = candidate.Estimate is null ? "-" : $"{candidate.Estimate.ExpectedPp:0}";
            string maximum = candidate.Estimate is null ? "-" : $"{candidate.Estimate.RealisticMaximumPp:0}";
            bool calculated = candidate.Estimate?.Method.StartsWith("Official osu! ruleset", StringComparison.Ordinal) == true;
            string confidence = calculated ? "PP ready" : "PP pending";
            string mods = candidate.SuggestedMods.Count == 0 ? "NM" : string.Join(" + ", candidate.SuggestedMods);
            OfficialBeatmapDifficulty? difficulty = set.Difficulties.FirstOrDefault(item => item.BeatmapId == candidate.BeatmapId);
            double passRate = difficulty is { PlayCount: > 0 } ? (double)difficulty.PassCount / difficulty.PlayCount : 0;
            string combo = candidate.MaximumCombo is > 0 ? $"{candidate.MaximumCombo:N0}x" : "-";

            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Y, Width = 4, Colour = difficultyColour },
                artwork = new Container
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = 136,
                    X = 4,
                    Masking = true,
                    Child = candidate.CoverUrl is null
                        ? new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised }
                        : new AimModOnlineArtworkHost(candidate.CoverUrl),
                },
                details = new FillFlowContainer
                {
                    Position = new(158, 8),
                    Width = 450,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new(1),
                    Children = new Drawable[]
                    {
                        title = new TruncatingSpriteText { Text = candidate.Title, Font = new FontUsage(size: 15, weight: "Bold"), Colour = AimModPalette.Text, MaxWidth = 450 },
                        artist = new TruncatingSpriteText { Text = $"{candidate.Artist}  /  mapped by {candidate.Creator}", Font = new FontUsage(size: 10, weight: "SemiBold"), Colour = AimModPalette.Muted, MaxWidth = 450 },
                        mapDetails = truncatingText($"[{candidate.Difficulty}]   {candidate.StarRating:0.00}*   {candidate.Bpm:0} BPM   {formatLength(candidate.TotalLengthSeconds)}   {combo}   {mods}", 10, difficultyColour, "Bold"),
                        mechanicsDetails = truncatingText(
                            $"AR {difficulty?.ApproachRate:0.#}   OD {difficulty?.OverallDifficulty:0.#}   CS {difficulty?.CircleSize:0.#}   HP {difficulty?.DrainRate:0.#}   " +
                            $"{set.Status.ToUpperInvariant()}   {set.PlayCount:N0} plays   {(passRate > 0 ? $"{passRate:P0} pass" : "pass rate -")}",
                            9, AimModPalette.Muted, "SemiBold"),
                        confidenceDetails = truncatingText($"{candidate.PreferenceFit:P0} personal fit   /   {confidence}", 9, calculated ? AimModPalette.Success : AimModPalette.Cyan, "SemiBold"),
                    },
                },
                expectedMetric = metric("EXPECTED PP", expected, AimModPalette.Cyan, candidate.Estimate is null ? "pending" : $"at {candidate.Attainability:P0} attainability"),
                maximumMetric = metric("REALISTIC MAX", maximum, Colour4.FromHex("FFD45A"), candidate.Estimate is null ? "pending" : $"+{Math.Max(0, candidate.EstimatedAttainableGainPp ?? 0):0} gain"),
                new Container
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Margin = new MarginPadding { Right = 12 },
                    Size = new(104, 76),
                    Children = new Drawable[]
                    {
                        actionButton(FontAwesome.Solid.Play, "Open osu!", AimModPalette.Cyan, () => openInOsu(candidate.BeatmapId)),
                        actionButton(FontAwesome.Solid.Download, set.DownloadDisabled ? "Unavailable" : "Save", AimModPalette.Pink, beginImport, 41, set.DownloadDisabled,
                            out saveBackground, out saveText),
                    },
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            const float actionColumn = 128;
            float metricWidth = DrawWidth < 880 ? 88 : 106;
            bool compact = DrawWidth < 980;
            artwork.Width = compact ? 86 : 136;
            details.X = compact ? 106 : 158;
            float expectedX = DrawWidth - actionColumn - metricWidth * 2;
            expectedMetric.X = expectedX;
            expectedMetric.Width = metricWidth;
            maximumMetric.X = expectedX + metricWidth;
            maximumMetric.Width = metricWidth;
            float detailWidth = Math.Max(100, expectedX - details.X - 18);
            details.Width = detailWidth;
            title.MaxWidth = detailWidth;
            artist.MaxWidth = detailWidth;
            mapDetails.MaxWidth = detailWidth;
            mechanicsDetails.MaxWidth = detailWidth;
            confidenceDetails.MaxWidth = detailWidth;
        }

        private void beginImport()
        {
            if (importing || set.DownloadDisabled)
                return;
            importing = true;
            saveText.Text = "Saving...";
            saveBackground.Colour = AimModPalette.PanelHover;
            _ = importAsync();
        }

        private async Task importAsync()
        {
            OnlineBeatmapImportResult result = await import(set).ConfigureAwait(false);
            if (!IsDisposed)
            {
                Schedule(() =>
                {
                    importing = false;
                    saveText.Text = result.Status == OnlineBeatmapImportStatus.Success ? "Saved" : "Try again";
                    saveBackground.Colour = result.Status == OnlineBeatmapImportStatus.Success ? AimModPalette.Success : AimModPalette.Pink;
                });
            }
        }

        private static Container metric(string caption, string value, Colour4 colour, string detail) => new()
        {
            Size = new(106, 112),
            Children = new Drawable[]
            {
                text(caption, 8, AimModPalette.Muted, "Bold").With(drawable => drawable.Position = new(0, 22)),
                text(value, 23, colour, "Bold").With(drawable => drawable.Position = new(0, 38)),
                text("pp", 9, AimModPalette.Muted, "SemiBold").With(drawable => drawable.Position = new(47, 48)),
                truncatingText(detail, 8, AimModPalette.Muted).With(drawable =>
                {
                    drawable.Position = new(0, 70);
                    drawable.MaxWidth = 96;
                }),
            },
        };

        private static ClickableContainer actionButton(IconUsage icon, string label, Colour4 colour, Action action, float y = 0, bool disabled = false) =>
            actionButton(icon, label, colour, action, y, disabled, out _, out _);

        private static ClickableContainer actionButton(
            IconUsage icon,
            string label,
            Colour4 colour,
            Action action,
            float y,
            bool disabled,
            out Box background,
            out SpriteText labelText)
        {
            background = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = disabled ? AimModPalette.PanelHover : colour,
                Alpha = disabled ? 0.55f : 1,
            };
            labelText = text(label, 9, disabled ? AimModPalette.Muted : AimModPalette.Canvas, "Bold").With(drawable =>
            {
                drawable.Anchor = Anchor.CentreLeft;
                drawable.Origin = Anchor.CentreLeft;
                drawable.X = 31;
            });
            return new ClickableContainer
            {
                Position = new(0, y),
                Size = new(104, AimModVisualStyle.CompactControlHeight),
                Action = disabled ? null : action,
                Masking = true,
                CornerRadius = AimModVisualStyle.ControlRadius,
                Children = new Drawable[]
                {
                    background,
                    new SpriteIcon
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Position = new(12, 0),
                        Size = new(11),
                        Icon = icon,
                        Colour = disabled ? AimModPalette.Muted : AimModPalette.Canvas,
                    },
                    labelText,
                },
            };
        }

        private static void openInOsu(int beatmapId)
        {
            if (beatmapId <= 0)
                return;

            try
            {
                Process.Start(new ProcessStartInfo(BeatmapLaunchUri(beatmapId)) { UseShellExecute = true });
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Console.Error.WriteLine($"[AimMod] Could not open beatmap {beatmapId} in osu!: {error.Message}");
            }
        }

        private static string formatLength(int seconds) => $"{Math.Max(0, seconds) / 60}:{Math.Max(0, seconds) % 60:00}";
    }
}
