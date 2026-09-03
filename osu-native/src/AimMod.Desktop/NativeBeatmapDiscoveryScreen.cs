using AimMod.Desktop.LocalLibrary;
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
    private readonly Container page = null!;
    private readonly Bindable<BeatmapDiscoveryTab> currentTab = new(BeatmapDiscoveryTab.Installed);
    private NativeLocalLibraryScreen? installedScreen;
    private NativeOfficialBeatmapSearchScreen? onlineScreen;

    public NativeBeatmapDiscoveryScreen(
        ILocalLibrarySource localLibrary,
        Func<IOfficialBeatmapDiscoveryClient?> client,
        Func<OnlineBeatmapImportService?> importer)
    {
        this.localLibrary = localLibrary ?? throw new ArgumentNullException(nameof(localLibrary));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.importer = importer ?? throw new ArgumentNullException(nameof(importer));
        RelativeSizeAxes = Axes.Both;

        InternalChildren = new Drawable[]
        {
            page = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = 48 },
                Masking = true,
            },
            new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = 42,
                Masking = true,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas },
                    new OsuTabControl<BeatmapDiscoveryTab>
                    {
                        Anchor = Anchor.TopRight,
                        Origin = Anchor.TopRight,
                        Position = new(0, 2),
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

    private void showTab(BeatmapDiscoveryTab tab)
    {
        if (tab == BeatmapDiscoveryTab.Installed)
        {
            installedScreen ??= new NativeLocalLibraryScreen(localLibrary, NativeLocalLibraryMode.Beatmaps) { RelativeSizeAxes = Axes.Both };
            page.Child = installedScreen;
            return;
        }

        onlineScreen ??= new NativeOfficialBeatmapSearchScreen(client, importer) { RelativeSizeAxes = Axes.Both };
        page.Child = onlineScreen;
    }

    private enum BeatmapDiscoveryTab
    {
        Installed,
        Online,
    }

}

public partial class NativeOfficialBeatmapSearchScreen : CompositeDrawable
{
    private const int result_limit = 24;

    private readonly Func<IOfficialBeatmapDiscoveryClient?> client;
    private readonly Func<OnlineBeatmapImportService?> importer;
    private readonly OsuTextBox searchBox;
    private readonly SpriteText resultStatus;
    private readonly FillFlowContainer results;
    private readonly AimModLoadingOverlay loadingOverlay;
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
            new SpriteText
            {
                Y = 72,
                Text = "SEARCH ONLINE",
                Font = new FontUsage(size: 10, weight: "Bold"),
                Colour = AimModPalette.Cyan,
                Depth = -20,
            },
            searchBox = new OsuTextBox
            {
                RelativeSizeAxes = Axes.X,
                Width = 0.43f,
                Height = 46,
                Y = 91,
                PlaceholderText = "Search title, artist, mapper, or tag",
                Depth = -20,
            },
            new RangeSlider
            {
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                Position = new(-292, 72),
                Size = new(350, 65),
                Label = "Star rating",
                LowerBound = minimumStars,
                UpperBound = maximumStars,
                DefaultStringLowerBound = "0",
                DefaultStringUpperBound = "10+",
                TooltipSuffix = "stars",
                NubWidth = 30,
                Depth = -20,
            },
            new OsuDropdown<OfficialBeatmapCategory>
            {
                Y = 151,
                Width = 190,
                Items = new[] { OfficialBeatmapCategory.Any, OfficialBeatmapCategory.Ranked, OfficialBeatmapCategory.Loved, OfficialBeatmapCategory.Pending },
                Current = category,
                Depth = -20,
            },
            new OsuDropdown<OfficialBeatmapSort>
            {
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                Y = 151,
                Width = 190,
                Items = new[] { OfficialBeatmapSort.Relevance, OfficialBeatmapSort.Updated, OfficialBeatmapSort.Plays },
                Current = sort,
                Depth = -20,
            },
            resultStatus = new SpriteText
            {
                Y = 202,
                Text = "Connecting to osu!...",
                Font = new FontUsage(size: 12, weight: "SemiBold"),
                Colour = AimModPalette.Muted,
                Depth = -20,
            },
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = 230 },
                Masking = true,
                Depth = 10,
                Child = new OsuScrollContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Child = results = new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Spacing = new(8),
                    },
                },
            },
            loadingOverlay = new AimModLoadingOverlay(),
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

    private partial class OnlineBeatmapCard : Container
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
            Height = 122;
            Masking = true;
            CornerRadius = 8;
            BorderThickness = 1;
            BorderColour = AimModPalette.Border;
            double maximumStars = set.Difficulties.Count == 0 ? 0 : set.Difficulties.Max(difficulty => difficulty.StarRating);
            Colour4 difficultyColour = AimModVisualStyle.DifficultyColour(maximumStars);

            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = ColourInfo.GradientHorizontal(difficultyColour, AimModPalette.Panel),
                    Alpha = 0.28f,
                },
                new AimModOnlineArtworkHost(set.CoverUrl),
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = ColourInfo.GradientHorizontal(AimModPalette.Canvas, AimModPalette.Panel),
                    Alpha = 0.80f,
                },
                new Box { RelativeSizeAxes = Axes.Y, Width = 4, Colour = difficultyColour },
                new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both,
                    Position = new(24, 13),
                    Direction = FillDirection.Vertical,
                    Spacing = new(3),
                    Children = new Drawable[]
                    {
                        titleText = new TruncatingSpriteText
                        {
                            Text = set.Title,
                            Font = new FontUsage(size: 18, weight: "Bold"),
                            Colour = AimModPalette.Text,
                            MaxWidth = 120,
                        },
                        artistText = new TruncatingSpriteText
                        {
                            Text = set.Artist,
                            Font = new FontUsage(size: 13, weight: "SemiBold"),
                            Colour = AimModPalette.Text,
                            MaxWidth = 120,
                        },
                        detailText = new TruncatingSpriteText
                        {
                            Text = $"mapped by {set.Creator}  ·  {set.Status}  ·  {set.PlayCount:N0} plays  ·  {set.FavouriteCount:N0} favourites",
                            Font = new FontUsage(size: 10),
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
                    Margin = new MarginPadding { Left = 24, Bottom = 12 },
                    Direction = FillDirection.Horizontal,
                    Spacing = new(6),
                    Children = visibleDifficulties(set).ToArray(),
                },
                new ClickableContainer
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Margin = new MarginPadding { Right = 22 },
                    Size = new(170, 42),
                    Masking = true,
                    CornerRadius = 8,
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
                            Font = new FontUsage(size: 12, weight: "Bold"),
                            Colour = set.DownloadDisabled ? AimModPalette.Muted : AimModPalette.Canvas,
                        },
                    },
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            float available = Math.Max(140, DrawWidth - 230);
            titleText.MaxWidth = available;
            artistText.MaxWidth = available;
            detailText.MaxWidth = available;
        }

        private static IEnumerable<Drawable> visibleDifficulties(OfficialBeatmapSet set)
        {
            foreach (OfficialBeatmapDifficulty difficulty in set.Difficulties.Take(4))
                yield return new DifficultyChip(difficulty);

            if (set.Difficulties.Count > 4)
                yield return new AimModPill($"+{set.Difficulties.Count - 4}", AimModPillTone.Neutral);
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
