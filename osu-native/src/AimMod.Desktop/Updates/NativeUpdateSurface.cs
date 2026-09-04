using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Updates;

internal partial class NativeUpdateSurface : CompositeDrawable
{
    private readonly INativeUpdateService updateService;
    private readonly FillFlowContainer statusFlow;
    private readonly TruncatingSpriteText title;
    private readonly TruncatingSpriteText detail;
    private readonly FillFlowContainer channelControls;
    private readonly UpdateChannelButton stableButton;
    private readonly UpdateChannelButton previewButton;
    private readonly UpdateActionButton actionButton;
    private readonly Box progressFill;

    public NativeUpdateSurface(INativeUpdateService updateService)
    {
        this.updateService = updateService;
        RelativeSizeAxes = Axes.X;
        Height = 108;
        Masking = true;
        CornerRadius = AimModVisualStyle.CardRadius;

        InternalChildren = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = AimModPalette.Panel,
            },
            new Box
            {
                RelativeSizeAxes = Axes.Y,
                Width = 4,
                Colour = AimModPalette.Pink,
            },
            statusFlow = new FillFlowContainer
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                AutoSizeAxes = Axes.Y,
                Width = 430,
                X = 22,
                Direction = FillDirection.Vertical,
                Spacing = new(4),
                Children = new Drawable[]
                {
                    new OsuSpriteText
                    {
                        Text = "APP UPDATE",
                        Font = new FontUsage(size: 10, weight: "Bold"),
                        Colour = AimModPalette.Cyan,
                    },
                    title = new TruncatingSpriteText
                    {
                        Font = new FontUsage(size: 18, weight: "SemiBold"),
                        Colour = AimModPalette.Text,
                    },
                    detail = new TruncatingSpriteText
                    {
                        Font = new FontUsage(size: 12),
                        Colour = AimModPalette.Muted,
                    },
                },
            },
            channelControls = new FillFlowContainer
            {
                Anchor = Anchor.CentreRight,
                Origin = Anchor.CentreRight,
                AutoSizeAxes = Axes.Both,
                Margin = new MarginPadding { Right = 174 },
                Direction = FillDirection.Horizontal,
                Spacing = new(5),
                Children = new Drawable[]
                {
                    stableButton = new UpdateChannelButton("Stable", () => _ = updateService.SelectChannelAsync(NativeUpdateChannel.Stable)),
                    previewButton = new UpdateChannelButton("Preview", () => _ = updateService.SelectChannelAsync(NativeUpdateChannel.Preview)),
                },
            },
            actionButton = new UpdateActionButton(runPrimaryAction)
            {
                Anchor = Anchor.CentreRight,
                Origin = Anchor.CentreRight,
                Margin = new MarginPadding { Right = 18 },
            },
            new Container
            {
                Anchor = Anchor.BottomLeft,
                Origin = Anchor.BottomLeft,
                RelativeSizeAxes = Axes.X,
                Height = 3,
                Children = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = AimModPalette.PanelHover,
                    },
                    progressFill = new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Width = 0,
                        Colour = AimModPalette.Pink,
                    },
                },
            },
        };

        updateService.StateChanged += updateStateChanged;
        applyState(updateService.State);
    }

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

        NativeUpdateSurfaceLayout layout = CalculateLayout(DrawWidth);
        statusFlow.X = layout.TextX;
        statusFlow.Width = layout.TextWidth;
        title.MaxWidth = layout.TextWidth;
        detail.MaxWidth = layout.TextWidth;
        detail.Alpha = layout.ShowDetail ? 1 : 0;
        channelControls.Alpha = layout.ShowChannels ? 1 : 0;
        actionButton.Width = layout.ActionWidth;
        actionButton.Margin = new MarginPadding { Right = layout.ActionRight };
    }

    private void updateStateChanged(NativeUpdateState state)
    {
        if (!IsDisposed)
            Schedule(() => applyState(state));
    }

    private void applyState(NativeUpdateState state)
    {
        title.Text = state.Title;
        detail.Text = state.Detail;
        stableButton.Active = state.Channel == NativeUpdateChannel.Stable;
        previewButton.Active = state.Channel == NativeUpdateChannel.Preview;
        progressFill.Width = state.Stage is NativeUpdateStage.Downloading or NativeUpdateStage.ReadyToRestart
            ? Math.Clamp(state.Progress / 100f, 0, 1)
            : 0;

        (string label, IconUsage icon, bool enabled) = state.Stage switch
        {
            NativeUpdateStage.Available => ("Download", FontAwesome.Solid.Download, true),
            NativeUpdateStage.ReadyToRestart => ("Restart", FontAwesome.Solid.Sync, true),
            NativeUpdateStage.Failed => ("Try again", FontAwesome.Solid.Sync, true),
            NativeUpdateStage.Current => ("Check again", FontAwesome.Solid.Sync, true),
            NativeUpdateStage.Idle => ("Check now", FontAwesome.Solid.Sync, true),
            NativeUpdateStage.Downloading => ($"{state.Progress}%", FontAwesome.Solid.Download, false),
            NativeUpdateStage.Checking => ("Checking", FontAwesome.Solid.Sync, false),
            _ => ("Unavailable", FontAwesome.Solid.Download, false),
        };
        actionButton.SetState(label, icon, enabled);
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
                _ = updateService.CheckAsync();
                break;
        }
    }

    protected override void Dispose(bool isDisposing)
    {
        updateService.StateChanged -= updateStateChanged;
        base.Dispose(isDisposing);
    }

    private partial class UpdateChannelButton : ClickableContainer
    {
        private readonly Action action;
        private readonly Box background;
        private readonly OsuSpriteText label;
        private bool active;

        public UpdateChannelButton(string text, Action action)
        {
            this.action = action;
            Size = new(76, AimModVisualStyle.CompactControlHeight);
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            Children = new Drawable[]
            {
                background = new Box { RelativeSizeAxes = Axes.Both },
                label = new OsuSpriteText
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Text = text,
                    Font = new FontUsage(size: 11, weight: "SemiBold"),
                },
            };
        }

        public bool Active
        {
            get => active;
            set
            {
                active = value;
                background.Colour = value ? AimModPalette.PinkDark : AimModPalette.PanelRaised;
                label.Colour = value ? AimModPalette.Text : AimModPalette.Muted;
            }
        }

        protected override bool OnClick(ClickEvent e)
        {
            action();
            return true;
        }
    }

    private partial class UpdateActionButton : AimModInteractiveSurface
    {
        private readonly Action action;
        private readonly SpriteIcon icon;
        private readonly OsuSpriteText label;
        private bool enabled;

        public UpdateActionButton(Action action)
        {
            this.action = action;
            Size = new(138, AimModVisualStyle.ControlHeight);
            BackgroundColour = AimModPalette.PinkDark;
            Children = new Drawable[]
            {
                new FillFlowContainer
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Horizontal,
                    Spacing = new(8),
                    Children = new Drawable[]
                    {
                        icon = new SpriteIcon
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Size = new(13),
                            Colour = AimModPalette.Text,
                        },
                        label = new OsuSpriteText
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Font = new FontUsage(size: 11, weight: "Bold"),
                            Colour = AimModPalette.Text,
                        },
                    },
                },
            };
        }

        public void SetState(string text, IconUsage iconUsage, bool isEnabled)
        {
            label.Text = text;
            icon.Icon = iconUsage;
            enabled = isEnabled;
            Alpha = isEnabled ? 1 : 0.45f;
        }

        protected override bool OnClick(ClickEvent e)
        {
            if (enabled)
                action();
            return true;
        }
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
