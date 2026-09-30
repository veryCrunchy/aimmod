using AimMod.Desktop.Coaching;
using AimMod.Desktop.Hub;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Desktop.Replays;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Platform;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Screens;
using osuTK;
using osuTK.Input;

namespace AimMod.Desktop;

public enum ReplaySidePanelTab
{
    Library,
    Summary,
    Mistakes,
}

/// <summary>
/// Native workspace around osu!'s official replay player. Score, artwork and
/// analysis values all originate in the local lazer library.
/// </summary>
/// <remarks>
/// Two columns: the playback column (title row, letterboxed viewport and transport) and one tabbed side
/// panel holding the library, the run summary and the mistake list. Narrow widths stack the side panel
/// below playback.
/// </remarks>
public partial class NativeReplayRouteView : Container
{
    private const float header_height = 80;
    private const float column_gap = 12;
    private const float side_panel_fraction = 0.28f;
    private const float side_panel_min_width = 340;
    private const float side_panel_max_width = 440;
    private const float stacked_width = 800;
    private const float stacked_panel_min_height = 300;
    private const float side_tabs_height = 52;
    private const float browser_list_top = 122;
    private const float max_playback_offset = 24;

    private readonly Container playbackColumn;
    private readonly Container playbackArea;
    private readonly Container sidePanel;
    private readonly Container[] sideContents;
    private readonly Bindable<ReplaySidePanelTab> sideTab = new(ReplaySidePanelTab.Library);
    private AimModLayout.ChangeTracker<Vector2> layoutTracker;
    private AimModLayout.ChangeTracker<float> statusWidthTracker;
    private bool footageOpen;
    private AimModButton? footageButton;
    public void OpenFootage() => footageButton?.Action?.Invoke();

    public OsuScreenStack ScreenStack { get; } = new() { RelativeSizeAxes = Axes.Both };

    private readonly Action<LocalReplay>? openReplay;
    private readonly Action<string>? openPractice;
    private readonly Func<LocalReplay, CancellationToken, Task>? openBeatmap;

    // Playback column.
    private readonly ReplayTransportController transport = new();
    private readonly ReplayNowPlayingBar nowPlaying;
    private readonly ReplayViewport viewport;
    private readonly ReplayTransportBar transportBar;
    private readonly ReplayDisplaySettingsPanel displaySettings;
    private readonly Container displaySettingsDismiss;
    private readonly BindableBool showGameplayHud = new(true);
    private AimModPopupLayer? displaySettingsLayer;
    private ReplayPlaybackLayout playbackLayout;
    private readonly Container statusLayer;
    private readonly SpriteIcon statusIcon;
    private readonly TruncatingSpriteText statusTitle;
    private readonly WrappedLabel statusDetail;
    private readonly FillFlowContainer statusActions;

    [Resolved(canBeNull: true)]
    private GameHost? host { get; set; }

    private NativeReplayPlayer? player => transport.Player;

    private LocalReplay? selectedReplay;

    public NativeReplayRouteView(
        ILocalLibrarySource? source = null,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult>? analyses = null,
        Action<LocalReplay>? openReplay = null,
        OsuHubReplayShareService? hubShareService = null,
        IHubCredentialStore? hubCredentialStore = null,
        IOsuHubUploadQueue? hubUploadQueue = null,
        IHubSharingPreferenceStore? hubPreferenceStore = null,
        Action<Uri>? openUrl = null,
        Action<string>? copyText = null,
        Action<string>? openPractice = null,
        Func<LocalReplay, CancellationToken, Task>? openBeatmap = null,
        Func<ILocalScorePpHydrationService?>? ppHydrator = null,
        Func<LocalReplay?, Action, Drawable>? footageFactory = null)
    {
        this.source = source;
        this.ppHydrator = ppHydrator;
        this.analyses = analyses ?? new Dictionary<Guid, ReplayAnalysisResult>();
        this.openReplay = openReplay;
        this.openPractice = openPractice;
        this.openBeatmap = openBeatmap;
        RelativeSizeAxes = Axes.Both;

        statusLayer = createStatusLayer(out statusIcon, out statusTitle, out statusDetail, out statusActions);
        sideContents = new Container[]
        {
            tabContent(createLibraryContent()),
            tabContent(createSummaryContent(hubShareService, hubCredentialStore, hubUploadQueue, hubPreferenceStore, openUrl, copyText)),
            tabContent(createMistakesContent()),
        };

        playbackColumn = new Container
        {
            Children = new Drawable[]
            {
                nowPlaying = new ReplayNowPlayingBar(),
                playbackArea = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Top = ReplayNowPlayingBar.BarHeight + AimModVisualStyle.RelatedSpacing },
                    Children = new Drawable[]
                    {
                        viewport = new ReplayViewport(ScreenStack, statusLayer) { RelativeSizeAxes = Axes.X, Height = 270 },
                        transportBar = new ReplayTransportBar(transport, toggleDisplaySettings),
                        displaySettingsDismiss = new ClickableContainer
                        {
                            RelativeSizeAxes = Axes.Both,
                            Alpha = 0,
                            Action = () => setDisplaySettingsOpen(false),
                        },
                        displaySettings = new ReplayDisplaySettingsPanel(showGameplayHud)
                        {
                            Anchor = Anchor.TopRight,
                            Origin = Anchor.TopRight,
                            Position = new(-AimModVisualStyle.RelatedSpacing, AimModVisualStyle.RelatedSpacing),
                            Alpha = 0,
                            CloseRequested = () => setDisplaySettingsOpen(false),
                        },
                    },
                },
            },
        };

        sidePanel = makePanel(new Container
        {
            RelativeSizeAxes = Axes.Both,
            Children = new Drawable[]
            {
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = side_tabs_height,
                    Padding = new MarginPadding { Horizontal = 12, Top = 12 },
                    Child = new AimModTabControl<ReplaySidePanelTab> { Current = sideTab },
                },
                new Box { RelativeSizeAxes = Axes.X, Height = 1, Y = side_tabs_height, Colour = AimModPalette.Border },
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Top = side_tabs_height + 1 },
                    Children = sideContents,
                },
            },
        }, Anchor.TopLeft, Anchor.TopLeft);

        Add(new AimModSectionHeader("Replays", "Watch your plays, compare attempts, and review mistakes.") { Width = .75f });
        Add(new Container
        {
            RelativeSizeAxes = Axes.Both,
            Padding = new MarginPadding { Top = header_height },
            // The side panel precedes playback so the playback column's popovers draw above it.
            Children = new Drawable[] { sidePanel, playbackColumn },
        });

        if (footageFactory is not null)
        {
            Drawable? footage = null;
            nowPlaying.AddAction(footageButton = new AimModButton("Find footage", () =>
            {
                if (footage is not null) return;
                SuspendPlayback();
                setDisplaySettingsOpen(false);
                var previousVisibility = Children.ToDictionary(child => child, child => child.Alpha);
                foreach (Drawable child in previousVisibility.Keys) child.Hide();
                footageOpen = true;
                footage = footageFactory(selectedReplay, () =>
                {
                    if (footage is null) return;
                    Remove(footage, true);
                    footage = null;
                    footageOpen = false;
                    foreach (var child in previousVisibility) child.Key.Alpha = child.Value;
                });
                footage.Depth = -100;
                Add(footage);
            }));
        }

        sideTab.BindValueChanged(tab =>
        {
            for (int index = 0; index < sideContents.Length; index++)
                sideContents[index].Alpha = index == (int)tab.NewValue ? 1 : 0;
        }, true);

        analysisTitle.Text = "No replay selected";
        analysisSummary.Text = "Choose an attempt to calculate exact judgements and coaching evidence.";
        analysisCard.Alpha = 1;
        showNotableState("Select a run to see misses, slider breaks, and timing errors.", AimModPalette.Muted);
        showMapPatternState("Choose a map with multiple attempts to compare repeated mistakes.");
    }

    private static Container tabContent(Drawable content) => new()
    {
        RelativeSizeAxes = Axes.Both,
        Alpha = 0,
        Child = content,
    };

    /// <summary>The side panel tab currently shown.</summary>
    public Bindable<ReplaySidePanelTab> SideTab => sideTab;

    protected override void LoadComplete()
    {
        base.LoadComplete();
        practiceButton.Enabled.BindValueChanged(enabled => practiceButton.FadeTo(enabled.NewValue ? 1 : 0.45f, AimModVisualStyle.FastTransition), true);
        searchBox.QueryChanged += _ => loadReplayBrowser();
        searchBox.MoveToResults += () => AimModInteractiveSurface.FocusFirst(replayList);
        gameMode.BindValueChanged(_ => loadReplayBrowser());
        modSelection.BindValueChanged(_ => loadReplayBrowser());
        if (source is not null)
            loadReplayBrowser();
    }

    public override bool HandleNonPositionalInput => true;

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (footageOpen || e.ControlPressed || e.AltPressed || GetContainingInputManager()?.FocusedDrawable is osu.Framework.Graphics.UserInterface.TextBox)
            return base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.Escape when displaySettings.Alpha > 0:
                setDisplaySettingsOpen(false);
                return true;

            case Key.Space when player is not null && !e.Repeat:
                transport.TogglePause();
                return true;

            case Key.Left or Key.Right when player is not null:
                double step = e.ShiftPressed ? ReplayTransportController.FineSeekStepMs : ReplayTransportController.SeekStepMs;
                transport.SeekBy(e.Key == Key.Left ? -step : step);
                return true;

            case Key.Comma or Key.Period when player is not null:
                transport.StepFrame(e.Key == Key.Comma ? -1 : 1);
                return true;

            case Key.J or Key.BracketLeft when player is not null:
                transport.JumpToMoment(-1);
                return true;

            case Key.K or Key.BracketRight when player is not null:
                transport.JumpToMoment(1);
                return true;
        }

        return base.OnKeyDown(e);
    }

    /// <summary>Splits the route into the playback column and the side panel.</summary>
    internal static ReplayRouteLayout CalculateLayout(float width, float height)
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        if (width >= stacked_width)
        {
            float side = Math.Clamp(width * side_panel_fraction, side_panel_min_width, side_panel_max_width);
            float playback = Math.Max(0, width - side - column_gap);
            ReplayPlaybackLayout block = ReplayPlaybackLayout.Calculate(playback, height - ReplayNowPlayingBar.BarHeight - AimModVisualStyle.RelatedSpacing);
            return new(ReplayRouteLayoutMode.TwoColumn, new(0, 0, playback, height), new(playback + column_gap, 0, side, height), block);
        }

        // Stacked: playback on top, as large as the width allows while leaving the panel usable.
        float available = height - ReplayNowPlayingBar.BarHeight - AimModVisualStyle.RelatedSpacing - column_gap - stacked_panel_min_height;
        ReplayPlaybackLayout stacked = ReplayPlaybackLayout.Calculate(width, available);
        float playbackHeight = ReplayNowPlayingBar.BarHeight + AimModVisualStyle.RelatedSpacing + stacked.Height;
        float panelTop = playbackHeight + column_gap;
        return new(ReplayRouteLayoutMode.Stacked, new(0, 0, width, playbackHeight), new(0, panelTop, width, Math.Max(0, height - panelTop)), stacked);
    }

    protected override void Update()
    {
        base.Update();
        if (footageOpen) { SuspendPlayback(); return; }

        var size = new Vector2(DrawWidth, Math.Max(0, DrawHeight - header_height));
        if (layoutTracker.Update(size))
            applyLayout(CalculateLayout(size.X, size.Y));

        updateBrowserStatusPadding();

        if (statusWidthTracker.Update(statusLayer.DrawWidth))
            statusTitle.MaxWidth = Math.Max(120, statusLayer.DrawWidth - 80);
    }

    private void applyLayout(ReplayRouteLayout layout)
    {
        playbackColumn.Position = layout.Playback.Location;
        playbackColumn.Size = layout.Playback.Size;
        sidePanel.Position = layout.Side.Location;
        sidePanel.Size = layout.Side.Size;
        playbackLayout = layout.Block;
        // When width limits the viewport, balance a small spare band above and below the player. The offset
        // is capped so the title row stays visually attached to the viewport.
        float area = layout.Playback.Height - ReplayNowPlayingBar.BarHeight - AimModVisualStyle.RelatedSpacing;
        float offset = layout.Mode == ReplayRouteLayoutMode.TwoColumn ? Math.Clamp((area - playbackLayout.Height) / 2, 0, max_playback_offset) : 0;
        viewport.Y = offset;
        viewport.Height = playbackLayout.ViewportSize.Y;
        transportBar.Y = offset + playbackLayout.TransportY;
        displaySettings.Y = offset + AimModVisualStyle.RelatedSpacing;
        displaySettings.MaximumHeight = Math.Max(160, layout.Playback.Height - ReplayNowPlayingBar.BarHeight - AimModVisualStyle.RelatedSpacing * 3);
    }

    private void toggleDisplaySettings() => setDisplaySettingsOpen(displaySettings.Alpha <= 0);

    private void setDisplaySettingsOpen(bool open)
    {
        bool isOpen = displaySettings.Alpha > 0;
        if (open == isOpen)
            return;

        displaySettingsLayer?.Dispose();
        displaySettingsLayer = null;
        if (open)
        {
            displaySettings.EnsurePopulated();
            // The popover must draw above the side panel and the transport.
            displaySettingsLayer = new AimModPopupLayer(displaySettings, action => (host?.UpdateThread.Scheduler ?? Scheduler).Add(action));
            displaySettings.FadeIn(AimModVisualStyle.FastTransition);
            displaySettingsDismiss.Alpha = 1;
            displaySettingsDismiss.AlwaysPresent = true;
        }
        else
        {
            displaySettings.FadeOut(AimModVisualStyle.FastTransition);
            displaySettings.Alpha = 0;
            displaySettingsDismiss.Alpha = 0;
            displaySettingsDismiss.AlwaysPresent = false;
        }

        transportBar.SetDisplaySettingsOpen(open);
    }

    internal bool DisplaySettingsOpen => displaySettings.Alpha > 0;

    internal ReplayDisplaySettingsPanel DisplaySettings => displaySettings;

    internal ReplayTransportBar TransportBar => transportBar;

    internal ReplayViewport Viewport => viewport;

    internal ReplayNowPlayingBar NowPlaying => nowPlaying;

    internal Container PlaybackColumn => playbackColumn;

    internal Container SidePanel => sidePanel;

    internal Drawable ModeFilter => modeDropdown;

    internal Drawable ModFilter => modDropdown;

    internal void OpenPlaybackSettingsForCapture() => setDisplaySettingsOpen(true);

    internal void OpenSpeedMenuForCapture() => transportBar.SpeedDropdown.SetMenuOpen(true);

    internal void ShowTabForCapture(ReplaySidePanelTab tab) => sideTab.Value = tab;

    private void retrySelectedReplay()
    {
        if (selectedReplay is { } replay && replay.RulesetShortName == "osu")
            openReplay?.Invoke(replay);
    }

    private void chooseAnotherReplay()
    {
        statusIcon.Icon = FontAwesome.Solid.PlayCircle;
        statusIcon.Colour = AimModPalette.Cyan;
        statusTitle.Text = "Choose a replay";
        statusTitle.Colour = AimModPalette.Text;
        statusDetail.Text = "Pick a map in the library, then choose an attempt to watch.";
        statusActions.FadeOut(AimModVisualStyle.FastTransition);
        sideTab.Value = ReplaySidePanelTab.Library;
        searchBox.FocusSearch();
    }

    public void SetReplaySummary(LocalReplay replay)
    {
        selectedReplay = replay;
        practiceButton.Enabled.Value = openPractice is not null;
        expandedReplayMaps.Add(ReplayBrowserModel.MapKeyFor(replay));
        analysisRevision = -1;
        analysisHasResult = false;
        showRunSummary(replay);
        nowPlaying.SetReplay(replay);
        statusIcon.Icon = FontAwesome.Solid.PlayCircle;
        statusIcon.Colour = AimModPalette.Cyan;
        statusTitle.Text = replay.Title;
        statusTitle.Colour = AimModPalette.Text;
        statusDetail.Text = $"{replay.Difficulty}  ·  {formatAccuracy(replay.Accuracy)}  ·  {replay.PlayedAt.LocalDateTime:g}";
        statusActions.Alpha = 0;
        statusLayer.FadeIn(80);
        // Choosing an attempt moves on to its summary; the Library tab is one click away.
        sideTab.Value = ReplaySidePanelTab.Summary;
        hubSharePanel.SetReplay(replay, analyses.ContainsKey(replay.ScoreId));

        if (replay.RulesetShortName != "osu")
        {
            practiceButton.Enabled.Value = false;
            analysisTitle.Text = "Score details";
            analysisSummary.Text = "PP and score statistics are available for this mode.";
            analysisNextPlay.Text = "Replay playback and coaching currently support osu!standard.";
            judgementCounts.Alpha = 0;
            mapPatternRows.Clear();
            showNotableState("Mistake review is available for osu!standard replays.", AimModPalette.Muted);
            clearMoments();
            transport.Attach(null);
            statusDetail.Text = "Playback and mistake review currently support osu!standard replays.";
        }
        else if (analyses.TryGetValue(replay.ScoreId, out ReplayAnalysisResult? cachedAnalysis))
            showCompletedAnalysis(cachedAnalysis);
        else
            showPendingAnalysis();

        // Selection only changes highlighting; the library itself has not changed.
        if (browserLoaded)
            renderReplayBrowser();
        else
            loadReplayBrowser();
    }

    public void AttachPlayer(NativeReplayPlayer replayPlayer)
    {
        player?.SuspendPlayback();
        transport.Attach(replayPlayer);
        replayPlayer.ShowGameplayHud.BindTo(showGameplayHud);
    }

    public void SuspendPlayback() => player?.SuspendPlayback();

    public bool SeekToMoment(double timeMs) => double.IsFinite(timeMs) && timeMs >= 0 && player?.SeekTo(timeMs) == true;

    private void jumpToMoment(ReplayMoment moment)
    {
        Console.Error.WriteLine($"[AimMod] Jumping to notable replay moment {ReplayTimeFormat.Precise(moment.TimeMs)}.");
        transport.JumpToMoment(moment);
    }

    public void ShowReady() => statusLayer.FadeOut(180);

    public void ShowError(string message)
    {
        statusIcon.Icon = FontAwesome.Solid.ExclamationTriangle;
        statusIcon.Colour = AimModPalette.Danger;
        statusTitle.Text = "Replay could not be opened";
        statusTitle.Colour = AimModPalette.Text;
        statusDetail.Text = message;
        statusActions.Alpha = 1;
        statusActions.Children[0].Alpha = selectedReplay?.RulesetShortName == "osu" && openReplay is not null ? 1 : 0;
        statusLayer.FadeIn(120);

        if (!analysisHasResult)
            showAnalysisFailure("Replay analysis could not start because the replay did not open.", "Resolve the replay loading error, then select the run again.");
    }

    protected override void Dispose(bool isDisposing)
    {
        SuspendPlayback();
        displaySettingsLayer?.Dispose();
        displaySettingsLayer = null;
        loading?.Cancel();
        loading?.Dispose();
        mapPatternLoading?.Cancel();
        mapPatternLoading?.Dispose();
        base.Dispose(isDisposing);
    }

    private Container createStatusLayer(out SpriteIcon icon, out TruncatingSpriteText title, out WrappedLabel detail, out FillFlowContainer actions) => new()
    {
        RelativeSizeAxes = Axes.Both,
        Children = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas },
            new FillFlowContainer
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Padding = new MarginPadding { Horizontal = 40 },
                Direction = FillDirection.Vertical,
                Spacing = new(10),
                Children = new Drawable[]
                {
                    new Container
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = 44,
                        Child = icon = new SpriteIcon
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            Size = new(34),
                            Icon = FontAwesome.Solid.PlayCircle,
                            Colour = AimModPalette.Cyan,
                        },
                    },
                    title = truncatingText("Choose a replay", 22, AimModPalette.Text, 540, "Bold", Anchor.TopCentre),
                    detail = new WrappedLabel("Pick a map in the library, then choose an attempt to watch.", 13, AimModPalette.Muted, centred: true),
                    actions = new FillFlowContainer
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Horizontal,
                        Spacing = new(AimModVisualStyle.RowSpacing),
                        Margin = new MarginPadding { Top = AimModVisualStyle.RelatedSpacing },
                        Alpha = 0,
                        Children = new Drawable[]
                        {
                            new AimModButton("Try again", retrySelectedReplay, primary: true),
                            new AimModButton("Choose another replay", chooseAnotherReplay),
                        },
                    },
                },
            },
        },
    };

    internal enum ReplayRouteLayoutMode
    {
        TwoColumn,
        Stacked,
    }

    internal readonly record struct ReplayRouteLayout(
        ReplayRouteLayoutMode Mode,
        osu.Framework.Graphics.Primitives.RectangleF Playback,
        osu.Framework.Graphics.Primitives.RectangleF Side,
        ReplayPlaybackLayout Block);
}
