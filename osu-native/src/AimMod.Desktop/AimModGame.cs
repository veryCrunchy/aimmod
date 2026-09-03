using System.Reflection;
using System.Text.Json;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Rulesets.Osu;
using osu.Game.Scoring;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Discovery;
using AimMod.Desktop.Coaching;
using AimMod.Desktop.Visuals;
using AimMod.Desktop.Skins;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop;

public partial class AimModGame : OsuGameBase
{
    // RulesetStore snapshots loaded assemblies during OsuGameBase's dependency load.
    // Keeping this reference on the concrete game type loads osu-standard first.
    private static readonly Assembly standardRulesetAssembly = typeof(OsuRuleset).Assembly;

    private Bindable<string>? configuredSkin;

    [Resolved]
    private FrameworkConfigManager frameworkConfig { get; set; } = null!;

    private readonly AimModLaunchOptions launchOptions;
    private readonly ILocalLibrarySource? configuredLocalLibrary;
    private Container content = null!;
    private NativeReplayRouteView? replayRoute;
    private CancellationTokenSource? replayAnalysisLifetime;
    private CancellationTokenSource? coachingAnalysisLifetime;
    private NativeCoachingWorkspace? coachingWorkspace;
    private readonly CancellationTokenSource appLifetime = new();
    private readonly Bindable<NativeRoute> currentRoute = new(NativeRoute.Home);
    private ILocalLibrarySource localLibrary = null!;
    private SwitchableLocalLibrarySource? switchableLocalLibrary;
    private RealmDetachedBeatmapStore? detachedBeatmapStore;
    private RealmLocalReplayMetadataSource? localReplayMetadata;
    private HeaderBar header = null!;
    private LazerSessionMonitor? lazerSessionMonitor;
    private LazerPreferencesMonitor? lazerPreferencesMonitor;
    private OfficialOsuApiClient? officialApiClient;
    private OfficialBeatmapDiscoveryClient? officialBeatmapDiscoveryClient;
    private OnlineBeatmapImportService? onlineBeatmapImportService;
    private ExternalLazerInstalledSkinSource? externalSkinSource;
    private ExternalLazerSkinApplyService? externalSkinApplyService;
    private NativeSkinsScreen? skinsScreen;
    private CancellationTokenSource? skinApplyLifetime;
    private Guid? observedLazerSkinId;
    private Guid? appliedExternalSkinId;
    private ExternalLazerReplayOpenService? externalReplayOpenService;
    private ReplayAnalysisBatchService? replayAnalysisBatchService;
    private readonly Dictionary<Guid, ReplayAnalysisResult> replayAnalyses = new();
    private ReplayAnalysisCache? replayAnalysisCache;
    private Guid? activeReplayScoreId;
    private CancellationTokenSource? profileRefreshCancellation;

    public AimModGame()
        : this(AimModLaunchOptions.Home)
    {
    }

    public AimModGame(AimModLaunchOptions launchOptions)
        : this(launchOptions, null)
    {
    }

    public AimModGame(AimModLaunchOptions launchOptions, ILocalLibrarySource? localLibrarySource)
    {
        this.launchOptions = launchOptions;
        configuredLocalLibrary = localLibrarySource;
        GC.KeepAlive(standardRulesetAssembly);
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        replayAnalysisCache = new ReplayAnalysisCache(Storage.GetFullPath("cache/replay-analysis-v1.json", true));
        foreach ((Guid scoreId, ReplayAnalysisResult analysis) in replayAnalysisCache.Load())
            replayAnalyses[scoreId] = analysis;

        configuredSkin = LocalConfig.GetBindable<string>(OsuSetting.Skin);
        SkinManager.SetSkinFromConfiguration(configuredSkin.Value);
        SkinManager.CurrentSkinInfo.ValueChanged += skin => configuredSkin.Value = skin.NewValue.ID.ToString();

        if (configuredLocalLibrary is not null)
        {
            localLibrary = configuredLocalLibrary;
        }
        else
        {
            detachedBeatmapStore = new RealmDetachedBeatmapStore();
            localReplayMetadata = new RealmLocalReplayMetadataSource();
            var inheritedRealmFallback = new OsuManagerLocalLibrarySource(detachedBeatmapStore, ScoreManager, localReplayMetadata);
            switchableLocalLibrary = new SwitchableLocalLibrarySource(inheritedRealmFallback);
            localLibrary = switchableLocalLibrary;
            LoadComponentAsync(detachedBeatmapStore, Add);
            LoadComponentAsync(localReplayMetadata, Add);
        }

        Add(new Box
        {
            RelativeSizeAxes = Axes.Both,
            Colour = AimModPalette.Canvas,
        });

        Add(new Container
        {
            RelativeSizeAxes = Axes.Both,
            Children = new Drawable[]
            {
                content = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Top = 88, Horizontal = 52, Bottom = 36 },
                },
                header = new HeaderBar(currentRoute, showHome, showBeatmaps, showSkins, showReplays, showStatistics, showCoaching),
            },
        });

        _ = connectLazerSession(appLifetime.Token);

        if (launchOptions.Error is not null)
        {
            showLaunchError(launchOptions.Error);
            return;
        }

        if (launchOptions.Replay is not null)
        {
            openReplay(launchOptions.Replay);
            return;
        }

        showHome();
    }

    private async Task connectLazerSession(CancellationToken cancellationToken)
    {
        try
        {
            OsuHostPlatform platform = OperatingSystem.IsWindows()
                ? OsuHostPlatform.Windows
                : OperatingSystem.IsMacOS()
                    ? OsuHostPlatform.MacOS
                    : OsuHostPlatform.Linux;
            var environment = new OsuDiscoveryEnvironment(
                HomeDirectory: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                XdgDataHome: Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
                AppData: Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ExplicitDataRoot: Environment.GetEnvironmentVariable(OsuLazerDiscoveryService.DataRootEnvironmentVariable));

            OsuLazerDiscoveryResult discovery = await Task.Run(
                () => new OsuLazerDiscoveryService(new PhysicalOsuDiscoveryFileSystem()).Discover(platform, environment),
                cancellationToken).ConfigureAwait(false);
            OsuLazerDataRoot? root = discovery.CompleteDataRoots.FirstOrDefault();
            if (root is null)
            {
                Schedule(() => header.SetSessionState(new LazerSessionState(LazerSessionStatus.Unavailable, null, 0)));
                return;
            }

            if (switchableLocalLibrary is not null)
            {
                var externalLibrary = new ExternalLazerLocalLibrarySource(root.CanonicalPath);
                Schedule(() =>
                {
                    switchableLocalLibrary.SwitchTo(externalLibrary);
                    externalReplayOpenService = new ExternalLazerReplayOpenService(root.CanonicalPath);
                    replayAnalysisBatchService = new ReplayAnalysisBatchService(externalReplayOpenService);
                    startCoachingAnalysis();
                });
            }

            Schedule(() =>
            {
                externalSkinSource = new ExternalLazerInstalledSkinSource(root.CanonicalPath);
                externalSkinApplyService = new ExternalLazerSkinApplyService(
                    root.CanonicalPath,
                    SkinManager,
                    Storage.GetFullPath("cache/external-skin-mappings-v1.json", true));
            });

            LazerPreferencesMonitor preferencesMonitor = await LazerPreferencesMonitor.CreateAsync(
                root.CanonicalPath,
                cancellationToken).ConfigureAwait(false);
            lazerPreferencesMonitor = preferencesMonitor;
            preferencesMonitor.StateChanged += lazerPreferencesChanged;
            Schedule(() => applyLazerPreferences(preferencesMonitor.Current));

            LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(
                Path.Combine(root.CanonicalPath, "game.ini"),
                cancellationToken).ConfigureAwait(false);
            lazerSessionMonitor = monitor;
            officialApiClient = new OfficialOsuApiClient(monitor);
            officialBeatmapDiscoveryClient = new OfficialBeatmapDiscoveryClient(monitor);
            var lazerBeatmapInstallService = new LazerBeatmapInstallService(
                Storage.GetFullPath("downloads/lazer-handoff", true));
            onlineBeatmapImportService = new OnlineBeatmapImportService(
                officialBeatmapDiscoveryClient,
                BeatmapManager,
                Storage.GetFullPath("downloads/beatmaps", true),
                localLibrary,
                lazerBeatmapInstallService);
            monitor.StateChanged += lazerSessionChanged;
            Schedule(() => applyLazerSessionState(monitor.Current));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            if (!IsDisposed)
                Schedule(() => header.SetSessionState(new LazerSessionState(LazerSessionStatus.Unavailable, null, 0)));
        }
    }

    private void lazerPreferencesChanged(LazerPreferencesState state)
    {
        if (!IsDisposed)
            Schedule(() => applyLazerPreferences(state));
    }

    private void applyLazerPreferences(LazerPreferencesState state)
    {
        Guid? nextSkinId = state.SkinId;
        bool skinChanged = nextSkinId is not null && nextSkinId != observedLazerSkinId;
        observedLazerSkinId = state.SkinId;
        skinsScreen?.SetExternalSelection(state.SkinId);
        if (skinChanged && nextSkinId is { } skinId)
            followLazerSkin(skinId);

        if (state.BeatmapSkins is { } beatmapSkins)
            LocalConfig.GetBindable<bool>(OsuSetting.BeatmapSkins).Value = beatmapSkins;
        if (state.BeatmapColours is { } beatmapColours)
            LocalConfig.GetBindable<bool>(OsuSetting.BeatmapColours).Value = beatmapColours;
        if (state.BeatmapHitsounds is { } beatmapHitsounds)
            LocalConfig.GetBindable<bool>(OsuSetting.BeatmapHitsounds).Value = beatmapHitsounds;
        if (state.AudioOffset is { } audioOffset)
            LocalConfig.GetBindable<double>(OsuSetting.AudioOffset).Value = audioOffset;
        if (state.PositionalHitsoundsLevel is { } positionalHitsoundsLevel)
            LocalConfig.GetBindable<float>(OsuSetting.PositionalHitsoundsLevel).Value = positionalHitsoundsLevel;
        if (state.VolumeUniversal is { } volumeUniversal)
            frameworkConfig.GetBindable<double>(FrameworkSetting.VolumeUniversal).Value = volumeUniversal;
        if (state.VolumeMusic is { } volumeMusic)
            frameworkConfig.GetBindable<double>(FrameworkSetting.VolumeMusic).Value = volumeMusic;
        if (state.VolumeEffect is { } volumeEffect)
            frameworkConfig.GetBindable<double>(FrameworkSetting.VolumeEffect).Value = volumeEffect;
    }

    private void lazerSessionChanged(LazerSessionState state)
    {
        if (!IsDisposed)
            Schedule(() => applyLazerSessionState(state));
    }

    private void applyLazerSessionState(LazerSessionState state)
    {
        header.SetSessionState(state);

        profileRefreshCancellation?.Cancel();
        profileRefreshCancellation?.Dispose();
        profileRefreshCancellation = null;

        if (state.Status != LazerSessionStatus.SignedIn || officialApiClient is null)
            return;

        profileRefreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        _ = refreshOfficialProfile(state.Revision, profileRefreshCancellation.Token);
    }

    private async Task refreshOfficialProfile(long sessionRevision, CancellationToken cancellationToken)
    {
        try
        {
            OsuProfileFetchResult result = await officialApiClient!.FetchCurrentProfileAsync(cancellationToken).ConfigureAwait(false);
            if (result.Status != OsuProfileFetchStatus.Success || result.Profile is null)
                return;

            if (!IsDisposed)
            {
                Schedule(() =>
                {
                    if (lazerSessionMonitor?.Current.Revision == sessionRevision)
                        header.SetProfile(result.Profile);
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void showHome()
    {
        cancelReplayWork();
        currentRoute.Value = NativeRoute.Home;
        content.Padding = new MarginPadding { Top = 88, Horizontal = 52, Bottom = 36 };
        content.Child = new HomeScreen(showBeatmaps, showReplays, showStatistics, showCoaching) { RelativeSizeAxes = Axes.Both };
    }

    private void showBeatmaps()
    {
        currentRoute.Value = NativeRoute.Beatmaps;
        cancelReplayWork();
        content.Padding = new MarginPadding { Top = 88, Horizontal = 52, Bottom = 36 };
        content.Child = new NativeBeatmapDiscoveryScreen(
            localLibrary,
            () => officialBeatmapDiscoveryClient,
            () => onlineBeatmapImportService)
        {
            RelativeSizeAxes = Axes.Both,
        };
    }

    private void showReplays()
    {
        currentRoute.Value = NativeRoute.Replays;
        cancelReplayWork();
        content.Padding = new MarginPadding { Top = 70, Horizontal = 12, Bottom = 12 };
        content.Child = replayRoute = new NativeReplayRouteView(
            localLibrary,
            new Dictionary<Guid, ReplayAnalysisResult>(replayAnalyses),
            prepareCatalogReplay)
        {
            RelativeSizeAxes = Axes.Both,
        };
    }

    private void showSkins()
    {
        currentRoute.Value = NativeRoute.Skins;
        cancelReplayWork();
        content.Padding = new MarginPadding { Top = 88, Horizontal = 52, Bottom = 36 };
        content.Child = skinsScreen = new NativeSkinsScreen(
            externalSkinSource,
            lazerPreferencesMonitor?.Current.SkinId,
            appliedExternalSkinId,
            applySelectedSkin)
        {
            RelativeSizeAxes = Axes.Both,
        };
    }

    private void followLazerSkin(Guid skinId)
    {
        if (externalSkinSource is null || externalSkinApplyService is null)
            return;

        skinApplyLifetime?.Cancel();
        skinApplyLifetime?.Dispose();
        skinApplyLifetime = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        _ = followLazerSkinAsync(skinId, skinApplyLifetime.Token);
    }

    private async Task followLazerSkinAsync(Guid skinId, CancellationToken cancellationToken)
    {
        try
        {
            InstalledLazerSkin? skin = await externalSkinSource!.GetAsync(skinId, cancellationToken).ConfigureAwait(false);
            if (skin is not null)
                await applySkinAsync(skin, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logFailure("follow lazer skin", error);
        }
    }

    private async Task applySelectedSkin(InstalledLazerSkin skin, CancellationToken cancellationToken)
    {
        skinApplyLifetime?.Cancel();
        skinApplyLifetime?.Dispose();
        skinApplyLifetime = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token, cancellationToken);
        await applySkinAsync(skin, skinApplyLifetime.Token).ConfigureAwait(false);
    }

    private async Task applySkinAsync(InstalledLazerSkin skin, CancellationToken cancellationToken)
    {
        ExternalLazerSkinApplyService service = externalSkinApplyService
            ?? throw new ExternalLazerSkinApplyException("lazer_library_unavailable", "AimMod is still connecting to the local lazer skin library.");
        Guid localSkinId = await service.PrepareAsync(skin, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsDisposed)
        {
            Schedule(() =>
            {
                SkinManager.SetSkinFromConfiguration(localSkinId.ToString());
                appliedExternalSkinId = skin.SkinId;
                skinsScreen?.SetAppliedSelection(skin.SkinId);
            });
        }
    }

    private void showStatistics()
    {
        currentRoute.Value = NativeRoute.Statistics;
        cancelReplayWork();
        content.Padding = new MarginPadding { Top = 88, Horizontal = 52, Bottom = 0 };
        content.Child = new ReplayHistoryScreen(
            localLibrary,
            ReplayHistoryScreenMode.Statistics,
            new Dictionary<Guid, ReplayAnalysisResult>(replayAnalyses),
            prepareCatalogReplay)
        {
            RelativeSizeAxes = Axes.Both,
        };
    }

    private void showCoaching()
    {
        currentRoute.Value = NativeRoute.Coaching;
        cancelReplayWork();
        content.Padding = new MarginPadding { Top = 88, Horizontal = 52, Bottom = 0 };
        content.Child = coachingWorkspace = new NativeCoachingWorkspace(
            localLibrary,
            replayAnalyses,
            prepareCatalogReplay)
        {
            RelativeSizeAxes = Axes.Both,
        };
        startCoachingAnalysis();
    }

    private void startCoachingAnalysis()
    {
        if (currentRoute.Value != NativeRoute.Coaching || coachingWorkspace is null || replayAnalysisBatchService is null)
            return;

        coachingAnalysisLifetime?.Cancel();
        coachingAnalysisLifetime?.Dispose();
        coachingAnalysisLifetime = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        _ = analyseRecentCoachingReplays(coachingWorkspace, replayAnalysisBatchService, coachingAnalysisLifetime.Token);
    }

    private async Task analyseRecentCoachingReplays(
        NativeCoachingWorkspace target,
        ReplayAnalysisBatchService service,
        CancellationToken cancellationToken)
    {
        try
        {
            LocalLibraryPage<LocalReplay> page = await localLibrary.SearchReplaysAsync(new LocalLibraryQuery(
                RulesetShortName: "osu",
                Sort: LocalLibrarySort.RecentlyPlayed,
                Limit: 20), cancellationToken).ConfigureAwait(false);
            var progress = new Progress<ReplayAnalysisBatchProgress>(value =>
            {
                if (!IsDisposed)
                    Schedule(() => target.SetAnalysisProgress(value.Completed, value.Total, value.CurrentTitle));
            });
            ReplayAnalysisBatchResult result = await service.AnalyseRecentAsync(
                page.Items,
                replayAnalyses.Keys.ToArray(),
                limit: 3,
                progress,
                cancellationToken).ConfigureAwait(false);

            if (!IsDisposed)
            {
                Schedule(() =>
                {
                    foreach ((Guid scoreId, ReplayAnalysisResult analysis) in result.Completed)
                        replayAnalyses[scoreId] = analysis;

                    if (result.Completed.Count > 0)
                        _ = persistReplayAnalyses();

                    target.ApplyNewAnalyses(result.Completed.Count, result.Failed.Count);
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logFailure("analyse recent coaching replays", error);
            if (!IsDisposed)
                Schedule(() => target.SetAnalysisError());
        }
    }

    private void showLaunchError(string message)
    {
        cancelReplayWork();
        content.Padding = new MarginPadding { Top = 88, Horizontal = 52, Bottom = 36 };
        content.Child = new LaunchErrorScreen(message) { RelativeSizeAxes = Axes.Both };
    }

    private void openReplay(ReplayOpenRequest request)
    {
        CancellationToken cancellationToken = beginReplayRoute(null);
        _ = loadReplay(request, cancellationToken, null, null);
    }

    private void prepareCatalogReplay(LocalReplay replay)
    {
        CancellationToken cancellationToken = beginReplayRoute(replay.ScoreId, replay);
        _ = prepareCatalogReplayAsync(replay, cancellationToken);
    }

    private CancellationToken beginReplayRoute(Guid? scoreId, LocalReplay? replay = null)
    {
        cancelReplayWork();
        activeReplayScoreId = scoreId;
        replayAnalysisLifetime = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        currentRoute.Value = NativeRoute.Replays;
        content.Padding = new MarginPadding { Top = 70, Horizontal = 12, Bottom = 12 };
        content.Child = replayRoute = new NativeReplayRouteView(
            localLibrary,
            new Dictionary<Guid, ReplayAnalysisResult>(replayAnalyses),
            prepareCatalogReplay);
        if (replay is not null)
            replayRoute.SetReplaySummary(replay);
        return replayAnalysisLifetime.Token;
    }

    private async Task prepareCatalogReplayAsync(LocalReplay replay, CancellationToken cancellationToken)
    {
        ExternalLazerPlayableReplayBundle? bundle = null;
        try
        {
            ExternalLazerReplayOpenService service = externalReplayOpenService
                ?? throw new ExternalLazerReplayOpenException(
                    "lazer_library_unavailable",
                    "AimMod is still connecting to the local osu!lazer library. Try this replay again in a moment.");
            bundle = await service.OpenAsync(replay, cancellationToken).ConfigureAwait(false);
            await loadReplay(bundle.OpenRequest, cancellationToken, bundle, replay.ScoreId).ConfigureAwait(false);
            bundle = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logFailure("prepare catalog replay", error);
            if (!IsDisposed)
                Schedule(() => replayRoute?.ShowError(toUserFacingReplayError(error)));
        }
        finally
        {
            if (bundle is not null)
                await bundle.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task loadReplay(
        ReplayOpenRequest request,
        CancellationToken cancellationToken,
        IAsyncDisposable? ownedFiles,
        Guid? scoreId)
    {
        try
        {
            string importPath = string.Equals(Path.GetExtension(request.BeatmapPath), ".osz", StringComparison.OrdinalIgnoreCase)
                ? request.BeatmapPath
                : Path.GetDirectoryName(request.BeatmapPath)
                  ?? throw new InvalidOperationException("The extracted beatmap does not have a containing folder.");

            Live<BeatmapSetInfo>? imported = await BeatmapManager.Import(new PreservedBeatmapImportTask(importPath)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (imported is null)
                throw new InvalidOperationException("osu! could not import the selected beatmap bundle.");

            BeatmapInfo[] candidates = imported.PerformRead(set => set.Beatmaps.Select(beatmap => beatmap.Detach()).ToArray());
            var decoder = new ImportedBeatmapReplayDecoder(BeatmapManager, candidates);

            Score score;
            await using (Stream replayStream = File.OpenRead(request.ReplayPath))
                score = decoder.Parse(replayStream);

            WorkingBeatmap workingBeatmap = decoder.SelectedBeatmap
                ?? throw new InvalidOperationException("The replay did not identify a difficulty in the selected beatmap bundle.");

            workingBeatmap.LoadTrack();
            Schedule(() => showReplay(workingBeatmap, score));

            if (scoreId is { } cachedScoreId && replayAnalyses.TryGetValue(cachedScoreId, out ReplayAnalysisResult? cachedAnalysis))
            {
                Schedule(() => replayRoute?.ShowAnalysisState(new ReplayAnalysisState(
                    0,
                    ReplayAnalysisStatus.Completed,
                    Result: cachedAnalysis)));
                return;
            }

            await analyseReplay(workingBeatmap, request.BeatmapPath, request.ReplayPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logFailure("load native replay", error);
            Schedule(() => replayRoute?.ShowError(toUserFacingReplayError(error)));
        }
        finally
        {
            if (ownedFiles is not null)
                await ownedFiles.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task analyseReplay(
        WorkingBeatmap workingBeatmap,
        string sourceBeatmapPath,
        string replayPath,
        CancellationToken cancellationToken)
    {
        ReplayAnalysisController? controller = null;

        try
        {
            await using ReplayAnalysisStaging staging = string.Equals(
                Path.GetExtension(sourceBeatmapPath),
                ".osu",
                StringComparison.OrdinalIgnoreCase)
                ? await ReplayAnalysisStaging.CreateAsync(sourceBeatmapPath, replayPath, cancellationToken).ConfigureAwait(false)
                : await ReplayAnalysisStaging.CreateAsync(workingBeatmap, replayPath, cancellationToken).ConfigureAwait(false);
            await using SidecarRuntimeClient runtime = SidecarRuntimeClient.Start();

            controller = new ReplayAnalysisController(
                new ReplayAnalysisClient(new SidecarRuntimeRequestClient(runtime)));
            controller.StateChanged += replayAnalysisStateChanged;

            await controller.AnalyseAsync(
                new ReplayAnalysisRequest(staging.DirectoryPath, staging.BeatmapPath, staging.ReplayPath),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logFailure("start exact replay analysis", error);
            if (!IsDisposed)
                Schedule(() => replayRoute?.ShowAnalysisError(toUserFacingAnalysisError(error)));
        }
        finally
        {
            if (controller is not null)
            {
                controller.StateChanged -= replayAnalysisStateChanged;
                controller.Dispose();
            }
        }
    }

    private void replayAnalysisStateChanged(object? sender, ReplayAnalysisStateChangedEventArgs e)
    {
        if (!IsDisposed)
        {
            Guid? scoreId = activeReplayScoreId;
            NativeReplayRouteView? targetRoute = replayRoute;

            Schedule(() =>
            {
                if (e.State is { Status: ReplayAnalysisStatus.Failed, Error: not null })
                    Console.Error.WriteLine($"[AimMod] exact replay analysis failed ({e.State.Error.Code}): {e.State.Error.Message}");

                if (scoreId is { } completedScoreId && e.State is { Status: ReplayAnalysisStatus.Completed, Result: not null })
                {
                    replayAnalyses[completedScoreId] = e.State.Result;
                    _ = persistReplayAnalyses();
                }

                if (ReferenceEquals(replayRoute, targetRoute))
                    targetRoute?.ShowAnalysisState(e.State);
            });
        }
    }

    private async Task persistReplayAnalyses()
    {
        ReplayAnalysisCache? cache = replayAnalysisCache;
        if (cache is null)
            return;

        var snapshot = new Dictionary<Guid, ReplayAnalysisResult>(replayAnalyses);
        try
        {
            await cache.SaveAsync(snapshot).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Console.Error.WriteLine($"[AimMod] could not save replay analysis cache: {error.Message}");
        }
    }

    private void showReplay(WorkingBeatmap workingBeatmap, Score score)
    {
        if (replayRoute is null)
            return;

        Beatmap.Value = workingBeatmap;
        Ruleset.Value = score.ScoreInfo.Ruleset;
        SelectedMods.Value = score.ScoreInfo.Mods;

        var player = new NativeReplayPlayer(score, replayRoute.ShowReady, replayRoute.ShowError);
        replayRoute.AttachPlayer(player);
        replayRoute.ScreenStack.Push(player);
    }

    private static string toUserFacingReplayError(Exception error) => error switch
    {
        UnauthorizedAccessException => "AimMod does not have permission to read the selected beatmap or replay.",
        IOException => "AimMod could not read the selected beatmap or replay. Check that the file is complete and try again.",
        _ => error.Message,
    };

    private static string toUserFacingAnalysisError(Exception error) => error switch
    {
        UnauthorizedAccessException => "AimMod does not have permission to stage this replay for analysis.",
        IOException => "AimMod could not prepare this replay for analysis.",
        _ => "AimMod could not start exact replay analysis.",
    };

    private static void logFailure(string operation, Exception error) =>
        Console.Error.WriteLine($"[AimMod] Failed to {operation}: {error}");

    private void cancelReplayWork()
    {
        CancellationTokenSource? work = replayAnalysisLifetime;
        replayAnalysisLifetime = null;
        work?.Cancel();
        work?.Dispose();
        replayRoute = null;
        activeReplayScoreId = null;

        CancellationTokenSource? coachingWork = coachingAnalysisLifetime;
        coachingAnalysisLifetime = null;
        coachingWork?.Cancel();
        coachingWork?.Dispose();
        coachingWorkspace = null;
        skinsScreen = null;
    }

    protected override void Dispose(bool isDisposing)
    {
        appLifetime.Cancel();
        profileRefreshCancellation?.Cancel();
        profileRefreshCancellation?.Dispose();
        skinApplyLifetime?.Cancel();
        skinApplyLifetime?.Dispose();
        officialApiClient?.Dispose();
        officialBeatmapDiscoveryClient?.Dispose();
        if (lazerSessionMonitor is not null)
        {
            lazerSessionMonitor.StateChanged -= lazerSessionChanged;
            _ = lazerSessionMonitor.DisposeAsync();
        }
        if (lazerPreferencesMonitor is not null)
        {
            lazerPreferencesMonitor.StateChanged -= lazerPreferencesChanged;
            _ = lazerPreferencesMonitor.DisposeAsync();
        }

        cancelReplayWork();
        appLifetime.Dispose();
        base.Dispose(isDisposing);
    }

    private partial class HeaderBar : Container
    {
        private readonly SpriteText sessionState;

        public HeaderBar(
            Bindable<NativeRoute> currentRoute,
            Action showHome,
            Action showBeatmaps,
            Action showSkins,
            Action showReplays,
            Action showStatistics,
            Action showCoaching)
        {
            RelativeSizeAxes = Axes.X;
            Height = 70;

            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = AimModPalette.Header,
                },
                new Box
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = 110,
                    X = -42,
                    Shear = new(-0.18f, 0),
                    Colour = AimModPalette.Pink,
                    Alpha = 0.08f,
                },
                new Box
                {
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomLeft,
                    RelativeSizeAxes = Axes.X,
                    Height = 1,
                    Colour = AimModPalette.Border,
                },
                new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both,
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Margin = new MarginPadding { Left = 28 },
                    Direction = FillDirection.Horizontal,
                    Spacing = new(12),
                    Children = new Drawable[]
                    {
                        text("AimMod", 26, AimModPalette.Text, "Bold"),
                        new AimModPill("osu!lazer", AimModPillTone.Accent),
                    },
                },
                new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both,
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Direction = FillDirection.Horizontal,
                    Spacing = new(10),
                    Children = new Drawable[]
                    {
                        new NavItem("Home", NativeRoute.Home, currentRoute, showHome),
                        new NavItem("Beatmaps", NativeRoute.Beatmaps, currentRoute, showBeatmaps),
                        new NavItem("Skins", NativeRoute.Skins, currentRoute, showSkins),
                        new NavItem("Replays", NativeRoute.Replays, currentRoute, showReplays),
                        new NavItem("Statistics", NativeRoute.Statistics, currentRoute, showStatistics),
                        new NavItem("Coaching", NativeRoute.Coaching, currentRoute, showCoaching),
                    },
                },
                sessionState = text("Finding osu!lazer...", 13, AimModPalette.Muted).With(drawable =>
                {
                    drawable.Anchor = Anchor.CentreRight;
                    drawable.Origin = Anchor.CentreRight;
                    drawable.Margin = new MarginPadding { Right = 28 };
                }),
            };
        }

        public void SetSessionState(LazerSessionState state)
        {
            sessionState.Text = state.Status switch
            {
                LazerSessionStatus.SignedIn => state.Username ?? "osu! signed in",
                LazerSessionStatus.Remembered => state.Username is null ? "osu! session waiting" : $"{state.Username} · session waiting",
                LazerSessionStatus.SignedOut => "osu! signed out",
                _ => "osu!lazer not connected",
            };
            sessionState.Colour = state.Status == LazerSessionStatus.SignedIn ? AimModPalette.Cyan : AimModPalette.Muted;
        }

        public void SetProfile(OsuProfile profile)
        {
            sessionState.Text = profile.Statistics?.GlobalRank is int rank and > 0
                ? $"{profile.Username}  ·  #{rank:N0}"
                : profile.Username;
            sessionState.Colour = AimModPalette.Cyan;
        }
    }

    private partial class HomeScreen : Container
    {
        public HomeScreen(Action showBeatmaps, Action showReplays, Action showStatistics, Action showCoaching)
        {
            Children = new Drawable[]
            {
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new(8),
                    Children = new Drawable[]
                    {
                        text("AIMMOD!LAZER", 13, AimModPalette.Pink, "Bold"),
                        text("Your osu! history, ready to use", 34, AimModPalette.Text, "Bold"),
                        text("Browse the local lazer library, watch saved plays, and turn them into a useful next run.", 17, AimModPalette.Muted),
                    },
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 210,
                    Y = 132,
                    Direction = FillDirection.Horizontal,
                    Spacing = new(16),
                    Children = new Drawable[]
                    {
                        new FeatureCard("Beatmaps", "Search installed sets, compare difficulties, and start a map.", "Browse library", AimModPalette.Pink, showBeatmaps),
                        new FeatureCard("Replays", "Review local scores with native playback and exact judgement data.", "Open replays", AimModPalette.Cyan, showReplays),
                    },
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 170,
                    Y = 360,
                    Direction = FillDirection.Horizontal,
                    Spacing = new(16),
                    Children = new Drawable[]
                    {
                        new FeatureCard("Statistics", "Follow score, accuracy, and consistency across your play history.", "View statistics", AimModPalette.Pink, showStatistics, compact: true),
                        new FeatureCard("Coaching", "Find recurring mistakes and choose a useful next map.", "Review coaching", AimModPalette.Cyan, showCoaching, compact: true),
                    },
                },
            };
        }
    }

    private partial class LaunchErrorScreen : Container
    {
        public LaunchErrorScreen(string message)
        {
            Child = new FillFlowContainer
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                AutoSizeAxes = Axes.Y,
                Width = 680,
                Direction = FillDirection.Vertical,
                Spacing = new(13),
                Children = new Drawable[]
                {
                    text("Replay could not be opened", 30, AimModPalette.Pink, "Bold"),
                    text(message, 17, AimModPalette.Text),
                    text("Use --beatmap <set.osz> --replay <play.osr> to open a replay.", 14, AimModPalette.Muted),
                },
            };
        }
    }

    private partial class FeatureCard : ClickableContainer
    {
        private readonly Box background;
        private readonly Box accent;

        private readonly Action? action;

        public FeatureCard(string title, string description, string actionLabel, Colour4 accentColour, Action? action = null, bool compact = false)
        {
            this.action = action;
            RelativeSizeAxes = Axes.X;
            Width = 0.5f;
            Height = compact ? 170 : 210;
            Masking = true;
            CornerRadius = 14;

            Children = new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = AimModPalette.Panel,
                },
                accent = new Box
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 3,
                    Colour = accentColour,
                    Alpha = 0.75f,
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding(24),
                    Direction = FillDirection.Vertical,
                    Spacing = new(11),
                    Children = new Drawable[]
                    {
                        text(title, 23, AimModPalette.Text, "Bold"),
                        text(description, 15, AimModPalette.Muted),
                        text(actionLabel + "  >", 14, accentColour, "SemiBold"),
                    },
                },
            };
        }

        protected override bool OnClick(ClickEvent e)
        {
            action?.Invoke();
            return action is not null || base.OnClick(e);
        }

        protected override bool OnHover(HoverEvent e)
        {
            background.FadeColour(AimModPalette.PanelHover, 120);
            accent.FadeTo(1, 120);
            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            background.FadeColour(AimModPalette.Panel, 120);
            accent.FadeTo(0.75f, 120);
            base.OnHoverLost(e);
        }
    }

    private partial class NavItem : ClickableContainer
    {
        private readonly Action? action;
        private readonly SpriteText label;
        private readonly Box underline;

        public NavItem(string textValue, NativeRoute route, Bindable<NativeRoute> currentRoute, Action? action = null)
        {
            this.action = action;
            AutoSizeAxes = Axes.Both;
            Padding = new MarginPadding { Horizontal = 13, Vertical = 9 };
            Children = new Drawable[]
            {
                label = text(textValue, 14, AimModPalette.Muted),
                underline = new Box
                {
                    Anchor = Anchor.BottomCentre,
                    Origin = Anchor.BottomCentre,
                    RelativeSizeAxes = Axes.X,
                    Height = 2,
                    Y = 9,
                    Colour = AimModPalette.Pink,
                    Alpha = 0,
                },
            };

            currentRoute.BindValueChanged(value =>
            {
                bool active = value.NewValue == route;
                label.Colour = active ? AimModPalette.Text : AimModPalette.Muted;
                label.Font = label.Font.With(weight: active ? "SemiBold" : "Regular");
                underline.FadeTo(active ? 1 : 0, 120);
            }, true);
        }

        protected override bool OnClick(ClickEvent e)
        {
            action?.Invoke();
            return action is not null || base.OnClick(e);
        }
    }

    private enum NativeRoute
    {
        Home,
        Beatmaps,
        Skins,
        Replays,
        Statistics,
        Coaching,
    }

    private partial class Pill : CircularContainer
    {
        public Pill(string label, Colour4 colour)
        {
            AutoSizeAxes = Axes.Both;
            Masking = true;
            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = colour,
                    Alpha = 0.16f,
                },
                new SpriteText
                {
                    Text = label,
                    Font = new FontUsage(size: 14, weight: "SemiBold"),
                    Colour = colour,
                    Padding = new MarginPadding { Horizontal = 14, Vertical = 8 },
                },
            };
        }
    }

    private static OsuSpriteText text(string value, float size, Colour4 colour, string weight = "Regular") => new()
    {
        Text = value,
        Font = new FontUsage(size: size, weight: weight),
        Colour = colour,
    };
}
