using System.Reflection;
using System.Net;
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
using osu.Framework.Platform;
using osu.Game;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Rulesets.Osu;
using osu.Game.Scoring;
using osuTK;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Discovery;
using AimMod.Desktop.Hub;
using AimMod.Desktop.Coaching;
using AimMod.Desktop.Visuals;
using AimMod.Desktop.Skins;
using AimMod.Desktop.Skins.Online;
using AimMod.Desktop.PpTargets;
using AimMod.Desktop.Practice;
using AimMod.Desktop.ScoreHistory;
using AimMod.Desktop.Updates;
using osu.Game.Graphics.Sprites;
using osu.Game.Overlays;

namespace AimMod.Desktop;

public partial class AimModGame : OsuGameBase
{
    // RulesetStore snapshots loaded assemblies during OsuGameBase's dependency load.
    // Keeping this reference on the concrete game type loads osu-standard first.
    private static readonly Assembly standardRulesetAssembly = typeof(OsuRuleset).Assembly;

    [Cached]
    private readonly OverlayColourProvider overlayColours = new(OverlayColourScheme.Pink);

    private Bindable<string>? configuredSkin;

    [Resolved]
    private FrameworkConfigManager frameworkConfig { get; set; } = null!;

    [Resolved]
    private Clipboard clipboard { get; set; } = null!;

    private readonly AimModLaunchOptions launchOptions;
    private readonly ILocalLibrarySource? configuredLocalLibrary;
    private Container content = null!;
    private HomeScreen? homeScreen;
    private NativeBeatmapDiscoveryScreen? beatmapsScreen;
    private NativeReplayRouteView? replayRoute;
    private NativeStatisticsWorkspace? statisticsScreen;
    private CancellationTokenSource? replayAnalysisLifetime;
    private CancellationTokenSource? replayLibraryAnalysisLifetime;
    private NativeCoachingWorkspace? coachingWorkspace;
    private NativePpTargetsWorkspace? ppTargetsWorkspace;
    private OsuClientSettingsScreen? settingsScreen;
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
    private IAccountScoreHistoryService? accountScoreHistoryService;
    private IOfficialBeatmapDiscoveryClient? officialBeatmapDiscoveryClient;
    private OnlineBeatmapImportService? onlineBeatmapImportService;
    private ILazerBeatmapInstallService? lazerBeatmapInstallService;
    private IOsuBeatmapDestinationService? beatmapDestinationService;
    private IPpTargetExactCalculationService? ppTargetExactCalculationService;
    private ILocalScorePpHydrationService? localScorePpHydrationService;
    private IInstalledSkinSource? externalSkinSource;
    private ExternalLazerSkinApplyService? externalSkinApplyService;
    private OsuStableSkinApplyService? stableSkinApplyService;
    private OnlineSkinCatalogBackend? onlineSkinCatalog;
    private OsuSkinArchiveDestinationService? onlineSkinDestination;
    private NativeSkinsScreen? skinsScreen;
    private CancellationTokenSource? skinApplyLifetime;
    private Guid? observedLazerSkinId;
    private Guid? appliedExternalSkinId;
    private ILocalReplayOpenService? replayOpenService;
    private ReplayAnalysisBatchService? replayAnalysisBatchService;
    private readonly Dictionary<Guid, ReplayAnalysisResult> replayAnalyses = new();
    private readonly HashSet<Guid> replayAnalysisFailures = new();
    private readonly Dictionary<NativeRoute, Container> workspaceHosts = new();
    private ReplayAnalysisCache? replayAnalysisCache;
    private Guid? activeReplayScoreId;
    private CancellationTokenSource? profileRefreshCancellation;
    private readonly INativeUpdateService? configuredUpdateService;
    private INativeUpdateService? updateService;
    private HttpClient? hubHttpClient;
    private IHubCredentialStore? hubCredentialStore;
    private HubDeviceLinkClient? hubDeviceLinkClient;
    private IHubSharingPreferenceStore? hubSharingPreferenceStore;
    private OsuHubUploadQueue? hubUploadQueue;
    private OsuHubReplayShareService? hubReplayShareService;
    private OsuProfile? currentOsuProfile;

    public AimModGame()
        : this(AimModLaunchOptions.Home)
    {
    }

    public AimModGame(AimModLaunchOptions launchOptions)
        : this(launchOptions, null)
    {
    }

    public AimModGame(AimModLaunchOptions launchOptions, ILocalLibrarySource? localLibrarySource)
        : this(launchOptions, localLibrarySource, null)
    {
    }

    internal AimModGame(
        AimModLaunchOptions launchOptions,
        ILocalLibrarySource? localLibrarySource,
        INativeUpdateService? updateService)
    {
        this.launchOptions = launchOptions;
        configuredLocalLibrary = localLibrarySource;
        configuredUpdateService = updateService;
        GC.KeepAlive(standardRulesetAssembly);
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        replayAnalysisCache = new ReplayAnalysisCache(Storage.GetFullPath("cache/replay-analysis-v1.json", true));
        onlineSkinCatalog = new OnlineSkinCatalogBackend(
            Storage.GetFullPath("cache/online-skins-v1", true),
            Path.Combine(Path.GetTempPath(), "AimMod", "skin-previews"));
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

        initialiseHubServices();

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
                    Depth = 0,
                },
                header = new HeaderBar(currentRoute, showHome, showBeatmaps, showSkins, showReplays, showStatistics, showCoaching, showPpTargets, showSettings)
                {
                    Depth = -100,
                },
            },
        });

        updateService = configuredUpdateService ?? new NativeUpdateService(
            new FileNativeUpdatePreferenceStore(Storage.GetFullPath("update-channel.txt", true)),
            new VelopackUpdateBackendFactory());
        _ = Task.Run(updateService.CheckAsync);

        // An injected library is an isolated host (tests and visual capture) and must not
        // discover or mutate the user's live lazer session.
        if (configuredLocalLibrary is null)
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
                ExplicitDataRoot: Environment.GetEnvironmentVariable(OsuLazerDiscoveryService.DataRootEnvironmentVariable),
                LocalAppData: Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ExplicitStableRoot: Environment.GetEnvironmentVariable(OsuStableDiscoveryService.InstallRootEnvironmentVariable),
                CurrentUserName: Environment.UserName);

            OsuStableDiscoveryResult stableDiscovery = await Task.Run(
                () => new OsuStableDiscoveryService(new PhysicalOsuDiscoveryFileSystem()).Discover(platform, environment),
                cancellationToken).ConfigureAwait(false);
            OsuStableInstallation? stable = stableDiscovery.CompleteInstallations.FirstOrDefault();
            ILocalLibrarySource? stableLibrary = stable is null
                ? null
                : new OsuStableLocalLibrarySource(stable.CanonicalPath, stable.SongsPath);

            var lazerInstall = new LazerBeatmapInstallService(LazerHandoffDirectory);
            beatmapDestinationService = new OsuBeatmapDestinationService(
                lazerInstall,
                new FileOsuClientDestinationPreferenceStore(Storage.GetFullPath("osu-client-destination.txt", true)),
                LazerHandoffDirectory,
                stable is null ? null : Path.Combine(stable.CanonicalPath, "osu!.exe"));
            lazerBeatmapInstallService = beatmapDestinationService;
            onlineSkinDestination = new OsuSkinArchiveDestinationService(
                () => beatmapDestinationService?.Destination ?? OsuClientDestination.Auto,
                Path.Combine(Path.GetTempPath(), "AimMod", "skin-handoff"),
                stable is null ? null : Path.Combine(stable.CanonicalPath, "osu!.exe"));
            Schedule(() => skinsScreen?.ConfigureOnlineDestination(onlineSkinDestination));

            if (switchableLocalLibrary is not null && stableLibrary is not null)
            {
                ILocalLibrarySource fallback = switchableLocalLibrary.Current;
                Schedule(() => switchableLocalLibrary.SwitchTo(new CompositeLocalLibrarySource(new[] { fallback, stableLibrary })));
            }

            if (stable is not null && stable.SkinsPath.Length > 0)
            {
                externalSkinSource = new OsuStableInstalledSkinSource(stable.SkinsPath);
                stableSkinApplyService = new OsuStableSkinApplyService(SkinManager);
            }

            replayOpenService = new CompositeLocalReplayOpenService();
            replayAnalysisBatchService = new ReplayAnalysisBatchService(replayOpenService);

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
                localScorePpHydrationService = new LocalScorePpHydrationService(
                    root.CanonicalPath,
                    Storage.GetFullPath("cache/local-score-pp-v1.json", true));
                Schedule(() =>
                {
                    ILocalLibrarySource fallback = switchableLocalLibrary.Current;
                    switchableLocalLibrary.SwitchTo(new CompositeLocalLibrarySource(new[] { externalLibrary, fallback }));
                    replayOpenService = new CompositeLocalReplayOpenService(new ExternalLazerReplayOpenService(root.CanonicalPath));
                    replayAnalysisBatchService = new ReplayAnalysisBatchService(replayOpenService);
                    startReplayLibraryAnalysis();
                });
            }

            Schedule(() =>
            {
                IInstalledSkinSource lazerSkins = new ExternalLazerInstalledSkinSource(root.CanonicalPath);
                externalSkinSource = externalSkinSource is null
                    ? lazerSkins
                    : new CompositeInstalledSkinSource(lazerSkins, externalSkinSource);
                externalSkinApplyService = new ExternalLazerSkinApplyService(
                    root.CanonicalPath,
                    SkinManager,
                    Storage.GetFullPath("cache/external-skin-mappings-v1.json", true));
                skinsScreen?.Configure(
                    externalSkinSource,
                    lazerPreferencesMonitor?.Current.SkinId,
                    appliedExternalSkinId,
                    applySelectedSkin);
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
            accountScoreHistoryService = new OfficialAccountScoreHistoryService(() => officialApiClient);
            officialBeatmapDiscoveryClient = new CachedOfficialBeatmapDiscoveryClient(
                new OfficialBeatmapDiscoveryClient(monitor),
                Storage.GetFullPath("cache/official-beatmap-search-v1.json", true));
            ppTargetExactCalculationService = new PpTargetExactCalculationService(
                root.CanonicalPath,
                Storage.GetFullPath("cache/pp-target-exact-v2.json", true),
                (IOfficialBeatmapDifficultyClient)officialBeatmapDiscoveryClient,
                Storage.GetFullPath("downloads/pp-target-difficulties", true));
            onlineBeatmapImportService = new OnlineBeatmapImportService(
                officialBeatmapDiscoveryClient,
                BeatmapManager,
                Storage.GetFullPath("downloads/beatmaps", true),
                localLibrary,
                beatmapDestinationService);
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
        {
            currentOsuProfile = null;
            return;
        }

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
                    {
                        currentOsuProfile = result.Profile;
                        header.SetProfile(result.Profile);
                    }
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void initialiseHubServices()
    {
        Uri hubBaseUri = OsuHubSyncClient.DefaultBaseUri;
        string? configuredHubUrl = Environment.GetEnvironmentVariable("AIMMOD_HUB_URL");
        if (Uri.TryCreate(configuredHubUrl, UriKind.Absolute, out Uri? configured)
            && (string.Equals(configured.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.Equals(configured.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            hubBaseUri = configured;
        }

        hubHttpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.Brotli | DecompressionMethods.Deflate | DecompressionMethods.GZip,
            AllowAutoRedirect = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        hubCredentialStore = new FileHubCredentialStore(Storage.GetFullPath("hub/credentials.bin", true));
        hubSharingPreferenceStore = new FileHubSharingPreferenceStore(Storage.GetFullPath("hub/sharing-preferences.json", true));
        var syncCache = new FileOsuHubSyncCache(Storage.GetFullPath("cache/hub-sync-v1.json", true));
        hubDeviceLinkClient = new HubDeviceLinkClient(hubHttpClient, hubBaseUri, hubCredentialStore);
        var syncClient = new OsuHubSyncClient(hubHttpClient, hubCredentialStore, syncCache, hubBaseUri);
        hubUploadQueue = new OsuHubUploadQueue(Storage.GetFullPath("hub/upload-queue-v1.json", true), syncClient);
        hubReplayShareService = new OsuHubReplayShareService(localLibrary, () => currentOsuProfile, replayAnalyses, hubUploadQueue);
    }

    private void openHubUrl(Uri uri)
    {
        if (uri.IsAbsoluteUri
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
            Host.OpenUrlExternally(uri.AbsoluteUri);
    }

    private void copyHubText(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            clipboard.SetText(value);
    }

    private void showHome()
    {
        homeScreen ??= new HomeScreen(updateService!, showBeatmaps, showSkins, showReplays, showStatistics, showCoaching, showPpTargets) { RelativeSizeAxes = Axes.Both };
        switchWorkspaceRoute(NativeRoute.Home, new MarginPadding { Top = 88, Horizontal = 52, Bottom = 36 }, homeScreen);
    }

    private void showBeatmaps()
    {
        beatmapsScreen ??= new NativeBeatmapDiscoveryScreen(
            localLibrary,
            () => officialBeatmapDiscoveryClient,
            () => onlineBeatmapImportService,
            () => ppTargetExactCalculationService,
            () => accountScoreHistoryService,
            openBeatmapInOsu)
        {
            RelativeSizeAxes = Axes.Both,
        };
        switchWorkspaceRoute(NativeRoute.Beatmaps, new MarginPadding { Top = 88, Horizontal = 52, Bottom = 36 }, beatmapsScreen);
    }

    private void showReplays()
    {
        replayRoute ??= new NativeReplayRouteView(
            localLibrary,
            replayAnalyses,
            prepareCatalogReplay,
            hubReplayShareService,
            hubCredentialStore,
            hubUploadQueue,
            hubSharingPreferenceStore,
            openHubUrl,
            copyHubText)
        {
            RelativeSizeAxes = Axes.Both,
        };
        switchWorkspaceRoute(NativeRoute.Replays, new MarginPadding { Top = 70, Horizontal = 12, Bottom = 12 }, replayRoute);
        startReplayLibraryAnalysis();
    }

    private void showSkins()
    {
        skinsScreen ??= new NativeSkinsScreen(
            externalSkinSource,
            lazerPreferencesMonitor?.Current.SkinId,
            appliedExternalSkinId,
            applySelectedSkin,
            onlineSkinCatalog,
            onlineSkinDestination,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AimMod Skins"))
        {
            RelativeSizeAxes = Axes.Both,
        };
        skinsScreen.Configure(
            externalSkinSource,
            lazerPreferencesMonitor?.Current.SkinId,
            appliedExternalSkinId,
            applySelectedSkin);
        skinsScreen.ConfigureOnlineDestination(onlineSkinDestination);
        switchWorkspaceRoute(NativeRoute.Skins, new MarginPadding { Top = 88, Horizontal = 52, Bottom = 36 }, skinsScreen);
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
        Guid localSkinId = skin.Origin == InstalledSkinOrigin.Stable
            ? await (stableSkinApplyService
                     ?? throw new ExternalLazerSkinApplyException("stable_library_unavailable", "AimMod is still connecting to the local osu!stable skin library."))
                .PrepareAsync(skin, cancellationToken).ConfigureAwait(false)
            : await (externalSkinApplyService
                     ?? throw new ExternalLazerSkinApplyException("lazer_library_unavailable", "AimMod is still connecting to the local lazer skin library."))
                .PrepareAsync(skin, cancellationToken).ConfigureAwait(false);
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
        statisticsScreen ??= new NativeStatisticsWorkspace(
            localLibrary,
            prepareCatalogReplay,
            () => accountScoreHistoryService)
        {
            RelativeSizeAxes = Axes.Both,
        };
        switchWorkspaceRoute(NativeRoute.Statistics, new MarginPadding { Top = 88, Horizontal = 52, Bottom = 0 }, statisticsScreen);
    }

    private void showSettings()
    {
        if (beatmapDestinationService is null)
            return;

        settingsScreen ??= new OsuClientSettingsScreen(
            beatmapDestinationService,
            hubDeviceLinkClient,
            hubCredentialStore,
            hubUploadQueue,
            hubSharingPreferenceStore,
            openHubUrl,
            copyHubText)
        {
            RelativeSizeAxes = Axes.Both,
        };
        switchWorkspaceRoute(NativeRoute.Settings, new MarginPadding { Top = 88, Horizontal = 52, Bottom = 36 }, settingsScreen);
    }

    private void showCoaching()
    {
        coachingWorkspace ??= new NativeCoachingWorkspace(
            localLibrary,
            replayAnalyses,
            prepareCatalogReplay,
            () => accountScoreHistoryService,
            createPracticeMap,
            installPracticeMap)
        {
            RelativeSizeAxes = Axes.Both,
        };
        switchWorkspaceRoute(NativeRoute.Coaching, new MarginPadding { Top = 88, Horizontal = 52, Bottom = 0 }, coachingWorkspace);
        startReplayLibraryAnalysis();
    }

    private async Task<PracticeMapGenerationResult> createPracticeMap(
        PracticeMapGenerationRequest request,
        CancellationToken cancellationToken)
    {
        ILocalReplayOpenService? replayService = replayOpenService;
        if (replayService is null)
            return new PracticeMapGenerationResult(false, "Connect an osu! installation before creating a practice map.");
        ILazerBeatmapInstallService? installService = lazerBeatmapInstallService;
        if (installService is null)
            return new PracticeMapGenerationResult(false, "Connect an osu! installation before creating a practice map.");

        string? root = null;
        LazerBeatmapArchive? lazerArchive = null;
        bool retainLazerArchive = false;
        try
        {
            await using IPlayableReplayBundle bundle = await replayService.OpenAsync(
                request.Candidate.SourceReplay,
                cancellationToken).ConfigureAwait(false);
            PracticeSourceBeatmap source = OsuPracticeBeatmapReader.Read(bundle.BeatmapPath);
            ReplayAnalysisResult[] evidence = request.Candidate.AnalysisScoreIds
                .Select(scoreId => replayAnalyses.GetValueOrDefault(scoreId))
                .Where(analysis => analysis?.Judgements is not null)
                .Cast<ReplayAnalysisResult>()
                .ToArray();
            IReadOnlyList<PracticeMapPlan> plans = PracticeMapPlanner.CreatePlans(
                source,
                evidence,
                new PracticeMapOptions(request.DrillType, MaximumSections: 1));
            if (plans.Count == 0)
            {
                string pattern = request.DrillType switch
                {
                    PracticeDrillType.LongJumps => "long-jump",
                    PracticeDrillType.Streams => "stream",
                    _ => "mixed-pattern",
                };
                return new PracticeMapGenerationResult(false, $"No evidence-backed {pattern} section was found on this difficulty.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            string folderName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            root = Storage.GetFullPath($"practice-maps/{folderName}", true);
            PracticeMapArtifact artifact = await new PracticeMapArtifactBuilder().BuildAsync(
                source,
                plans[0],
                root,
                cancellationToken).ConfigureAwait(false);
            lazerArchive = await installService.PreserveAsync(artifact.ArchivePath, 0, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            retainLazerArchive = true;
            return new PracticeMapGenerationResult(
                true,
                $"{plans[0].OutputVersion} is ready to open in osu!.",
                root,
                artifact.ArchivePath,
                lazerArchive);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PracticeMapArtifactBuilder.TryDelete(root);
            throw;
        }
        catch (FileNotFoundException error) when (error.Message.Contains("FFmpeg", StringComparison.OrdinalIgnoreCase))
        {
            PracticeMapArtifactBuilder.TryDelete(root);
            return new PracticeMapGenerationResult(false, "FFmpeg could not be found. Restart AimMod after confirming the installation.");
        }
        catch (TimeoutException)
        {
            PracticeMapArtifactBuilder.TryDelete(root);
            return new PracticeMapGenerationResult(false, "Audio preparation took too long. Try another section.");
        }
        catch (Exception error)
        {
            PracticeMapArtifactBuilder.TryDelete(root);
            logFailure("create practice map", error);
            return new PracticeMapGenerationResult(false, "The practice map could not be created from this source section.");
        }
        finally
        {
            if (!retainLazerArchive && lazerArchive is not null)
                installService.Discard(lazerArchive);
        }
    }

    private Task<LazerBeatmapInstallResult> installPracticeMap(
        LazerBeatmapArchive archive,
        CancellationToken cancellationToken) =>
        beatmapDestinationService?.InstallAsync(archive, cancellationToken)
        ?? Task.FromResult(new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LazerNotFound));

    internal static string LazerHandoffDirectory =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AimMod", "lazer-handoff"));

    private async Task openBeatmapInOsu(int beatmapId, CancellationToken cancellationToken)
    {
        IOsuBeatmapDestinationService? service = beatmapDestinationService;
        if (service is not null)
            await service.OpenBeatmapAsync(beatmapId, cancellationToken).ConfigureAwait(false);
    }

    private void showPpTargets()
    {
        ppTargetsWorkspace ??= new NativePpTargetsWorkspace(
            localLibrary,
            () => officialBeatmapDiscoveryClient,
            () => onlineBeatmapImportService,
            () => ppTargetExactCalculationService,
            () => localScorePpHydrationService,
            () => officialApiClient,
            new PpTargetWorkspaceCache(Storage.GetFullPath("cache/pp-target-workspace-v1.json", true)),
            () => accountScoreHistoryService,
            openBeatmapInOsu)
        {
            RelativeSizeAxes = Axes.Both,
        };
        switchWorkspaceRoute(NativeRoute.PpTargets, new MarginPadding { Top = 88, Horizontal = 52, Bottom = 24 }, ppTargetsWorkspace);
    }

    private void switchWorkspaceRoute(NativeRoute route, MarginPadding padding, Drawable screen)
    {
        if (workspaceHosts.TryGetValue(route, out Container? existingHost)
            && currentRoute.Value == route
            && existingHost.IsPresent)
            return;

        NativeRoute previousRoute = currentRoute.Value;
        if (previousRoute == NativeRoute.Replays && route != NativeRoute.Replays)
        {
            replayRoute?.SuspendPlayback();
        }
        if (previousRoute != route && isReplayAnalysisRoute(previousRoute))
            stopReplayLibraryAnalysis();

        foreach (Container host in workspaceHosts.Values)
            host.Hide();

        if (existingHost is null)
        {
            existingHost = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = padding,
                Alpha = 0,
                Child = screen,
            };
            workspaceHosts.Add(route, existingHost);
            content.Add(existingHost);
        }

        currentRoute.Value = route;
        existingHost.Show();
        if (isReplayAnalysisRoute(route))
            startReplayLibraryAnalysis();
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
        _ = openReplayAsync(request, cancellationToken);
    }

    private async Task openReplayAsync(ReplayOpenRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await loadReplay(request, cancellationToken, null, null).ConfigureAwait(false);
        }
        finally
        {
            finishReplaySelection(cancellationToken);
        }
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
        showReplays();
        if (replay is not null)
            replayRoute!.SetReplaySummary(replay);
        return replayAnalysisLifetime.Token;
    }

    private async Task prepareCatalogReplayAsync(LocalReplay replay, CancellationToken cancellationToken)
    {
        IPlayableReplayBundle? bundle = null;
        try
        {
            ILocalReplayOpenService service = replayOpenService
                ?? throw new ExternalLazerReplayOpenException(
                    "local_library_unavailable",
                    "AimMod is still connecting to the local osu! library. Try this replay again in a moment.");
            bundle = await service.OpenAsync(replay, cancellationToken).ConfigureAwait(false);
            await loadReplay(bundle.OpenRequest, cancellationToken, bundle, replay.ScoreId).ConfigureAwait(false);
            bundle = null;
            try
            {
                await analyseMatchingMapReplays(replay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                logFailure("analyse matching map replays", error);
                if (!IsDisposed)
                    Schedule(() => replayRoute?.RefreshMapPattern());
            }
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
            finishReplaySelection(cancellationToken);
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

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private void cancelReplayWork()
    {
        stopReplayLibraryAnalysis();

        CancellationTokenSource? work = replayAnalysisLifetime;
        replayAnalysisLifetime = null;
        work?.Cancel();
        work?.Dispose();
        replayRoute?.SuspendPlayback();
        activeReplayScoreId = null;

    }

    private void startReplayLibraryAnalysis()
    {
        if (!isReplayAnalysisRoute(currentRoute.Value)
            || activeReplayScoreId is not null
            || replayAnalysisLifetime is not null
            || replayAnalysisBatchService is null
            || replayLibraryAnalysisLifetime is not null)
            return;

        replayLibraryAnalysisLifetime = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        Guid[] cachedScoreIds = replayAnalyses.Keys.ToArray();
        Guid[] failedScoreIds = replayAnalysisFailures.ToArray();
        if (currentRoute.Value == NativeRoute.Coaching)
            coachingWorkspace?.BeginAnalysisProgress();
        _ = analyseReplayLibrary(replayAnalysisBatchService, cachedScoreIds, failedScoreIds, replayLibraryAnalysisLifetime.Token);
    }

    private void stopReplayLibraryAnalysis()
    {
        CancellationTokenSource? work = replayLibraryAnalysisLifetime;
        replayLibraryAnalysisLifetime = null;
        work?.Cancel();
        work?.Dispose();
    }

    private void finishReplaySelection(CancellationToken cancellationToken)
    {
        if (IsDisposed)
            return;

        Schedule(() =>
        {
            CancellationTokenSource? work = replayAnalysisLifetime;
            if (work is null || work.Token != cancellationToken)
                return;

            replayAnalysisLifetime = null;
            activeReplayScoreId = null;
            work.Dispose();
            startReplayLibraryAnalysis();
        });
    }

    private async Task analyseReplayLibrary(
        ReplayAnalysisBatchService service,
        IEnumerable<Guid> cachedScoreIds,
        IEnumerable<Guid> failedScoreIds,
        CancellationToken cancellationToken)
    {
        try
        {
            LocalReplay[] library = await loadReplayAnalysisWorkingSet(cancellationToken).ConfigureAwait(false);
            var processed = cachedScoreIds.Concat(failedScoreIds).ToHashSet();
            ReplayAnalysisCumulativeAccounting accounting = ReplayAnalysisCumulativeAccounting.Create(
                library,
                cachedScoreIds,
                failedScoreIds);
            int newlyCompleted = 0;
            int newlyFailed = 0;

            reportCoachingAnalysisProgress(
                accounting.MapBatchProgress(new ReplayAnalysisBatchProgress(0, 0, string.Empty)),
                cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                ReplayAnalysisCumulativeAccounting batchStart = accounting;
                var progress = new CallbackProgress<ReplayAnalysisBatchProgress>(batchProgress =>
                    reportCoachingAnalysisProgress(batchStart.MapBatchProgress(batchProgress), cancellationToken));
                ReplayAnalysisBatchResult result = await service.AnalyseBreadthFirstAsync(
                    library,
                    processed,
                    ReplayAnalysisBatchService.MaximumBatchSize,
                    progress,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (result.Completed.Count == 0 && result.Failed.Count == 0)
                    break;

                foreach (Guid scoreId in result.Completed.Keys)
                    processed.Add(scoreId);
                foreach (Guid scoreId in result.Failed)
                    processed.Add(scoreId);

                await applyReplayAnalysisBatch(result, cancellationToken).ConfigureAwait(false);
                accounting = accounting.Add(result);
                newlyCompleted += result.Completed.Count;
                newlyFailed += result.Failed.Count;
            }

            cancellationToken.ThrowIfCancellationRequested();
            reportCoachingAnalysisProgress(
                accounting.MapBatchProgress(new ReplayAnalysisBatchProgress(0, 0, string.Empty)),
                cancellationToken);
            Schedule(() =>
            {
                if (!cancellationToken.IsCancellationRequested && currentRoute.Value == NativeRoute.Coaching)
                    coachingWorkspace?.ApplyNewAnalyses(newlyCompleted, newlyFailed);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logFailure("analyse replay library", error);
            if (!IsDisposed)
            {
                Schedule(() =>
                {
                    if (!cancellationToken.IsCancellationRequested && currentRoute.Value == NativeRoute.Coaching)
                        coachingWorkspace?.SetAnalysisError();
                });
            }
        }
    }

    private void reportCoachingAnalysisProgress(
        ReplayAnalysisBatchProgress progress,
        CancellationToken cancellationToken)
    {
        if (progress.Total <= 0 || cancellationToken.IsCancellationRequested || IsDisposed)
            return;

        Schedule(() =>
        {
            if (!cancellationToken.IsCancellationRequested && currentRoute.Value == NativeRoute.Coaching)
                coachingWorkspace?.SetAnalysisProgress(progress.Completed, progress.Total, progress.CurrentTitle);
        });
    }

    private async Task<LocalReplay[]> loadReplayAnalysisWorkingSet(CancellationToken cancellationToken)
    {
        const int page_size = 200;
        var replays = new List<LocalReplay>();
        int offset = 0;

        while (true)
        {
            LocalLibraryPage<LocalReplay> page = await localLibrary.SearchReplaysAsync(new LocalLibraryQuery(
                RulesetShortName: "osu",
                Sort: LocalLibrarySort.RecentlyPlayed,
                Offset: offset,
                Limit: page_size), cancellationToken).ConfigureAwait(false);
            replays.AddRange(page.Items);
            if (!page.HasMore || page.Items.Count == 0)
                break;
            offset += page.Items.Count;
        }

        return ReplayAnalysisBatchService.OrderBreadthFirst(replays)
                                         .Take(ReplayAnalysisCache.MaximumEntries)
                                         .ToArray();
    }

    private async Task applyReplayAnalysisBatch(ReplayAnalysisBatchResult result, CancellationToken cancellationToken)
    {
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Schedule(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                applied.TrySetCanceled(cancellationToken);
                return;
            }

            foreach ((Guid scoreId, ReplayAnalysisResult analysis) in result.Completed)
            {
                replayAnalyses[scoreId] = analysis;
                replayAnalysisFailures.Remove(scoreId);
            }
            foreach (Guid scoreId in result.Failed)
                replayAnalysisFailures.Add(scoreId);

            replayRoute?.RefreshMapPattern();
            applied.TrySetResult();
        });
        await applied.Task.ConfigureAwait(false);

        if (result.Completed.Count > 0)
            await persistReplayAnalyses().ConfigureAwait(false);
    }

    private static bool isReplayAnalysisRoute(NativeRoute route) =>
        route is NativeRoute.Replays or NativeRoute.Coaching;

    private async Task analyseMatchingMapReplays(LocalReplay selected, CancellationToken cancellationToken)
    {
        ReplayAnalysisBatchService? service = replayAnalysisBatchService;
        if (service is null)
            return;

        LocalLibraryPage<LocalReplay> page = await localLibrary.SearchReplaysAsync(new LocalLibraryQuery(
            SearchText: selected.Title,
            RulesetShortName: "osu",
            Sort: LocalLibrarySort.RecentlyPlayed,
            Limit: 200), cancellationToken).ConfigureAwait(false);
        LocalReplay[] matching = page.Items.Where(run => ReplayMapPatternAnalyzer.IsSameDifficultyAndSetup(selected, run))
                                           .ToArray();
        var progress = new Progress<ReplayAnalysisBatchProgress>(value =>
        {
            if (!IsDisposed)
                Schedule(() => replayRoute?.ShowMapAnalysisProgress(value.Completed, value.Total, value.CurrentTitle));
        });
        ReplayAnalysisBatchResult result = await service.AnalyseRecentAsync(
            matching,
            replayAnalyses.Keys.Concat(replayAnalysisFailures).ToArray(),
            ReplayAnalysisBatchService.MaximumBatchSize,
            progress,
            cancellationToken).ConfigureAwait(false);

        if (IsDisposed || cancellationToken.IsCancellationRequested)
            return;

        Schedule(() =>
        {
            foreach ((Guid scoreId, ReplayAnalysisResult analysis) in result.Completed)
            {
                replayAnalyses[scoreId] = analysis;
                replayAnalysisFailures.Remove(scoreId);
            }

            foreach (Guid scoreId in result.Failed)
                replayAnalysisFailures.Add(scoreId);

            if (result.Completed.Count > 0)
                _ = persistReplayAnalyses();
            replayRoute?.RefreshMapPattern();
        });
    }

    protected override void Dispose(bool isDisposing)
    {
        appLifetime.Cancel();
        profileRefreshCancellation?.Cancel();
        profileRefreshCancellation?.Dispose();
        skinApplyLifetime?.Cancel();
        skinApplyLifetime?.Dispose();
        officialApiClient?.Dispose();
        (officialBeatmapDiscoveryClient as IDisposable)?.Dispose();
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
        updateService?.Dispose();
        hubUploadQueue?.Dispose();
        hubHttpClient?.Dispose();
        onlineSkinCatalog?.Dispose();
        appLifetime.Dispose();
        base.Dispose(isDisposing);
    }

    private partial class HeaderBar : Container
    {
        private readonly TruncatingSpriteText sessionState;
        private readonly Drawable productPill;
        private readonly FillFlowContainer<Drawable> navigation;

        public HeaderBar(
            Bindable<NativeRoute> currentRoute,
            Action showHome,
            Action showBeatmaps,
            Action showSkins,
            Action showReplays,
            Action showStatistics,
            Action showCoaching,
            Action showPpTargets,
            Action showSettings)
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
                        productPill = new AimModPill("osu!", AimModPillTone.Accent),
                    },
                },
                navigation = new FillFlowContainer<Drawable>
                {
                    AutoSizeAxes = Axes.Both,
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Direction = FillDirection.Horizontal,
                    Spacing = new(5),
                    Children = new Drawable[]
                    {
                        new NavItem("Home", NativeRoute.Home, currentRoute, showHome),
                        new NavItem("Beatmaps", NativeRoute.Beatmaps, currentRoute, showBeatmaps),
                        new NavItem("Skins", NativeRoute.Skins, currentRoute, showSkins),
                        new NavItem("Replays", NativeRoute.Replays, currentRoute, showReplays),
                        new NavItem("Statistics", NativeRoute.Statistics, currentRoute, showStatistics),
                        new NavItem("Coaching", NativeRoute.Coaching, currentRoute, showCoaching),
                        new NavItem("PP Targets", NativeRoute.PpTargets, currentRoute, showPpTargets),
                        new NavItem("Settings", NativeRoute.Settings, currentRoute, showSettings),
                    },
                },
                sessionState = new TruncatingSpriteText
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Margin = new MarginPadding { Right = 28 },
                    Text = "Finding osu!lazer...",
                    Font = new FontUsage(size: 13),
                    Colour = AimModPalette.Muted,
                    MaxWidth = 220,
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            bool compact = DrawWidth < 1_080;
            productPill.Alpha = compact ? 0 : 1;
            sessionState.Alpha = compact ? 0 : 1;
            navigation.Scale = new Vector2(DrawWidth < 900 ? 0.88f : 1);
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
        public HomeScreen(
            INativeUpdateService updateService,
            Action showBeatmaps,
            Action showSkins,
            Action showReplays,
            Action showStatistics,
            Action showCoaching,
            Action showPpTargets)
        {
            Children = new Drawable[]
            {
                new AimModSectionHeader(
                    "Your osu! workspace",
                    "Maps, scores, replay evidence, and your next training decision.",
                    "aimmod!lazer"),
                text("WORKSPACES", 10, AimModPalette.Cyan, "Bold").With(drawable => drawable.Y = 98),
                new GridContainer
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 324,
                    Y = 122,
                    ColumnDimensions = new[]
                    {
                        new Dimension(GridSizeMode.Relative, 0.5f),
                        new Dimension(GridSizeMode.Relative, 0.5f),
                    },
                    RowDimensions = new[]
                    {
                        new Dimension(GridSizeMode.Absolute, 108),
                        new Dimension(GridSizeMode.Absolute, 108),
                        new Dimension(GridSizeMode.Absolute, 108),
                    },
                    Content = new[]
                    {
                        new Drawable[]
                        {
                            new WorkspaceLink(FontAwesome.Solid.Music, "Beatmaps", "Installed and online map library", AimModPalette.Pink, showBeatmaps),
                            new WorkspaceLink(FontAwesome.Solid.Play, "Replays", "Playback, judgements, and miss evidence", AimModPalette.Cyan, showReplays),
                        },
                        new Drawable[]
                        {
                            new WorkspaceLink(FontAwesome.Solid.ChartLine, "Statistics", "Performance history and map detail", AimModPalette.Pink, showStatistics),
                            new WorkspaceLink(FontAwesome.Solid.Bullseye, "Coaching", "Global skill profile and practice maps", AimModPalette.Cyan, showCoaching),
                        },
                        new Drawable[]
                        {
                            new WorkspaceLink(FontAwesome.Solid.Crosshairs, "PP targets", "Personal opportunities by difficulty", AimModPalette.Pink, showPpTargets),
                            new WorkspaceLink(FontAwesome.Solid.PaintBrush, "Skins", "Installed osu!stable and lazer skins", AimModPalette.Cyan, showSkins),
                        },
                    },
                },
                new NativeUpdateSurface(updateService)
                {
                    Y = 470,
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

    private partial class WorkspaceLink : AimModInteractiveSurface
    {
        private readonly TruncatingSpriteText titleText;
        private readonly TruncatingSpriteText descriptionText;

        public WorkspaceLink(
            IconUsage icon,
            string title,
            string description,
            Colour4 accentColour,
            Action? action = null)
        {
            RelativeSizeAxes = Axes.Both;
            Padding = new MarginPadding(AimModVisualStyle.RelatedSpacing);
            CornerRadius = AimModVisualStyle.ControlRadius;
            BackgroundColour = AimModPalette.Panel;
            Action = action;

            Children = new Drawable[]
            {
                new SpriteIcon
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Margin = new MarginPadding { Left = 18 },
                    Icon = icon,
                    Size = new(20),
                    Colour = accentColour,
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    AutoSizeAxes = Axes.Y,
                    RelativeSizeAxes = Axes.X,
                    Width = 1,
                    Margin = new MarginPadding { Left = 54 },
                    Padding = new MarginPadding { Right = 48 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(2),
                    Children = new Drawable[]
                    {
                        titleText = new TruncatingSpriteText
                        {
                            Text = title,
                            Font = new FontUsage(size: 17, weight: "SemiBold"),
                            Colour = AimModPalette.Text,
                        },
                        descriptionText = new TruncatingSpriteText
                        {
                            Text = description,
                            Font = new FontUsage(size: 11),
                            Colour = AimModPalette.Muted,
                        },
                    },
                },
                new SpriteIcon
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Margin = new MarginPadding { Right = 16 },
                    Icon = FontAwesome.Solid.ChevronRight,
                    Size = new(10),
                    Colour = AimModPalette.Muted,
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            float textWidth = Math.Max(80, DrawWidth - 112);
            titleText.MaxWidth = textWidth;
            descriptionText.MaxWidth = textWidth;
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
            Padding = new MarginPadding { Horizontal = 9, Vertical = 9 };
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
        PpTargets,
        Settings,
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
