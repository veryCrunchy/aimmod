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
                "Browse your installed library or discover maps from the official osu! catalog.",
                "MAP LIBRARY")
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
    private readonly Container searchGroup;
    private readonly Container categoryGroup;
    private readonly Container sortGroup;
    private readonly Container resultViewport;
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
            filterBand = new Container
            {
                Position = new(0, 0),
                RelativeSizeAxes = Axes.X,
                Height = 72,
                Depth = -20,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                    searchGroup = new Container
                    {
                        Children = new Drawable[]
                        {
                            filterLabel("SEARCH"),
                            searchBox = new AimModSearchBox
                            {
                                Position = new(0, 17),
                                RelativeSizeAxes = Axes.X,
                                Height = AimModVisualStyle.CompactControlHeight,
                                SearchHint = "Title, artist, mapper or tag",
                            },
                        },
                    },
                    starSlider = new AimModStarRatingFilter
                    {
                        LowerBound = minimumStars,
                        UpperBound = maximumStars,
                        DefaultStringLowerBound = "0",
                        DefaultStringUpperBound = "10+",
                    },
                    categoryGroup = dropdownGroup("STATUS", categoryDropdown = new BeatmapFilterDropdown<OfficialBeatmapCategory>()
                    {
                        Items = new[] { OfficialBeatmapCategory.Any, OfficialBeatmapCategory.Ranked, OfficialBeatmapCategory.Loved, OfficialBeatmapCategory.Pending },
                        Current = category,
                    }),
                    sortGroup = dropdownGroup("SORT", sortDropdown = new BeatmapFilterDropdown<OfficialBeatmapSort>()
                    {
                        Items = new[] { OfficialBeatmapSort.Relevance, OfficialBeatmapSort.Updated, OfficialBeatmapSort.Plays },
                        Current = sort,
                    }),
                },
            },
            resultStatus = new TruncatingSpriteText
            {
                Y = 84,
                Text = "Connecting to osu!...",
                Font = new FontUsage(size: 11, weight: "SemiBold"),
                Colour = AimModPalette.Muted,
                Depth = 0,
            },
            resultViewport = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = 108 },
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
                                Spacing = new(AimModVisualStyle.RelatedSpacing),
                            },
                        },
                    },
                },
            },
            resetFilters = new AimModResetButton(() => {
                searchBox.Current.Value = string.Empty; minimumStars.Value = 0; maximumStars.Value = 10;
                category.Value = OfficialBeatmapCategory.Any; sort.Value = OfficialBeatmapSort.Relevance; scheduleSearch();
            }) { Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Height = 24 },
        };
        resultBlocks = new KeyedFlow<int, OfficialBeatmapSet, OnlineBeatmapSetBlock>(results,
            set => set.BeatmapSetId,
            set => $"{set.Status}|{set.DownloadDisabled}|{string.Join(',', set.Difficulties.Select(d => d.BeatmapId))}",
            set => new OnlineBeatmapSetBlock(set, importBeatmap, installInLazer, openBeatmap));
    }

    internal static NativeBeatmapFilterLayout CalculateFilterLayout(float width) => width switch
    {
        < 640 => NativeBeatmapFilterLayout.Stacked,
        < 940 => NativeBeatmapFilterLayout.TwoColumns,
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

        if (!layoutTracker.Update(DrawWidth))
            return;

        float width = Math.Max(1, DrawWidth);
        const float gap = AimModVisualStyle.RelatedSpacing;
        NativeBeatmapFilterLayout layout = CalculateFilterLayout(width);

        if (layout == NativeBeatmapFilterLayout.Stacked)
        {
            // Phone-width windows stack every filter so none of them clip or overlap.
            float column = width - content_inset * 2;
            filterBand.Height = 248;
            placeGroup(searchGroup, content_inset, 8, column, 54);
            placeSlider(starSlider, content_inset, 64, column);
            placeGroup(categoryGroup, content_inset, 128, column, 52);
            placeGroup(sortGroup, content_inset, 186, column, 52);
            resultStatus.Y = 260;
            resultViewport.Padding = new MarginPadding { Top = 284 };
        }
        else if (layout == NativeBeatmapFilterLayout.TwoColumns)
        {
            float columnWidth = (width - content_inset * 2 - gap) / 2;
            filterBand.Height = 128;
            placeGroup(searchGroup, content_inset, 8, columnWidth, 54);
            placeSlider(starSlider, content_inset + columnWidth + gap, 3, columnWidth);
            placeGroup(categoryGroup, content_inset, 68, columnWidth, 52);
            placeGroup(sortGroup, content_inset + columnWidth + gap, 68, columnWidth, 52);
            resultStatus.Y = 140;
            resultViewport.Padding = new MarginPadding { Top = 164 };
        }
        else
        {
            float available = width - content_inset * 2 - gap * 3;
            float searchWidth = Math.Clamp(available * 0.34f, 280, 430);
            float sliderWidth = Math.Clamp(available * 0.29f, 240, 360);
            float dropdownWidth = (available - searchWidth - sliderWidth) / 2;
            filterBand.Height = 72;
            placeGroup(searchGroup, content_inset, 8, searchWidth, 54);
            placeSlider(starSlider, content_inset + searchWidth + gap, 3, sliderWidth);
            placeGroup(categoryGroup, content_inset + searchWidth + gap + sliderWidth + gap, 8, dropdownWidth, 54);
            placeGroup(sortGroup, width - content_inset - dropdownWidth, 8, dropdownWidth, 54);
            resultStatus.Y = 84;
            resultViewport.Padding = new MarginPadding { Top = 108 };
        }

        resultStatus.MaxWidth = Math.Max(0, width - content_inset * 2 - 120);
        resetFilters.Y = resultStatus.Y - 4;
    }

    private static void placeSlider(Drawable slider, float x, float y, float width)
    {
        slider.Anchor = Anchor.TopLeft;
        slider.Origin = Anchor.TopLeft;
        slider.Position = new(x, y + 18);
        slider.Size = new(width, 30);
    }

    private static void placeGroup(Container group, float x, float y, float width, float height)
    {
        group.Position = new(x, y);
        group.Size = new(width, height);
    }

    private static SpriteText filterLabel(string value) => new()
    {
        Text = value,
        Font = AimModVisualStyle.LabelFont,
        Colour = AimModPalette.Cyan,
    };

    private static Container dropdownGroup(string label, Drawable dropdown)
    {
        dropdown.Position = new(0, 17);
        dropdown.RelativeSizeAxes = Axes.X;
        dropdown.Width = 1;
        return new Container
        {
            Children = new Drawable[]
            {
                filterLabel(label),
                dropdown,
            },
        };
    }

    private sealed partial class BeatmapFilterDropdown<T> : AimMod.Desktop.Coaching.BoundedShearedDropdown<T>
    {
        public BeatmapFilterDropdown() : base(string.Empty)
        {
            if (Header is ShearedDropdownHeader header)
            {
                // Match the native labelled header without constraining its popup menu.
                header.LabelContainer.AutoSizeAxes = Axes.X;
                header.LabelContainer.Height = 30;
            }
        }
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
            resultBlocks.Clear();
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
            resultBlocks.Clear();
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

        resultStatus.Text = response.BeatmapSets.Count switch
        {
            1 when selectedSetId is int id => $"Beatmap set {id}",
            0 => "No matching osu!standard beatmap sets",
            1 => "1 matching beatmap set",
            _ => $"{response.BeatmapSets.Count:N0} sets shown from {response.ServerTotal:N0} server matches",
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

    /// <summary>One search result: the set card followed by its difficulties, kept together by set id.</summary>
    private partial class OnlineBeatmapSetBlock : FillFlowContainer<Drawable>
    {
        public OnlineBeatmapCard Card { get; }

        public OnlineBeatmapSetBlock(
            OfficialBeatmapSet set,
            Func<OfficialBeatmapSet, Task<OnlineBeatmapImportResult>> import,
            Func<LazerBeatmapArchive, Task<LazerBeatmapInstallResult>> installInLazer,
            Func<int, CancellationToken, Task>? openBeatmap)
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            Direction = FillDirection.Vertical;
            Spacing = new(AimModVisualStyle.RelatedSpacing);
            Add(Card = new OnlineBeatmapCard(set, import, installInLazer));
            foreach (OfficialBeatmapDifficulty difficulty in set.Difficulties)
            {
                Add(new Container
                {
                    RelativeSizeAxes = Axes.X, Height = 54,
                    Children = [
                        new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                        new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Left = 18, Right = 168, Top = 7 }, Children = [
                            new TruncatingSpriteText { RelativeSizeAxes = Axes.X, Text = difficulty.Name,
                                Font = new FontUsage(size:14,weight:"SemiBold"), Colour = AimModPalette.Text },
                            new TruncatingSpriteText { RelativeSizeAxes = Axes.X, Y = 22,
                                Text = $"{difficulty.StarRating:0.00} stars · {difficulty.Bpm:0.#} BPM · {difficulty.TotalLengthSeconds / 60}:{difficulty.TotalLengthSeconds % 60:00} · CS {difficulty.CircleSize:0.#} · AR {difficulty.ApproachRate:0.#} · OD {difficulty.OverallDifficulty:0.#}",
                                Font = new FontUsage(size:11), Colour = AimModPalette.Muted },
                        ] },
                        new OpenBeatmapButton(openBeatmap is null ? null : token => openBeatmap(difficulty.BeatmapId, token)) {
                            Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, X = -12, Height = 30,
                        },
                    ],
                });
            }
        }
    }

    private partial class OnlineBeatmapCard : AimModInteractiveSurface
    {
        private readonly OfficialBeatmapSet set;
        private readonly Func<OfficialBeatmapSet, Task<OnlineBeatmapImportResult>> import;
        private readonly Func<LazerBeatmapArchive, Task<LazerBeatmapInstallResult>> installInLazer;
        private readonly TruncatingSpriteText titleText;
        private readonly TruncatingSpriteText artistText;
        private readonly TruncatingSpriteText detailText;
        private readonly SpriteText actionText;
        private readonly Box actionBackground;
        private AimModLayout.ChangeTracker<float> widthTracker;
        private bool importing;
        private bool imported;
        private bool installed;
        private bool installingInLazer;
        private bool sentToLazer;
        private LazerBeatmapArchive? lazerArchive;

        public OnlineBeatmapCard(
            OfficialBeatmapSet set,
            Func<OfficialBeatmapSet, Task<OnlineBeatmapImportResult>> import,
            Func<LazerBeatmapArchive, Task<LazerBeatmapInstallResult>> installInLazer)
        {
            this.set = set;
            this.import = import;
            this.installInLazer = installInLazer;
            RelativeSizeAxes = Axes.X;
            Height = 104;
            CornerRadius = AimModVisualStyle.ControlRadius;
            BackgroundColour = AimModPalette.Panel;
            double maximumStars = set.Difficulties.Count == 0 ? 0 : set.Difficulties.Max(difficulty => difficulty.StarRating);
            Colour4 difficultyColour = AimModVisualStyle.DifficultyColour(maximumStars);

            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = ColourInfo.GradientHorizontal(difficultyColour, AimModPalette.Panel),
                    Alpha = 0.18f,
                },
                new AimModOnlineArtworkHost(set.CoverUrl),
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = ColourInfo.GradientHorizontal(AimModPalette.Canvas, AimModPalette.Panel),
                    Alpha = 0.86f,
                },
                new Box { RelativeSizeAxes = Axes.Y, Width = 4, Colour = difficultyColour },
                new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both,
                    Position = new(18, 9),
                    Direction = FillDirection.Vertical,
                    Spacing = new(3),
                    Children = new Drawable[]
                    {
                        titleText = new TruncatingSpriteText
                        {
                            Text = set.Title,
                            Font = new FontUsage(size: 15, weight: "Bold"),
                            Colour = AimModPalette.Text,
                            MaxWidth = 120,
                        },
                        artistText = new TruncatingSpriteText
                        {
                            Text = set.Artist,
                            Font = new FontUsage(size: 11, weight: "SemiBold"),
                            Colour = AimModPalette.Muted,
                            MaxWidth = 120,
                        },
                        detailText = new TruncatingSpriteText
                        {
                            Text = $"mapped by {set.Creator}  /  {set.Status}  /  {set.PlayCount:N0} plays  /  {set.FavouriteCount:N0} favourites",
                            Font = AimModVisualStyle.CaptionFont,
                            Colour = AimModPalette.Muted,
                            MaxWidth = 120,
                        },
                    },
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomLeft,
                    AutoSizeAxes = Axes.Both,
                    Margin = new MarginPadding { Left = 18, Bottom = 9 },
                    Direction = FillDirection.Horizontal,
                    Spacing = new(AimModVisualStyle.RelatedSpacing),
                    Children = visibleDifficulties(set).ToArray(),
                },
                new ImportAction(set.DownloadDisabled
                    ? "The mapper or osu! has disabled downloads for this beatmap set."
                    : "Download this set into AimMod, then add it to your osu! client.")
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Margin = new MarginPadding { Right = 12 },
                    Size = new(148, AimModVisualStyle.CompactControlHeight),
                    Masking = true,
                    CornerRadius = AimModVisualStyle.ControlRadius,
                    Action = beginImport,
                    Children = new Drawable[]
                    {
                        actionBackground = new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = set.DownloadDisabled ? AimModPalette.PanelHover : AimModPalette.Accent,
                        },
                        actionText = new SpriteText
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            Text = set.DownloadDisabled ? "Download disabled" : "Save in AimMod",
                            Font = new FontUsage(size: 11, weight: "Bold"),
                            Colour = set.DownloadDisabled ? AimModPalette.Muted : AimModPalette.Canvas,
                        },
                    },
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            if (!widthTracker.Update(DrawWidth))
                return;
            float available = Math.Max(80, DrawWidth - 190);
            titleText.MaxWidth = available;
            artistText.MaxWidth = available;
            detailText.MaxWidth = available;
        }

        private partial class ImportAction : ClickableContainer, IHasTooltip
        {
            public ImportAction(string tooltip) => TooltipText = tooltip;

            public LocalisableString TooltipText { get; }
        }

        private static IEnumerable<Drawable> visibleDifficulties(OfficialBeatmapSet set)
        {
            foreach (OfficialBeatmapDifficulty difficulty in set.Difficulties.Take(3))
                yield return new DifficultyChip(difficulty);

            if (set.Difficulties.Count > 3)
                yield return new AimModPill($"+{set.Difficulties.Count - 3}", AimModPillTone.Neutral);
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
            actionBackground.Colour = AimModPalette.Cyan;
            _ = importAsync();
        }

        public void SetInstalled()
        {
            installed = true;
            actionText.Text = "Installed";
            actionBackground.Colour = AimModPalette.PanelHover;
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
            actionBackground.Colour = result.Status == OnlineBeatmapImportStatus.Success && result.LazerArchive is not null
                ? AimModPalette.Accent
                : result.Status == OnlineBeatmapImportStatus.Success
                    ? AimModPalette.Success
                : AimModPalette.AccentMuted;
            actionText.Colour = result.Status == OnlineBeatmapImportStatus.Success
                ? AimModPalette.Canvas
                : AimModPalette.Text;
        }

        private void beginLazerInstall(LazerBeatmapArchive archive)
        {
            installingInLazer = true;
            actionText.Text = "Opening osu!...";
            actionBackground.Colour = AimModPalette.Cyan;
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
            actionBackground.Colour = sentToLazer ? AimModPalette.Success : AimModPalette.AccentMuted;
            actionText.Colour = sentToLazer ? AimModPalette.Canvas : AimModPalette.Text;
        }

        private partial class DifficultyChip : CircularContainer
        {
            public DifficultyChip(OfficialBeatmapDifficulty difficulty)
            {
                AutoSizeAxes = Axes.Both;
                Masking = true;
                Colour4 colour = AimModVisualStyle.DifficultyColour(difficulty.StarRating);
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = colour, Alpha = 0.2f },
                    new TruncatingSpriteText
                    {
                        Text = $"{difficulty.Name}  {difficulty.StarRating:0.00}*",
                        Font = new FontUsage(size: 11, weight: "SemiBold"),
                        Colour = colour,
                        Padding = new MarginPadding { Horizontal = 10, Vertical = 4 },
                        MaxWidth = 150,
                    },
                };
            }
        }
    }
}

public enum NativeBeatmapFilterLayout
{
    Row,
    TwoColumns,
    Stacked,
}
