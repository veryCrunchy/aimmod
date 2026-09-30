using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime.Contracts;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Replays;

/// <summary>
/// AimMod's replay transport under the viewport: timeline scrubber with judgement markers,
/// playback buttons, speed and display settings. It is intentionally not masked so the speed
/// menu can open over neighbouring content.
/// </summary>
internal partial class ReplayTransportBar : Container
{
    private const float padding = 12;
    private const float time_width = 58;
    private const float compact_width = 520;
    private const float controls_y = ReplayTimelineScrubber.ScrubberHeight + 8;

    private readonly ReplayTransportController controller;
    private readonly ReplayTimelineScrubber scrubber;
    private readonly OsuSpriteText currentTimeText;
    private readonly OsuSpriteText durationText;
    private readonly TransportButton playButton;
    private readonly TransportButton[] frameButtons;
    private readonly FillFlowContainer controls;
    private readonly Bindable<double> speed = new(1);
    private AimModLayout.ChangeTracker<int> currentTimeTracker;
    private AimModLayout.ChangeTracker<int> durationTracker;
    private AimModLayout.ChangeTracker<bool> pausedTracker;
    private AimModLayout.ChangeTracker<bool> readyTracker;
    private AimModLayout.ChangeTracker<bool> compactTracker;
    private bool syncingSpeed;

    internal ReplaySpeedDropdown SpeedDropdown { get; }

    internal AimModButton DisplayButton { get; }

    internal ReplayTimelineScrubber Scrubber => scrubber;

    public ReplayTransportBar(ReplayTransportController controller, Action toggleDisplaySettings)
    {
        this.controller = controller;
        RelativeSizeAxes = Axes.X;
        Height = ReplayPlaybackLayout.TransportHeight;
        Children = new Drawable[]
        {
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Masking = true,
                CornerRadius = AimModVisualStyle.CardRadius,
                Child = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
            },
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Horizontal = padding, Top = 6, Bottom = 8 },
                Children = new Drawable[]
                {
                    currentTimeText = timeText(Anchor.TopLeft),
                    durationText = timeText(Anchor.TopRight),
                    new Container
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = ReplayTimelineScrubber.ScrubberHeight,
                        Padding = new MarginPadding { Horizontal = time_width + 6 },
                        Child = scrubber = new ReplayTimelineScrubber(
                            () => controller.CurrentTime,
                            () => controller.Duration,
                            time => controller.SeekTo(time),
                            moment => controller.JumpToMoment(moment)),
                    },
                    // Rows are top-anchored: the open speed menu grows the right group downwards.
                    controls = new FillFlowContainer
                    {
                        Y = controls_y,
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Horizontal,
                        Spacing = new(6, 0),
                        Children = new Drawable[]
                        {
                            new TransportButton(FontAwesome.Solid.StepBackward, "Previous mistake  [J]", () => controller.JumpToMoment(-1)),
                            new TransportButton("-5s", "Back 5 seconds  [Left]", () => controller.SeekBy(-ReplayTransportController.SeekStepMs)),
                            new TransportButton(FontAwesome.Solid.ChevronLeft, "Previous frame  [,]", () => controller.StepFrame(-1)),
                            playButton = new TransportButton(FontAwesome.Solid.Play, "Play / pause  [Space]", () => controller.TogglePause(), primary: true),
                            new TransportButton(FontAwesome.Solid.ChevronRight, "Next frame  [.]", () => controller.StepFrame(1)),
                            new TransportButton("+5s", "Forward 5 seconds  [Right]", () => controller.SeekBy(ReplayTransportController.SeekStepMs)),
                            new TransportButton(FontAwesome.Solid.StepForward, "Next mistake  [K]", () => controller.JumpToMoment(1)),
                        },
                    },
                    new FillFlowContainer
                    {
                        Anchor = Anchor.TopRight,
                        Origin = Anchor.TopRight,
                        Y = controls_y,
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Horizontal,
                        Spacing = new(AimModVisualStyle.RelatedSpacing, 0),
                        Children = new Drawable[]
                        {
                            SpeedDropdown = new ReplaySpeedDropdown
                            {
                                Width = 92,
                                Items = ReplayTransportController.PlaybackRates,
                                Current = speed,
                            },
                            DisplayButton = new AimModButton("Display", toggleDisplaySettings),
                        },
                    },
                },
            },
        };
        frameButtons = controls.Children.OfType<TransportButton>().Where(button => button.Icon is { } icon
            && (icon.Equals(FontAwesome.Solid.ChevronLeft) || icon.Equals(FontAwesome.Solid.ChevronRight))).ToArray();
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        speed.BindValueChanged(rate =>
        {
            if (!syncingSpeed)
                controller.SetPlaybackRate(rate.NewValue);
        });
    }

    public void SetAnalysis(ReplayAnalysisResult? result) => scrubber.SetAnalysis(result, controller.Moments);

    public void SetDisplaySettingsOpen(bool open) => DisplayButton.SetSelected(open);

    protected override void Update()
    {
        base.Update();

        double now = controller.CurrentTime;
        if (currentTimeTracker.Update((int)(now / 100)))
            currentTimeText.Text = ReplayTimeFormat.Clock(now);
        double total = controller.Duration;
        if (durationTracker.Update((int)(Math.Max(0, total) / 100)))
            durationText.Text = ReplayTimeFormat.Clock(total);
        if (pausedTracker.Update(controller.IsPaused))
            playButton.SetIcon(controller.IsPaused ? FontAwesome.Solid.Play : FontAwesome.Solid.Pause);
        if (readyTracker.Update(controller.IsReady))
            controls.FadeTo(controller.IsReady ? 1 : 0.5f, AimModVisualStyle.HoverTransition);
        if (compactTracker.Update(DrawWidth < compact_width))
        {
            foreach (TransportButton button in frameButtons)
                button.Alpha = DrawWidth < compact_width ? 0 : 1;
        }

        // osu!'s own fast-forward key also changes the rate; show whatever the clock uses.
        double rate = controller.PlaybackRate;
        if (controller.IsReady && Math.Abs(rate - speed.Value) > 0.001 && ReplayTransportController.PlaybackRates.Contains(rate))
        {
            syncingSpeed = true;
            speed.Value = rate;
            syncingSpeed = false;
        }
    }

    private static OsuSpriteText timeText(Anchor anchor) => new()
    {
        Anchor = anchor,
        Origin = anchor,
        // Aligned with the scrubber track.
        Y = 18,
        Width = time_width,
        Text = "0:00.0",
        Font = new FontUsage(size: 12, weight: "SemiBold", fixedWidth: true),
        Colour = anchor == Anchor.TopLeft ? AimModPalette.Text : AimModPalette.Muted,
    };

    internal partial class ReplaySpeedDropdown : AimModDropdown<double>
    {
        protected override LocalisableString GenerateItemText(double item) => ReplayTransportController.FormatRate(item);

        internal bool MenuOpen => Menu.State == osu.Framework.Graphics.UserInterface.MenuState.Open;

        internal void SetMenuOpen(bool open) =>
            Menu.State = open ? osu.Framework.Graphics.UserInterface.MenuState.Open : osu.Framework.Graphics.UserInterface.MenuState.Closed;
    }

    internal partial class TransportButton : AimModInteractiveSurface, IHasTooltip
    {
        private readonly SpriteIcon? icon;
        private readonly bool primary;

        public IconUsage? Icon => icon?.Icon;

        public LocalisableString TooltipText { get; }

        public TransportButton(IconUsage icon, string tooltip, Action action, bool primary = false)
            : this(tooltip, action, primary, 36)
        {
            Add(this.icon = new SpriteIcon
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Size = new(primary ? 14 : 12),
                Icon = icon,
                Colour = primary ? AimModPalette.Canvas : AimModPalette.Text,
            });
        }

        public TransportButton(string label, string tooltip, Action action)
            : this(tooltip, action, false, 44)
        {
            Add(new OsuSpriteText
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Text = label,
                Font = new FontUsage(size: 13, weight: "SemiBold"),
                Colour = AimModPalette.Text,
            });
        }

        private TransportButton(string tooltip, Action action, bool primary, float width)
        {
            this.primary = primary;
            TooltipText = tooltip;
            Action = action;
            Size = new(primary ? 48 : width, AimModVisualStyle.ControlHeight);
            BackgroundColour = primary ? AimModPalette.Accent : AimModPalette.PanelRaised;
            if (primary)
                BorderColour = AimModPalette.Accent;
        }

        public void SetIcon(IconUsage value)
        {
            if (icon is not null)
                icon.Icon = value;
        }

        internal bool IsPrimary => primary;
    }
}
