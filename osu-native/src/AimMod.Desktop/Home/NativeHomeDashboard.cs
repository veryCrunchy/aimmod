using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Desktop.ScoreHistory;
using AimMod.Desktop.Trainers;
using AimMod.Desktop.Updates;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Threading;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Home;

/// <summary>Where Home reads its data. Every delegate is called off the update thread and may return null.</summary>
internal sealed record HomeDashboardSources(
    ILocalLibrarySource Library,
    Func<IAccountScoreHistoryService?> AccountHistory,
    Func<OsuProfile?> Profile,
    Func<OsuProfile, HomeProfileChange?> RecordProfile,
    Func<CoachingTrainingPlan?> CoachingPlan,
    Func<TrainerGuidedPlan?> GuidedPlan,
    Func<IReadOnlyList<TrainerResult>> TrainerHistory,
    Func<PpTargetWorkspaceSnapshot?> PpTargets,
    Func<ILocalScorePpHydrationService?> PpHydration)
{
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.Now;
}

internal sealed record HomeDashboardActions(
    Action ShowBeatmaps,
    Action ShowSkins,
    Action ShowReplays,
    Action ShowStatistics,
    Action ShowCoaching,
    Action ShowPpTargets,
    Action ShowTrainers,
    Action ShowSettings,
    Action ResumeGuidedPractice,
    Action<string> PractiseMap,
    Action<LocalReplay> OpenReplay);

/// <summary>
/// The start page: who you are, how your recent plays went compared with before, what to do next,
/// your latest plays and a compact app-update status.
/// </summary>
internal partial class NativeHomeDashboard : CompositeDrawable
{
    private const float wide_width = 960;
    private const float panel_padding = 16;

    private readonly HomeDashboardSources sources;
    private readonly HomeDashboardActions actions;
    private readonly AimModSectionHeader header;
    private readonly AimModInlineStatus status;
    private readonly HomeProfileBand profileBand;
    private readonly HomeFormPanel formPanel;
    private readonly HomeNextUpPanel nextUpPanel;
    private readonly HomeRecentPlaysPanel recentPanel;
    private readonly HomeShortcutsPanel shortcutsPanel;
    private readonly HomeColumns formRow;
    private readonly HomeColumns recentRow;
    private readonly INativeUpdateService updateService;
    private readonly AimModButton updateBadge;
    private CancellationTokenSource? loading;
    private ScheduledDelegate? pendingRefresh;
    private HomeDashboardData? data;

    public NativeHomeDashboard(HomeDashboardSources sources, HomeDashboardActions actions, INativeUpdateService updateService)
    {
        this.sources = sources;
        this.actions = actions;
        this.updateService = updateService;
        RelativeSizeAxes = Axes.Both;
        InternalChildren = new Drawable[]
        {
            header = new AimModSectionHeader("Home", "Loading your plays..."),
            // An actionable update is also offered at the top, so it is not missed below the fold.
            updateBadge = new AimModButton(string.Empty, runUpdate, primary: true)
            {
                Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Y = 4, Height = AimModVisualStyle.CompactControlHeight, Alpha = 0,
            },
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = 80 },
                Child = new AimModScrollContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Child = new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Spacing = new(AimModVisualStyle.SectionSpacing),
                        Padding = new MarginPadding { Right = 8, Bottom = 16 },
                        Children = new Drawable[]
                        {
                            status = new AimModInlineStatus { Alpha = 0 },
                            profileBand = new HomeProfileBand(actions.ShowSettings),
                            formRow = new HomeColumns(.6f,
                                formPanel = new HomeFormPanel(actions),
                                nextUpPanel = new HomeNextUpPanel(runRecommendation) { Alpha = 0 }) { RightFirstWhenStacked = true },
                            recentRow = new HomeColumns(.6f,
                                recentPanel = new HomeRecentPlaysPanel(actions) { Alpha = 0 },
                                shortcutsPanel = new HomeShortcutsPanel(actions)),
                            new NativeUpdateSurface(updateService),
                        },
                    },
                },
            },
        };
    }

    public HomeDashboardData? Data => data;

    protected override void LoadComplete()
    {
        base.LoadComplete();
        updateService.StateChanged += updateStateChanged;
        applyUpdateState(updateService.State);
        Refresh();
    }

    private void updateStateChanged(NativeUpdateState state)
    {
        if (!IsDisposed)
            Schedule(() => applyUpdateState(state));
    }

    private void applyUpdateState(NativeUpdateState state)
    {
        (string? caption, bool show) = UpdateBadge(state);
        updateBadge.SetCaption(caption ?? string.Empty);
        updateBadge.Alpha = show ? 1 : 0;
    }

    /// <summary>The header offers only updates the player can act on now.</summary>
    internal static (string? Caption, bool Show) UpdateBadge(NativeUpdateState state) => state.Stage switch
    {
        NativeUpdateStage.Available => ($"Download AimMod {state.Version}", true),
        NativeUpdateStage.ReadyToRestart => ("Restart to update", true),
        _ => (null, false),
    };

    private void runUpdate()
    {
        switch (updateService.State.Stage)
        {
            case NativeUpdateStage.Available:
                _ = updateService.DownloadAsync();
                break;
            case NativeUpdateStage.ReadyToRestart:
                updateService.ApplyAndRestart();
                break;
        }
    }

    /// <summary>Reloads in the background. Repeated calls within a short window collapse into one load.</summary>
    public void Refresh()
    {
        pendingRefresh?.Cancel();
        pendingRefresh = Scheduler.AddDelayed(startLoad, data is null ? 0 : 150);
    }

    private void startLoad()
    {
        loading?.Cancel();
        loading?.Dispose();
        loading = new CancellationTokenSource();
        CancellationToken token = loading.Token;
        if (data is null)
            formPanel.ShowLoading();
        _ = Task.Run(() => loadAsync(token), token);
    }

    private async Task loadAsync(CancellationToken token)
    {
        try
        {
            HomeDashboardData loaded = await HomeDashboardLoader.LoadAsync(sources, token).ConfigureAwait(false);
            if (!IsDisposed)
                Schedule(() =>
                {
                    if (!token.IsCancellationRequested)
                        apply(loaded);
                });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Home could not be loaded: {error}");
            if (!IsDisposed)
                Schedule(() =>
                {
                    if (token.IsCancellationRequested) return;
                    status.Alpha = 1;
                    status.ShowError(error, "load your plays", Refresh);
                    if (data is null) formPanel.ShowError();
                });
        }
    }

    private void apply(HomeDashboardData loaded)
    {
        data = loaded;
        status.Dismiss();
        status.Alpha = 0;
        nextUpPanel.Alpha = 1;
        DateTimeOffset now = sources.Clock();
        header.Title = loaded.PlayerName is { Length: > 0 } name ? $"Welcome back, {name}" : "Home";
        header.Subtitle = Subtitle(loaded, now);
        profileBand.SetData(loaded);
        formPanel.SetData(loaded);
        nextUpPanel.SetData(loaded.Recommendations);
        recentPanel.SetData(loaded, now);
        // Without plays there is nothing to compare or list: show the first steps on their own.
        formPanel.Alpha = loaded.HasPlays ? 1 : 0;
        recentPanel.Alpha = loaded.HasPlays ? 1 : 0;
        nextUpPanel.SetTitle(loaded.HasPlays ? "Next up" : "Get started");
    }

    /// <summary>One line on the latest activity; the numbers live in the panels below.</summary>
    internal static string Subtitle(HomeDashboardData data, DateTimeOffset now)
    {
        if (!data.HasPlays)
            return data.Profile is null ? "Connect osu! and play a map to see your progress here." : "Play a map in osu! and your progress appears here.";
        int today = data.Week.Daily.Count > 0 ? data.Week.Daily[^1].Plays : 0;
        string last = data.RecentPlays.Count > 0 ? $"last play {HomeDashboardBuilder.Ago(data.RecentPlays[0].PlayedAt, now)}" : string.Empty;
        return today > 0 ? $"{today} {(today == 1 ? "play" : "plays")} today · {last}" : $"No plays today · {last}";
    }

    private void runRecommendation(HomeRecommendation recommendation)
    {
        switch (recommendation.Kind)
        {
            case HomeRecommendationKind.ContinueCoaching:
                actions.ShowCoaching();
                break;
            case HomeRecommendationKind.PractiseMap when recommendation.MapTitle is { } title:
                actions.PractiseMap(title);
                break;
            case HomeRecommendationKind.PractiseMap:
                actions.ShowCoaching();
                break;
            case HomeRecommendationKind.PpTarget:
            case HomeRecommendationKind.FindPpTargets:
                actions.ShowPpTargets();
                break;
            case HomeRecommendationKind.ResumeGuidedPractice:
                actions.ResumeGuidedPractice();
                break;
            case HomeRecommendationKind.WarmUp:
                actions.ShowTrainers();
                break;
            case HomeRecommendationKind.PlayFirstMap:
                actions.ShowBeatmaps();
                break;
        }
    }

    protected override void Update()
    {
        base.Update();
        bool wide = DrawWidth >= wide_width;
        formRow.Wide = wide;
        recentRow.Wide = wide;
    }

    protected override void Dispose(bool isDisposing)
    {
        updateService.StateChanged -= updateStateChanged;
        pendingRefresh?.Cancel();
        loading?.Cancel();
        loading?.Dispose();
        base.Dispose(isDisposing);
    }

    internal static OsuSpriteText Text(string value, FontUsage font, Colour4 colour) => new() { Text = value, Font = font, Colour = colour };

    internal static TruncatingSpriteText Truncating(string value, FontUsage font, Colour4 colour) => new() { Text = value, Font = font, Colour = colour };

    /// <summary>A titled card surface. The optional header content sits on the right of the title.</summary>
    internal partial class HomePanel : Container
    {
        private readonly FillFlowContainer body;
        protected readonly FillFlowContainer HeaderActions;
        protected readonly OsuSpriteText TitleText;

        protected override Container<Drawable> Content => body;

        /// <summary>A null title leaves out the title row.</summary>
        public HomePanel(string? title)
        {
            AutoSizeAxes = Axes.Y;
            Masking = true;
            CornerRadius = AimModVisualStyle.CardRadius;
            var headerRow = new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = AimModVisualStyle.CompactControlHeight,
                Children = new Drawable[]
                {
                    TitleText = Text(title ?? string.Empty, AimModVisualStyle.TitleFont, AimModPalette.Text).With(t => { t.Anchor = Anchor.CentreLeft; t.Origin = Anchor.CentreLeft; }),
                    HeaderActions = new FillFlowContainer
                    {
                        Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Horizontal, Spacing = new(AimModVisualStyle.RelatedSpacing),
                    },
                },
            };
            body = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new(AimModVisualStyle.RelatedSpacing),
            };
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Padding = new MarginPadding(panel_padding),
                    Spacing = new(12),
                    Children = title is null ? new Drawable[] { body } : new Drawable[] { headerRow, body },
                },
            };
        }
    }

    /// <summary>Two panels side by side on wide pages, stacked on narrow ones.</summary>
    internal partial class HomeColumns : FillFlowContainer
    {
        private readonly float leftShare;
        private readonly Drawable left;
        private readonly Drawable right;
        private bool? wide;
        private AimModLayout.ChangeTracker<float> widthTracker;

        public HomeColumns(float leftShare, Drawable left, Drawable right)
        {
            this.leftShare = leftShare;
            this.left = left;
            this.right = right;
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            Direction = FillDirection.Full;
            Spacing = new(AimModVisualStyle.SectionSpacing);
            Children = new[] { left, right };
        }

        public bool RightFirstWhenStacked { get; init; }

        public bool Wide
        {
            set
            {
                if (wide == value) return;
                wide = value;
                widthTracker.Reset();
            }
        }

        protected override void Update()
        {
            base.Update();
            bool single = !left.IsPresent || !right.IsPresent;
            if (!widthTracker.Update(single ? -DrawWidth : DrawWidth) || DrawWidth <= 0)
                return;
            // Stacked, the right-hand panel (the next action) comes first.
            SetLayoutPosition(left, wide == true || !RightFirstWhenStacked ? 0 : 1);
            SetLayoutPosition(right, wide == true || !RightFirstWhenStacked ? 1 : 0);
            if (wide == true && !single)
            {
                // One unit of slack keeps rounding from wrapping the right column.
                float available = DrawWidth - AimModVisualStyle.SectionSpacing - 1;
                left.Width = (float)Math.Floor(available * leftShare);
                right.Width = (float)Math.Floor(available - left.Width);
            }
            else
            {
                left.Width = right.Width = DrawWidth;
            }
        }
    }
}
