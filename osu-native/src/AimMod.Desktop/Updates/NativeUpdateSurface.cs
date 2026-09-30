using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Updates;

/// <summary>
/// A one-line app-update status for the bottom of Home. The action appears only when there is something to do;
/// the release channel and changelog stay behind "Options".
/// </summary>
internal partial class NativeUpdateSurface : CompositeDrawable
{
    private const float row_height = 48;
    private const float notes_height = 370;

    private readonly INativeUpdateService updateService;
    private readonly SpriteIcon statusIcon;
    private readonly FillFlowContainer statusFlow;
    private readonly TruncatingSpriteText title;
    private readonly TruncatingSpriteText detail;
    private readonly FillFlowContainer rowActions;
    private readonly AimModButton primaryAction;
    private readonly AimModButton secondaryAction;
    private readonly AimModButton optionsButton;
    private readonly Box progressFill;
    private readonly FillFlowContainer options;
    private readonly AimModButton stableButton;
    private readonly AimModButton previewButton;
    private readonly AimModButton notesButton;
    private readonly Container notesHost;
    private readonly NativeReleaseNotesPanel notesPanel;
    private bool optionsOpen;
    private bool notesOpen;
    private AimModLayout.ChangeTracker<float> widthTracker;

    public NativeUpdateSurface(INativeUpdateService updateService)
    {
        this.updateService = updateService;
        RelativeSizeAxes = Axes.X;
        Height = row_height;
        Masking = true;
        CornerRadius = AimModVisualStyle.CardRadius;

        InternalChildren = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
            new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Children = new Drawable[]
                {
                    new Container
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = row_height,
                        Children = new Drawable[]
                        {
                            statusIcon = new SpriteIcon { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, X = 16, Size = new(14) },
                            statusFlow = new FillFlowContainer
                            {
                                Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, X = 42, AutoSizeAxes = Axes.Both,
                                Direction = FillDirection.Horizontal, Spacing = new(10),
                                Children = new Drawable[]
                                {
                                    title = new TruncatingSpriteText
                                    {
                                        Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft,
                                        Font = AimModVisualStyle.BodyStrongFont, Colour = AimModPalette.Text,
                                    },
                                    detail = new TruncatingSpriteText
                                    {
                                        Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft,
                                        Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted,
                                    },
                                },
                            },
                            rowActions = new FillFlowContainer
                            {
                                Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, X = -8, AutoSizeAxes = Axes.Both,
                                Direction = FillDirection.Horizontal, Spacing = new(AimModVisualStyle.RelatedSpacing),
                                Children = new Drawable[]
                                {
                                    primaryAction = new AimModButton(string.Empty, runPrimaryAction, primary: true) { Height = AimModVisualStyle.CompactControlHeight },
                                    secondaryAction = new AimModButton(string.Empty, runPrimaryAction) { Height = AimModVisualStyle.CompactControlHeight },
                                    optionsButton = new AimModButton("Options", toggleOptions) { Height = AimModVisualStyle.CompactControlHeight },
                                },
                            },
                            new Container
                            {
                                Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, RelativeSizeAxes = Axes.X, Height = 2,
                                Child = progressFill = new Box { RelativeSizeAxes = Axes.Both, Width = 0, Colour = AimModPalette.Accent },
                            },
                        },
                    },
                    options = new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        Direction = FillDirection.Vertical,
                        Spacing = new(AimModVisualStyle.RelatedSpacing),
                        Padding = new MarginPadding { Horizontal = 16, Bottom = 16 },
                        Height = 0,
                        Masking = true,
                        Alpha = 0,
                        Children = new Drawable[]
                        {
                            new Box { RelativeSizeAxes = Axes.X, Height = 1, Colour = AimModPalette.Border },
                            new FillFlowContainer
                            {
                                AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(AimModVisualStyle.RelatedSpacing),
                                Margin = new MarginPadding { Top = 8 },
                                Children = new Drawable[]
                                {
                                    new OsuSpriteText
                                    {
                                        Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Margin = new MarginPadding { Right = 4 },
                                        Text = "Release channel", Font = AimModVisualStyle.BodyStrongFont, Colour = AimModPalette.Text,
                                    },
                                    stableButton = new AimModButton("Stable", () => _ = updateService.SelectChannelAsync(NativeUpdateChannel.Stable))
                                        { Height = AimModVisualStyle.CompactControlHeight },
                                    previewButton = new AimModButton("Preview", () => _ = updateService.SelectChannelAsync(NativeUpdateChannel.Preview))
                                        { Height = AimModVisualStyle.CompactControlHeight },
                                    new OsuSpriteText
                                    {
                                        Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Margin = new MarginPadding { Left = 4 },
                                        Text = "Preview gets new features first.", Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted,
                                    },
                                },
                            },
                            notesButton = new AimModButton("What's new", toggleNotes) { Height = AimModVisualStyle.CompactControlHeight },
                            notesHost = new Container
                            {
                                RelativeSizeAxes = Axes.X, Height = 0, Alpha = 0, Masking = true,
                                Child = notesPanel = new NativeReleaseNotesPanel(),
                            },
                        },
                    },
                },
            },
        };

        updateService.StateChanged += updateStateChanged;
        applyState(updateService.State);
    }

    internal bool OptionsOpen => optionsOpen;

    /// <summary>Which parts of the status row fit. Channels share the row only on wide pages.</summary>
    internal static NativeUpdateSurfaceLayout CalculateLayout(float width)
    {
        bool showChannels = width >= 840;
        bool showDetail = width >= 620;
        float actionWidth = width >= 600 ? 138 : 110;
        float actionRight = width >= 600 ? 18 : 12;
        float actionLeft = width - actionRight - actionWidth;
        float textX = showChannels ? 22 : 14;
        float channelLeft = width - 331;
        float textBoundary = showChannels ? channelLeft : actionLeft;
        float textWidth = Math.Max(80, Math.Min(430, textBoundary - textX - 20));

        return new NativeUpdateSurfaceLayout(
            textX,
            textWidth,
            actionWidth,
            actionRight,
            actionLeft,
            channelLeft,
            showChannels,
            showDetail);
    }

    protected override void Update()
    {
        base.Update();
        if (!widthTracker.Update(DrawWidth + rowActions.DrawWidth))
            return;
        NativeUpdateSurfaceLayout layout = CalculateLayout(DrawWidth);
        float available = Math.Max(80, DrawWidth - statusFlow.X - rowActions.DrawWidth - 24);
        detail.Alpha = layout.ShowDetail ? 1 : 0;
        title.MaxWidth = layout.ShowDetail ? Math.Max(80, available * .5f) : available;
        detail.MaxWidth = Math.Max(0, available - title.DrawWidth - 10);
    }

    private void updateStateChanged(NativeUpdateState state)
    {
        if (!IsDisposed)
            Schedule(() => applyState(state));
    }

    /// <summary>What the row says for a state: short title, one-line detail and the icon colour.</summary>
    internal static (string Title, string Detail, IconUsage Icon, Colour4 Colour) Describe(NativeUpdateState state) => state.Stage switch
    {
        NativeUpdateStage.Unavailable => ("Automatic updates off", "Only copies set up by the AimMod installer can update themselves.",
            FontAwesome.Solid.InfoCircle, AimModPalette.Muted),
        NativeUpdateStage.Current => ("AimMod is up to date", state.Version is { Length: > 0 } version ? $"Version {version}" : state.Detail,
            FontAwesome.Solid.CheckCircle, AimModPalette.Accent),
        NativeUpdateStage.Available or NativeUpdateStage.ReadyToRestart => (state.Title, state.Detail, FontAwesome.Solid.ArrowCircleUp, AimModPalette.Accent),
        NativeUpdateStage.Downloading => (state.Title, state.Detail, FontAwesome.Solid.Download, AimModPalette.Cyan),
        NativeUpdateStage.Failed => (state.Title, state.Detail, FontAwesome.Solid.ExclamationTriangle, AimModPalette.Yellow),
        NativeUpdateStage.Checking => ("Checking for updates", string.Empty, FontAwesome.Solid.Sync, AimModPalette.Muted),
        _ => ("App updates", string.Empty, FontAwesome.Solid.Sync, AimModPalette.Muted),
    };

    private void applyState(NativeUpdateState state)
    {
        notesPanel.SetState(state);
        (string heading, string text, IconUsage icon, Colour4 colour) = Describe(state);
        title.Text = heading;
        detail.Text = text;
        statusIcon.Icon = icon;
        statusIcon.Colour = colour;
        stableButton.SetSelected(state.Channel == NativeUpdateChannel.Stable);
        previewButton.SetSelected(state.Channel == NativeUpdateChannel.Preview);
        // Progress arrives in coarse steps; ease between them instead of jumping.
        progressFill.ResizeWidthTo(ProgressFraction(state), AimModVisualStyle.HoverTransition * 2, Easing.OutQuint);

        (string label, _, bool enabled, _) = ActionFor(state);
        bool important = state.Stage is NativeUpdateStage.Available or NativeUpdateStage.ReadyToRestart;
        primaryAction.SetCaption(label);
        secondaryAction.SetCaption(label);
        primaryAction.Alpha = enabled && important ? 1 : 0;
        secondaryAction.Alpha = enabled && !important ? 1 : 0;
        widthTracker.Reset();
    }

    internal static float ProgressFraction(NativeUpdateState state) =>
        state.Stage is NativeUpdateStage.Downloading or NativeUpdateStage.ReadyToRestart
            ? Math.Clamp(state.Progress / 100f, 0, 1)
            : 0;

    internal static (string Label, IconUsage Icon, bool Enabled, string Tooltip) ActionFor(NativeUpdateState state) => state.Stage switch
    {
        NativeUpdateStage.Available => ("Download", FontAwesome.Solid.Download, true, "Download the update in the background."),
        NativeUpdateStage.ReadyToRestart => ("Restart", FontAwesome.Solid.Sync, true, "Restart AimMod to finish updating."),
        NativeUpdateStage.Failed => ("Try again", FontAwesome.Solid.Sync, true, "Check for updates again."),
        NativeUpdateStage.Current => ("Check again", FontAwesome.Solid.Sync, true, "Check for a newer release."),
        NativeUpdateStage.Idle => ("Check now", FontAwesome.Solid.Sync, true, "Check for a newer release."),
        NativeUpdateStage.Downloading => ($"Cancel {Math.Clamp(state.Progress, 0, 100)}%", FontAwesome.Solid.Times, true, "Stop downloading this update."),
        NativeUpdateStage.Checking => ("Checking", FontAwesome.Solid.Sync, false, "Looking for a newer release."),
        _ => ("Unavailable", FontAwesome.Solid.Download, false,
            "This copy of AimMod was not set up by its installer, so it cannot update itself. Install AimMod to receive updates."),
    };

    private void toggleOptions()
    {
        optionsOpen = !optionsOpen;
        options.Alpha = optionsOpen ? 1 : 0;
        applyHeight();
        optionsButton.SetSelected(optionsOpen);
        if (!optionsOpen && notesOpen)
            toggleNotes();
    }

    private void toggleNotes()
    {
        notesOpen = !notesOpen;
        if (notesOpen && !optionsOpen)
            toggleOptions();
        notesHost.Alpha = notesOpen ? 1 : 0;
        notesHost.Height = notesOpen ? notes_height : 0;
        notesButton.SetCaption(notesOpen ? "Hide changelog" : "What's new");
        notesButton.SetSelected(notesOpen);
        applyHeight();
    }

    /// <summary>Explicit heights keep the page flow exact while the options open and close.</summary>
    internal static float HeightFor(bool optionsOpen, bool notesOpen)
    {
        if (!optionsOpen)
            return row_height;
        // Divider, channel row, changelog button and the optional notes, with their spacing and bottom padding.
        float options = 1 + AimModVisualStyle.RelatedSpacing + 8 + AimModVisualStyle.CompactControlHeight
                        + AimModVisualStyle.RelatedSpacing + AimModVisualStyle.CompactControlHeight + 16;
        if (notesOpen)
            options += AimModVisualStyle.RelatedSpacing + notes_height;
        return row_height + options;
    }

    private void applyHeight()
    {
        float target = HeightFor(optionsOpen, notesOpen);
        options.Height = target - row_height;
        Height = target;
    }

    private void runPrimaryAction()
    {
        switch (updateService.State.Stage)
        {
            case NativeUpdateStage.Available:
                _ = updateService.DownloadAsync();
                break;

            case NativeUpdateStage.ReadyToRestart:
                updateService.ApplyAndRestart();
                break;

            case NativeUpdateStage.Idle:
            case NativeUpdateStage.Current:
            case NativeUpdateStage.Failed:
            // Starting a new check cancels the running download and returns to the available release.
            case NativeUpdateStage.Downloading:
                _ = updateService.CheckAsync();
                break;
        }
    }

    protected override void Dispose(bool isDisposing)
    {
        updateService.StateChanged -= updateStateChanged;
        base.Dispose(isDisposing);
    }
}

internal readonly record struct NativeUpdateSurfaceLayout(
    float TextX,
    float TextWidth,
    float ActionWidth,
    float ActionRight,
    float ActionLeft,
    float ChannelLeft,
    bool ShowChannels,
    bool ShowDetail);
