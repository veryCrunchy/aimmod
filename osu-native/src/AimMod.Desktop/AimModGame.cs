using System.Reflection;
using System.Collections.Concurrent;
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
using osu.Framework.Input.Handlers;
using osu.Framework.Input.Handlers.Mouse;
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
using AimMod.Desktop.Trainers;
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
    private readonly OverlayColourProvider overlayColours = new(OverlayColourScheme.Blue);

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
    private NativeTrainersWorkspace? trainersWorkspace;
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
    private IAccountScoreHistoryService? stablePublicScoreHistoryService;
    private Uri hubBaseUri = OsuHubSyncClient.DefaultBaseUri;
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
    private readonly ConcurrentDictionary<Guid, ReplayAnalysisResult> replayAnalyses = new();
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
    private HubAutomaticShareService? hubAutomaticShareService;
    private HubTrainingSyncService? hubTrainingSyncService;
    private OsuProfile? currentOsuProfile;
    private OsuProfile? stableOsuProfile;
    private OsuProfile? verifiedLazerProfile;

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

    internal AimModLinkInbox? LinkInbox { get; init; }

    public override void SetHost(GameHost host)
    {
        base.SetHost(host);
        // OsuGame applies this above OsuGameBase, which AimMod derives from directly.
        if (host.Window is not null)
            host.Window.CursorState |= CursorState.Hidden;
    }

    internal static void UseDesktopMouseCoordinates(IEnumerable<InputHandler> handlers)
    {
        // Absolute desktop coordinates already include the user's OS pointer settings.
        // Keep osu! sensitivity and tablet mapping untouched; only bypass raw mouse scaling.
        foreach (MouseHandler mouse in handlers.OfType<MouseHandler>())
            mouse.UseRelativeMode.Value = false;
    }

    protected override void Update()
    {
        base.Update();
        while (LinkInbox?.TryTake(out var link) == true)
            openDeepLink(link!);
    }

    private void openDeepLink(AimModDeepLink link)
    {
        if (link.BeatmapSetId is int setId)
        {
            showBeatmaps();
            beatmapsScreen!.OpenSet(setId);
        }
        else
        {
            showSkins();
            skinsScreen!.OpenOnlineSkin(link.ProviderId!, link.SourceId!);
        }
        Host.Window?.Raise();
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        UseDesktopMouseCoordinates(Host.AvailableInputHandlers);

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
                header = new HeaderBar(currentRoute, showHome, showBeatmaps, showSkins, showReplays, showStatistics, showCoaching, showTrainers, showPpTargets, showSettings)
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

        if (launchOptions.DeepLink is not null)
        {
            openDeepLink(launchOptions.DeepLink);
            return;
        }

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
        if(configuredLocalLibrary is null)Scheduler.AddDelayed(playStartupTheme, 600);
        Scheduler.AddDelayed(tickAutomaticPractice, 15_000, true);
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
                CurrentUserName: Environment.UserName,
                RegisteredStableRoots: WindowsStableInstallationPaths.Read());

            OsuStableDiscoveryResult stableDiscovery = await Task.Run(
                () => new OsuStableDiscoveryService(new PhysicalOsuDiscoveryFileSystem()).Discover(platform, environment),
                cancellationToken).ConfigureAwait(false);
            OsuStableInstallation? stable = stableDiscovery.CompleteInstallations.FirstOrDefault();
            trainerStableRoot = stable?.CanonicalPath;
            trainerStableConfiguration = stable?.ConfigurationPath;
            Schedule(() => header.SetStableAccount(stable?.RememberedUsername, stable is not null));
            if (stable is not null)
            {
                localScorePpHydrationService = new LocalScorePpHydrationService(
                    stable.CanonicalPath, Storage.GetFullPath("cache/local-score-pp-v1.json", true));
                stablePublicScoreHistoryService = new HubPublicAccountScoreHistoryService(hubHttpClient!, hubBaseUri,
                    stable.RememberedUsername, userId: stable.RememberedUserId);
                accountScoreHistoryService = stablePublicScoreHistoryService;
                if (stable.RememberedUserId is > 0 && !string.IsNullOrWhiteSpace(stable.RememberedUsername))
                {
                    var localProfile = new OsuProfile(stable.RememberedUserId.Value, stable.RememberedUsername, null, null, null);
                    Schedule(() => applyStableProfile(localProfile, false));
                }
                _ = refreshStablePublicProfile(stablePublicScoreHistoryService, cancellationToken);
            }
            ILocalLibrarySource? stableLibrary = stable is null
                ? null
                : new CachedLocalLibrarySource(new OsuStableLocalLibrarySource(stable.CanonicalPath, stable.SongsPath),
                    Storage.GetFullPath("cache/library-stable-v3", true),
                    Path.Combine(stable.CanonicalPath, "osu!.db"), Path.Combine(stable.CanonicalPath, "scores.db"),
                    Path.Combine(stable.CanonicalPath, "Data", "r"), Path.Combine(stable.CanonicalPath, "Replays"));

            var lazerInstall = new LazerBeatmapInstallService(LazerHandoffDirectory);
            beatmapDestinationService = new OsuBeatmapDestinationService(
                lazerInstall,
                new FileOsuClientDestinationPreferenceStore(Storage.GetFullPath("osu-client-destination.txt", true)),
                LazerHandoffDirectory,
                stable is null ? null : Path.Combine(stable.CanonicalPath, "osu!.exe"));
            lazerBeatmapInstallService = beatmapDestinationService;
            beatmapDestinationService.DestinationChanged += accountDestinationChanged;
            Schedule(showFirstRunSetup);
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
            replayAnalysisBatchService = new ReplayAnalysisBatchService(replayOpenService, onCompleted: cacheCompletedReplay);

            OsuLazerDiscoveryResult discovery = await Task.Run(
                () => new OsuLazerDiscoveryService(new PhysicalOsuDiscoveryFileSystem()).Discover(platform, environment),
                cancellationToken).ConfigureAwait(false);
            OsuLazerDataRoot? root = discovery.CompleteDataRoots.FirstOrDefault();
            if (root is null)
            {
                Schedule(() =>
                {
                    header.SetSessionState(new LazerSessionState(LazerSessionStatus.Unavailable, null, 0));
                    skinsScreen?.Configure(externalSkinSource, null, appliedExternalSkinId, applySelectedSkin);
                    startReplayLibraryAnalysis();
                });
                return;
            }

            if (switchableLocalLibrary is not null)
            {
                var externalLibrary = new CachedLocalLibrarySource(new ExternalLazerLocalLibrarySource(root.CanonicalPath),
                    Storage.GetFullPath("cache/library-lazer-v1", true), Path.Combine(root.CanonicalPath, "client.realm"));
                localScorePpHydrationService = new LocalScorePpHydrationService(
                    root.CanonicalPath,
                    Storage.GetFullPath("cache/local-score-pp-v1.json", true));
                Schedule(() =>
                {
                    ILocalLibrarySource fallback = switchableLocalLibrary.Current;
                    switchableLocalLibrary.SwitchTo(new CompositeLocalLibrarySource(new[] { externalLibrary, fallback }));
                    replayOpenService = new CompositeLocalReplayOpenService(new ExternalLazerReplayOpenService(root.CanonicalPath));
                    replayAnalysisBatchService = new ReplayAnalysisBatchService(replayOpenService, onCompleted: cacheCompletedReplay);
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

            trainerLazerRoot = root.CanonicalPath;
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
        // A locally remembered lazer session is not yet a verified online account.
        verifiedLazerProfile = null;
        header.SetSessionState(state);
        refreshActiveAccount();

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
            {
                if (!IsDisposed)
                    Schedule(() =>
                    {
                        if (lazerSessionMonitor?.Current.Revision == sessionRevision)
                            header.SetAccountUnavailable();
                    });
                return;
            }

            if (!IsDisposed)
            {
                Schedule(() =>
                {
                    if (lazerSessionMonitor?.Current.Revision == sessionRevision)
                    {
                        verifiedLazerProfile = result.Profile;
                        refreshActiveAccount();
                    }
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task refreshStablePublicProfile(IAccountScoreHistoryService service, CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.FetchAccountAsync(cancellationToken).ConfigureAwait(false);
            if (!IsDisposed && result.Profile is { } profile)
                Schedule(() => applyStableProfile(profile, true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void initialiseHubServices()
    {
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
        hubTrainingSyncService = new HubTrainingSyncService(Storage.GetFullPath("hub/training-queue-v1.json", true),
            hubHttpClient, hubBaseUri, hubCredentialStore, hubSharingPreferenceStore, () => currentOsuProfile?.UserId);
        if (configuredLocalLibrary is null)
            _ = Task.Run(() => hubTrainingSyncService.RunAsync(appLifetime.Token));
        var syncCache = new FileOsuHubSyncCache(Storage.GetFullPath("cache/hub-sync-v1.json", true));
        hubDeviceLinkClient = new HubDeviceLinkClient(hubHttpClient, hubBaseUri, hubCredentialStore);
        var syncClient = new OsuHubSyncClient(hubHttpClient, hubCredentialStore, syncCache, hubBaseUri);
        hubUploadQueue = new OsuHubUploadQueue(Storage.GetFullPath("hub/upload-queue-v1.json", true), syncClient);
        hubReplayShareService = new OsuHubReplayShareService(localLibrary, () => currentOsuProfile, replayAnalyses, hubUploadQueue,
            () => replayOpenService, Storage.GetFullPath("hub/upload-spool", true), () => localScorePpHydrationService);
        hubAutomaticShareService = new HubAutomaticShareService(
            Storage.GetFullPath("hub/automatic-sharing-state.json", true), hubSharingPreferenceStore, hubReplayShareService, () =>
            {
                HubCredential? credential = hubCredentialStore.Load();
                OsuProfile? profile = currentOsuProfile;
                return credential is null || profile is null || string.IsNullOrWhiteSpace(credential.AccountLabel)
                    ? null
                    : new HubAutomaticShareAccount($"{hubBaseUri.AbsoluteUri.TrimEnd('/')}/|{credential.AccountLabel}", profile.UserId, profile.Username);
            });
        if (configuredLocalLibrary is null)
            _ = Task.Run(() => observeHubAutomaticShares(appLifetime.Token));
    }

    private async Task observeHubAutomaticShares(CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        DateTimeOffset nextRefresh = DateTimeOffset.MinValue;
        Guid generation = Guid.Empty;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                try
                {
                    HubSharingPreferences preferences = hubSharingPreferenceStore!.Load();
                    if (!preferences.AutomaticSharingEnabled || currentOsuProfile is null || hubCredentialStore?.Load() is null)
                        continue;
                    // Establish the cutoff before a potentially slow history query starts.
                    await hubAutomaticShareService!.ObserveAsync([], cancellationToken).ConfigureAwait(false);
                    if (generation == preferences.AutomaticSharingGeneration && DateTimeOffset.UtcNow < nextRefresh)
                        continue;
                    generation = preferences.AutomaticSharingGeneration;
                    nextRefresh = DateTimeOffset.UtcNow.AddSeconds(30);
                    LocalLibraryPage<LocalReplay> local = await localLibrary.SearchReplaysAsync(new LocalLibraryQuery(
                        RulesetShortName: "osu", Sort: LocalLibrarySort.RecentlyPlayed, Limit: 200), cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<LocalReplay> recent = local.Items.Where(play => play.PlayedAt > startedAt).ToArray();
                    if (localScorePpHydrationService is { } hydrator && recent.Any(play => play.PerformancePoints is null))
                        recent = (await hydrator.HydrateAsync(recent, cancellationToken).ConfigureAwait(false)).Runs;
                    // Local scores remain available even when the online history request fails.
                    await hubAutomaticShareService.ObserveAsync(recent, cancellationToken).ConfigureAwait(false);
                    if (accountScoreHistoryService is { } history)
                    {
                        OnlineAccountScoreHistoryResult online = await history.FetchAccountAsync(cancellationToken).ConfigureAwait(false);
                        if (online.Profile is { } profile && profile.UserId == currentOsuProfile?.UserId)
                        {
                            LocalReplay[] merged = ScoreHistoryMerger.MergeAsLocalReplays(recent, online.Scores)
                                .Select(play => !play.IsLocallyStored ? play with { Player = profile.Username } : play).ToArray();
                            await hubAutomaticShareService.ObserveAsync(merged, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) { logFailure("observe automatic Hub shares", error); }
            } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
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
        homeScreen ??= new HomeScreen(updateService!, showBeatmaps, showSkins, showReplays, showStatistics, showCoaching, showPpTargets, showTrainers, showSettings) { RelativeSizeAxes = Axes.Both };
        switchWorkspaceRoute(NativeRoute.Home, homeScreen);
    }

    private void showBeatmaps()
    {
        beatmapsScreen ??= new NativeBeatmapDiscoveryScreen(
            localLibrary,
            () => officialBeatmapDiscoveryClient,
            () => onlineBeatmapImportService,
            () => ppTargetExactCalculationService,
            () => accountScoreHistoryService,
            openBeatmapInOsu,
            openBeatmapPractice)
        {
            RelativeSizeAxes = Axes.Both,
        };
        switchWorkspaceRoute(NativeRoute.Beatmaps, beatmapsScreen);
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
            copyHubText,
            openBeatmapPractice,
            openReplayBeatmap,
            () => localScorePpHydrationService)
        {
            RelativeSizeAxes = Axes.Both,
        };
        switchWorkspaceRoute(NativeRoute.Replays, replayRoute);
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
        switchWorkspaceRoute(NativeRoute.Skins, skinsScreen);
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
            () => accountScoreHistoryService, openReplayBeatmap)
        {
            RelativeSizeAxes = Axes.Both,
        };
        switchWorkspaceRoute(NativeRoute.Statistics, statisticsScreen);
    }

    private void showSettings()
    {
        if (beatmapDestinationService is null)
            return;

        if(currentRoute.Value != NativeRoute.Settings && workspaceHosts.Remove(NativeRoute.Settings,out var previousSettings))
        {
            content.Remove(previousSettings,true);
            settingsScreen=null;
        }
        settingsScreen ??= createUserSettings();
        switchWorkspaceRoute(NativeRoute.Settings, settingsScreen);
    }

    private void showTrainers()
    {
        trainersWorkspace ??= new NativeTrainersWorkspace(
            () => new TrainerHistoryStore(Storage.GetFullPath($"trainers/history-{currentOsuProfile?.UserId ?? 0}.json", true)), showCoaching);
        trainersWorkspace.LaunchOsuSession = startOsuTrainer;
        trainersWorkspace.BeginTrainingSync = () => hubTrainingSyncService?.BeginSession();
        trainersWorkspace.SongLibrary = localLibrary;
        trainersWorkspace.CurrentSkillAccountId=()=>currentOsuProfile?.UserId;
        switchWorkspaceRoute(NativeRoute.Trainers, trainersWorkspace);
        trainersWorkspace.RefreshHistory();
        _ = refreshTrainerSettingsAsync();
        _ = refreshTrainerSkillEvidenceAsync();
    }

    private void showCoaching()
    {
        coachingWorkspace ??= new NativeCoachingWorkspace(
            localLibrary,
            replayAnalyses,
            prepareCatalogReplay,
            () => accountScoreHistoryService,
            createPracticeMap,
            installPracticeMap,
            new NativePracticeWorkspace(inspectPracticeMap, createPracticeMap, openSavedPracticeMap,
                new PracticeMapLibrary(Storage.GetFullPath("practice-maps", true)), () => coachingWorkspace?.ClosePractice(), openReplayBeatmap)
                { StartDifficulty = startPracticeDifficulty }, openReplayBeatmap,
            new CoachingTrainingStore(Storage.GetFullPath($"coaching/session-{currentOsuProfile?.UserId ?? 0}.json", true)),
            new PracticeMapLibrary(Storage.GetFullPath("practice-maps", true)), () => currentOsuProfile?.UserId ?? 0,
            prepareCatalogReplayMoment, () => { showTrainers(); trainersWorkspace?.StartGuidedPractice(TrainerGuidedFocus.MovementComparison); }, showTrainers)
        {
            RelativeSizeAxes = Axes.Both,
        };
        coachingWorkspace.ConfigurePracticeSessions(new CoachingPracticeSessionStore(Storage.GetFullPath($"coaching/practice-sessions-{currentOsuProfile?.UserId ?? 0}.json", true)),
            () => new TrainerHistoryStore(Storage.GetFullPath($"trainers/history-{currentOsuProfile?.UserId ?? 0}.json", true)).Load());
        switchWorkspaceRoute(NativeRoute.Coaching, coachingWorkspace);
        coachingWorkspace.SetAutomaticPracticeStatus(automaticPracticeStatus);
        startReplayLibraryAnalysis();
    }

    private void openBeatmapPractice(string title)
    {
        showCoaching();
        coachingWorkspace!.FocusPracticeMap(title);
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

        int creationAccount = currentOsuProfile?.UserId ?? 0;
        string creationPlayer = currentOsuProfile?.Username ?? request.Candidate.SourceReplay.Player;
        string? root = null;
        LazerBeatmapArchive? lazerArchive = null;
        bool retainLazerArchive = false;
        try
        {
            request.Progress?.Report("Loading source beatmap and audio");
            LocalReplay sourceReplay = await ReplayBeatmapResolver.ResolveSourceAsync(localLibrary, request.Candidate.SourceReplay, cancellationToken).ConfigureAwait(false);
            await using IBeatmapSourceLease bundle = await replayService.OpenBeatmapSourceAsync(
                sourceReplay,
                cancellationToken).ConfigureAwait(false);
            PracticeSourceBeatmap source = OsuPracticeBeatmapReader.Read(bundle.BeatmapPath);
            var history = request.SourceHistory ?? [request.Candidate.SourceReplay];
            string player = creationPlayer;
            var tracking = new PracticeTracking(player, creationAccount, sourceReplay.OnlineBeatmapId,
                sourceReplay.BeatmapHash, sourceReplay.BeatmapId, ScoreMods.Configuration(request.Candidate.SourceReplay),
                PracticeProgressTracker.Stable(request.Candidate.SourceReplay), [], []);
            ReplayAnalysisResult[] evidence = practiceEvidence(request.Candidate, tracking, request.Automatic ? history.Where(r => r.ScoreId == request.Candidate.SourceReplay.ScoreId).ToArray() : history);
            var createdAt = DateTimeOffset.UtcNow;
            tracking = tracking with { Baseline = PracticeProgressTracker.Baseline(history, tracking, createdAt) };
            IReadOnlyList<PracticeMapPlan> plans = PracticeSetArtifactBuilder.Plan(source, evidence,
                (request.Options ?? new PracticeMapOptions(request.DrillType, MaximumSections: 1)) with { DrillType = request.DrillType, AllowPatternPractice = true }, request.CreateSet, request.CreateBreakdown);
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
            string folderName = Guid.NewGuid().ToString("N");
            root = Storage.GetFullPath($"practice-maps/{folderName}", true);
            request.Progress?.Report($"Looping source audio / {plans[0].RepeatCount} rounds");
            PracticeSetArtifact artifact = await new PracticeSetArtifactBuilder().BuildAsync(
                source, plans, root, request.Progress, cancellationToken).ConfigureAwait(false);
            plans = artifact.Plans;
            tracking = tracking with { Difficulties = artifact.Difficulties };
            lazerArchive = await installService.PreserveAsync(artifact.ArchivePath, 0, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (request.Automatic && (currentOsuProfile?.UserId != creationAccount || !new AutomaticPracticeStore(Storage.GetFullPath("practice-maps",true)).Load().Enabled))
                throw new OperationCanceledException("Automatic practice was stopped.");
            request.Progress?.Report("Saving your practice map");
            PracticeMapPlan plan = plans[0];
            new PracticeMapLibrary(Storage.GetFullPath("practice-maps", true)).Save(new SavedPracticeMap(
                folderName, plan.SourceTitle, plan.SourceVersion, plan.DrillType, createdAt,
                plan.SourceSection.SourceStartTimeMs, plan.SourceSection.SourceEndTimeMs,
                plans.Sum(p => p.AudioLeadInMs + p.AudioSlice.OutputDurationMs), plan.RepeatCount, plans.Sum(p => p.HitObjects.Count), PlaybackRate: plan.AudioSlice.PlaybackRate, Tracking: tracking, Automatic: request.Automatic, RevisionScoreId: request.Candidate.SourceReplay.ScoreId));

            retainLazerArchive = true;
            if (!request.Automatic) Schedule(() => nextAutomaticPractice = DateTimeOffset.MinValue);
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
        catch (FfmpegSetupException error)
        {
            PracticeMapArtifactBuilder.TryDelete(root);
            logFailure("prepare practice audio tool", error);
            return new PracticeMapGenerationResult(false, error.Message);
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

    private ReplayAnalysisResult[] practiceEvidence(PracticeMapCandidate candidate, PracticeTracking tracking, IReadOnlyList<LocalReplay> history) =>
        history.Append(candidate.SourceReplay).Where(run => PracticeProgressTracker.SamePlayer(run, tracking.Player) && PracticeProgressTracker.SameSource(run, tracking))
            .Select(run => run.ScoreId).Distinct().Select(id => replayAnalyses.GetValueOrDefault(id))
            .Where(result => result is not null).Cast<ReplayAnalysisResult>().ToArray();

    private async Task<IReadOnlyList<PracticeSectionChoice>> inspectPracticeMap(PracticeMapCandidate candidate, CancellationToken cancellationToken)
    {
        ILocalReplayOpenService service = replayOpenService ?? throw new InvalidOperationException("No local osu! source is connected.");
        LocalReplay sourceReplay = await ReplayBeatmapResolver.ResolveSourceAsync(localLibrary, candidate.SourceReplay, cancellationToken).ConfigureAwait(false);
        await using IBeatmapSourceLease bundle = await service.OpenBeatmapSourceAsync(sourceReplay, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            PracticeSourceBeatmap source = OsuPracticeBeatmapReader.Read(bundle.BeatmapPath);
            var tracking = new PracticeTracking(currentOsuProfile?.Username ?? candidate.SourceReplay.Player,
                currentOsuProfile?.UserId ?? 0, sourceReplay.OnlineBeatmapId, sourceReplay.BeatmapHash, sourceReplay.BeatmapId,
                ScoreMods.Configuration(candidate.SourceReplay), PracticeProgressTracker.Stable(candidate.SourceReplay), [], []);
            ReplayAnalysisResult[] evidence = practiceEvidence(candidate, tracking, coachingWorkspace?.PracticeSourceHistory ?? []);
            return (IReadOnlyList<PracticeSectionChoice>)Enum.GetValues<PracticeDrillType>()
                .SelectMany(type => PracticeMapPlanner.FindSections(source, evidence, new PracticeMapOptions(type, AllowPatternPractice: true, IncludeOverlappingSections: true))
                    .Select(section => new PracticeSectionChoice(type, section))).ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LazerBeatmapInstallResult> openSavedPracticeMap(SavedPracticeMap map, CancellationToken cancellationToken)
    {
        ILazerBeatmapInstallService service = lazerBeatmapInstallService ?? throw new InvalidOperationException("No osu! installation is connected.");
        string path = new PracticeMapLibrary(Storage.GetFullPath("practice-maps", true)).ArchivePath(map.Id);
        LazerBeatmapArchive archive = await service.PreserveAsync(path, 0, cancellationToken).ConfigureAwait(false);
        return await installPracticeMap(archive, cancellationToken).ConfigureAwait(false);
    }

    internal static string LazerHandoffDirectory =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AimMod", "lazer-handoff"));

    private async Task openBeatmapInOsu(int beatmapId, CancellationToken cancellationToken)
    {
        IOsuBeatmapDestinationService? service = beatmapDestinationService;
        if (service is not null)
            await service.OpenBeatmapAsync(beatmapId, cancellationToken).ConfigureAwait(false);
    }

    private async Task openReplayBeatmap(LocalReplay replay, CancellationToken cancellationToken)
    {
        int id = await ReplayBeatmapResolver.ResolveAsync(localLibrary, replay, cancellationToken).ConfigureAwait(false);
        if (beatmapDestinationService is null) throw new InvalidOperationException("Choose an osu! client in Settings first.");
        var result = await beatmapDestinationService.OpenBeatmapAsync(id, cancellationToken).ConfigureAwait(false);
        if (result.Status is not (LazerBeatmapInstallStatus.Sent or LazerBeatmapInstallStatus.LazerStarted))
            throw new InvalidOperationException("Could not open your selected osu! client. Check the client preference in Settings.");
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
            openBeatmapInOsu,
            replayAnalyses,
            () => currentOsuProfile?.Username)
        {
            RelativeSizeAxes = Axes.Both,
        };
        switchWorkspaceRoute(NativeRoute.PpTargets, ppTargetsWorkspace);
    }

    private void switchWorkspaceRoute(NativeRoute route, Drawable screen)
    {
        if (workspaceHosts.TryGetValue(route, out Container? existingHost)
            && currentRoute.Value == route
            && existingHost.IsPresent)
            return;

        if (trainerPlayer is not null) trainerPlayer.StopSession();
        NativeRoute previousRoute = currentRoute.Value;
        if (previousRoute == NativeRoute.Trainers && route != NativeRoute.Trainers)
            trainersWorkspace?.Suspend();
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
                Padding = AimModVisualStyle.PagePadding,
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
        content.Padding = AimModVisualStyle.PagePadding;
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

    private void accountDestinationChanged(OsuClientDestination destination)
    {
        if (!IsDisposed)
            Schedule(refreshActiveAccount);
    }

    private void applyStableProfile(OsuProfile profile, bool publicProfile)
    {
        stableOsuProfile = profile;
        if (publicProfile) header.SetPublicProfile(profile);
        refreshActiveAccount();
    }

    private void refreshActiveAccount()
    {
        bool useLazer = UseLazerAccount(beatmapDestinationService?.Destination ?? OsuClientDestination.Auto,
            trainerStableRoot is not null, verifiedLazerProfile);
        currentOsuProfile = useLazer ? verifiedLazerProfile : stableOsuProfile;
        accountScoreHistoryService = useLazer
            ? new OfficialAccountScoreHistoryService(() => officialApiClient)
            : stablePublicScoreHistoryService;
        header.SetProfilePreference(useLazer ? verifiedLazerProfile : null);
    }

    internal static bool UseLazerAccount(OsuClientDestination destination, bool stableInstalled, OsuProfile? verifiedLazer)
        => verifiedLazer is not null && !(destination == OsuClientDestination.Stable && stableInstalled);

    private void prepareCatalogReplayMoment(LocalReplay replay, double timeMs)
    {
        if (!double.IsFinite(timeMs) || timeMs < 0) return;
        CancellationToken cancellationToken = beginReplayRoute(replay.ScoreId, replay);
        _ = prepareCatalogReplayAsync(replay, cancellationToken, Math.Max(0, timeMs - 1500));
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

    private async Task prepareCatalogReplayAsync(LocalReplay replay, CancellationToken cancellationToken, double? initialTimeMs = null)
    {
        IPlayableReplayBundle? bundle = null;
        try
        {
            ILocalReplayOpenService service = replayOpenService
                ?? throw new ExternalLazerReplayOpenException(
                    "local_library_unavailable",
                    "AimMod is still connecting to the local osu! library. Try this replay again in a moment.");
            bundle = await service.OpenAsync(replay, cancellationToken).ConfigureAwait(false);
            await loadReplay(bundle.OpenRequest, cancellationToken, bundle, replay.ScoreId, initialTimeMs).ConfigureAwait(false);
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
        Guid? scoreId, double? initialTimeMs = null)
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
            Schedule(() => { if (!cancellationToken.IsCancellationRequested) showReplay(workingBeatmap, score, initialTimeMs); });

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

    private async Task cacheCompletedReplay(Guid scoreId, ReplayAnalysisResult result)
    {
        replayAnalyses[scoreId] = result;
        await persistReplayAnalyses().ConfigureAwait(false);
    }

    private async Task persistReplayAnalyses()
    {
        if (!IsDisposed)
            Schedule(() => ppTargetsWorkspace?.RefreshSkillEvidence());
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

    private void showReplay(WorkingBeatmap workingBeatmap, Score score, double? initialTimeMs = null)
    {
        if (replayRoute is null)
            return;

        Beatmap.Value = workingBeatmap;
        Ruleset.Value = score.ScoreInfo.Ruleset;
        SelectedMods.Value = score.ScoreInfo.Mods;

        var player = new NativeReplayPlayer(score, () =>
        {
            replayRoute.ShowReady();
            if (initialTimeMs is { } moment) replayRoute.SeekToMoment(moment);
        }, replayRoute.ShowError);
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

    private void startReplayLibraryAnalysis(bool automatic = false)
    {
        if ((!automatic && !isReplayAnalysisRoute(currentRoute.Value))
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
        _ = analyseReplayLibrary(replayAnalysisBatchService, cachedScoreIds, failedScoreIds, replayLibraryAnalysisLifetime.Token,
            automatic || currentRoute.Value == NativeRoute.PpTargets);
    }

    private void stopReplayLibraryAnalysis()
    {
        CancellationTokenSource? work = replayLibraryAnalysisLifetime;
        replayLibraryAnalysisLifetime = null;
        work?.Cancel();
        work?.Dispose();
        if (!IsDisposed) ppTargetsWorkspace?.SetSkillAnalysisProgress(0, 0);
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
        CancellationToken cancellationToken,
        bool recentSkillEvidenceOnly = false)
    {
        try
        {
            LocalReplay[] library = await loadReplayAnalysisWorkingSet(cancellationToken).ConfigureAwait(false);
            if (recentSkillEvidenceOnly)
            {
                DateTimeOffset start = new(DateTime.UtcNow.Date.AddDays(-30), TimeSpan.Zero);
                library = library.Where(run => run.PlayedAt >= start && run.PlayedAt <= DateTimeOffset.UtcNow).ToArray();
            }
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
                if (!cancellationToken.IsCancellationRequested)
                    ppTargetsWorkspace?.SetSkillAnalysisProgress(0, 0);
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
                    if (!cancellationToken.IsCancellationRequested)
                        ppTargetsWorkspace?.SetSkillAnalysisProgress(0, 0);
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
            if (!cancellationToken.IsCancellationRequested)
                ppTargetsWorkspace?.SetSkillAnalysisProgress(progress.Completed, progress.Total);
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
        route is NativeRoute.Replays or NativeRoute.Coaching or NativeRoute.PpTargets;

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
        restoreTrainerSettings?.Invoke();
        appLifetime.Cancel();
        profileRefreshCancellation?.Cancel();
        profileRefreshCancellation?.Dispose();
        if (beatmapDestinationService is not null)
            beatmapDestinationService.DestinationChanged -= accountDestinationChanged;
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
        private string? stableUsername;
        private bool stableAvailable;
        private OsuProfile? publicProfile;
        private OsuProfile? verifiedProfile;
        private LazerSessionStatus sessionStatus = LazerSessionStatus.Unavailable;
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
            Action showTrainers,
            Action showPpTargets,
            Action showSettings)
        {
            RelativeSizeAxes = Axes.Y;
            Width = AimModVisualStyle.SidebarWidth;
            Children = [
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Header },
                new Box { RelativeSizeAxes = Axes.Y, Width = 1, Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Colour = AimModPalette.Border },
                new AimModBrandMark { Position = new(20, 24), Size = new(34, 28), FillMode = FillMode.Fit },
                text("AimMod", 22, AimModPalette.Text, "SemiBold").With(t => t.Position = new(64, 23)),
                productPill = text("osu! workspace", 11, AimModPalette.Muted).With(t => t.Position = new(64, 51)),
                navigation = new FillFlowContainer<Drawable> {
                    Y = 104, X = 12, Width = 160, AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical, Spacing = new(6),
                    Children = [
                        new NavItem("Home", FontAwesome.Solid.Home, NativeRoute.Home, currentRoute, showHome),
                        navGroup("LIBRARY"),
                        new NavItem("Beatmaps", FontAwesome.Solid.Music, NativeRoute.Beatmaps, currentRoute, showBeatmaps),
                        new NavItem("Skins", FontAwesome.Solid.PaintBrush, NativeRoute.Skins, currentRoute, showSkins),
                        new NavItem("Replays", FontAwesome.Solid.PlayCircle, NativeRoute.Replays, currentRoute, showReplays),
                        navGroup("IMPROVE"),
                        new NavItem("Statistics", FontAwesome.Solid.ChartLine, NativeRoute.Statistics, currentRoute, showStatistics),
                        new NavItem("Coaching", FontAwesome.Solid.Bullseye, NativeRoute.Coaching, currentRoute, showCoaching),
                        new NavItem("Trainers", FontAwesome.Solid.Keyboard, NativeRoute.Trainers, currentRoute, showTrainers),
                        new NavItem("PP targets", FontAwesome.Solid.Crosshairs, NativeRoute.PpTargets, currentRoute, showPpTargets),
                        navGroup("APP"),
                        new NavItem("Settings", FontAwesome.Solid.Cog, NativeRoute.Settings, currentRoute, showSettings),
                    ],
                },
                sessionState = new TruncatingSpriteText {
                    Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, Position = new(20, -24),
                    Text = "Connecting to osu!", Font = new FontUsage(size:11), Colour = AimModPalette.Muted, MaxWidth = 146,
                },
            ];
        }

        private static Drawable navGroup(string title) => new Container {
            Width = 160, Height = 28,
            Child = text(title, 10, AimModPalette.Muted, "SemiBold").With(t => t.Position = new(12, 13)),
        };

        public void SetSessionState(LazerSessionState state)
        {
            sessionStatus = state.Status;
            verifiedProfile = null;
            updateAccountLabel();
        }

        private void updateAccountLabel()
        {
            // A remembered lazer token says nothing about the connected stable library.
            // Keep the usable account visible until a lazer profile is actually verified.
            sessionState.Text = sessionStatus switch
            {
                _ when verifiedProfile is not null => verifiedProfile.Statistics?.GlobalRank is int verifiedRank and > 0
                    ? $"{verifiedProfile.Username}  ·  #{verifiedRank:N0}"
                    : verifiedProfile.Username,
                _ when publicProfile is not null => publicProfile.Statistics?.GlobalRank is int rank and > 0
                    ? $"{publicProfile.Username}  ·  #{rank:N0} (public)"
                    : $"{publicProfile.Username} (public profile)",
                _ when stableAvailable => stableUsername is null ? "osu!stable connected (local)" : $"{stableUsername} (osu!stable, local)",
                LazerSessionStatus.SignedIn => "Checking osu!lazer account...",
                LazerSessionStatus.Remembered => "osu!lazer session expired",
                LazerSessionStatus.SignedOut => "osu!lazer signed out",
                _ => "osu!lazer not connected",
            };
            sessionState.Colour = verifiedProfile is not null || publicProfile is not null ? AimModPalette.Cyan : AimModPalette.Muted;
        }

        public void SetProfile(OsuProfile profile)
        {
            verifiedProfile = profile;
            updateAccountLabel();
        }

        public void SetProfilePreference(OsuProfile? profile)
        {
            verifiedProfile = profile;
            updateAccountLabel();
        }

        public void SetStableAccount(string? username, bool installed)
        {
            stableAvailable = installed;
            stableUsername = installed && !string.IsNullOrWhiteSpace(username) ? username.Trim() : null;
            updateAccountLabel();
        }

        public void SetPublicProfile(OsuProfile profile)
        {
            publicProfile = profile;
            updateAccountLabel();
        }

        public void SetAccountUnavailable()
        {
            sessionStatus = LazerSessionStatus.Unavailable;
            verifiedProfile = null;
            if (publicProfile is not null || stableAvailable)
            {
                updateAccountLabel();
                return;
            }
            sessionState.Text = "Online account unavailable";
            sessionState.Colour = AimModPalette.Muted;
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
            Action showPpTargets,
            Action showTrainers,
            Action showSettings)
        {
            var choices = new GridContainer
            {
                RelativeSizeAxes = Axes.X, Height = 512, Y = 24,
                ColumnDimensions = [new Dimension(GridSizeMode.Relative, .5f), new Dimension(GridSizeMode.Relative, .5f)],
                Content = new Drawable[][]
                {
                    [new WorkspaceLink("Improve a map", "Coaching · Turn difficult sections into exercises", showCoaching, WorkspaceIllustrationKind.Coaching),
                     new WorkspaceLink("Train a skill", "Trainers · Timing, aim, bursts and reading", showTrainers, WorkspaceIllustrationKind.Trainers)],
                    [new WorkspaceLink("Review a play", "Replays · Watch your movement and timing", showReplays, WorkspaceIllustrationKind.Replays),
                     new WorkspaceLink("Find your next PP play", "PP targets · Find maps that fit your skills", showPpTargets, WorkspaceIllustrationKind.Targets)],
                    [new WorkspaceLink("See your progress", "Statistics · Follow your results over time", showStatistics, WorkspaceIllustrationKind.Statistics),
                     new WorkspaceLink("Browse beatmaps", "Beatmaps · Find songs and install maps", showBeatmaps, WorkspaceIllustrationKind.Beatmaps)],
                    [new WorkspaceLink("Choose your skin", "Skins · Make osu! feel like home", showSkins, WorkspaceIllustrationKind.Skins),
                     new WorkspaceLink("Connect & customise", "Settings · Your osu! setup, keys and audio", showSettings, WorkspaceIllustrationKind.Settings)],
                },
            };
            Children = [
                new AimModSectionHeader("Your osu! workspace", "Find a map, review your plays, and choose what to practise next.", "AimMod"),
                new Container
                {
                    RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Top = 80 },
                    Child = new AimModScrollContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        Child = new Container
                        {
                            RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Padding = new MarginPadding { Right = 8, Bottom = 16 },
                            Children = [text("WHAT WOULD YOU LIKE TO WORK ON?", 10, AimModPalette.Accent, "Bold"), choices,
                                new NativeUpdateSurface(updateService) { Y = 556 }],
                        },
                    },
                },
            ];
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
        private readonly AimModWorkspaceIllustration illustration;
        private readonly FillFlowContainer<Drawable> labels;

        public WorkspaceLink(string title, string description, Action action, WorkspaceIllustrationKind kind)
        {
            RelativeSizeAxes = Axes.Both;
            Padding = new MarginPadding(AimModVisualStyle.RelatedSpacing);
            CornerRadius = AimModVisualStyle.ControlRadius;
            BackgroundColour = AimModPalette.Panel;
            Action = action;
            illustration = new AimModWorkspaceIllustration(kind, () => IsHovered)
            {
                Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, X = 12, Width = 144, Height = 96,
            };
            labels = new FillFlowContainer<Drawable>
            {
                Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(6),
                Children = [copy(title, 17, AimModPalette.Text, "SemiBold"), copy(description, 12, AimModPalette.Muted)],
            };
            Children = [illustration, labels,
                new SpriteIcon { Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, X = -10,
                    Icon = FontAwesome.Solid.ChevronRight, Size = new(9), Colour = AimModPalette.Muted }];
        }

        private static osu.Game.Graphics.Containers.OsuTextFlowContainer copy(string value, float size, Colour4 colour, string weight = "Regular") => new(t =>
            { t.Font = new FontUsage(size: size, weight: weight); t.Colour = colour; })
            { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = value };

        protected override void Update()
        {
            base.Update();
            illustration.Width = DrawWidth < 420 ? 104 : 144;
            labels.Padding = new MarginPadding { Left = illustration.Width + 28, Right = 28 };
        }
    }

    private partial class NavItem : AimModInteractiveSurface
    {
        public NavItem(string title, IconUsage icon, NativeRoute route, Bindable<NativeRoute> currentRoute, Action action)
        {
            Width = 160; Height = 38; Action = action; BorderThickness = 0;
            var label = text(title, 14, AimModPalette.Muted, "SemiBold");
            label.Anchor = label.Origin = Anchor.CentreLeft; label.X = 39;
            var symbol = new SpriteIcon { Icon = icon, Size = new(15), Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, X = 13 };
            Children = [symbol, label];
            currentRoute.BindValueChanged(v => {
                bool active = v.NewValue == route;
                BackgroundColour = active ? AimModPalette.AccentMuted : AimModPalette.Header;
                label.Colour = symbol.Colour = active ? AimModPalette.Accent : AimModPalette.Muted;
            }, true);
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
        Trainers,
        PpTargets,
        Settings,
        Setup,
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
