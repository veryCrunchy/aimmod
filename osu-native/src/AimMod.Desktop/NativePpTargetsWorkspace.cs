using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Desktop.ScoreHistory;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
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
    private const int calculation_scan_limit = 2_000;
    private const int local_set_limit = 5_000;
    private const float content_inset = 12;

    private readonly ILocalLibrarySource source;
    private readonly ILocalLibrarySourceChanged? sourceChanges;
    private readonly Func<IOfficialBeatmapDiscoveryClient?> client;
    private readonly Func<OnlineBeatmapImportService?> importer;
    private readonly Func<IPpTargetExactCalculationService?> exactCalculator;
    private readonly Func<ILocalScorePpHydrationService?> localPpHydrator;
    private readonly Func<OfficialOsuApiClient?> officialApi;
    private readonly Func<IAccountScoreHistoryService?> accountHistory;
    private readonly Func<string?> activePlayer;
    private readonly Func<int, CancellationToken, Task>? openBeatmap;
    private readonly IReadOnlyDictionary<Guid, ReplayAnalysisResult> replayAnalyses;
    private IReadOnlyList<LocalReplay> patternHistory = [];
    private int observedAnalysisCount = -1;
    private ScheduledDelegate? scheduledPatternRefresh;
    private CancellationTokenSource? patternRefresh;
    private string skillProgress = string.Empty;
    private PpTargetCatalogScanner? catalogScanner;
    private IOfficialBeatmapDiscoveryClient? scannerClient;
    private bool exactScanRunning;
    private bool catalogScanRunning;
    private string catalogScanStatus = string.Empty;
    private PpPatternProfile? pendingPatternProfile;
    private string? catalogQueryIdentity;
    private DateTimeOffset? catalogUpdatedAt;
    private int renderGeneration;
    private readonly PpTargetWorkspaceCache? workspaceCache;
    private readonly AimModSearchBox search;
    private readonly TruncatingSpriteText status;
    private readonly TruncatingSpriteText profileSummary;
    private readonly TruncatingSpriteText scanHint;
    private readonly System.Diagnostics.Stopwatch refreshElapsed = new();
    private bool firstScan = true;
    private bool refreshIndeterminate;
    private bool automaticRefreshPending;
    private bool patternBuildRunning;
    private bool exactScanWaitingForPatterns;
    private bool skillAnalysisRunning;
    private string? lastCompletedScanIdentity;
    internal static string ScanHint(bool first) => first
        ? "First scan takes longer. Saved map analysis makes later refreshes faster."
        : "Reusing saved analysis. New maps and recent plays may still need calculation.";
    private DateTimeOffset nextFormCheck;
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
    private readonly Container modsGroup;
    private readonly PpTargetDropdown<string> modsDropdown;
    private readonly Dictionary<string,LocalReplay> observedModSetups = new();
    private PpTargetPreferenceProfile selectedModProfile(PpTargetPreferenceProfile value)
    {
        return resolveModProfile(value, selectedMods.Value, patternHistory, observedModSetups.GetValueOrDefault(selectedMods.Value));
    }
    private static PpTargetPreferenceProfile resolveModProfile(PpTargetPreferenceProfile value, string selection,
        IReadOnlyList<LocalReplay> history, LocalReplay? run)
    {
        if (selection == "Automatic")
            run = history.Where(ScoreMods.IsManualPlay)
                .Where(r=>PpTargetMods.Normalise(r.Mods).SequenceEqual(value.PreferredModSetup ?? []))
                .GroupBy(ScoreMods.Configuration).OrderByDescending(g=>g.Count()).Select(g=>g.First()).FirstOrDefault();
        return run is not null
            ? value with { PreferredModSetup=ScoreMods.Acronyms(run), PreferredModsJson=run.ModsJson, LegacyScore=run.LegacyScore || run.Origin == LocalLibraryOrigin.Stable }
            : WithSelectedMods(value,selection) with { PreferredModsJson=null };
    }
    private readonly Bindable<string> selectedMods = new("Automatic");
    internal static readonly string[] ModChoices = ["Automatic", "NM", "HD", "HR", "DT", "NC", "HD+DT", "HD+NC", "HD+HR", "HR+DT", "HD+HR+DT", "HT", "EZ", "HD+EZ", "FL", "HD+FL"];
    internal static PpTargetPreferenceProfile WithSelectedMods(PpTargetPreferenceProfile value, string mods) =>
        mods == "Automatic" ? value : value with { PreferredModSetup = PpTargetMods.Normalise(mods.Split('+')) };
    private readonly Container resultViewport;
    private readonly OsuScrollContainer resultScroll;
    private readonly Container detailViewport;
    private int? selectedBeatmapId;
    private bool detailOpen;
    private PpTargetDetails? selectedDetails;
    private readonly Dictionary<int, PpTargetRow> targetRows = new();
    private readonly PpTargetWorkspaceState workspaceState;
    private readonly AimModStarRatingFilter starSlider;
    private readonly ShearedRangeSlider expectedPpSlider;
    private readonly ShearedRangeSlider maximumPpSlider;
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
    private ScheduledDelegate? scheduledResults;
    private ScheduledDelegate? scheduledRows;
    private readonly LatestBackgroundQuery<PpTargetCandidate[]> ranking = new();
    private readonly LatestBackgroundQuery<bool> snapshotWriter = new();
    private IReadOnlyList<LocalBeatmapSet>? installedIndexSource;
    private HashSet<int> installedBeatmapIds = new();
    private PpTargetPreferenceProfile profile = PpTargetPreferenceProfile.Empty;
    private IReadOnlyList<OfficialBeatmapSet> catalog = Array.Empty<OfficialBeatmapSet>();
    private IReadOnlyList<LocalBeatmapSet> localSets = Array.Empty<LocalBeatmapSet>();
    private readonly HashSet<int> savedSetIds = new();

    internal static bool HasInstalledDifficulty(IEnumerable<LocalBeatmapSet> sets, int beatmapId) =>
        beatmapId > 0 && sets.Any(set => set.Difficulties.Any(difficulty => difficulty.OnlineId == beatmapId));

    private bool isInstalled(PpTargetCandidate candidate)
    {
        if (!ReferenceEquals(installedIndexSource, localSets))
        {
            installedBeatmapIds = localSets.SelectMany(set => set.Difficulties)
                .Select(difficulty => difficulty.OnlineId).Where(id => id > 0).ToHashSet();
            installedIndexSource = localSets;
        }
        return savedSetIds.Contains(candidate.BeatmapSetId) || installedBeatmapIds.Contains(candidate.BeatmapId);
    }
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
        Func<IAccountScoreHistoryService?>? accountHistory = null,
        Func<int, CancellationToken, Task>? openBeatmap = null,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult>? replayAnalyses = null,
        Func<string?>? activePlayer = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.importer = importer ?? throw new ArgumentNullException(nameof(importer));
        this.exactCalculator = exactCalculator ?? (() => null);
        this.localPpHydrator = localPpHydrator ?? (() => null);
        this.officialApi = officialApi ?? (() => null);
        this.workspaceCache = workspaceCache;
        this.accountHistory = accountHistory ?? (() => null);
        this.activePlayer = activePlayer ?? (() => null);
        this.openBeatmap = openBeatmap;
        this.replayAnalyses = replayAnalyses ?? new Dictionary<Guid, ReplayAnalysisResult>();
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
                        "Targets matched to your recent performance, strengths, and preferred difficulty.",
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
                            refreshProgressFill = new Box { RelativeSizeAxes = Axes.Both, Width = 0, Colour = AimModPalette.Accent },
                        },
                    },
                    filterBand = new Container
                    {
                        // Menus extend beyond the band; its entire subtree must precede status siblings.
                        Depth = -1,
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
                                },
                            },
                            searchGroup = new Container
                            {
                                Children = new Drawable[]
                                {
                                    filterLabel("FIND A MAP"),
                                    search = new AimModSearchBox
                                    {
                                        Position = new(0, 18),
                                        RelativeSizeAxes = Axes.X,
                                        Height = AimModVisualStyle.CompactControlHeight,
                                        SearchHint = "Title, artist or mapper",
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
                            expectedPpSlider = new ShearedRangeSlider("Expected PP")
                            {
                                LowerBound = minimumExpectedPp,
                                UpperBound = maximumExpectedPp,
                                DefaultStringLowerBound = "0",
                                DefaultStringUpperBound = "1000+",
                                NubWidth = 52,
                            },
                            maximumPpSlider = new ShearedRangeSlider("Max PP")
                            {
                                LowerBound = minimumMaximumPp,
                                UpperBound = maximumMaximumPp,
                                DefaultStringLowerBound = "0",
                                DefaultStringUpperBound = "1000+",
                                NubWidth = 52,
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
                            modsGroup = dropdownGroup("MODS", modsDropdown = new PpTargetDropdown<string>(value => value)
                            {
                                Items = ModChoices,
                                Current = selectedMods,
                            }),
                        },
                    },
                    new AimModResetButton(() => {
                        search.Current.Value = string.Empty; minimumStars.Value = 0; maximumStars.Value = 10;
                        minimumExpectedPp.Value = 0; maximumExpectedPp.Value = 1000;
                        minimumMaximumPp.Value = 0; maximumMaximumPp.Value = 1000;
                        category.Value = OfficialBeatmapCategory.Ranked; length.Value = TargetLength.Any;
                        sort.Value = TargetSort.BestFit; selectedMods.Value = "Automatic";
                        scheduleCatalogSearch();
                    }) { Anchor = Anchor.TopRight, Origin = Anchor.TopRight, X = -128, Y = 56 },
                    status = truncatingText("Loading local history...", 10, AimModPalette.Muted, "SemiBold").With(drawable => drawable.Position = new(0, 245)),
                    scanHint = truncatingText(string.Empty, 10, AimModPalette.Muted),
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
                    resultScroll = new AimModScrollContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        Child = results = new FillFlowContainer<Drawable>
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Spacing = new(AimModVisualStyle.RelatedSpacing),
                            Padding = new MarginPadding { Right = content_inset, Bottom = 32 },
                        },
                    },
                    workspaceState = new PpTargetWorkspaceState(),
                    detailViewport = new Container { RelativeSizeAxes = Axes.Both, Alpha = 0, Depth = -1 },
                },
            },
            loadingOverlay = new AimModLoadingOverlay(),
        };
    }

    protected override void Update()
    {
        base.Update();
        if (profile.PlayerName is not null && activePlayer() is { Length: > 0 } player && !CacheMatchesPlayer(profile, player))
        {
            profile = PpTargetPreferenceProfile.Empty with { PlayerName = player.Trim() };
            patternHistory = [];
            pendingPatternProfile = null;
            exactEstimates = new Dictionary<int, PpTargetEstimate>();
            hasVisibleSnapshot = false;
            lastCompletedScanIdentity = null;
            renderResults();
            reloadProfile();
        }
        if (refreshElapsed.IsRunning)
        {
            scanHint.Text = $"{ScanHint(firstScan)}  /  {refreshElapsed.Elapsed:mm\\:ss} elapsed";
            if (refreshIndeterminate)
                refreshProgressFill.X = (float)(.5 + .5 * Math.Sin(Time.Current / 650)) * Math.Max(0, refreshProgress.DrawWidth * .82f);
        }

        if (DateTimeOffset.UtcNow >= nextFormCheck)
        {
            nextFormCheck = DateTimeOffset.UtcNow.AddMinutes(1);
            if (profile.PatternProfile?.SessionForm is { Patterns.Count: > 0 } form && form.ExpiresAt <= DateTimeOffset.UtcNow)
                scheduledPatternRefresh ??= Scheduler.AddDelayed(refreshPatternEvidence, 0);
        }

        if (patternHistory.Count > 0 && observedAnalysisCount != replayAnalyses.Count)
        {
            observedAnalysisCount = replayAnalyses.Count;
            if (skillAnalysisRunning) automaticRefreshPending = true;
            else scheduledPatternRefresh ??= Scheduler.AddDelayed(refreshPatternEvidence, 2_000);
        }

        float width = Math.Max(640, DrawWidth);
        bool compact = width < 1_120;
        float headerHeight = compact ? 322 : 250;
        filterHeader.Height = headerHeight;
        resultViewport.Padding = new MarginPadding { Top = headerHeight + 7 };
        workspaceState.Y = compact ? -28 : 0;

        if (compact)
        {
            const float columnGap = 12;
            float columnWidth = (width - content_inset * 2 - columnGap) / 2;
            float secondColumnX = content_inset + columnWidth + columnGap;
            filterBand.Height = 187;
            placeGroup(searchGroup, content_inset, 8, columnWidth, 56);
            placeSlider(starSlider, secondColumnX, 3, columnWidth);
            placeSlider(expectedPpSlider, content_inset, 65, columnWidth);
            placeSlider(maximumPpSlider, secondColumnX, 65, columnWidth);

            float dropdownWidth = (width - 60) / 4;
            placeGroup(categoryGroup, content_inset, 126, dropdownWidth, 55);
            placeGroup(lengthGroup, 24 + dropdownWidth, 126, dropdownWidth, 55);
            placeGroup(sortGroup, 36 + dropdownWidth * 2, 126, dropdownWidth, 55);
            placeGroup(modsGroup, 48 + dropdownWidth * 3, 126, dropdownWidth, 55);
            status.Position = new(0, 296);
            resultCount.Position = new(0, 296);
        }
        else
        {
            float searchWidth = Math.Clamp(width * 0.22f, 250, 310);
            float sliderWidth = Math.Max(190, (width - searchWidth - 78) / 3);
            float starX = 30 + searchWidth;
            float expectedX = starX + sliderWidth + 18;
            float maximumX = expectedX + sliderWidth + 18;
            filterBand.Height = 120;
            placeGroup(searchGroup, content_inset, 8, searchWidth, 58);
            placeSlider(starSlider, starX, 3, sliderWidth);
            placeSlider(expectedPpSlider, expectedX, 3, sliderWidth);
            placeSlider(maximumPpSlider, maximumX, 3, sliderWidth);

            float dropdownWidth = (width - 60) / 4;
            placeGroup(categoryGroup, content_inset, 64, dropdownWidth, 55);
            placeGroup(lengthGroup, 24 + dropdownWidth, 64, dropdownWidth, 55);
            placeGroup(sortGroup, 36 + dropdownWidth * 2, 64, dropdownWidth, 55);
            placeGroup(modsGroup, 48 + dropdownWidth * 3, 64, dropdownWidth, 55);
            status.Position = new(0, 224);
            resultCount.Position = new(0, 224);
        }

        status.MaxWidth = width * 0.62f;
        scanHint.Position = new(0, status.Y + 14);
        scanHint.MaxWidth = width;
        resultCount.MaxWidth = width * 0.34f;
        profileSummary.MaxWidth = Math.Max(180, width - 260);
        bool sidebar = DrawWidth >= 1120;
        bool showDetails = selectedDetails is not null && (sidebar || detailOpen);
        float paneWidth = sidebar ? 340 : DrawWidth;
        resultScroll.Width = sidebar && showDetails ? Math.Max(0, (DrawWidth - paneWidth - 12) / DrawWidth) : 1;
        resultScroll.Alpha = !sidebar && showDetails ? 0 : 1;
        detailViewport.Alpha = showDetails ? 1 : 0;
        detailViewport.Width = DrawWidth > 0 ? paneWidth / DrawWidth : 1;
        detailViewport.X = sidebar ? DrawWidth - paneWidth : 0;
        selectedDetails?.SetBackVisible(!sidebar);
    }

    private static void placeSlider(Drawable slider, float x, float y, float width)
    {
        slider.Anchor = Anchor.TopLeft;
        slider.Origin = Anchor.TopLeft;
        slider.Position = new(x, y + 16);
        slider.Size = new(width, 30);
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
        search.Committed += () => startCatalogSearch();
        search.Current.BindValueChanged(_ => filterChanged(scheduleCatalogSearch));
        minimumStars.BindValueChanged(_ => filterChanged(scheduleCatalogSearch));
        maximumStars.BindValueChanged(_ => filterChanged(scheduleCatalogSearch));
        minimumExpectedPp.BindValueChanged(_ => filterChanged(scheduleResults));
        maximumExpectedPp.BindValueChanged(_ => filterChanged(scheduleResults));
        minimumMaximumPp.BindValueChanged(_ => filterChanged(scheduleResults));
        maximumMaximumPp.BindValueChanged(_ => filterChanged(scheduleResults));
        category.BindValueChanged(_ => filterChanged(startCatalogSearch));
        selectedMods.BindValueChanged(_ => filterChanged(() =>
        {
            exactCalculation?.Cancel();
            exactEstimates = new Dictionary<int, PpTargetEstimate>();
            renderResults();
            startExactCalculations();
            saveSnapshot();
        }));
        length.BindValueChanged(_ => filterChanged(renderResults));
        sort.BindValueChanged(_ => { if (!suppressFilterEvents) { renderResults(); saveSnapshot(); } });

        _ = restoreSnapshotAsync(renderGeneration);
    }

    private async Task restoreSnapshotAsync(int generation)
    {
        var snapshot = await Task.Run(() => workspaceCache?.Load()).ConfigureAwait(false);
        if (IsDisposed) return;
        Schedule(() =>
        {
            if (IsDisposed || profileRefresh is not null) return;
            // A late disk read must not overwrite filters the player has already changed.
            if (generation != renderGeneration || snapshot is null || !CacheMatchesPlayer(snapshot.Profile, activePlayer()))
            {
                reloadProfile();
                return;
            }
            applySnapshot(snapshot);
            if (!workspaceCache!.IsFresh(snapshot)) reloadProfile();
            else
            {
                status.Text = withCatalogStatus($"Ready from cache  /  updated {relativeAge(snapshot.CachedAt)}");
                replaceToken(ref profileRefresh);
                _ = loadPatternHistoryAsync(profileRefresh!.Token);
            }
        });
    }

    internal static bool CacheMatchesPlayer(PpTargetPreferenceProfile cached, string? player) =>
        !string.IsNullOrWhiteSpace(player) && string.Equals(cached.PlayerName, player.Trim(), StringComparison.OrdinalIgnoreCase);

    private void filterChanged(Action action)
    {
        if (!suppressFilterEvents)
            action();
    }

    private void applySnapshot(PpTargetWorkspaceSnapshot snapshot)
    {
        firstScan = false;
        suppressFilterEvents = true;
        profile = snapshot.Profile ?? PpTargetPreferenceProfile.Empty;
        pendingPatternProfile = snapshot.PendingPatternProfile;
        localSets = snapshot.LocalSets ?? [];
        catalog = snapshot.Catalog ?? [];
        catalogQueryIdentity = snapshot.CatalogQueryIdentity;
        catalogUpdatedAt = snapshot.CatalogUpdatedAt;
        exactEstimates = snapshot.ExactEstimates ?? new Dictionary<int, PpTargetEstimate>();
        onlineBestCount = snapshot.OnlineBestCount;
        scoreDataStatus = snapshot.ScoreDataStatus ?? string.Empty;
        catalogScanStatus = snapshot.CatalogScanStatus ?? string.Empty;
        search.Current.Value = snapshot.SearchText ?? string.Empty;
        minimumStars.Value = Math.Clamp(snapshot.MinimumStars, 0, 10);
        maximumStars.Value = Math.Clamp(snapshot.MaximumStars, 0, 10);
        category.Value = snapshot.Category;
        sort.Value = Enum.TryParse(snapshot.Sort, out TargetSort savedSort) && Enum.IsDefined(savedSort) ? savedSort : TargetSort.BestFit;
        selectedMods.Value = ModChoices.Contains(snapshot.ModSelection) ? snapshot.ModSelection : "Automatic";
        suppressFilterEvents = false;
        setsById = catalog.GroupBy(set => set.BeatmapSetId).ToDictionary(group => group.Key, group => group.First());
        hasVisibleSnapshot = catalog.Count > 0;
        updateProfileSummary();
        renderResults();
    }

    private void reloadProfile()
    {
        automaticRefreshPending = false;
        patternBuildRunning = false;
        exactScanWaitingForPatterns = false;
        scheduledSearch?.Cancel();
        cancelToken(ref catalogSearch);
        cancelToken(ref exactCalculation);
        exactScanRunning = false;
        catalogScanRunning = true;
        if (pendingPatternProfile is { } retained)
            profile = profile with { PatternProfile = retained };
        pendingPatternProfile = null;
        renderGeneration++;
        cancelToken(ref patternRefresh);
        scheduledPatternRefresh?.Cancel();
        scheduledPatternRefresh = null;
        replaceToken(ref profileRefresh);
        firstScan = !hasVisibleSnapshot;
        if (!hasVisibleSnapshot)
        {
            loadingOverlay.LoadingHint = ScanHint(firstScan);
            loadingOverlay.ShowLoading("Preparing your first PP targets", "Reading your score history");
        }
        showRefresh("Reading your score history", 0, 0);
        _ = loadProfileAsync(profileRefresh!.Token);
    }

    private async Task loadProfileAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var loadingProgress = new ScanProgress<LocalScorePpHydrationProgress>(action => Schedule(action),
                () => !IsDisposed && !cancellationToken.IsCancellationRequested,
                value => showRefresh("Updating PP for your recorded plays", value.Completed, value.Total));
            StatisticsHistoryLoadResult history = await StatisticsHistoryLoader.LoadAsync(source, cancellationToken).ConfigureAwait(false);
            OnlineAccountScoreHistoryResult? online = await loadOnlineScores(cancellationToken).ConfigureAwait(false);
            string? player = activePlayer() ?? online?.Profile?.Username;
            if (!string.IsNullOrWhiteSpace(player) && online?.Profile is { } owner
                && !string.Equals(player, owner.Username, StringComparison.OrdinalIgnoreCase)) online = null;
            history = history with { Runs = PpTargetSkillHistory.ForPlayer(history.Runs, player) };
            LocalScorePpHydrationResult? hydration = null;
            if (localPpHydrator() is { } hydrator)
            {
                hydration = await hydrator.HydrateAsync(history.Runs, cancellationToken, loadingProgress).ConfigureAwait(false);
            }
            loadingProgress.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<LocalBeatmapSet> loadedSets = await loadLocalSets(cancellationToken).ConfigureAwait(false);
            if (!IsDisposed)
                Schedule(() =>
                {
                    if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                        showRefresh("Checking your submitted scores", 0, 0);
                });
            IReadOnlyList<LocalReplay> runs = PpTargetSkillHistory.Merge(
                hydration?.Runs ?? history.Runs,
                online?.Scores ?? [], loadedSets);
            PpTargetPreferenceProfile next = PpTargetPreferenceProfiler.Build(runs, loadedSets) with
            {
                Opportunities = PpTargetOpportunityModel.Build(PpTargetSkillHistory.PassHistory(hydration?.Runs ?? history.Runs, online?.Scores ?? [], loadedSets)),
                PlayerName = player?.Trim() ?? history.Runs.FirstOrDefault()?.Player.Trim(),
            };
            if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                Schedule(() =>
                {
                    if (IsDisposed || cancellationToken.IsCancellationRequested)
                        return;
                    patternHistory = runs;
                    localSets = loadedSets;
                    refreshPatternEvidence();
                    applyProfile(next, loadedSets, hydration, online);
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!IsDisposed)
                Schedule(() =>
                {
                    if (IsDisposed || cancellationToken.IsCancellationRequested)
                        return;
                    loadingOverlay.HideLoading();
                    catalogScanRunning = false;
                    hideRefresh();
                    status.Text = $"Could not build the PP profile: {error.Message}";
                    if (!hasVisibleSnapshot)
                        workspaceState.ShowState(FontAwesome.Solid.ExclamationTriangle, "PP profile unavailable", "Refresh to try loading your local and submitted score history again.");
                });
        }
    }

    private async Task loadPatternHistoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            StatisticsHistoryLoadResult history = await StatisticsHistoryLoader.LoadAsync(source, cancellationToken).ConfigureAwait(false);
            OnlineAccountScoreHistoryResult? online = await loadOnlineScores(cancellationToken).ConfigureAwait(false);
            string? player = activePlayer() ?? online?.Profile?.Username;
            if (!string.IsNullOrWhiteSpace(player) && online?.Profile is { } owner
                && !string.Equals(player, owner.Username, StringComparison.OrdinalIgnoreCase)) online = null;
            history = history with { Runs = PpTargetSkillHistory.ForPlayer(history.Runs, player) };
            LocalScorePpHydrationResult? hydration = localPpHydrator() is { } hydrator
                ? await hydrator.HydrateAsync(history.Runs, cancellationToken).ConfigureAwait(false) : null;
            IReadOnlyList<LocalBeatmapSet> maps = await loadLocalSets(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<LocalReplay> runs = PpTargetSkillHistory.Merge(hydration?.Runs ?? history.Runs, online?.Scores ?? [], maps);
            if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                Schedule(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;
                    patternHistory = runs;
                    localSets = maps;
                    if (online is not null && online.RecentCoverage.IsSuccess)
                        profile = profile with { Opportunities = PpTargetOpportunityModel.Build(PpTargetSkillHistory.PassHistory(hydration?.Runs ?? history.Runs, online.Scores, maps)) };
                    refreshPatternEvidence();
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"PP target skill history refresh failed: {error}");
            if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                Schedule(() =>
                {
                    if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                        status.Text = "Skill history could not refresh. Showing saved targets.";
                });
        }
    }

    private void refreshPatternEvidence()
    {
        scheduledPatternRefresh?.Cancel();
        scheduledPatternRefresh = null;
        if (IsDisposed)
            return;
        observedAnalysisCount = replayAnalyses.Count;
        patternBuildRunning = true;
        replaceToken(ref patternRefresh);
        var snapshot = new Dictionary<Guid, ReplayAnalysisResult>(replayAnalyses);
        _ = rebuildPatternEvidenceAsync(patternHistory, localSets, snapshot, patternRefresh!.Token);
    }

    public void RefreshSkillEvidence()
    {
        if (IsDisposed)
            return;
        scheduledPatternRefresh?.Cancel();
        if (skillAnalysisRunning)
        {
            scheduledPatternRefresh = null;
            automaticRefreshPending = true;
            return;
        }
        scheduledPatternRefresh = Scheduler.AddDelayed(requestAutomaticRefresh, 5_000);
    }

    private void requestAutomaticRefresh()
    {
        scheduledPatternRefresh = null;
        if (IsDisposed) return;
        if (catalogScanRunning || exactScanRunning || patternBuildRunning || skillAnalysisRunning)
        {
            automaticRefreshPending = true;
            return;
        }
        reloadProfile();
        showRefresh("Updating targets with your latest plays", 0, 0);
    }

    public void SetSkillAnalysisProgress(int completed, int total)
    {
        bool wasRunning = skillAnalysisRunning;
        skillAnalysisRunning = total > completed;
        skillProgress = total > completed ? $"  /  Skill replays {completed:N0}/{total:N0}" : string.Empty;
        updateProfileSummary();
        if (wasRunning && !skillAnalysisRunning && automaticRefreshPending) RefreshSkillEvidence();
    }

    private async Task rebuildPatternEvidenceAsync(
        IReadOnlyList<LocalReplay> runs,
        IReadOnlyList<LocalBeatmapSet> maps,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        CancellationToken cancellationToken)
    {
        try
        {
            PpPatternProfile? previous = pendingPatternProfile ?? profile.PatternProfile;
            PpPatternProfile patterns = await Task.Run(
                () => PpTargetPatternModel.BuildProfile(runs, analyses, localSets: maps, cachedProfile: previous), cancellationToken).ConfigureAwait(false);
            if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                Schedule(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;
                    patternBuildRunning = false;
                    if (profile.PatternProfile?.Identity == patterns.Identity)
                    {
                        if (!exactScanRunning && !catalogScanRunning)
                        {
                            if (exactScanWaitingForPatterns) startExactCalculations();
                            else if (automaticRefreshPending) requestAutomaticRefresh();
                        }
                        return;
                    }
                    if (exactScanRunning || catalogScanRunning || (skillAnalysisRunning && !exactScanWaitingForPatterns))
                    {
                        if (skillAnalysisRunning) automaticRefreshPending = true;
                        pendingPatternProfile = patterns;
                        updateProfileSummary();
                        saveSnapshot();
                        return;
                    }
                    profile = profile with { PatternProfile = patterns };
                    exactEstimates = new Dictionary<int, PpTargetEstimate>();
                    updateProfileSummary();
                    saveSnapshot();
                    startExactCalculations();
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"PP target pattern profile refresh failed: {error}");
            if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                Schedule(() =>
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    patternBuildRunning = false;
                    if (!exactScanRunning && !catalogScanRunning && exactScanWaitingForPatterns) startExactCalculations();
                    status.Text = "Could not refresh skill evidence. Refresh to retry.";
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
        profile = next with { PatternProfile = pendingPatternProfile ?? profile.PatternProfile };
        observedModSetups.Clear();
        foreach(var run in patternHistory.Where(ScoreMods.IsManualPlay)) {
            string label = "Saved: " + ScoreMods.Display(run);
            observedModSetups.TryAdd(label,run);
        }
        modsDropdown.Items = ModChoices.Concat(observedModSetups.Keys.Order()).ToArray();
        localSets = loadedSets;
        // Keep the visible estimates; the ranker checks profile/mod/score inputs and
        // rejects stale values. Refresh must not reset the user's filters or catalog.
        if (!hasVisibleSnapshot && next.PreferredStarRange is { } stars)
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
        catalogScanRunning = true;
        cancelToken(ref catalogSearch);
        cancelToken(ref exactCalculation);
        exactScanRunning = false;
        renderGeneration++;
        scheduledSearch?.Cancel();
        scheduledSearch = Scheduler.AddDelayed(startCatalogSearch, 250);
    }

    private void startCatalogSearch()
    {
        catalogScanRunning = true;
        scheduledSearch?.Cancel();
        scheduledSearch = null;
        replaceToken(ref catalogSearch);
        cancelToken(ref exactCalculation);
        exactScanRunning = false;
        renderGeneration++;
        var query = new OfficialBeatmapSearchQuery(search.Current.Value,
            minimumStars.Value <= 0 ? null : minimumStars.Value,
            maximumStars.Value >= 10 ? null : maximumStars.Value,
            category.Value, OfficialBeatmapSort.Rating, Limit: 50);
        if (catalog.Count > 0 && CanReuseCatalog(query, catalogQueryIdentity, catalogUpdatedAt, DateTimeOffset.UtcNow))
        {
            catalogScanRunning = false;
            status.Text = withCatalogStatus("Updating your targets from saved beatmap analysis");
            renderResults();
            startExactCalculations();
            return;
        }
        catalogQueryIdentity = null;
        catalogUpdatedAt = null;
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
        if (!ReferenceEquals(scannerClient, currentClient))
        {
            scannerClient = currentClient;
            catalogScanner = new PpTargetCatalogScanner(currentClient);
        }
        _ = searchCatalogAsync(catalogScanner!, query, catalogSearch!.Token);
    }

    internal static bool CanReuseCatalog(OfficialBeatmapSearchQuery query, string? identity, DateTimeOffset? updated, DateTimeOffset now) =>
        updated is { } time && time <= now && now - time < PpTargetWorkspaceCache.Freshness
        && identity == System.Text.Json.JsonSerializer.Serialize(query);

    private async Task searchCatalogAsync(PpTargetCatalogScanner scanner, OfficialBeatmapSearchQuery query, CancellationToken cancellationToken)
    {
        try
        {
            using var progress = new ScanProgress<PpTargetCatalogScanProgress>(action => Schedule(action), () => !IsDisposed && !cancellationToken.IsCancellationRequested, value =>
            {
                showRefresh($"Searching catalog: {value.Pages:N0} pages / {value.Sets:N0} sets / {value.Difficulties:N0} difficulties", 0, 0);
            });
            using var preview = new ScanProgress<PpTargetCatalogScanResult>(action => Schedule(action), () => !IsDisposed && !cancellationToken.IsCancellationRequested, value =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                applyCatalog(new OfficialBeatmapSearchResult(OfficialBeatmapRequestStatus.Success, value.BeatmapSets, value.SetCount, true), preview: true);
            });
            PpTargetCatalogScanResult scan = await scanner.ScanAsync(query, cancellationToken, progress,
                profile.PreferredStarRange, preview).ConfigureAwait(false);
            progress.Dispose();
            preview.Dispose();
            if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                Schedule(() =>
                {
                    if (IsDisposed || cancellationToken.IsCancellationRequested)
                        return;
                    catalogScanRunning = false;
                    catalogScanStatus = CatalogScanSummary(scan);
                    if (scan.SetCount > 0 && scan.Status == OfficialBeatmapRequestStatus.Success)
                    {
                        catalogQueryIdentity = System.Text.Json.JsonSerializer.Serialize(query);
                        catalogUpdatedAt = DateTimeOffset.UtcNow;
                    }
                    applyCatalog(new OfficialBeatmapSearchResult(scan.SetCount > 0 ? OfficialBeatmapRequestStatus.Success : scan.Status,
                        scan.BeatmapSets, scan.SetCount, scan.IsPartial));
                });
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
                    if (IsDisposed || cancellationToken.IsCancellationRequested)
                        return;
                    status.Text = $"Could not search the osu! catalog: {error.Message}";
                    catalogScanRunning = false;
                    hideRefresh();
                    if (!hasVisibleSnapshot)
                        workspaceState.ShowState(FontAwesome.Solid.ExclamationTriangle, "Beatmap search unavailable", "Check your connection and refresh to search again.");
                });
            }
        }
    }

    private void applyCatalog(OfficialBeatmapSearchResult response, bool preview = false)
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
        setsById = catalog.GroupBy(set => set.BeatmapSetId).ToDictionary(group => group.Key, group => group.First());
        hasVisibleSnapshot = catalog.Count > 0;
        if (preview)
        {
            renderResults();
            saveSnapshot();
            return;
        }
        status.Text = profile.PpSampleCount == 0
            ? "No complete PP results are available for recommendations."
            : scoreDataStatus.Length > 0
                ? scoreDataStatus
                : "Recommendations are based on your calculated and submitted PP results.";
        status.Text = withCatalogStatus(status.Text.ToString());
        renderResults();
        saveSnapshot();
        startExactCalculations();
    }

    private void startExactCalculations()
    {
        if (catalogScanRunning) return;
        if (patternBuildRunning)
        {
            exactScanWaitingForPatterns = true;
            showRefresh("Updating your skill profile", 0, 0);
            return;
        }
        exactScanWaitingForPatterns = false;
        cancelToken(ref exactCalculation);
        exactScanRunning = false;
        if (pendingPatternProfile is { } pending)
        {
            pendingPatternProfile = null;
            profile = profile with { PatternProfile = pending };
        }
        IPpTargetExactCalculationService? calculator = exactCalculator();
        if (calculator is null || profile.TypicalAccuracy is null || catalog.Count == 0)
        {
            if (!catalogScanRunning) hideRefresh();
            return;
        }

        replaceToken(ref exactCalculation);
        exactScanRunning = true;
        showRefresh("Choosing maps that fit your skills", 0, 0);
        var filters = new PpTargetFilters(
            MinimumStars: minimumStars.Value <= 0 ? null : minimumStars.Value,
            MaximumStars: maximumStars.Value >= 10 ? null : maximumStars.Value,
            Statuses: category.Value == OfficialBeatmapCategory.Any ? null : [PpTargetStatus.FromCategory(category.Value)]);
        _ = planExactScanAsync(calculator, selectedModProfile(profile), catalog, localSets, filters, exactCalculation!.Token);
    }

    private async Task planExactScanAsync(IPpTargetExactCalculationService calculator,
        PpTargetPreferenceProfile scanProfile, IReadOnlyList<OfficialBeatmapSet> scanCatalog,
        IReadOnlyList<LocalBeatmapSet> installedSets, PpTargetFilters filters, CancellationToken cancellationToken)
    {
        try
        {
            var candidates = await Task.Run(() => PpTargetScanPlanner.Select(scanProfile, scanCatalog, filters, calculation_scan_limit), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<int, LocalBeatmapDifficulty> installed = installedSets.SelectMany(set => set.Difficulties)
                .Where(difficulty => difficulty.OnlineId > 0 && !string.IsNullOrWhiteSpace(difficulty.BeatmapHash))
                .GroupBy(difficulty => difficulty.OnlineId)
                .ToDictionary(group => group.Key, group => group.First());
            PpTargetExactRequest[] requests = candidates
                .Select(candidate => new PpTargetExactRequest(
                    candidate.BeatmapId,
                    installed.GetValueOrDefault(candidate.BeatmapId)?.BeatmapHash,
                    candidate.SuggestedMods,
                    candidate.ExpectedScoreAccuracy ?? scanProfile.TypicalAccuracy!.Value,
                    candidate.Attainability,
                    scanProfile.PatternProfile, scanProfile.PreferredModsJson, scanProfile.LegacyScore))
                .ToArray();
            if (requests.Length == 0)
            {
                if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                    Schedule(() =>
                    {
                        if (IsDisposed || cancellationToken.IsCancellationRequested) return;
                        exactScanRunning = false;
                        hideRefresh();
                    });
                return;
            }

            string identity = ScanIdentity(requests, catalogUpdatedAt);
            if (identity == lastCompletedScanIdentity && requests.All(r => exactEstimates.ContainsKey(r.BeatmapId)))
            {
                if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                    Schedule(() =>
                    {
                        if (IsDisposed || cancellationToken.IsCancellationRequested) return;
                        exactScanRunning = false;
                        status.Text = withCatalogStatus("Targets are up to date.");
                        renderResults();
                        hideRefresh();
                        if (!skillAnalysisRunning && pendingPatternProfile is { } pending)
                        {
                            pendingPatternProfile = null;
                            profile = profile with { PatternProfile = pending };
                            startExactCalculations();
                        }
                    });
                return;
            }
            await calculateExactAsync(calculator, requests, identity, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                Schedule(() =>
                {
                    if (IsDisposed || cancellationToken.IsCancellationRequested) return;
                    exactScanRunning = false;
                    hideRefresh();
                    status.Text = withCatalogStatus($"Scan could not start: {error.Message}");
                });
        }
    }

    internal static string ScanIdentity(IEnumerable<PpTargetExactRequest> requests, DateTimeOffset? catalogRevision) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(new { catalogRevision,
                // Match the ranker's tolerance; normalized weights can introduce
                // sub-micro precision noise without any new player evidence.
                Inputs = requests.Select(r => PpTargetExactCalculationService.CacheIdentity(r with {
                    ExpectedAccuracy = Math.Round(r.ExpectedAccuracy, 6), Attainability = Math.Round(r.Attainability, 6)
                }, r.BeatmapHash ?? "catalog"))
                    .Order(StringComparer.Ordinal).ToArray() }))));

    private async Task calculateExactAsync(
        IPpTargetExactCalculationService calculator,
        IReadOnlyList<PpTargetExactRequest> requests,
        string scanIdentity,
        CancellationToken cancellationToken)
    {
        try
        {
            var calculated = new Dictionary<int, PpTargetEstimate>();
            for (int offset = 0; offset < requests.Count; offset += 200)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int completedBeforeBatch = offset;
                using var progress = new ScanProgress<PpTargetExactCalculationProgress>(action => Schedule(action), () => !IsDisposed && !cancellationToken.IsCancellationRequested, value =>
                {
                    showRefresh("Checking map patterns and PP", completedBeforeBatch + value.Completed, requests.Count);
                });
                var batch = await calculator.CalculateAsync(requests.Skip(offset).Take(200).ToArray(), cancellationToken, progress).ConfigureAwait(false);
                progress.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var item in batch)
                    calculated[item.Key] = item.Value;
                var snapshot = new Dictionary<int, PpTargetEstimate>(calculated);
                if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                    Schedule(() =>
                    {
                        if (IsDisposed || cancellationToken.IsCancellationRequested) return;
                        exactEstimates = snapshot;
                        renderResults();
                        saveSnapshot();
                    });
            }
            if (!IsDisposed && !cancellationToken.IsCancellationRequested)
            {
                Schedule(() =>
                {
                    if (IsDisposed || cancellationToken.IsCancellationRequested)
                        return;
                    exactScanRunning = false;
                    exactEstimates = calculated;
                    lastCompletedScanIdentity = calculated.Count == requests.Count ? scanIdentity : null;
                    status.Text = withCatalogStatus(calculated.Count == 0
                        ? "PP values are unavailable for these difficulties."
                        : $"PP ready for {calculated.Count:N0} beatmap difficult{(calculated.Count == 1 ? "y" : "ies")}.");
                    renderResults();
                    hideRefresh();
                    saveSnapshot();
                    if (!skillAnalysisRunning && pendingPatternProfile is { } pending)
                    {
                        pendingPatternProfile = null;
                        profile = profile with { PatternProfile = pending };
                        startExactCalculations();
                    }
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                Schedule(() =>
                {
                    if (IsDisposed || cancellationToken.IsCancellationRequested)
                        return;
                    exactScanRunning = false;
                    status.Text = withCatalogStatus($"PP calculation failed: {error.Message}");
                    hideRefresh();
                });
        }
    }

    private void scheduleResults()
    {
        ++renderGeneration;
        scheduledResults?.Cancel();
        scheduledResults = Scheduler.AddDelayed(renderResults, 150);
    }

    private void renderResults()
    {
        scheduledResults?.Cancel();
        scheduledResults = null;
        int generation = ++renderGeneration;
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
            Limit: 50_000);
        // Capture bindables on the update thread. Rank only the latest request on one worker.
        var currentProfile = profile;
        var currentCatalog = catalog;
        var estimates = exactEstimates;
        var ordering = sort.Value;
        var modSelection = selectedMods.Value;
        var history = patternHistory;
        var observed = observedModSetups.GetValueOrDefault(modSelection);
        ranking.Submit(token =>
        {
            var ranked = PpTargetRanker.Rank(resolveModProfile(currentProfile, modSelection, history, observed), currentCatalog, filters, estimates, token);
            token.ThrowIfCancellationRequested();
            return OrderTargets(ranked.Candidates.Where(candidate => estimates.Count == 0 || candidate.Estimate is not null), ordering).Take(200).ToArray();
        }, visibleCandidates =>
        {
            if (!IsDisposed) Schedule(() =>
            {
                if (!IsDisposed && generation == renderGeneration) renderCandidates(visibleCandidates);
            });
        }, error => Console.Error.WriteLine($"PP target ranking failed: {error}"));
    }

    private void renderCandidates(PpTargetCandidate[] visibleCandidates)
    {
        scheduledRows?.Cancel();
        targetRows.Clear();
        results.Clear();
        int generation = renderGeneration;
        int cursor = 0;
        void addRows()
        {
            if (IsDisposed || generation != renderGeneration) return;
            var budget = System.Diagnostics.Stopwatch.StartNew();
            int added = 0;
            while (cursor < visibleCandidates.Length)
            {
                PpTargetCandidate candidate = visibleCandidates[cursor++];
                if (setsById.TryGetValue(candidate.BeatmapSetId, out OfficialBeatmapSet? set))
                {
                    var row = new PpTargetRow(candidate, set, importSet, openBeatmap, sort.Value, isInstalled(candidate))
                    {
                        Action = () => selectTarget(candidate, set, true),
                        BackgroundColour = candidate.BeatmapId == selectedBeatmapId ? AimModPalette.PanelRaised : AimModPalette.Panel,
                    };
                    targetRows[candidate.BeatmapId] = row;
                    results.Add(row);
                }
                if (++added >= 12 || budget.Elapsed.TotalMilliseconds >= 4) break;
            }
            if (cursor < visibleCandidates.Length) scheduledRows = Scheduler.AddDelayed(addRows, 16);
        }
        addRows();
        PpTargetCandidate? selected = visibleCandidates.FirstOrDefault(c => c.BeatmapId == selectedBeatmapId)
            ?? visibleCandidates.FirstOrDefault();
        if (selected is not null && setsById.TryGetValue(selected.BeatmapSetId, out var selectedSet))
            selectTarget(selected, selectedSet, false);
        else
        {
            selectedBeatmapId = null;
            selectedDetails = null;
            detailViewport.Clear();
            detailOpen = false;
        }
        if (visibleCandidates.Length == 0)
            workspaceState.ShowState(FontAwesome.Solid.Filter, "No matching beatmaps",
                minimumExpectedPp.Value > 0 || maximumExpectedPp.Value < 1000
                    ? "Expected PP needs comparable completed plays and pass evidence. Clear the PP range to include unverified maps."
                    : "Try widening the star, PP, status, or length filters.");
        else
            workspaceState.HideState();
        resultCount.Text = $"{visibleCandidates.Length:N0} maps / {visibleCandidates.Count(c => c.EvidenceTier == 2):N0} supported / {visibleCandidates.Count(c => c.EvidenceTier == 1):N0} stretch / {visibleCandidates.Count(c => c.EvidenceTier == 0):N0} unverified";
    }

    private void selectTarget(PpTargetCandidate candidate, OfficialBeatmapSet set, bool open)
    {
        bool same = selectedBeatmapId == candidate.BeatmapId;
        if (same && selectedDetails?.IsBusy == true) { detailOpen |= open; return; }
        float scroll = same ? selectedDetails?.ScrollPosition ?? 0 : 0;
        selectedBeatmapId = candidate.BeatmapId;
        detailOpen |= open;
        foreach (var (id, row) in targetRows)
            row.BackgroundColour = id == candidate.BeatmapId ? AimModPalette.PanelRaised : AimModPalette.Panel;
        detailViewport.Clear();
        detailViewport.Add(selectedDetails = new PpTargetDetails(candidate, set, pendingPatternProfile ?? profile.PatternProfile,
            importSet, openBeatmap, () => detailOpen = false, scroll, isInstalled(candidate)));
    }

    private async Task<OnlineBeatmapImportResult> importSet(OfficialBeatmapSet set)
    {
        OnlineBeatmapImportService? current = importer();
        var result = current is null
            ? new OnlineBeatmapImportResult(OnlineBeatmapImportStatus.SessionUnavailable, set.BeatmapSetId)
            : await current.ImportAsync(set).ConfigureAwait(false);
        if (result.Status == OnlineBeatmapImportStatus.Success && !IsDisposed)
            Schedule(() =>
            {
                savedSetIds.Add(set.BeatmapSetId);
                foreach (var difficulty in set.Difficulties)
                    if (targetRows.TryGetValue(difficulty.BeatmapId, out var row)) row.SetInstalled();
                if (set.Difficulties.Any(d => d.BeatmapId == selectedBeatmapId)) selectedDetails?.SetInstalled();
            });
        return result;
    }

    private void updateProfileSummary()
    {
        string mods = profile.CommonMods.Count == 0 ? "No dominant mods" : string.Join(", ", profile.CommonMods.Take(3).Select(item => item.Value));
        PpPatternProfile? evidence = pendingPatternProfile ?? profile.PatternProfile;
        string evidenceSummary = evidence is null ? "Skill evidence loading"
            : $"{evidence.ScoreEvidence?.Select(item => item.MapKey).Distinct().Count() ?? 0:N0} score-supported maps / " +
              $"{evidence.Evidence.Select(item => item.MapKey).Distinct().Count():N0} exact-replay maps (30 days)";
        profileSummary.Text = profile.ValidRunCount == 0
            ? "No score history is available for PP recommendations."
            : evidence?.SessionForm is { Patterns.Count: > 0 } form
                ? $"{form.Summary}  /  {profile.ValidRunCount:N0} plays{skillProgress}"
                : $"{profile.ValidRunCount:N0} plays  /  {onlineBestCount:N0} submitted  /  " +
                  $"{evidenceSummary}  /  {mods}{skillProgress}";
    }

    private void showRefresh(string message, int completed, int total)
    {
        if (!refreshElapsed.IsRunning) refreshElapsed.Restart();
        refreshIndeterminate = total <= 0;
        refreshText.Text = total > 0 ? $"{Math.Clamp(completed * 100d / total, 0, 100):0}% checked" : "Refreshing";
        refreshProgress.Alpha = 1;
        refreshProgressFill.X = 0;
        refreshProgressFill.Width = total > 0 ? Math.Clamp((float)completed / total, 0.02f, 1) : 0.18f;
        status.Text = total > 0 ? $"{message}  /  {completed:N0} of {total:N0}" : message;
        scanHint.Alpha = 1;
        if (!hasVisibleSnapshot) loadingOverlay.SetProgress(message, completed, total);
    }

    private void hideRefresh()
    {
        refreshElapsed.Stop();
        refreshIndeterminate = false;
        scanHint.Text = skillAnalysisRunning && exactEstimates.Count > 0
            ? "Targets ready. Your skill profile is still updating; recommendations will refresh when it finishes." : string.Empty;
        refreshProgressFill.X = 0;
        refreshText.Text = "Refresh";
        refreshProgress.Alpha = 0;
        refreshProgressFill.Width = 0;
        if (automaticRefreshPending)
            Schedule(() =>
            {
                if (!IsDisposed && automaticRefreshPending && !catalogScanRunning && !exactScanRunning && !patternBuildRunning && !skillAnalysisRunning)
                    requestAutomaticRefresh();
            });
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
            category.Value,
            catalogScanStatus,
            sort.Value.ToString(),
            pendingPatternProfile, selectedMods.Value, catalogQueryIdentity, catalogUpdatedAt);
        snapshotWriter.Submit(token =>
        {
            workspaceCache.SaveAsync(snapshot, token).GetAwaiter().GetResult();
            return true;
        }, _ => { }, error => Console.Error.WriteLine($"PP target cache failed: {error}"));
    }

    private string withCatalogStatus(string message) => string.IsNullOrEmpty(catalogScanStatus) ? message : $"{message} {catalogScanStatus}";

    public static string CatalogScanSummary(PpTargetCatalogScanResult scan)
    {
        if (!scan.IsPartial) return string.Empty;
        string reason = scan.StopReason switch
        {
            PpTargetCatalogScanStopReason.PageLimit => "page limit reached",
            PpTargetCatalogScanStopReason.SetLimit => "set limit reached",
            PpTargetCatalogScanStopReason.RepeatedCursor => "repeated page cursor",
            _ when scan.Status == OfficialBeatmapRequestStatus.RateLimited => "osu! rate limit reached",
            _ => "catalog request failed",
        };
        return $"Partial catalog: {scan.Pages:N0} pages, {scan.SetCount:N0} sets ({reason}).";
    }

    // Reports enter the update scheduler directly; closing the scope invalidates already queued callbacks.
    private sealed class ScanProgress<T>(Action<Action> schedule, Func<bool> isCurrent, Action<T> report) : IProgress<T>, IDisposable
    {
        private int closed;

        public void Report(T value)
        {
            if (Volatile.Read(ref closed) != 0 || !isCurrent()) return;
            schedule(() =>
            {
                if (Volatile.Read(ref closed) == 0 && isCurrent()) report(value);
            });
        }

        public void Dispose() => Interlocked.Exchange(ref closed, 1);
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
            Schedule(requestAutomaticRefresh);
    }

    protected override void Dispose(bool isDisposing)
    {
        cancelToken(ref profileRefresh);
        cancelToken(ref catalogSearch);
        cancelToken(ref exactCalculation);
        cancelToken(ref patternRefresh);
        scheduledPatternRefresh?.Cancel();
        scheduledSearch?.Cancel();
        scheduledResults?.Cancel();
        scheduledRows?.Cancel();
        ranking.Dispose();
        snapshotWriter.Dispose();
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
        TargetSort.AccountGain => "Best account PP gain",
        TargetSort.PassProbability => "Safest estimated pass",
        TargetSort.GainPerMinute => "Account PP per minute",
        TargetSort.ExpectedPp => "Highest expected PP",
        TargetSort.MaximumPp => "Highest max PP",
        TargetSort.Stars => "Lowest star rating",
        _ => "Best skill fit",
    };

    internal static IEnumerable<PpTargetCandidate> OrderTargets(IEnumerable<PpTargetCandidate> candidates, TargetSort ordering)
    {
        var ordered = ordering switch
        {
            TargetSort.AccountGain => candidates.OrderByDescending(c => c.EstimatedAccountGainPp).ThenByDescending(c => c.RankScore),
            TargetSort.PassProbability => candidates.OrderByDescending(c => c.PassEstimate?.Lower).ThenByDescending(c => c.RankScore),
            TargetSort.GainPerMinute => candidates.OrderByDescending(c => c.AccountGainPerMinute).ThenByDescending(c => c.RankScore),
            TargetSort.ExpectedPp => candidates.OrderByDescending(c => c.FirstAttemptPp).ThenByDescending(c => c.RankScore),
            TargetSort.MaximumPp => candidates.OrderByDescending(c => c.Estimate?.RealisticMaximumPp).ThenByDescending(c => c.RankScore),
            TargetSort.Stars => candidates.OrderBy(c => c.StarRating).ThenByDescending(c => c.RankScore),
            _ => candidates.OrderByDescending(c => c.RankScore),
        };
        // Stable ordering keeps the selected sort within each evidence tier.
        return ordered.OrderByDescending(c => c.EvidenceTier);
    }

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
        AccountGain,
        PassProbability,
        GainPerMinute,
    }

    private sealed partial class PpTargetDropdown<T> : AimMod.Desktop.Coaching.BoundedShearedDropdown<T>
        where T : notnull
    {
        private readonly Func<T, string> formatter;

        public PpTargetDropdown(Func<T, string> formatter) : base(string.Empty)
        {
            this.formatter = formatter;
            if (Header is ShearedDropdownHeader header)
            {
                // Empty external labels otherwise collapse the native 30-unit header.
                header.LabelContainer.AutoSizeAxes = Axes.X;
                header.LabelContainer.Height = 30;
            }
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
                    Colour = AimModPalette.Accent,
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

    private partial class PpTargetRow : AimModInteractiveSurface, IHasTooltip
    {
        public LocalisableString TooltipText { get; }
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
        private readonly TruncatingSpriteText patternDetails;
        private readonly Container artwork;
        private readonly Container expectedMetric;
        private readonly Container maximumMetric;
        private bool importing;
        private bool installed;

        public PpTargetRow(
            PpTargetCandidate candidate,
            OfficialBeatmapSet set,
            Func<OfficialBeatmapSet, Task<OnlineBeatmapImportResult>> import,
            Func<int, CancellationToken, Task>? openBeatmap,
            TargetSort ordering, bool installed = false)
        {
            this.installed = installed;
            this.set = set;
            this.import = import;
            PpPatternPrediction? pattern = candidate.Estimate?.PatternPrediction;
            string skillLabel = pattern?.Fit is { } fit ? $"{fit:P0} skill fit" : "Pattern fit unmeasured";
            string patternSummary = pattern?.Risks.FirstOrDefault()
                ?? pattern?.Strengths.FirstOrDefault() ?? "More recent replay evidence needed";
            string passDetails = candidate.PassEstimate is { } pass
                ? $"Estimated pass: {pass.Probability:P0} ({pass.Lower:P0}-{pass.Upper:P0}). {pass.Attempts} recent attempts {(pass.SameMap ? "on this difficulty" : $"across {pass.Maps} comparable maps")}. {pass.Confidence} confidence; {(pass.DurationAdjusted ? "adjusted from shorter maps; stamina is unverified" : pass.BroaderComparison ? "broader comparisons" : "matching mod setup")}."
                : "Pass chance unknown: more comparable recent pass/fail results needed.";
            string gainDetails = candidate.EstimatedAccountGainPp is { } gainPp
                ? $"Estimated account gain per attempt: +{gainPp:0.0}pp, weighted by pass chance. Uses known best plays; excludes bonus PP and unknown scores."
                : "Account gain unverified: comparable completed plays and pass evidence are needed.";
            TooltipText = string.Join("\n", new[] { candidate.ReadinessLabel, passDetails, gainDetails }
                .Concat(pattern?.Risks.Take(2) ?? []));
            if (candidate.Learning is { } retry)
                TooltipText += $"\nEstimated first/next try: {retry.FirstTryPp:0} PP. Target: {retry.TargetPp:0} PP in about {retry.LikelyTries} tries ({retry.ReachProbability:P0} chance). Low confidence; {retry.Sessions} recorded sessions on {retry.Maps} similar-pattern maps.";
            RelativeSizeAxes = Axes.X;
            Height = 112;
            CornerRadius = AimModVisualStyle.ControlRadius;
            BackgroundColour = AimModPalette.Panel;

            Colour4 difficultyColour = AimModVisualStyle.DifficultyColour(candidate.StarRating);
            string expected = candidate.Learning is { } learning ? $"{learning.FirstTryPp:0}"
                : candidate.ExpectedEarnedPp is { } earned ? $"{earned:0}" : "-";
            string maximum = candidate.Estimate is null ? "-" : $"{candidate.Estimate.RealisticMaximumPp:0}";
            bool calculated = candidate.Estimate?.Method.StartsWith("Official osu! ruleset", StringComparison.Ordinal) == true;
            string confidence = calculated ? "PP ready" : "PP pending";
            string recommendationConfidence = candidate.RecommendationConfidence switch
            {
                PpTargetConfidence.High => "high evidence",
                PpTargetConfidence.Medium => "medium evidence",
                PpTargetConfidence.Low => "low evidence",
                _ => "limited evidence",
            };
            string gain = candidate.EstimatedAccountGainPp is { } expectedGain
                ? $"   /   +{expectedGain:0.0} account pp / try"
                : string.Empty;
            string personalPass = candidate.PassEstimate is { } predictedPass ? $"{predictedPass.Probability:P0} est. pass" : "Pass unverified";
            (string priorityCaption, string priorityValue, string priorityDetail) = ordering switch
            {
                TargetSort.AccountGain => ("ACCOUNT PP GAIN", candidate.EstimatedAccountGainPp is { } account ? $"+{account:0.0}" : "-", personalPass),
                TargetSort.GainPerMinute => ("ACCOUNT PP / MIN", candidate.AccountGainPerMinute is { } efficient ? $"+{efficient:0.0}" : "-", personalPass),
                TargetSort.PassProbability => ("EST. PASS", candidate.PassEstimate is { } chance ? $"{chance.Probability:P0}" : "-",
                    candidate.PassEstimate is { } range ? $"{range.Lower:P0}-{range.Upper:P0} range" : "More history needed"),
                _ when candidate.Learning is { } forecast => ("TARGET PP", $"{forecast.TargetPp:0}", $"~{forecast.LikelyTries} {(forecast.PreviousTries > 0 ? "more " : "")}tries"),
                _ => ("MAX PP", maximum, candidate.Estimate is null ? "pending" : "100% FC ceiling"),
            };
            string mods = ScoreMods.Display(candidate.SuggestedMods, candidate.Estimate?.ModsJson);
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
                            $"{set.Status.ToUpperInvariant()}   {set.PlayCount:N0} plays   {(passRate > 0 ? $"{passRate:P0} global pass" : "global pass rate -")}",
                            9, AimModPalette.Muted, "SemiBold"),
                        confidenceDetails = truncatingText(
                            $"{candidate.ReadinessLabel}{(candidate.PassEstimate is null ? string.Empty : $"   /   {personalPass}")}{gain}",
                            9, candidate.EvidenceTier == 2 ? AimModPalette.Success : AimModPalette.Muted, "SemiBold"),
                        patternDetails = truncatingText(candidate.ExpectedEarnedPp is null
                            ? "Max PP is a full-combo ceiling, not a prediction you can complete this map."
                            : $"{skillLabel} / {patternSummary}", 10, AimModPalette.Muted),
                    },
                },
                expectedMetric = metric(candidate.Learning is { } prediction ? prediction.PreviousTries > 0 ? "NEXT TRY PP" : "FIRST TRY PP" : "EXPECTED PP", expected, AimModPalette.Cyan,
                    candidate.Learning is not null ? "low confidence" : candidate.Estimate is null ? "pending" : candidate.ExpectedEarnedPp is not null ? "per attempt" : candidate.ReadinessLabel),
                maximumMetric = metric(priorityCaption, priorityValue, Colour4.FromHex("FFD45A"), priorityDetail),
                new Container
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Margin = new MarginPadding { Right = 12 },
                    Size = new(104, 76),
                    Children = new Drawable[]
                    {
                        actionButton(
                            FontAwesome.Solid.Play,
                            "Open osu!",
                            AimModPalette.Accent,
                            openBeatmap is null ? () => { } : () => _ = openBeatmap(candidate.BeatmapId, CancellationToken.None),
                            disabled: openBeatmap is null),
                        actionButton(installed ? FontAwesome.Solid.Check : FontAwesome.Solid.Download, installed ? "Installed" : set.DownloadDisabled ? "Unavailable" : "Install", AimModPalette.PanelRaised, beginImport, 41, installed || set.DownloadDisabled,
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
            patternDetails.MaxWidth = detailWidth;
        }

        private void beginImport()
        {
            if (importing || installed || set.DownloadDisabled)
                return;
            importing = true;
            saveText.Text = "Installing...";
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
                    if (result.Status == OnlineBeatmapImportStatus.Success) { SetInstalled(); return; }
                    saveText.Text = result.Status == OnlineBeatmapImportStatus.Success ? "Installed"
                        : result.Status == OnlineBeatmapImportStatus.OsuInstallFailed ? "Retry osu!" : "Try again";
                    saveBackground.Colour = AimModPalette.PanelRaised;
                });
            }
        }

        public void SetInstalled()
        {
            installed = true;
            saveText.Text = "Installed";
            saveBackground.Colour = AimModPalette.PanelHover;
            if (saveText.Parent is Container parent)
                foreach (var icon in parent.Children.OfType<SpriteIcon>()) icon.Icon = FontAwesome.Solid.Check;
        }

        private static Container metric(string caption, string value, Colour4 colour, string detail) => new()
        {
            Size = new(106, 112),
            Children = new Drawable[]
            {
                truncatingText(caption, 8, AimModPalette.Muted, "Bold").With(drawable => { drawable.Position = new(0, 22); drawable.MaxWidth = 82; }),
                truncatingText(value, 23, colour, "Bold").With(drawable => { drawable.Position = new(0, 38); drawable.MaxWidth = 82; }),
                truncatingText(detail, 8, AimModPalette.Muted).With(drawable =>
                {
                    drawable.Position = new(0, 70);
                    drawable.MaxWidth = 82;
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
            Colour4 foreground = disabled ? AimModPalette.Muted : colour == AimModPalette.PanelRaised ? AimModPalette.Text : AimModPalette.Canvas;
            labelText = text(label, 9, foreground, "Bold").With(drawable =>
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
                        Colour = foreground,
                    },
                    labelText,
                },
            };
        }

        private static string formatLength(int seconds) => $"{Math.Max(0, seconds) / 60}:{Math.Max(0, seconds) % 60:00}";
    }
}
