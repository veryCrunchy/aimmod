using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Desktop.ScoreHistory;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
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
    private readonly Container page = null!;
    private readonly Container tabBar = null!;
    private readonly AimModSectionHeader workspaceHeader = null!;
    private readonly OsuTabControl<BeatmapDiscoveryTab> tabs = null!;
    private readonly Bindable<BeatmapDiscoveryTab> currentTab = new(BeatmapDiscoveryTab.Installed);
    private NativeInstalledBeatmapBrowser? installedScreen;
    private NativeOfficialBeatmapSearchScreen? onlineScreen;
    private Drawable? activeScreen;

    public NativeBeatmapDiscoveryScreen(
        ILocalLibrarySource localLibrary,
        Func<IOfficialBeatmapDiscoveryClient?> client,
        Func<OnlineBeatmapImportService?> importer,
        Func<IPpTargetExactCalculationService?>? exactCalculator = null,
        Func<IAccountScoreHistoryService?>? onlineScoreHistory = null)
    {
        this.localLibrary = localLibrary ?? throw new ArgumentNullException(nameof(localLibrary));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.importer = importer ?? throw new ArgumentNullException(nameof(importer));
        this.exactCalculator = exactCalculator ?? (() => null);
        this.onlineScoreHistory = onlineScoreHistory ?? (() => null);
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
                Padding = new MarginPadding { Top = 76 },
                Masking = true,
                Depth = 0,
            },
            tabBar = new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = 72,
                Depth = -100,
                Children = new Drawable[]
                {
                    tabs = new OsuTabControl<BeatmapDiscoveryTab>
                    {
                        Anchor = Anchor.TopRight,
                        Origin = Anchor.TopRight,
                        Position = new(0, 18),
                        Size = new(210, 38),
                        AccentColour = AimModPalette.Pink,
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

    protected override void Update()
    {
        base.Update();

        float inspectorWidth = currentTab.Value == BeatmapDiscoveryTab.Installed && DrawWidth >= 1_280
            ? Math.Clamp(DrawWidth * 0.27f, 350, 390)
            : 0;
        tabs.Position = new(-inspectorWidth, 18);
        workspaceHeader.Width = Math.Max(0, DrawWidth - inspectorWidth - 230);
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
                installedScreen = new NativeInstalledBeatmapBrowser(localLibrary, exactCalculator, onlineScoreHistory) { RelativeSizeAxes = Axes.Both };
                page.Add(installedScreen);
            }
            setActiveScreen(installedScreen, onlineScreen);
            return;
        }

        if (onlineScreen is null)
        {
            onlineScreen = new NativeOfficialBeatmapSearchScreen(client, importer) { RelativeSizeAxes = Axes.Both };
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
    private const int result_limit = 24;
    private const float content_inset = 12;

    private readonly Func<IOfficialBeatmapDiscoveryClient?> client;
    private readonly Func<OnlineBeatmapImportService?> importer;
    private readonly OsuTextBox searchBox;
    private readonly TruncatingSpriteText resultStatus;
    private readonly FillFlowContainer results;
    private readonly AimModLoadingOverlay loadingOverlay;
    private readonly Container filterBand;
    private readonly Container searchGroup;
    private readonly Container categoryGroup;
    private readonly Container sortGroup;
    private readonly Container resultViewport;
    private readonly RangeSlider starSlider;
    private readonly OsuDropdown<OfficialBeatmapCategory> categoryDropdown;
    private readonly OsuDropdown<OfficialBeatmapSort> sortDropdown;
    private readonly BindableDouble minimumStars = new(0) { MinValue = 0, MaxValue = 10, Default = 0 };
    private readonly BindableDouble maximumStars = new(10) { MinValue = 0, MaxValue = 10, Default = 10 };
    private readonly Bindable<OfficialBeatmapCategory> category = new(OfficialBeatmapCategory.Any);
    private readonly Bindable<OfficialBeatmapSort> sort = new(OfficialBeatmapSort.Relevance);
    private CancellationTokenSource? requestCancellation;
    private ScheduledDelegate? scheduledSearch;
    private int connectionAttempts;

    public NativeOfficialBeatmapSearchScreen(
        Func<IOfficialBeatmapDiscoveryClient?> client,
        Func<OnlineBeatmapImportService?> importer)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.importer = importer ?? throw new ArgumentNullException(nameof(importer));
        RelativeSizeAxes = Axes.Both;

        InternalChildren = new Drawable[]
        {
            new AimModSectionHeader(
                "Discover beatmaps",
                "Save a set in AimMod, then choose whether to send the same download to osu!lazer.",
                "osu! API") { Depth = -20 },
            filterBand = new Container
            {
                Position = new(0, 76),
                RelativeSizeAxes = Axes.X,
                Height = 72,
                Masking = true,
                CornerRadius = AimModVisualStyle.ControlRadius,
                Depth = -20,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                    searchGroup = new Container
                    {
                        Children = new Drawable[]
                        {
                            filterLabel("SEARCH"),
                            searchBox = new OsuTextBox
                            {
                                Position = new(0, 17),
                                RelativeSizeAxes = Axes.X,
                                Height = AimModVisualStyle.CompactControlHeight,
                                PlaceholderText = "Title, artist, mapper, or tag",
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
                    categoryGroup = dropdownGroup("STATUS", categoryDropdown = new OsuDropdown<OfficialBeatmapCategory>
                    {
                        Items = new[] { OfficialBeatmapCategory.Any, OfficialBeatmapCategory.Ranked, OfficialBeatmapCategory.Loved, OfficialBeatmapCategory.Pending },
                        Current = category,
                    }),
                    sortGroup = dropdownGroup("SORT", sortDropdown = new OsuDropdown<OfficialBeatmapSort>
                    {
                        Items = new[] { OfficialBeatmapSort.Relevance, OfficialBeatmapSort.Updated, OfficialBeatmapSort.Plays },
                        Current = sort,
                    }),
                },
            },
            resultStatus = new TruncatingSpriteText
            {
                Y = 160,
                Text = "Connecting to osu!...",
                Font = new FontUsage(size: 11, weight: "SemiBold"),
                Colour = AimModPalette.Muted,
                Depth = -20,
            },
            resultViewport = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = 184 },
                Masking = true,
                Depth = 10,
                Child = new AimModScrollContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Child = results = new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Spacing = new(AimModVisualStyle.RelatedSpacing),
                        Padding = new MarginPadding { Right = content_inset, Bottom = 32 },
                    },
                },
            },
            loadingOverlay = new AimModLoadingOverlay(),
        };
    }

    protected override void Update()
    {
        base.Update();

        float width = Math.Max(640, DrawWidth);
        const float gap = AimModVisualStyle.RelatedSpacing;
        bool compact = width < 940;

        if (compact)
        {
            float columnWidth = (width - content_inset * 2 - gap) / 2;
            filterBand.Height = 128;
            placeGroup(searchGroup, content_inset, 8, columnWidth, 54);
            placeSlider(starSlider, content_inset + columnWidth + gap, 3, columnWidth);
            placeGroup(categoryGroup, content_inset, 68, columnWidth, 52);
            placeGroup(sortGroup, content_inset + columnWidth + gap, 68, columnWidth, 52);
            resultStatus.Y = 216;
            resultViewport.Padding = new MarginPadding { Top = 240 };
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
            resultStatus.Y = 160;
            resultViewport.Padding = new MarginPadding { Top = 184 };
        }

        resultStatus.MaxWidth = Math.Max(0, width - content_inset * 2);
    }

    private static void placeSlider(RangeSlider slider, float x, float y, float width)
    {
        slider.Anchor = Anchor.TopLeft;
        slider.Origin = Anchor.TopLeft;
        slider.Position = new(x, y);
        slider.Size = new(width, 62);
    }

    private static void placeGroup(Container group, float x, float y, float width, float height)
    {
        group.Position = new(x, y);
        group.Size = new(width, height);
    }

    private static SpriteText filterLabel(string value) => new()
    {
        Text = value,
        Font = new FontUsage(size: 8, weight: "Bold"),
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

    protected override void LoadComplete()
    {
        base.LoadComplete();
        searchBox.OnCommit += (_, _) => startSearch();
        minimumStars.BindValueChanged(_ => scheduleSearch());
        maximumStars.BindValueChanged(_ => scheduleSearch());
        category.BindValueChanged(_ => startSearch());
        sort.BindValueChanged(_ => startSearch());
        startSearch();
    }

    private void scheduleSearch()
    {
        requestCancellation?.Cancel();
        scheduledSearch?.Cancel();
        scheduledSearch = Scheduler.AddDelayed(startSearch, 250);
    }

    private void startSearch()
    {
        scheduledSearch?.Cancel();
        scheduledSearch = null;
        requestCancellation?.Cancel();
        requestCancellation?.Dispose();
        requestCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = requestCancellation.Token;
        IOfficialBeatmapDiscoveryClient? currentClient = client();
        if (currentClient is null)
        {
            connectionAttempts++;
            resultStatus.Text = connectionAttempts < 10
                ? "AimMod is still connecting to osu!lazer..."
                : "AimMod could not find a usable osu!lazer session.";
            if (connectionAttempts < 10)
                loadingOverlay.ShowLoading("Connecting to osu!", "Waiting for the signed-in lazer session");
            else
                loadingOverlay.HideLoading();
            results.Clear();
            if (connectionAttempts < 10)
                scheduledSearch = Scheduler.AddDelayed(startSearch, 1000);
            return;
        }

        connectionAttempts = 0;

        resultStatus.Text = "Searching osu!...";
        loadingOverlay.ShowLoading("Searching beatmaps", "Loading results from the osu! catalog");
        _ = searchAsync(currentClient, cancellationToken);
    }

    private async Task searchAsync(IOfficialBeatmapDiscoveryClient currentClient, CancellationToken cancellationToken)
    {
        try
        {
            OfficialBeatmapSearchResult response = await currentClient.SearchAsync(new OfficialBeatmapSearchQuery(
                searchBox.Current.Value,
                minimumStars.IsDefault ? null : minimumStars.Value,
                maximumStars.IsDefault ? null : maximumStars.Value,
                category.Value,
                sort.Value,
                Limit: result_limit), cancellationToken).ConfigureAwait(false);

            if (!IsDisposed)
                Schedule(() => applySearchResult(response));
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
                    resultStatus.Text = $"Could not search osu!: {error.Message}";
                    loadingOverlay.HideLoading();
                });
            }
        }
    }

    private void applySearchResult(OfficialBeatmapSearchResult response)
    {
        results.Clear();
        loadingOverlay.HideLoading();
        if (response.Status != OfficialBeatmapRequestStatus.Success)
        {
            resultStatus.Text = searchFailureMessage(response.Status);
            if (response.Status is OfficialBeatmapRequestStatus.SignedOut or
                OfficialBeatmapRequestStatus.TokenExpired or
                OfficialBeatmapRequestStatus.SessionUnavailable or
                OfficialBeatmapRequestStatus.SessionChanged)
                scheduledSearch = Scheduler.AddDelayed(startSearch, 5000);
            return;
        }

        foreach (OfficialBeatmapSet set in response.BeatmapSets)
            results.Add(new OnlineBeatmapCard(set, importBeatmap, installInLazer));

        resultStatus.Text = response.BeatmapSets.Count switch
        {
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

    private static string searchFailureMessage(OfficialBeatmapRequestStatus status) => status switch
    {
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
        private bool importing;
        private bool imported;
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
                            Font = new FontUsage(size: 9),
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
                new ClickableContainer
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
                            Colour = set.DownloadDisabled ? AimModPalette.PanelHover : AimModPalette.Pink,
                        },
                        actionText = new SpriteText
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            Text = set.DownloadDisabled ? "Unavailable" : "Save in AimMod",
                            Font = new FontUsage(size: 10, weight: "Bold"),
                            Colour = set.DownloadDisabled ? AimModPalette.Muted : AimModPalette.Canvas,
                        },
                    },
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            float available = Math.Max(80, DrawWidth - 190);
            titleText.MaxWidth = available;
            artistText.MaxWidth = available;
            detailText.MaxWidth = available;
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
            if (importing || installingInLazer || sentToLazer || set.DownloadDisabled)
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
            imported = result.Status == OnlineBeatmapImportStatus.Success;
            lazerArchive = result.LazerArchive;
            actionText.Text = result.Status switch
            {
                OnlineBeatmapImportStatus.Success when result.LazerArchive is not null => "Install in osu!lazer",
                OnlineBeatmapImportStatus.Success => "Saved in AimMod",
                OnlineBeatmapImportStatus.SignedOut => "Sign in to lazer",
                OnlineBeatmapImportStatus.TokenExpired => "Session refreshing",
                OnlineBeatmapImportStatus.Unauthorized => "Session refused",
                OnlineBeatmapImportStatus.SessionChanged => "Account changed",
                OnlineBeatmapImportStatus.NetworkError => "Network error",
                OnlineBeatmapImportStatus.DownloadDisabled => "Unavailable",
                OnlineBeatmapImportStatus.InvalidDownload => "Invalid download",
                OnlineBeatmapImportStatus.ServerError => "osu! server error",
                _ => "Import failed",
            };
            actionBackground.Colour = result.Status == OnlineBeatmapImportStatus.Success && result.LazerArchive is not null
                ? AimModPalette.Pink
                : result.Status == OnlineBeatmapImportStatus.Success
                    ? AimModPalette.Success
                : AimModPalette.PinkDark;
            actionText.Colour = result.Status == OnlineBeatmapImportStatus.Success
                ? AimModPalette.Canvas
                : AimModPalette.Text;
        }

        private void beginLazerInstall(LazerBeatmapArchive archive)
        {
            installingInLazer = true;
            actionText.Text = "Opening osu!lazer...";
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
                LazerBeatmapInstallStatus.Sent => "Sent to osu!lazer",
                LazerBeatmapInstallStatus.LazerStarted => "Opened in osu!lazer",
                LazerBeatmapInstallStatus.ArchiveUnavailable => "Saved in AimMod",
                LazerBeatmapInstallStatus.LazerNotFound => "osu!lazer not found",
                LazerBeatmapInstallStatus.LazerRejected => "osu!lazer refused it",
                _ => "Could not open osu!lazer",
            };
            actionBackground.Colour = sentToLazer ? AimModPalette.Success : AimModPalette.PinkDark;
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
                        Font = new FontUsage(size: 10, weight: "SemiBold"),
                        Colour = colour,
                        Padding = new MarginPadding { Horizontal = 10, Vertical = 4 },
                        MaxWidth = 150,
                    },
                };
            }
        }
    }
}
