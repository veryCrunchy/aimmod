using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Desktop.ScoreHistory;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Framework.Threading;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop;

public partial class NativeBeatmapDiscoveryScreen : CompositeDrawable
{
    private readonly ILocalLibrarySource localLibrary;
    private readonly Func<IOfficialBeatmapDiscoveryClient?> client;
    private readonly Func<OnlineBeatmapImportService?> importer;
    private readonly Func<IPpTargetExactCalculationService?> exactCalculator;
    private readonly Func<IAccountScoreHistoryService?> onlineScoreHistory;
    private readonly Func<int, CancellationToken, Task>? openBeatmap;
    private readonly Action<string>? openPractice;
    private readonly Container page = null!;
    private readonly Container tabBar = null!;
    private readonly AimModSectionHeader workspaceHeader = null!;
    private readonly AimModTabControl<BeatmapDiscoveryTab> tabs = null!;
    private readonly Bindable<BeatmapDiscoveryTab> currentTab = new(BeatmapDiscoveryTab.Installed);
    private NativeInstalledBeatmapBrowser? installedScreen;
    private NativeOfficialBeatmapSearchScreen? onlineScreen;
    private Drawable? activeScreen;

    public NativeBeatmapDiscoveryScreen(
        ILocalLibrarySource localLibrary,
        Func<IOfficialBeatmapDiscoveryClient?> client,
        Func<OnlineBeatmapImportService?> importer,
        Func<IPpTargetExactCalculationService?>? exactCalculator = null,
        Func<IAccountScoreHistoryService?>? onlineScoreHistory = null,
        Func<int, CancellationToken, Task>? openBeatmap = null,
        Action<string>? openPractice = null)
    {
        this.localLibrary = localLibrary ?? throw new ArgumentNullException(nameof(localLibrary));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.importer = importer ?? throw new ArgumentNullException(nameof(importer));
        this.exactCalculator = exactCalculator ?? (() => null);
        this.onlineScoreHistory = onlineScoreHistory ?? (() => null);
        this.openBeatmap = openBeatmap;
        this.openPractice = openPractice;
        RelativeSizeAxes = Axes.Both;

        InternalChildren = new Drawable[]
        {
            workspaceHeader = new AimModSectionHeader(
                "Beatmaps",
                "Your installed maps and the official osu! catalog.")
            {
                Depth = -110,
            },
            page = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = 120 },
                Masking = true,
                Depth = 0,
            },
            tabBar = new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = 116,
                Depth = -100,
                Children = new Drawable[]
                {
                    tabs = new AimModTabControl<BeatmapDiscoveryTab>
                    {
                        Anchor = Anchor.TopLeft,
                        Origin = Anchor.TopLeft,
                        Position = new(0, 72),
                        Height = AimModVisualStyle.ControlHeight,

                        Current = currentTab,
                    },
                },
            },
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        currentTab.BindValueChanged(tab => showTab(tab.NewValue), true);
    }

    public void OpenSet(int setId)
    {
        SelectTab(BeatmapDiscoveryTab.Online);
        onlineScreen!.OpenSet(setId);
    }

    internal void SelectTab(BeatmapDiscoveryTab tab)
    {
        currentTab.Value = tab;
        showTab(tab);
    }

    internal BeatmapDiscoveryTab GetCurrentTabForTesting() => currentTab.Value;

    internal Type? GetActiveScreenTypeForTesting() => activeScreen?.GetType();

    internal Drawable? GetActiveScreenForTesting() => activeScreen;

    private void showTab(BeatmapDiscoveryTab tab)
    {
        if (tab == BeatmapDiscoveryTab.Installed)
        {
            if (installedScreen is null)
            {
                installedScreen = new NativeInstalledBeatmapBrowser(localLibrary, exactCalculator, onlineScoreHistory, openBeatmap, openPractice) { RelativeSizeAxes = Axes.Both };
                page.Add(installedScreen);
            }
            setActiveScreen(installedScreen, onlineScreen);
            return;
        }

        if (onlineScreen is null)
        {
            onlineScreen = new NativeOfficialBeatmapSearchScreen(client, importer, localLibrary, openBeatmap) { RelativeSizeAxes = Axes.Both };
            page.Add(onlineScreen);
        }
        setActiveScreen(onlineScreen, installedScreen);
    }

    private void setActiveScreen(Drawable shown, Drawable? hidden)
    {
        shown.Alpha = 1;
        shown.AlwaysPresent = true;
        if (hidden is not null)
        {
            hidden.Alpha = 0;
            hidden.AlwaysPresent = false;
        }
        activeScreen = shown;
    }

    internal enum BeatmapDiscoveryTab
    {
        Installed,
        Online,
    }

}

public partial class NativeOfficialBeatmapSearchScreen : CompositeDrawable
{
    private readonly ILocalLibrarySource? localLibrary;
    private readonly Func<int, CancellationToken, Task>? openBeatmap;
    private const int result_limit = 24;
    private const float content_inset = 12;
    private const int connection_attempt_limit = 10;
    private const int session_retry_limit = 3;
    private const double max_rate_limit_auto_retry_ms = 30_000;
    private static readonly TimeSpan installed_cache_lifetime = TimeSpan.FromMinutes(2);
    private readonly KeyedFlow<int, OfficialBeatmapSet, OnlineBeatmapSetBlock> resultBlocks;
    private readonly AimModInlineStatus searchStatus;
    private AimModLayout.ChangeTracker<float> layoutTracker;
    private int sessionRetries;
    private bool retryWhenVisible;
    private (DateTime LoadedAt, HashSet<int> Difficulties)? installedCache;

    private readonly Func<IOfficialBeatmapDiscoveryClient?> client;
    private readonly Func<OnlineBeatmapImportService?> importer;
    private readonly AimModSearchBox searchBox;
    private readonly TruncatingSpriteText resultStatus;
    private readonly FillFlowContainer<Drawable> results;
    private bool searchLoaded;
    private readonly Container filterBand;
    private readonly AimModResetButton resetFilters;
    private readonly Container resultViewport;
    private readonly Container inspectorRail;
    private readonly OnlineSetInspector inspector;
    private OnlineBeatmapSetBlock? selectedBlock;
    private bool sideInspector;
    internal const float SideInspectorWidth = 900;
    private readonly AimModStarRatingFilter starSlider;
    private readonly osu.Game.Graphics.UserInterfaceV2.ShearedDropdown<OfficialBeatmapCategory> categoryDropdown;
    private readonly osu.Game.Graphics.UserInterfaceV2.ShearedDropdown<OfficialBeatmapSort> sortDropdown;
    private readonly BindableDouble minimumStars = new(0) { MinValue = 0, MaxValue = 10, Default = 0 };
    private readonly BindableDouble maximumStars = new(10) { MinValue = 0, MaxValue = 10, Default = 10 };
    private readonly Bindable<OfficialBeatmapCategory> category = new(OfficialBeatmapCategory.Any);
    private readonly Bindable<OfficialBeatmapSort> sort = new(OfficialBeatmapSort.Relevance);
    private CancellationTokenSource? requestCancellation;
    private ScheduledDelegate? scheduledSearch;
    private int connectionAttempts;
    private int? selectedSetId;
    internal int? SelectedSetIdForTesting => selectedSetId;

    public void OpenSet(int setId)
    {
        selectedSetId = setId;
        connectionAttempts = 0;
        sessionRetries = 0;
        if (IsLoaded)
            startSearch();
    }

    public NativeOfficialBeatmapSearchScreen(
        Func<IOfficialBeatmapDiscoveryClient?> client,
        Func<OnlineBeatmapImportService?> importer, ILocalLibrarySource? localLibrary = null,
        Func<int, CancellationToken, Task>? openBeatmap = null)
    {
        this.localLibrary = localLibrary;
        this.openBeatmap = openBeatmap;
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.importer = importer ?? throw new ArgumentNullException(nameof(importer));
        RelativeSizeAxes = Axes.Both;

        InternalChildren = new Drawable[]
        {
            // Filters render ahead of the status line and results so their menus are never covered.
            filterBand = new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = AimModVisualStyle.ControlHeight,
                Depth = -20,
                Children = new Drawable[]
                {
                    searchBox = new AimModSearchBox
                    {
                        Height = AimModVisualStyle.ControlHeight,
                        SearchHint = "Search title, artist, mapper or tag",
                    },
                    starSlider = new AimModStarRatingFilter
                    {
                        Size = new(264, AimModVisualStyle.ControlHeight),
                        LowerBound = minimumStars,
                        UpperBound = maximumStars,
                        DefaultStringLowerBound = "0",
                        DefaultStringUpperBound = "10+",
                    },
                    categoryDropdown = new BeatmapFilterDropdown<OfficialBeatmapCategory>("Status", formatCategory)
                    {
                        Items = new[] { OfficialBeatmapCategory.Any, OfficialBeatmapCategory.Ranked, OfficialBeatmapCategory.Loved, OfficialBeatmapCategory.Pending },
                        Current = category,
                    },
                    sortDropdown = new BeatmapFilterDropdown<OfficialBeatmapSort>("Sort", formatSort)
                    {
                        Items = new[] { OfficialBeatmapSort.Relevance, OfficialBeatmapSort.Updated, OfficialBeatmapSort.Plays },
                        Current = sort,
                    },
                },
            },
            resultStatus = new TruncatingSpriteText
            {
                Text = "Connecting to osu!...",
                Font = AimModVisualStyle.BodyStrongFont,
                Colour = AimModPalette.Text,
                Depth = 0,
            },
            resultViewport = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Masking = true,
                Depth = 10,
                Child = new AimModScrollContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Child = new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Spacing = new(AimModVisualStyle.RelatedSpacing),
                        Padding = new MarginPadding { Right = content_inset, Bottom = 32 },
                        Children = new Drawable[]
                        {
                            searchStatus = new AimModInlineStatus(),
                            results = new FillFlowContainer<Drawable>
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
            inspectorRail = new Container
            {
                Masking = true,
                Depth = 5,
                Alpha = 0,
                Child = inspector = new OnlineSetInspector(openBeatmap),
            },
            resetFilters = new AimModResetButton(() => {
                searchBox.Current.Value = string.Empty; minimumStars.Value = 0; maximumStars.Value = 10;
                category.Value = OfficialBeatmapCategory.Any; sort.Value = OfficialBeatmapSort.Relevance; scheduleSearch();
            }, "Reset all") { Height = 26, Width = 84, Alpha = 0 },
        };
        resultBlocks = new KeyedFlow<int, OfficialBeatmapSet, OnlineBeatmapSetBlock>(results,
            set => set.BeatmapSetId,
            set => $"{set.Status}|{set.DownloadDisabled}|{string.Join(',', set.Difficulties.Select(d => d.BeatmapId))}",
            set => new OnlineBeatmapSetBlock(set, importBeatmap, installInLazer, openBeatmap, !sideInspector && selectedSetId == set.BeatmapSetId, blockClicked));
    }

    internal static NativeBeatmapFilterLayout CalculateFilterLayout(float width) => width switch
    {
        < 640 => NativeBeatmapFilterLayout.Stacked,
        < 980 => NativeBeatmapFilterLayout.TwoColumns,
        _ => NativeBeatmapFilterLayout.Row,
    };

    protected override void Update()
    {
        base.Update();

        if (retryWhenVisible && isVisible())
        {
            retryWhenVisible = false;
            startSearch();
        }

        bool filtered = !string.IsNullOrWhiteSpace(searchBox.Current.Value) || !minimumStars.IsDefault || !maximumStars.IsDefault
                        || category.Value != OfficialBeatmapCategory.Any || sort.Value != OfficialBeatmapSort.Relevance;
        resetFilters.Alpha = filtered ? 1 : 0;

        if (!layoutTracker.Update(DrawWidth))
            return;

        float width = Math.Max(1, DrawWidth);
        const float gap = AimModVisualStyle.RelatedSpacing;
        const float row = AimModVisualStyle.ControlHeight + gap;
        NativeBeatmapFilterLayout layout = CalculateFilterLayout(width);
        float rows;

        if (layout == NativeBeatmapFilterLayout.Stacked)
        {
            // Phone-width windows stack every filter so none of them clip or overlap.
            float half = (width - gap) / 2;
            place(searchBox, 0, 0, width);
            place(starSlider, 0, row, width);
            place(categoryDropdown, 0, row * 2, half);
            place(sortDropdown, half + gap, row * 2, half);
            rows = 3;
        }
        else if (layout == NativeBeatmapFilterLayout.TwoColumns)
        {
            const float dropdownWidth = 180;
            place(searchBox, 0, 0, width);
            place(starSlider, 0, row, Math.Max(0, width - dropdownWidth * 2 - gap * 2));
            place(categoryDropdown, width - dropdownWidth * 2 - gap, row, dropdownWidth);
            place(sortDropdown, width - dropdownWidth, row, dropdownWidth);
            rows = 2;
        }
        else
        {
            const float stars_width = 264, category_width = 180, sort_width = 196;
            float searchWidth = width - stars_width - category_width - sort_width - gap * 3;
            place(searchBox, 0, 0, searchWidth);
            place(starSlider, searchWidth + gap, 0, stars_width);
            place(categoryDropdown, searchWidth + stars_width + gap * 2, 0, category_width);
            place(sortDropdown, width - sort_width, 0, sort_width);
            rows = 1;
        }

        filterBand.Height = rows * row - gap;
        resultStatus.Y = rows * row + 4;
        resetFilters.Position = new(width - resetFilters.Width, rows * row);
        resultStatus.MaxWidth = Math.Max(0, width - resetFilters.Width - 16);
        float top = rows * row + 26 + 14;
        sideInspector = width >= SideInspectorWidth;
        float railWidth = sideInspector ? Math.Clamp(width * 0.32f, 330, 470) : 0;
        resultViewport.Padding = new MarginPadding { Top = top, Right = sideInspector ? railWidth : 0 };
        inspectorRail.Position = new(width - railWidth, top);
        inspectorRail.Size = new(railWidth, Math.Max(0, DrawHeight - top));
        inspectorRail.Padding = new MarginPadding { Left = AimModVisualStyle.SectionSpacing };
        inspectorRail.Alpha = sideInspector ? 1 : 0;
        applyInspectorMode();
    }

    internal static bool UsesSideInspector(float width) => width >= SideInspectorWidth;

    private void clearResults()
    {
        resultBlocks.Clear();
        selectedBlock = null;
        inspector.ClearSelection();
    }

    private void applyInspectorMode()
    {
        foreach (OnlineBeatmapSetBlock block in resultBlocks.Rows)
            block.SetInline(!sideInspector);
        if (sideInspector && selectedBlock is null && resultBlocks.Rows.FirstOrDefault() is { } first)
            selectBlock(first);
    }

    private void blockClicked(OnlineBeatmapSetBlock block)
    {
        if (sideInspector)
            selectBlock(block);
        else
            block.Toggle();
    }

    private void selectBlock(OnlineBeatmapSetBlock block)
    {
        selectedBlock?.Card.SetSelected(false);
        selectedBlock = block;
        block.Card.SetSelected(true);
        inspector.Show(block.Set, block.Card);
    }

    private void refreshSelection()
    {
        if (selectedBlock is not null && !resultBlocks.Rows.Contains(selectedBlock))
            selectedBlock = null;
        OnlineBeatmapSetBlock? target = selectedBlock
                                        ?? (selectedSetId is int id && resultBlocks.TryGet(id, out var match) ? match : resultBlocks.Rows.FirstOrDefault());
        if (target is null)
        {
            inspector.ClearSelection();
            return;
        }
        foreach (OnlineBeatmapSetBlock block in resultBlocks.Rows)
            block.SetInline(!sideInspector);
        if (sideInspector)
            selectBlock(target);
    }

    private static void place(Drawable drawable, float x, float y, float width)
    {
        drawable.Anchor = Anchor.TopLeft;
        drawable.Origin = Anchor.TopLeft;
        drawable.Position = new(x, y);
        drawable.Width = width;
    }

    private static string formatCategory(OfficialBeatmapCategory value) => value switch
    {
        OfficialBeatmapCategory.Any => "Any status",
        _ => value.ToString(),
    };

    private static string formatSort(OfficialBeatmapSort value) => value switch
    {
        OfficialBeatmapSort.Updated => "Recently updated",
        OfficialBeatmapSort.Plays => "Most played",
        _ => "Best match",
    };

    private sealed partial class BeatmapFilterDropdown<T> : AimMod.Desktop.Coaching.BoundedShearedDropdown<T>
    {
        private readonly Func<T, string> format;

        public BeatmapFilterDropdown(string label, Func<T, string> format) : base(label)
        {
            this.format = format;
        }

        protected override LocalisableString GenerateItemText(T item) => format(item);
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        searchBox.QueryChanged += _ => { selectedSetId = null; startSearch(); };
        searchBox.MoveToResults += () => AimModInteractiveSurface.FocusFirst(results);
        minimumStars.BindValueChanged(_ => scheduleSearch());
        maximumStars.BindValueChanged(_ => scheduleSearch());
        category.BindValueChanged(_ => startSearch());
        sort.BindValueChanged(_ => startSearch());
        startSearch();
    }

    private void scheduleSearch()
    {
        selectedSetId = null;
        requestCancellation?.Cancel();
        scheduledSearch?.Cancel();
        scheduledSearch = Scheduler.AddDelayed(startSearch, 250);
    }

    private void retrySearch()
    {
        connectionAttempts = 0;
        sessionRetries = 0;
        startSearch();
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

    /// <summary>Automatic retries wait while the tab is hidden and resume when it is shown again.</summary>
    private void scheduleAutomaticRetry(double delay)
    {
        scheduledSearch?.Cancel();
        scheduledSearch = Scheduler.AddDelayed(() =>
        {
            scheduledSearch = null;
            if (isVisible())
                startSearch();
            else
                retryWhenVisible = true;
        }, delay);
    }

    private void cancelSearch()
    {
        scheduledSearch?.Cancel();
        scheduledSearch = null;
        retryWhenVisible = false;
        requestCancellation?.Cancel();
        resultStatus.Text = searchLoaded ? "Search cancelled. Showing the previous results." : "Search cancelled.";
        searchStatus.ShowMessage("Search cancelled.", retrySearch);
    }

    private void startSearch()
    {
        scheduledSearch?.Cancel();
        scheduledSearch = null;
        retryWhenVisible = false;
        requestCancellation?.Cancel();
        requestCancellation?.Dispose();
        requestCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = requestCancellation.Token;
        IOfficialBeatmapDiscoveryClient? currentClient = client();
        if (currentClient is null)
        {
            connectionAttempts++;
            if (connectionAttempts < connection_attempt_limit)
            {
                resultStatus.Text = "AimMod is still connecting to osu!...";
                searchStatus.ShowLoading("Waiting for your osu! installation to connect...", cancelSearch);
                scheduleAutomaticRetry(1000);
            }
            else
            {
                resultStatus.Text = "osu! is not connected";
                searchStatus.ShowError("AimMod could not connect to osu! for online search.",
                    "Check that osu! is installed, then retry. Installed beatmaps remain available on the Installed tab.", retrySearch);
            }
            clearResults();
            return;
        }

        connectionAttempts = 0;

        resultStatus.Text = "Searching osu!...";
        searchStatus.ShowLoading(searchLoaded ? "Updating results..." : "Searching the osu! beatmap catalog...", cancelSearch);
        _ = searchAsync(currentClient, cancellationToken);
    }

    private async Task searchAsync(IOfficialBeatmapDiscoveryClient currentClient, CancellationToken cancellationToken)
    {
        try
        {
            OfficialBeatmapSearchResult response = selectedSetId is int setId
                ? await currentClient.GetSetAsync(setId, cancellationToken).ConfigureAwait(false)
                : await currentClient.SearchAsync(new OfficialBeatmapSearchQuery(
                searchBox.Current.Value,
                minimumStars.IsDefault ? null : minimumStars.Value,
                maximumStars.IsDefault ? null : maximumStars.Value,
                category.Value,
                sort.Value,
                Limit: result_limit), cancellationToken).ConfigureAwait(false);

            if (!IsDisposed)
                Schedule(() => { if (!cancellationToken.IsCancellationRequested) applySearchResult(response); });
            if (response.Status == OfficialBeatmapRequestStatus.Success && localLibrary is not null)
                await markInstalledAsync(response.BeatmapSets, cancellationToken).ConfigureAwait(false);
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
                    if (cancellationToken.IsCancellationRequested)
                        return;
                    resultStatus.Text = searchLoaded ? "Showing the previous results" : "Search unavailable";
                    searchStatus.ShowError(error, "Searching osu!", retrySearch);
                    // Keep older results readable but clearly out of date until a retry succeeds.
                    results.FadeTo(0.45f, AimModVisualStyle.HoverTransition);
                });
            }
        }
    }

    private void applySearchResult(OfficialBeatmapSearchResult response)
    {
        searchLoaded = true;
        results.FadeIn(AimModVisualStyle.HoverTransition);
        if (response.Status != OfficialBeatmapRequestStatus.Success)
        {
            // Results from another account or an expired session are stale; do not leave them under the error.
            clearResults();
            string message = searchFailureMessage(response.Status, response.RetryAfter);
            resultStatus.Text = "Online search unavailable";
            bool sessionProblem = response.Status is OfficialBeatmapRequestStatus.SignedOut or
                OfficialBeatmapRequestStatus.TokenExpired or
                OfficialBeatmapRequestStatus.SessionUnavailable or
                OfficialBeatmapRequestStatus.SessionChanged;
            double retryDelay = 5000;
            if (response.Status == OfficialBeatmapRequestStatus.RateLimited && response.RetryAfter is DateTimeOffset retryAt)
            {
                retryDelay = Math.Max(1000, (retryAt - DateTimeOffset.UtcNow).TotalMilliseconds);
                sessionProblem = retryDelay <= max_rate_limit_auto_retry_ms;
            }

            searchStatus.ShowError(message, null, retrySearch);
            if (sessionProblem && sessionRetries++ < session_retry_limit)
                scheduleAutomaticRetry(retryDelay);
            return;
        }

        sessionRetries = 0;
        searchStatus.Dismiss();
        resultBlocks.Apply(response.BeatmapSets);
        refreshSelection();

        resultStatus.Text = response.BeatmapSets.Count switch
        {
            1 when selectedSetId is int id => $"Beatmap set {id}",
            0 => "No matching sets",
            1 => "1 set",
            var count when response.ServerTotal > count => $"{count:N0} of {response.ServerTotal:N0} sets",
            var count => $"{count:N0} sets",
        };
    }

    private async Task<OnlineBeatmapImportResult> importBeatmap(OfficialBeatmapSet set)
    {
        OnlineBeatmapImportService? currentImporter = importer();
        if (currentImporter is null)
            return new OnlineBeatmapImportResult(OnlineBeatmapImportStatus.SessionUnavailable, set.BeatmapSetId);
        return await currentImporter.ImportAsync(set).ConfigureAwait(false);
    }

    private async Task<LazerBeatmapInstallResult> installInLazer(LazerBeatmapArchive archive)
    {
        OnlineBeatmapImportService? currentImporter = importer();
        if (currentImporter is null)
            return new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LazerNotFound);
        return await currentImporter.InstallInLazerAsync(archive).ConfigureAwait(false);
    }

    private static string searchFailureMessage(OfficialBeatmapRequestStatus status, DateTimeOffset? retryAfter = null) => status switch
    {
        OfficialBeatmapRequestStatus.RateLimited => $"osu! is rate limiting searches. {OsuRateLimitText.RetryHint(retryAfter)}",
        OfficialBeatmapRequestStatus.SignedOut => "Sign in to osu!lazer to search the official beatmap catalog.",
        OfficialBeatmapRequestStatus.TokenExpired => "osu!lazer's session is refreshing. Try the search again in a moment.",
        OfficialBeatmapRequestStatus.Unauthorized => "osu! refused this inherited session. Reopen osu!lazer, then try again.",
        OfficialBeatmapRequestStatus.SessionChanged => "The active osu! account changed during this search. Search again for the new account.",
        OfficialBeatmapRequestStatus.NetworkError => "AimMod could not reach osu!. Check the connection and try again.",
        OfficialBeatmapRequestStatus.ServerError => "osu! could not complete the search. Try again shortly.",
        OfficialBeatmapRequestStatus.InvalidResponse => "osu! returned an unreadable beatmap catalog response.",
        _ => "AimMod has not inherited a usable osu!lazer session yet.",
    };

    protected override void Dispose(bool isDisposing)
    {
        requestCancellation?.Cancel();
        requestCancellation?.Dispose();
        scheduledSearch?.Cancel();
        base.Dispose(isDisposing);
    }

    private async Task markInstalledAsync(IReadOnlyList<OfficialBeatmapSet> sets, CancellationToken token)
    {
        try
        {
            // Scanning the whole local library per search is expensive; reuse a recent snapshot.
            HashSet<int> difficulties;
            if (installedCache is { } cached && DateTime.UtcNow - cached.LoadedAt < installed_cache_lifetime)
                difficulties = cached.Difficulties;
            else
            {
                difficulties = new HashSet<int>();
                int offset = 0;
                while (true)
                {
                    var page = await localLibrary!.SearchBeatmapSetsAsync(new LocalLibraryQuery(RulesetShortName: "", Offset: offset, Limit: 200), token).ConfigureAwait(false);
                    foreach (var map in page.Items)
                        foreach (var difficulty in map.Difficulties)
                            if (difficulty.OnlineId > 0) difficulties.Add(difficulty.OnlineId);
                    if (!page.HasMore || page.Items.Count == 0) break;
                    offset += page.Items.Count;
                }
                installedCache = (DateTime.UtcNow, difficulties);
            }
            var installed = sets.Where(set => set.Difficulties.Count > 0 && set.Difficulties.All(d => difficulties.Contains(d.BeatmapId)))
                .Select(set => set.BeatmapSetId).ToArray();
            if (!IsDisposed) Schedule(() =>
            {
                if (token.IsCancellationRequested) return;
                foreach (int id in installed)
                    if (resultBlocks.TryGet(id, out var block)) block.Card.SetInstalled();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { /* Unknown availability must not disable downloads. */ }
    }

    /// <summary>Details and the one primary action for the selected online set.</summary>
    private sealed partial class OnlineSetInspector : Container
    {
        private readonly Func<int, CancellationToken, Task>? openBeatmap;
        private readonly AimModScrollContainer scroll;
        private readonly FillFlowContainer<Drawable> content;
        private OnlineBeatmapCard? card;
        private PrimaryAction? action;

        public OnlineSetInspector(Func<int, CancellationToken, Task>? openBeatmap)
        {
            this.openBeatmap = openBeatmap;
            RelativeSizeAxes = Axes.Both;
            Child = scroll = new AimModScrollContainer
            {
                RelativeSizeAxes = Axes.Both,
                Child = content = new FillFlowContainer<Drawable>
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new(AimModVisualStyle.SectionSpacing),
                    Padding = new MarginPadding { Right = AimModVisualStyle.RelatedSpacing + 4, Bottom = AimModVisualStyle.SectionSpacing },
                },
            };
        }

        internal string? ActionLabelForTesting => action?.Label;

        public void ClearSelection()
        {
            card = null;
            action = null;
            content.Clear();
        }

        public void Show(OfficialBeatmapSet set, OnlineBeatmapCard owner)
        {
            card = owner;
            content.Clear();
            double[] stars = set.Difficulties.Select(d => d.StarRating).ToArray();
            double bpm = set.Difficulties.Count == 0 ? 0 : set.Difficulties.Max(d => d.Bpm);
            int length = set.Difficulties.Count == 0 ? 0 : set.Difficulties.Max(d => d.TotalLengthSeconds);
            long plays = set.Difficulties.Sum(d => (long)d.PlayCount);
            long passes = set.Difficulties.Sum(d => (long)d.PassCount);

            content.Add(new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = 136,
                Masking = true,
                CornerRadius = AimModVisualStyle.CardRadius,
                BorderThickness = 1,
                BorderColour = AimModPalette.Border,
                Children = new Drawable[]
                {
                    new MapBrowserCover(null, set.CoverUrl ?? set.CardUrl, stars.Length == 0 ? 0 : stars.Max(), fullResolution: true,
                        showPlaceholderIcon: false, cornerRadius: AimModVisualStyle.CardRadius) { RelativeSizeAxes = Axes.Both },
                    new Box { RelativeSizeAxes = Axes.Both, Colour = ColourInfo.GradientVertical(AimModPalette.Canvas.Opacity(0.05f), AimModPalette.Canvas.Opacity(0.92f)) },
                    new MapBrowserStatusBadge(set.Status) { Position = new(12, 12) },
                    new FillFlowContainer
                    {
                        Anchor = Anchor.BottomLeft,
                        Origin = Anchor.BottomLeft,
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Spacing = new(3),
                        Padding = new MarginPadding(14),
                        Children = new Drawable[]
                        {
                            new TruncatingSpriteText { RelativeSizeAxes = Axes.X, Text = set.Title, Font = new FontUsage(size: 20, weight: "Bold"), Colour = AimModPalette.Text },
                            new TruncatingSpriteText { RelativeSizeAxes = Axes.X, Text = $"{set.Artist}  ·  mapped by {set.Creator}", Font = AimModVisualStyle.BodyFont, Colour = AimModPalette.Text },
                            new TruncatingSpriteText
                            {
                                RelativeSizeAxes = Axes.X,
                                Text = $"{MapBrowserFormat.Bpm(bpm)}  ·  {MapBrowserFormat.DurationSeconds(length)}  ·  {MapBrowserFormat.DifficultyCount(set.Difficulties.Count)}",
                                Font = AimModVisualStyle.CaptionStrongFont,
                                Colour = AimModPalette.Muted,
                            },
                        },
                    },
                },
            });

            content.Add(new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new(12),
                Children = new Drawable[]
                {
                    action = new PrimaryAction(owner),
                    new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Horizontal,
                        Children = new Drawable[]
                        {
                            stat("Plays", MapBrowserFormat.Count(plays > 0 ? plays : set.PlayCount), 0),
                            stat("Pass rate", plays > 0 ? $"{Math.Clamp(passes / (double)plays, 0, 1) * 100:0}%" : "–", 1),
                            stat("Favourites", MapBrowserFormat.Count(set.FavouriteCount), 2),
                        },
                    },
                    new TruncatingSpriteText
                    {
                        RelativeSizeAxes = Axes.X,
                        Text = set.RankedAt is { } ranked
                            ? $"{MapBrowserFormat.Status(set.Status)} {ranked.ToLocalTime().ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}  ·  pass rate = plays that finished"
                            : "Pass rate = plays that finished the map",
                        Font = AimModVisualStyle.CaptionFont,
                        Colour = AimModPalette.Muted,
                    },
                },
            });

            var list = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new(6),
            };
            foreach (OfficialBeatmapDifficulty difficulty in set.Difficulties.OrderBy(d => d.StarRating))
                list.Add(new InspectorDifficulty(difficulty, openBeatmap));
            content.Add(new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new(10),
                Children = new Drawable[]
                {
                    new SpriteText { Text = "Difficulties", Font = new FontUsage(size: 15, weight: "Bold"), Colour = AimModPalette.Text },
                    list,
                },
            });
            scroll.ScrollToStart(false);
        }

        private static Drawable stat(string label, string value, int index) => new Container
        {
            RelativeSizeAxes = Axes.X,
            Width = 1 / 3f,
            Height = 50,
            Padding = new MarginPadding { Left = index == 0 ? 0 : 4, Right = index == 2 ? 0 : 4 },
            Child = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Masking = true,
                CornerRadius = AimModVisualStyle.ControlRadius,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                    new SpriteText { Position = new(12, 8), Text = value, Font = new FontUsage(size: 16, weight: "Bold"), Colour = AimModPalette.Text },
                    new SpriteText { Position = new(12, 29), Text = label, Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted },
                },
            },
        };

        /// <summary>Mint while the set can be downloaded; mirrors the row action's progress afterwards.</summary>
        private sealed partial class PrimaryAction : ClickableContainer
        {
            private readonly OnlineBeatmapCard owner;
            private readonly Box background;
            private readonly SpriteText caption;

            public string Label => caption.Text.ToString();

            public PrimaryAction(OnlineBeatmapCard owner)
            {
                this.owner = owner;
                RelativeSizeAxes = Axes.X;
                Height = AimModVisualStyle.ControlHeight;
                Masking = true;
                CornerRadius = AimModVisualStyle.ControlRadius;
                Action = owner.TriggerAction;
                Children = new Drawable[]
                {
                    background = new Box { RelativeSizeAxes = Axes.Both },
                    caption = new SpriteText { Anchor = Anchor.Centre, Origin = Anchor.Centre, Font = new FontUsage(size: 14, weight: "SemiBold") },
                };
            }

            protected override void Update()
            {
                base.Update();
                bool actionable = owner.CanAct;
                string label = owner.ActionLabel == "Download" ? "Download and add to osu!" : owner.ActionLabel;
                if (caption.Text != label)
                    caption.Text = label;
                background.Colour = actionable ? (IsHovered ? Colour4.FromHex("74E9C8") : AimModPalette.Accent) : AimModPalette.AccentMuted;
                caption.Colour = actionable ? AimModPalette.Canvas : AimModPalette.Accent;
            }
        }
    }

    private sealed partial class InspectorDifficulty : CompositeDrawable
    {
        public InspectorDifficulty(OfficialBeatmapDifficulty difficulty, Func<int, CancellationToken, Task>? openBeatmap)
        {
            RelativeSizeAxes = Axes.X;
            Height = 52;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            string? passRate = MapBrowserFormat.PassRate(difficulty.PlayCount, difficulty.PassCount);
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                new FillFlowContainer
                {
                    Position = new(10, 8),
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Horizontal,
                    Spacing = new(8, 0),
                    Children = new Drawable[]
                    {
                        new AimModDifficultyPill(difficulty.StarRating) { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft },
                        new TruncatingSpriteText { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Text = difficulty.Name, MaxWidth = 150, Font = AimModVisualStyle.BodyStrongFont, Colour = AimModPalette.Text },
                    },
                },
                new SpriteText
                {
                    Position = new(12, 32),
                    Text = $"AR {difficulty.ApproachRate:0.#} · OD {difficulty.OverallDifficulty:0.#} · CS {difficulty.CircleSize:0.#}" + (passRate is null ? string.Empty : $" · {passRate}"),
                    Font = AimModVisualStyle.CaptionFont,
                    Colour = AimModPalette.Muted,
                },
                new OpenBeatmapButton(openBeatmap is null ? null : token => openBeatmap(difficulty.BeatmapId, token))
                {
                    Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, X = -10, Width = 112, Height = 30,
                },
            };
        }
    }

    /// <summary>One search result: the set card, with its difficulties revealed on demand.</summary>
    private partial class OnlineBeatmapSetBlock : FillFlowContainer<Drawable>
    {
        public OnlineBeatmapCard Card { get; }
        private readonly FillFlowContainer<Drawable> difficultyList;

        public OnlineBeatmapSetBlock(
            OfficialBeatmapSet set,
            Func<OfficialBeatmapSet, Task<OnlineBeatmapImportResult>> import,
            Func<LazerBeatmapArchive, Task<LazerBeatmapInstallResult>> installInLazer,
            Func<int, CancellationToken, Task>? openBeatmap,
            bool expanded = false,
            Action<OnlineBeatmapSetBlock>? clicked = null)
        {
            Set = set;
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            Direction = FillDirection.Vertical;
            Spacing = new(0, 2);
            Add(Card = new OnlineBeatmapCard(set, import, installInLazer) { ToggleDetails = () => { if (clicked is null) Toggle(); else clicked(this); } });
            Add(difficultyList = new FillFlowContainer<Drawable>
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new(0, 2),
                Alpha = 0,
            });
            foreach (OfficialBeatmapDifficulty difficulty in set.Difficulties.OrderBy(d => d.StarRating))
                difficultyList.Add(new OnlineDifficultyRow(difficulty, openBeatmap));
            if (expanded)
                Toggle();
        }

        public OfficialBeatmapSet Set { get; }

        public bool Expanded => difficultyList.Alpha > 0;

        /// <summary>Inline difficulty lists are only used when no inspector is beside the results.</summary>
        public void SetInline(bool inline)
        {
            Card.SetInlineDetails(inline);
            if (!inline && Expanded)
                Toggle();
        }

        public void Toggle()
        {
            bool open = difficultyList.Alpha == 0;
            difficultyList.Alpha = open ? 1 : 0;
            Card.SetExpanded(open);
        }
    }

    private sealed partial class OnlineDifficultyRow : CompositeDrawable
    {
        public OnlineDifficultyRow(OfficialBeatmapDifficulty difficulty, Func<int, CancellationToken, Task>? openBeatmap)
        {
            RelativeSizeAxes = Axes.X;
            Height = 42;
            Masking = true;
            CornerRadius = 4;
            string? passRate = MapBrowserFormat.PassRate(difficulty.PlayCount, difficulty.PassCount);
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel, Alpha = 0.7f },
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    X = 150,
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Horizontal,
                    Spacing = new(8, 0),
                    Children = new Drawable[]
                    {
                        new AimModDifficultyPill(difficulty.StarRating) { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft },
                        new TruncatingSpriteText { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Text = difficulty.Name, MaxWidth = 220, Font = AimModVisualStyle.BodyStrongFont, Colour = AimModPalette.Text },
                        new SpriteText
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Text = $"AR {difficulty.ApproachRate:0.#}  ·  OD {difficulty.OverallDifficulty:0.#}  ·  CS {difficulty.CircleSize:0.#}" + (passRate is null ? string.Empty : $"  ·  {passRate}"),
                            Font = AimModVisualStyle.CaptionFont,
                            Colour = AimModPalette.Muted,
                        },
                    },
                },
                new OpenBeatmapButton(openBeatmap is null ? null : token => openBeatmap(difficulty.BeatmapId, token))
                {
                    Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, X = -12, Height = 30,
                },
            };
        }
    }

    private partial class OnlineBeatmapCard : AimModInteractiveSurface
    {
        public const float CardHeight = 88;
        private const float cover_width = 128;
        private const float facts_width = 90;
        private const float stats_width = 110;
        private const float action_width = 148;
        private readonly OfficialBeatmapSet set;
        private readonly Func<OfficialBeatmapSet, Task<OnlineBeatmapImportResult>> import;
        private readonly Func<LazerBeatmapArchive, Task<LazerBeatmapInstallResult>> installInLazer;
        private readonly Container textColumn;
        private readonly TruncatingSpriteText titleText;
        private readonly TruncatingSpriteText artistText;
        private readonly Container factsColumn;
        private readonly Container statsColumn;
        private readonly Container expandHint;
        private readonly SpriteIcon chevron;
        private readonly SpriteText actionText;
        private readonly SpriteIcon actionIcon;
        private readonly Box selectionBar;
        private readonly Box actionBackground;
        private AimModLayout.ChangeTracker<float> widthTracker;
        private bool importing;
        private bool imported;
        private bool installed;
        private bool installingInLazer;
        private bool sentToLazer;
        private LazerBeatmapArchive? lazerArchive;

        /// <summary>Shows or hides the set's difficulty list when the card body is clicked.</summary>
        public Action? ToggleDetails { get; set; }

        public OnlineBeatmapCard(
            OfficialBeatmapSet set,
            Func<OfficialBeatmapSet, Task<OnlineBeatmapImportResult>> import,
            Func<LazerBeatmapArchive, Task<LazerBeatmapInstallResult>> installInLazer)
        {
            this.set = set;
            this.import = import;
            this.installInLazer = installInLazer;
            RelativeSizeAxes = Axes.X;
            Height = CardHeight;
            BorderThickness = 0;
            BackgroundColour = AimModPalette.Panel;
            Action = () => ToggleDetails?.Invoke();
            double[] stars = set.Difficulties.Select(difficulty => difficulty.StarRating).ToArray();
            double maximumStars = stars.Length == 0 ? 0 : stars.Max();
            double bpm = set.Difficulties.Count == 0 ? 0 : set.Difficulties.Max(difficulty => difficulty.Bpm);
            int length = set.Difficulties.Count == 0 ? 0 : set.Difficulties.Max(difficulty => difficulty.TotalLengthSeconds);
            long plays = set.Difficulties.Sum(difficulty => (long)difficulty.PlayCount);
            long passes = set.Difficulties.Sum(difficulty => (long)difficulty.PassCount);

            Children = new Drawable[]
            {
                new MapBrowserCover(null, set.CardUrl ?? set.CoverUrl, maximumStars)
                {
                    Position = new(8, 8),
                    Size = new(cover_width, CardHeight - 16),
                },
                textColumn = new Container
                {
                    RelativeSizeAxes = Axes.Y,
                    X = 8 + cover_width + 14,
                    Children = new Drawable[]
                    {
                        titleText = new TruncatingSpriteText { Y = 11, Text = set.Title, Font = new FontUsage(size: 15, weight: "Bold"), Colour = AimModPalette.Text },
                        artistText = new TruncatingSpriteText { Y = 32, Text = $"{set.Artist}  ·  {set.Creator}", Font = AimModVisualStyle.BodyFont, Colour = AimModPalette.Muted },
                        new FillFlowContainer
                        {
                            Y = 55,
                            AutoSizeAxes = Axes.Both,
                            Direction = FillDirection.Horizontal,
                            Spacing = new(10, 0),
                            Children = new Drawable[]
                            {
                                new MapBrowserStatusBadge(set.Status) { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft },
                                new MapBrowserDifficultySpread(stars) { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft },
                            },
                        },
                    },
                },
                factsColumn = column(facts_width, action_width + stats_width + 52,
                    MapBrowserFormat.Bpm(bpm), MapBrowserFormat.DurationSeconds(length)),
                statsColumn = column(stats_width, action_width + 38,
                    $"{MapBrowserFormat.Count(plays > 0 ? plays : set.PlayCount)} plays", MapBrowserFormat.PassRate(plays, passes) ?? $"{MapBrowserFormat.Count(set.FavouriteCount)} favourites"),
                expandHint = new Container
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    AutoSizeAxes = Axes.Both,
                    X = -(action_width + 28),
                    Child = new FillFlowContainer
                    {
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Horizontal,
                        Spacing = new(5, 0),
                        Children = new Drawable[]
                        {
                            new SpriteText { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Text = set.Difficulties.Count == 1 ? "1 diff" : $"{set.Difficulties.Count} diffs", Font = AimModVisualStyle.CaptionStrongFont, Colour = AimModPalette.Muted },
                            chevron = new SpriteIcon { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Size = new(10), Icon = FontAwesome.Solid.ChevronDown, Colour = AimModPalette.Muted },
                        },
                    },
                },
                new ImportAction(set.DownloadDisabled
                    ? "The mapper or osu! has disabled downloads for this beatmap set."
                    : "Download this set, then add it to your osu! client.")
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Margin = new MarginPadding { Right = 12 },
                    Size = new(action_width, AimModVisualStyle.CompactControlHeight),
                    Masking = true,
                    CornerRadius = AimModVisualStyle.ControlRadius,
                    BorderThickness = 1,
                    BorderColour = AimModPalette.Border,
                    Action = beginImport,
                    Children = new Drawable[]
                    {
                        actionBackground = new Box { RelativeSizeAxes = Axes.Both },
                        new FillFlowContainer
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            AutoSizeAxes = Axes.Both,
                            Direction = FillDirection.Horizontal,
                            Spacing = new(7, 0),
                            Children = new Drawable[]
                            {
                                actionIcon = new SpriteIcon { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Size = new(11) },
                                actionText = new SpriteText
                                {
                                    Anchor = Anchor.CentreLeft,
                                    Origin = Anchor.CentreLeft,
                                    Text = set.DownloadDisabled ? "Download disabled" : "Download",
                                    Font = new FontUsage(size: 13, weight: "SemiBold"),
                                },
                            },
                        },
                    },
                },
                selectionBar = new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = AimModPalette.Accent, Alpha = 0 },
            };
            style(set.DownloadDisabled ? ActionTone.Disabled : ActionTone.Idle);
        }

        private static Container column(float width, float right, string primary, string secondary) => new()
        {
            Anchor = Anchor.TopRight,
            Origin = Anchor.TopRight,
            Width = width,
            RelativeSizeAxes = Axes.Y,
            Margin = new MarginPadding { Right = right },
            Children = new Drawable[]
            {
                new SpriteText { Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Y = 24, Text = primary, Font = AimModVisualStyle.BodyStrongFont, Colour = AimModPalette.Text },
                new SpriteText { Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Y = 46, Text = secondary, Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted },
            },
        };

        /// <summary>Beside an inspector the difficulties live there, so the inline expander is hidden.</summary>
        public void SetInlineDetails(bool inline)
        {
            if (inlineDetails == inline)
                return;
            inlineDetails = inline;
            expandHint.Alpha = inline ? 1 : 0;
            widthTracker = default;
        }

        private bool inlineDetails = true;

        public void SetExpanded(bool expanded)
        {
            chevron.Icon = expanded ? FontAwesome.Solid.ChevronUp : FontAwesome.Solid.ChevronDown;
            BackgroundColour = expanded ? AimModPalette.PanelRaised : AimModPalette.Panel;
        }

        protected override void Update()
        {
            base.Update();
            if (!widthTracker.Update(DrawWidth))
                return;
            // Narrow cards drop the play statistics first, then BPM and length.
            bool showStats = DrawWidth >= (inlineDetails ? 820 : 760);
            bool showFacts = DrawWidth >= (inlineDetails ? 640 : 580);
            statsColumn.Alpha = showStats ? 1 : 0;
            factsColumn.Alpha = showFacts ? 1 : 0;
            // Columns are laid out from the right edge so hidden ones leave no gap.
            float right = 12 + action_width + 16;
            if (inlineDetails)
            {
                expandHint.X = -right;
                right += 64;
            }
            if (showStats)
            {
                statsColumn.Margin = new MarginPadding { Right = right };
                right += stats_width + 14;
            }
            if (showFacts)
            {
                factsColumn.Margin = new MarginPadding { Right = right };
                right += facts_width + 14;
            }
            float available = Math.Max(60, DrawWidth - textColumn.X - right);
            textColumn.Width = available;
            titleText.MaxWidth = available;
            artistText.MaxWidth = available;
        }

        private partial class ImportAction : ClickableContainer, IHasTooltip
        {
            public ImportAction(string tooltip) => TooltipText = tooltip;

            public LocalisableString TooltipText { get; }
        }

        private void beginImport()
        {
            if (installed || importing || installingInLazer || sentToLazer || set.DownloadDisabled)
                return;

            if (imported)
            {
                if (lazerArchive is not null)
                    beginLazerInstall(lazerArchive);
                return;
            }

            importing = true;
            actionText.Text = "Downloading...";
            style(ActionTone.Busy);
            _ = importAsync();
        }

        public void SetInstalled()
        {
            installed = true;
            actionText.Text = "Installed";
            style(ActionTone.Done);
        }

        private async Task importAsync()
        {
            OnlineBeatmapImportResult result;
            try
            {
                result = await import(set).ConfigureAwait(false);
            }
            catch
            {
                result = new OnlineBeatmapImportResult(OnlineBeatmapImportStatus.ImportFailed, set.BeatmapSetId);
            }
            if (!IsDisposed)
                Schedule(() => applyImportResult(result));
        }

        private void applyImportResult(OnlineBeatmapImportResult result)
        {
            importing = false;
            imported = result.Status == OnlineBeatmapImportStatus.Success
                || (result.Status == OnlineBeatmapImportStatus.OsuInstallFailed && result.LazerArchive is not null);
            lazerArchive = result.LazerArchive;
            sentToLazer = result.Status == OnlineBeatmapImportStatus.Success && result.LazerArchive is not null;
            actionText.Text = result.Status switch
            {
                OnlineBeatmapImportStatus.Success when result.LazerArchive is not null => "Added to osu!",
                OnlineBeatmapImportStatus.Success => "Saved in AimMod",
                OnlineBeatmapImportStatus.OsuInstallFailed => "Retry osu! import",
                OnlineBeatmapImportStatus.SignedOut => "Sign in to lazer",
                OnlineBeatmapImportStatus.TokenExpired => "Session refreshing",
                OnlineBeatmapImportStatus.Unauthorized => "Session refused",
                OnlineBeatmapImportStatus.SessionChanged => "Account changed",
                OnlineBeatmapImportStatus.NetworkError => "Network error",
                OnlineBeatmapImportStatus.RateLimited => "Rate limited, wait",
                OnlineBeatmapImportStatus.DownloadDisabled => "Unavailable",
                OnlineBeatmapImportStatus.InvalidDownload => "Invalid download",
                OnlineBeatmapImportStatus.ServerError => "osu! server error",
                _ => "Import failed",
            };
            style(result.Status == OnlineBeatmapImportStatus.Success ? ActionTone.Done : ActionTone.Problem);
        }

        private void beginLazerInstall(LazerBeatmapArchive archive)
        {
            installingInLazer = true;
            actionText.Text = "Opening osu!...";
            style(ActionTone.Busy);
            _ = installInLazerAsync(archive);
        }

        private async Task installInLazerAsync(LazerBeatmapArchive archive)
        {
            LazerBeatmapInstallResult result;
            try
            {
                result = await installInLazer(archive).ConfigureAwait(false);
            }
            catch
            {
                result = new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LaunchFailed);
            }
            if (!IsDisposed)
                Schedule(() => applyLazerInstallResult(result));
        }

        private void applyLazerInstallResult(LazerBeatmapInstallResult result)
        {
            installingInLazer = false;
            sentToLazer = result.Status is LazerBeatmapInstallStatus.Sent or LazerBeatmapInstallStatus.LazerStarted;
            if (result.Status == LazerBeatmapInstallStatus.ArchiveUnavailable)
                lazerArchive = null;

            actionText.Text = result.Status switch
            {
                LazerBeatmapInstallStatus.Sent => "Sent to osu!",
                LazerBeatmapInstallStatus.LazerStarted => "Opened in osu!",
                LazerBeatmapInstallStatus.ArchiveUnavailable => "Saved in AimMod",
                LazerBeatmapInstallStatus.LazerNotFound => "osu! client not found",
                LazerBeatmapInstallStatus.LazerRejected => "osu! refused it",
                _ => "Could not open osu!",
            };
            style(sentToLazer ? ActionTone.Done : ActionTone.Problem);
        }

        private enum ActionTone
        {
            Idle,
            Busy,
            Done,
            Problem,
            Disabled,
        }

        /// <summary>Row actions stay secondary; the selected set's inspector owns the one primary action.</summary>
        private void style(ActionTone tone)
        {
            (Colour4 background, Colour4 text) = tone switch
            {
                ActionTone.Busy or ActionTone.Done => (AimModPalette.AccentMuted, AimModPalette.Accent),
                ActionTone.Problem => (AimModPalette.PanelRaised, AimModPalette.Yellow),
                ActionTone.Disabled => (AimModPalette.Panel, AimModPalette.Muted),
                _ => (AimModPalette.PanelRaised, AimModPalette.Text),
            };
            actionBackground.Colour = background;
            actionText.Colour = text;
            actionIcon.Colour = text;
            actionIcon.Icon = tone switch
            {
                ActionTone.Done => FontAwesome.Solid.Check,
                ActionTone.Problem => FontAwesome.Solid.Redo,
                ActionTone.Disabled => FontAwesome.Solid.Ban,
                _ => FontAwesome.Solid.Download,
            };
        }

        /// <summary>The current action caption, mirrored by the inspector's primary button.</summary>
        public string ActionLabel => actionText.Text.ToString();

        /// <summary>True when pressing the action would start a download or an osu! import.</summary>
        public bool CanAct => !(installed || importing || installingInLazer || sentToLazer || set.DownloadDisabled);

        public void TriggerAction() => beginImport();

        public void SetSelected(bool value)
        {
            selectionBar.Alpha = value ? 1 : 0;
            BackgroundColour = value ? AimModPalette.PanelRaised : AimModPalette.Panel;
        }
    }
}

public enum NativeBeatmapFilterLayout
{
    Row,
    TwoColumns,
    Stacked,
}
