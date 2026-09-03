using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
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
    private readonly OsuTextBox search;
    private readonly TruncatingSpriteText status;
    private readonly SpriteText profileSummary;
    private readonly TruncatingSpriteText resultCount;
    private readonly FillFlowContainer<Drawable> results;
    private readonly AimModLoadingOverlay loadingOverlay;
    private readonly Container filterHeader;
    private readonly Container resultViewport;
    private readonly RangeSlider starSlider;
    private readonly RangeSlider expectedPpSlider;
    private readonly RangeSlider maximumPpSlider;
    private readonly OsuDropdown<OfficialBeatmapCategory> categoryDropdown;
    private readonly OsuEnumDropdown<TargetLength> lengthDropdown;
    private readonly OsuEnumDropdown<TargetSort> sortDropdown;
    private readonly BindableDouble minimumStars = new(0) { MinValue = 0, MaxValue = 10, Default = 0 };
    private readonly BindableDouble maximumStars = new(10) { MinValue = 0, MaxValue = 10, Default = 10 };
    private readonly BindableDouble minimumExpectedPp = new(0) { MinValue = 0, MaxValue = 1_000, Default = 0 };
    private readonly BindableDouble maximumExpectedPp = new(1_000) { MinValue = 0, MaxValue = 1_000, Default = 1_000 };
    private readonly BindableDouble minimumMaximumPp = new(0) { MinValue = 0, MaxValue = 1_000, Default = 0 };
    private readonly BindableDouble maximumMaximumPp = new(1_000) { MinValue = 0, MaxValue = 1_000, Default = 1_000 };
    private readonly Bindable<OfficialBeatmapCategory> category = new(OfficialBeatmapCategory.Ranked);
    private readonly Bindable<TargetLength> length = new(TargetLength.Any);
    private readonly Bindable<TargetSort> sort = new(TargetSort.BestFit);

    private CancellationTokenSource? lifetime;
    private ScheduledDelegate? scheduledSearch;
    private PpTargetPreferenceProfile profile = PpTargetPreferenceProfile.Empty;
    private IReadOnlyList<OfficialBeatmapSet> catalog = Array.Empty<OfficialBeatmapSet>();
    private IReadOnlyList<LocalBeatmapSet> localSets = Array.Empty<LocalBeatmapSet>();
    private IReadOnlyDictionary<int, PpTargetEstimate> exactEstimates = new Dictionary<int, PpTargetEstimate>();
    private Dictionary<int, OfficialBeatmapSet> setsById = new();
    private int connectionAttempts;
    private string scoreDataStatus = string.Empty;

    public NativePpTargetsWorkspace(
        ILocalLibrarySource source,
        Func<IOfficialBeatmapDiscoveryClient?> client,
        Func<OnlineBeatmapImportService?> importer,
        Func<IPpTargetExactCalculationService?>? exactCalculator = null,
        Func<ILocalScorePpHydrationService?>? localPpHydrator = null,
        Func<OfficialOsuApiClient?>? officialApi = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.importer = importer ?? throw new ArgumentNullException(nameof(importer));
        this.exactCalculator = exactCalculator ?? (() => null);
        this.localPpHydrator = localPpHydrator ?? (() => null);
        this.officialApi = officialApi ?? (() => null);
        sourceChanges = source as ILocalLibrarySourceChanged;
        if (sourceChanges is not null)
            sourceChanges.SourceChanged += sourceChanged;

        RelativeSizeAxes = Axes.Both;
        InternalChildren = new Drawable[]
        {
            filterHeader = new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = 238,
                Depth = -20,
                Children = new Drawable[]
                {
                    new AimModSectionHeader(
                        "PP targets",
                        "Maps ranked against your local osu!standard history, preferred difficulty, and demonstrated PP range.",
                        "personal map finder"),
                    profileSummary = text("Building your preference profile...", 12, AimModPalette.Muted).With(drawable => drawable.Y = 65),
                    search = new OsuTextBox
                    {
                        Position = new(0, 91),
                        Size = new(270, 44),
                        PlaceholderText = "Search title, artist, mapper, or source",
                    },
                    starSlider = new RangeSlider
                    {
                        Position = new(294, 76),
                        Size = new(220, 62),
                        Label = "Stars",
                        LowerBound = minimumStars,
                        UpperBound = maximumStars,
                        DefaultStringLowerBound = "0",
                        DefaultStringUpperBound = "10+",
                        TooltipSuffix = "stars",
                        NubWidth = 28,
                    },
                    expectedPpSlider = new RangeSlider
                    {
                        Position = new(536, 76),
                        Size = new(220, 62),
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
                        Anchor = Anchor.TopRight,
                        Origin = Anchor.TopRight,
                        Position = new(0, 76),
                        Size = new(220, 62),
                        Label = "Realistic max PP",
                        LowerBound = minimumMaximumPp,
                        UpperBound = maximumMaximumPp,
                        DefaultStringLowerBound = "0",
                        DefaultStringUpperBound = "1000+",
                        TooltipSuffix = "pp",
                        NubWidth = 28,
                    },
                    categoryDropdown = new OsuDropdown<OfficialBeatmapCategory>
                    {
                        Position = new(0, 151),
                        Width = 210,
                        Items = new[] { OfficialBeatmapCategory.Ranked, OfficialBeatmapCategory.Loved, OfficialBeatmapCategory.Pending, OfficialBeatmapCategory.Any },
                        Current = category,
                    },
                    lengthDropdown = new OsuEnumDropdown<TargetLength>
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Position = new(0, 151),
                        Width = 210,
                        Current = length,
                    },
                    sortDropdown = new OsuEnumDropdown<TargetSort>
                    {
                        Anchor = Anchor.TopRight,
                        Origin = Anchor.TopRight,
                        Position = new(0, 151),
                        Width = 210,
                        Current = sort,
                    },
                    status = truncatingText("Loading local history...", 11, AimModPalette.Muted).With(drawable => drawable.Position = new(0, 211)),
                    resultCount = truncatingText(string.Empty, 11, AimModPalette.Muted, "SemiBold").With(drawable =>
                    {
                        drawable.Anchor = Anchor.TopRight;
                        drawable.Origin = Anchor.TopRight;
                        drawable.Position = new(0, 211);
                    }),
                },
            },
            resultViewport = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = 245 },
                Masking = true,
                Depth = 10,
                Child = new OsuScrollContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Child = results = new FillFlowContainer<Drawable>
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Spacing = new(8),
                        Padding = new MarginPadding { Bottom = 32 },
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
        bool compact = width < 1_000;
        float headerHeight = compact ? 318 : 238;
        filterHeader.Height = headerHeight;
        resultViewport.Padding = new MarginPadding { Top = headerHeight + 7 };

        if (compact)
        {
            float halfWidth = (width - 18) / 2;
            search.Position = new(0, 91);
            search.Size = new(halfWidth, 44);
            placeSlider(starSlider, width - halfWidth, 76, halfWidth);
            placeSlider(expectedPpSlider, 0, 143, halfWidth);
            placeSlider(maximumPpSlider, width - halfWidth, 143, halfWidth);

            float dropdownWidth = (width - 24) / 3;
            placeDropdown(categoryDropdown, 0, 220, dropdownWidth, Anchor.TopLeft);
            placeDropdown(lengthDropdown, (width - dropdownWidth) / 2, 220, dropdownWidth, Anchor.TopLeft);
            placeDropdown(sortDropdown, width - dropdownWidth, 220, dropdownWidth, Anchor.TopLeft);
            status.Position = new(0, 286);
            resultCount.Position = new(0, 286);
        }
        else
        {
            float searchWidth = Math.Clamp(width * 0.22f, 250, 310);
            float sliderWidth = Math.Clamp((width - searchWidth - 72) / 3, 190, 260);
            search.Position = new(0, 91);
            search.Size = new(searchWidth, 44);
            placeSlider(starSlider, searchWidth + 24, 76, sliderWidth);
            placeSlider(expectedPpSlider, searchWidth + 48 + sliderWidth, 76, sliderWidth);
            placeSlider(maximumPpSlider, width - sliderWidth, 76, sliderWidth);
            placeDropdown(categoryDropdown, 0, 151, 210, Anchor.TopLeft);
            placeDropdown(lengthDropdown, (width - 210) / 2, 151, 210, Anchor.TopLeft);
            placeDropdown(sortDropdown, width - 210, 151, 210, Anchor.TopLeft);
            status.Position = new(0, 211);
            resultCount.Position = new(0, 211);
        }

        status.MaxWidth = width * 0.62f;
        resultCount.MaxWidth = width * 0.34f;
    }

    private static void placeSlider(RangeSlider slider, float x, float y, float width)
    {
        slider.Anchor = Anchor.TopLeft;
        slider.Origin = Anchor.TopLeft;
        slider.Position = new(x, y);
        slider.Size = new(width, 62);
    }

    private static void placeDropdown(Drawable dropdown, float x, float y, float width, Anchor anchor)
    {
        dropdown.Anchor = anchor;
        dropdown.Origin = anchor;
        dropdown.Position = new(x, y);
        dropdown.Width = width;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        search.OnCommit += (_, _) => startCatalogSearch();
        minimumStars.BindValueChanged(_ => scheduleCatalogSearch());
        maximumStars.BindValueChanged(_ => scheduleCatalogSearch());
        minimumExpectedPp.BindValueChanged(_ => renderResults());
        maximumExpectedPp.BindValueChanged(_ => renderResults());
        minimumMaximumPp.BindValueChanged(_ => renderResults());
        maximumMaximumPp.BindValueChanged(_ => renderResults());
        category.BindValueChanged(_ => startCatalogSearch());
        length.BindValueChanged(_ => renderResults());
        sort.BindValueChanged(_ => renderResults());
        reloadProfile();
    }

    private void reloadProfile()
    {
        replaceLifetime();
        status.Text = "Loading local history...";
        loadingOverlay.ShowLoading("Building your PP profile", "Reading local osu!standard scores");
        _ = loadProfileAsync(lifetime!.Token);
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
                    Schedule(() => loadingOverlay.ShowLoading("Building your PP profile", $"Calculating performance for {history.Runs.Count:N0} local scores"));
                var progress = new Progress<LocalScorePpHydrationProgress>(value =>
                {
                    if (!IsDisposed)
                    {
                        Schedule(() => loadingOverlay.ShowLoading(
                            "Building your PP profile",
                            $"Calculating local performance {value.Completed:N0}/{value.Total:N0}",
                            value.Completed,
                            value.Total));
                    }
                });
                hydration = await hydrator.HydrateAsync(history.Runs, cancellationToken, progress).ConfigureAwait(false);
            }
            IReadOnlyList<LocalBeatmapSet> loadedSets = await loadLocalSets(cancellationToken).ConfigureAwait(false);
            if (!IsDisposed)
                Schedule(() => loadingOverlay.ShowLoading("Building your PP profile", "Refreshing submitted best scores from osu!"));
            OsuBestScoresFetchResult? online = await loadOnlineScores(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<LocalReplay> runs = PpScoreHistoryMerger.Merge(
                hydration?.Runs ?? history.Runs,
                online?.Status == OsuBestScoresFetchStatus.Success ? online.Scores ?? [] : [],
                loadedSets);
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
                    status.Text = $"Could not build the PP profile: {error.Message}";
                });
        }
    }

    private async Task<OsuBestScoresFetchResult?> loadOnlineScores(CancellationToken cancellationToken)
    {
        OfficialOsuApiClient? api = officialApi();
        if (api is null)
            return null;
        OsuProfileFetchResult profileResult = await api.FetchCurrentProfileAsync(cancellationToken).ConfigureAwait(false);
        if (profileResult.Status != OsuProfileFetchStatus.Success || profileResult.Profile is null)
            return new OsuBestScoresFetchResult(mapProfileStatus(profileResult.Status));
        return await api.FetchBestScoresAsync(profileResult.Profile, cancellationToken).ConfigureAwait(false);
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
        OsuBestScoresFetchResult? online)
    {
        profile = next;
        localSets = loadedSets;
        exactEstimates = new Dictionary<int, PpTargetEstimate>();
        if (next.PreferredStarRange is { } stars)
        {
            minimumStars.Value = Math.Clamp(Math.Floor((stars.Minimum - 0.5) * 10) / 10, 0, 10);
            maximumStars.Value = Math.Clamp(Math.Ceiling((stars.Maximum + 0.8) * 10) / 10, 0, 10);
        }
        string confidence = next.Confidence.ToString().ToLowerInvariant();
        string mods = next.CommonMods.Count == 0 ? "No dominant mods" : string.Join(", ", next.CommonMods.Take(3).Select(item => item.Value));
        int onlineCount = online?.Status == OsuBestScoresFetchStatus.Success ? online.Scores?.Count ?? 0 : 0;
        profileSummary.Text = next.ValidRunCount == 0
            ? "No score history is available for PP recommendations."
            : $"{next.ValidRunCount:N0} plays / {next.PpSampleCount:N0} PP results / {onlineCount:N0} online best / {confidence} confidence / {mods}";
        if (online is { Status: not OsuBestScoresFetchStatus.Success })
            scoreDataStatus = onlineFailureMessage(online.Status);
        else if (hydration is { UnavailableCount: > 0 })
            scoreDataStatus = $"{hydration.UnavailableCount:N0} local score{(hydration.UnavailableCount == 1 ? string.Empty : "s")} need complete beatmap or judgement data.";
        else
            scoreDataStatus = string.Empty;
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
        replaceLifetime();
        IOfficialBeatmapDiscoveryClient? currentClient = client();
        if (currentClient is null)
        {
            connectionAttempts++;
            status.Text = connectionAttempts < 10 ? "Connecting to osu!lazer..." : "A signed-in osu!lazer session is required for map suggestions.";
            if (connectionAttempts < 10)
                loadingOverlay.ShowLoading("Connecting to osu!", "Waiting for the signed-in lazer session");
            else
                loadingOverlay.HideLoading();
            if (connectionAttempts < 10)
                scheduledSearch = Scheduler.AddDelayed(startCatalogSearch, 1000);
            return;
        }

        connectionAttempts = 0;
        status.Text = "Searching the osu! catalog...";
        loadingOverlay.ShowLoading("Finding PP targets", "Searching ranked osu!standard beatmaps");
        _ = searchCatalogAsync(currentClient, lifetime!.Token);
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
    }

    private void applyCatalog(OfficialBeatmapSearchResult response)
    {
        if (response.Status != OfficialBeatmapRequestStatus.Success)
        {
            catalog = Array.Empty<OfficialBeatmapSet>();
            setsById.Clear();
            results.Clear();
            resultCount.Text = string.Empty;
            status.Text = failureMessage(response.Status);
            loadingOverlay.HideLoading();
            return;
        }

        catalog = response.BeatmapSets;
        setsById = catalog.GroupBy(set => set.BeatmapSetId).ToDictionary(group => group.Key, group => group.First());
        status.Text = profile.PpSampleCount == 0
            ? "No complete PP results are available for recommendations."
            : scoreDataStatus.Length > 0
                ? scoreDataStatus
                : "Recommendations are based on your calculated and submitted PP results.";
        renderResults();
        startExactCalculations();
        loadingOverlay.HideLoading();
    }

    private void startExactCalculations()
    {
        IPpTargetExactCalculationService? calculator = exactCalculator();
        if (calculator is null || profile.TypicalAccuracy is null || catalog.Count == 0 || lifetime is null)
            return;

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
            return;

        status.Text = $"Calculating PP for {requests.Length:N0} beatmap difficulties...";
        loadingOverlay.ShowLoading("Calculating beatmap PP", "Preparing difficulty calculations", 0, requests.Length);
        _ = calculateExactAsync(calculator, requests, lifetime.Token);
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
                    Schedule(() => loadingOverlay.ShowLoading(
                        "Calculating beatmap PP",
                        $"Difficulty {value.Completed:N0} of {value.Total:N0}",
                        value.Completed,
                        value.Total));
            });
            IReadOnlyDictionary<int, PpTargetEstimate> calculated = await calculator.CalculateAsync(requests, cancellationToken, progress).ConfigureAwait(false);
            if (!IsDisposed && !cancellationToken.IsCancellationRequested)
            {
                Schedule(() =>
                {
                    exactEstimates = calculated;
                    status.Text = calculated.Count == 0
                        ? "Official PP could not be calculated for these difficulties."
                        : $"Official PP calculated for {calculated.Count:N0} beatmap difficult{(calculated.Count == 1 ? "y" : "ies")}.";
                    renderResults();
                    loadingOverlay.HideLoading();
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!IsDisposed)
                Schedule(loadingOverlay.HideLoading);
        }
        catch (Exception error)
        {
            if (!IsDisposed)
                Schedule(() =>
                {
                    status.Text = $"Official PP calculation failed: {error.Message}";
                    loadingOverlay.HideLoading();
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
        PpTargetCandidate[] calculatedCandidates = ordered.Where(candidate => candidate.Estimate is not null).ToArray();

        results.Clear();
        foreach (PpTargetCandidate candidate in calculatedCandidates)
        {
            if (setsById.TryGetValue(candidate.BeatmapSetId, out OfficialBeatmapSet? set))
                results.Add(new PpTargetRow(candidate, set, importSet));
        }
        if (results.Count == 0)
            results.Add(text("No maps match the current PP and difficulty filters.", 14, AimModPalette.Muted).With(drawable => drawable.Padding = new MarginPadding(18)));
        resultCount.Text = $"{calculatedCandidates.Length:N0} calculated difficulties from {catalog.Count:N0} sets";
    }

    private async Task<OnlineBeatmapImportResult> importSet(OfficialBeatmapSet set)
    {
        OnlineBeatmapImportService? current = importer();
        return current is null
            ? new OnlineBeatmapImportResult(OnlineBeatmapImportStatus.SessionUnavailable, set.BeatmapSetId)
            : await current.ImportAsync(set).ConfigureAwait(false);
    }

    private void replaceLifetime()
    {
        lifetime?.Cancel();
        lifetime?.Dispose();
        lifetime = new CancellationTokenSource();
    }

    private void sourceChanged()
    {
        if (!IsDisposed)
            Schedule(reloadProfile);
    }

    protected override void Dispose(bool isDisposing)
    {
        lifetime?.Cancel();
        lifetime?.Dispose();
        scheduledSearch?.Cancel();
        if (sourceChanges is not null)
            sourceChanges.SourceChanged -= sourceChanged;
        base.Dispose(isDisposing);
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

    private enum TargetLength
    {
        Any,
        Short,
        Medium,
        Long,
    }

    private enum TargetSort
    {
        BestFit,
        ExpectedPp,
        MaximumPp,
        Stars,
    }

    private partial class PpTargetRow : Container
    {
        private readonly OfficialBeatmapSet set;
        private readonly Func<OfficialBeatmapSet, Task<OnlineBeatmapImportResult>> import;
        private readonly SpriteText actionText;
        private readonly Box actionBackground;
        private readonly FillFlowContainer details;
        private readonly TruncatingSpriteText title;
        private readonly TruncatingSpriteText artist;
        private readonly TruncatingSpriteText mapDetails;
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
            Height = 108;
            Masking = true;
            CornerRadius = 7;
            BorderThickness = 1;
            BorderColour = AimModPalette.Border;
            Colour4 difficultyColour = AimModVisualStyle.DifficultyColour(candidate.StarRating);
            string expected = candidate.Estimate is null ? "-" : $"{candidate.Estimate.ExpectedPp:0}";
            string maximum = candidate.Estimate is null ? "-" : $"{candidate.Estimate.RealisticMaximumPp:0}";
            bool calculated = candidate.Estimate?.Method.StartsWith("Official osu! ruleset", StringComparison.Ordinal) == true;
            string confidence = calculated ? "calculated" : $"{candidate.Estimate?.Confidence.ToString().ToLowerInvariant() ?? "insufficient"} estimate";
            string mods = candidate.SuggestedMods.Count == 0 ? "NM preference" : string.Join(" + ", candidate.SuggestedMods);

            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                new Box { RelativeSizeAxes = Axes.Y, Width = 4, Colour = difficultyColour },
                artwork = new Container
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = 126,
                    X = 4,
                    Masking = true,
                    Child = candidate.CoverUrl is null
                        ? new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised }
                        : new AimModOnlineArtworkHost(candidate.CoverUrl),
                },
                details = new FillFlowContainer
                {
                    Position = new(148, 13),
                    Width = 450,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new(3),
                    Children = new Drawable[]
                    {
                        title = new TruncatingSpriteText { Text = $"{candidate.Title} [{candidate.Difficulty}]", Font = new FontUsage(size: 15, weight: "Bold"), Colour = AimModPalette.Text, MaxWidth = 450 },
                        artist = new TruncatingSpriteText { Text = $"{candidate.Artist} / mapped by {candidate.Creator}", Font = new FontUsage(size: 11, weight: "SemiBold"), Colour = AimModPalette.Muted, MaxWidth = 450 },
                        mapDetails = truncatingText($"{candidate.StarRating:0.00}*  /  {candidate.Bpm:0} BPM  /  {formatLength(candidate.TotalLengthSeconds)}  /  {mods}", 10, difficultyColour, "SemiBold"),
                        confidenceDetails = truncatingText($"{candidate.PreferenceFit:P0} preference fit  /  {confidence}", 10, calculated ? AimModPalette.Success : AimModPalette.Muted),
                    },
                },
                expectedMetric = metric("EXPECTED", expected, "pp", AimModPalette.Cyan),
                maximumMetric = metric(calculated ? "MAX PP" : "EST. MAX", maximum, "pp", Colour4.FromHex("FFD45A")),
                new ClickableContainer
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Margin = new MarginPadding { Right = 18 },
                    Size = new(130, 38),
                    Action = beginImport,
                    Masking = true,
                    CornerRadius = 7,
                    Children = new Drawable[]
                    {
                        actionBackground = new Box { RelativeSizeAxes = Axes.Both, Colour = set.DownloadDisabled ? AimModPalette.PanelHover : AimModPalette.Pink },
                        actionText = text(set.DownloadDisabled ? "Unavailable" : "Save map", 11, set.DownloadDisabled ? AimModPalette.Muted : AimModPalette.Canvas, "Bold").With(drawable =>
                        {
                            drawable.Anchor = Anchor.Centre;
                            drawable.Origin = Anchor.Centre;
                        }),
                    },
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            const float action_column = 166;
            const float metric_width = 112;
            bool compact = DrawWidth < 900;
            artwork.Width = compact ? 84 : 126;
            details.X = compact ? 104 : 148;
            float expectedX = DrawWidth - action_column - metric_width * 2;
            expectedMetric.X = expectedX;
            maximumMetric.X = expectedX + metric_width;
            float detailWidth = Math.Max(100, expectedX - details.X - 18);
            details.Width = detailWidth;
            title.MaxWidth = detailWidth;
            artist.MaxWidth = detailWidth;
            mapDetails.MaxWidth = detailWidth;
            confidenceDetails.MaxWidth = detailWidth;
        }

        private void beginImport()
        {
            if (importing || set.DownloadDisabled)
                return;
            importing = true;
            actionText.Text = "Saving...";
            actionBackground.Colour = AimModPalette.PanelHover;
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
                    actionText.Text = result.Status == OnlineBeatmapImportStatus.Success ? "Saved" : "Try again";
                    actionBackground.Colour = result.Status == OnlineBeatmapImportStatus.Success ? AimModPalette.Success : AimModPalette.Pink;
                });
            }
        }

        private static Container metric(string caption, string value, string suffix, Colour4 colour) => new()
        {
            Size = new(112, 108),
            Children = new Drawable[]
            {
                text(caption, 9, AimModPalette.Muted, "Bold").With(drawable => drawable.Position = new(0, 24)),
                text(value, 24, colour, "Bold").With(drawable => drawable.Position = new(0, 42)),
                text(suffix, 10, AimModPalette.Muted, "SemiBold").With(drawable => drawable.Position = new(58, 52)),
            },
        };

        private static string formatLength(int seconds) => $"{Math.Max(0, seconds) / 60}:{Math.Max(0, seconds) % 60:00}";
    }
}
