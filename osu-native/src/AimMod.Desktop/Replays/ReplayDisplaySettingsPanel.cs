using AimMod.Desktop.Visuals;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Effects;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game.Configuration;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Overlays;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Configuration;

namespace AimMod.Desktop.Replays;

/// <summary>
/// AimMod-styled replacement for osu!'s in-player VISUAL SETTINGS and replay analysis panels.
/// Every control binds to the same osu! configuration bindable the official panels use, so changes
/// apply to the running replay immediately and persist like they do in osu!.
/// </summary>
public partial class ReplayDisplaySettingsPanel : Container
{
    public const float PanelWidth = 300;

    private readonly Bindable<bool> showGameplayHud;
    private readonly FillFlowContainer<Drawable> sections;
    private readonly AimModScrollContainer scroll;
    private readonly Container body;

    // osu!'s slider nub takes its colour from the nearest overlay colour provider.
    [Cached]
    private readonly OverlayColourProvider sliderColours = new(OverlayColourScheme.Aquamarine);

    [Resolved(canBeNull: true)]
    private OsuConfigManager? config { get; set; }

    [Resolved(canBeNull: true)]
    private IRulesetConfigCache? rulesetConfigs { get; set; }

    /// <summary>Invoked by the close button and Escape.</summary>
    public Action? CloseRequested { get; set; }

    /// <summary>Largest height the panel may use; the content scrolls beyond it.</summary>
    public float MaximumHeight { get; set; } = 480;

    internal IReadOnlyList<Drawable> Rows => sections.Children;

    public ReplayDisplaySettingsPanel(Bindable<bool> showGameplayHud)
    {
        this.showGameplayHud = showGameplayHud;
        Width = PanelWidth;
        Masking = true;
        CornerRadius = AimModVisualStyle.CardRadius;
        BorderThickness = 1;
        BorderColour = AimModPalette.Border;
        EdgeEffect = new EdgeEffectParameters
        {
            Type = EdgeEffectType.Shadow,
            Colour = Colour4.Black.Opacity(0.45f),
            Radius = 18,
            Offset = new(0, 6),
        };
        Children = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
            new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = 48,
                Padding = new MarginPadding { Horizontal = 16 },
                Children = new Drawable[]
                {
                    new FillFlowContainer
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Vertical,
                        Spacing = new(0, 1),
                        Children = new Drawable[]
                        {
                            new OsuSpriteText { Text = "Display", Font = AimModVisualStyle.TitleFont, Colour = AimModPalette.Text },
                            new OsuSpriteText { Text = "Applies to the running replay", Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted },
                        },
                    },
                    new CloseButton(() => CloseRequested?.Invoke())
                    {
                        Anchor = Anchor.CentreRight,
                        Origin = Anchor.CentreRight,
                    },
                },
            },
            new Box { RelativeSizeAxes = Axes.X, Height = 1, Y = 48, Colour = AimModPalette.Border },
            body = new Container
            {
                RelativeSizeAxes = Axes.X,
                Y = 49,
                Child = scroll = new AimModScrollContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Child = sections = new FillFlowContainer<Drawable>
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Padding = new MarginPadding { Horizontal = 16, Top = 10, Bottom = 14 },
                        Direction = FillDirection.Vertical,
                        Spacing = new(0, 2),
                    },
                },
            },
        };
    }

    private bool populated;

    /// <summary>
    /// Builds the rows on first use. The ruleset configuration cache is only available once the
    /// game has finished loading, and closed panels should not hold config bindings.
    /// </summary>
    internal void EnsurePopulated()
    {
        if (populated)
            return;

        populated = true;
        sections.Add(sectionLabel("Playfield"));
        if (config is not null)
        {
            sections.Add(new SliderRow("Background dim", config.GetBindable<double>(OsuSetting.DimLevel)));
            sections.Add(new SliderRow("Background blur", config.GetBindable<double>(OsuSetting.BlurLevel)));
            sections.Add(new ToggleRow("Storyboard / video", config.GetBindable<bool>(OsuSetting.ShowStoryboard)));
        }

        sections.Add(new ToggleRow("Gameplay HUD", showGameplayHud, "Score, accuracy and combo"));

        if (config is not null)
        {
            sections.Add(sectionLabel("Beatmap"));
            sections.Add(new ToggleRow("Beatmap skin", config.GetBindable<bool>(OsuSetting.BeatmapSkins)));
            sections.Add(new ToggleRow("Beatmap colours", config.GetBindable<bool>(OsuSetting.BeatmapColours)));
            sections.Add(new ToggleRow("Beatmap hitsounds", config.GetBindable<bool>(OsuSetting.BeatmapHitsounds)));
        }

        if (rulesetConfigs?.GetConfigFor(new OsuRuleset()) is OsuRulesetConfigManager osuConfig)
        {
            sections.Add(sectionLabel("Replay analysis"));
            sections.Add(new ToggleRow("Click markers", osuConfig.GetBindable<bool>(OsuRulesetSetting.ReplayClickMarkersEnabled)));
            sections.Add(new ToggleRow("Aim markers", osuConfig.GetBindable<bool>(OsuRulesetSetting.ReplayFrameMarkersEnabled)));
            sections.Add(new ToggleRow("Cursor path", osuConfig.GetBindable<bool>(OsuRulesetSetting.ReplayCursorPathEnabled)));
            sections.Add(new ToggleRow("Hide skin cursor", osuConfig.GetBindable<bool>(OsuRulesetSetting.ReplayCursorHideEnabled)));
        }
    }

    protected override void Update()
    {
        base.Update();
        float contentHeight = sections.DrawHeight;
        float bodyHeight = Math.Max(60, Math.Min(contentHeight, MaximumHeight - 49));
        if (Math.Abs(body.Height - bodyHeight) > 0.5f)
        {
            body.Height = bodyHeight;
            Height = bodyHeight + 49;
        }
    }

    // The panel is a popover: clicks and scrolling inside it must not reach the replay below.
    protected override bool OnMouseDown(MouseDownEvent e) => true;

    protected override bool OnClick(ClickEvent e) => true;

    protected override bool OnScroll(ScrollEvent e) => true;

    private static Drawable sectionLabel(string text) => new Container
    {
        RelativeSizeAxes = Axes.X,
        Height = 28,
        Child = new OsuSpriteText
        {
            Anchor = Anchor.BottomLeft,
            Origin = Anchor.BottomLeft,
            Y = -4,
            Text = text,
            Font = AimModVisualStyle.LabelFont,
            Colour = AimModPalette.Muted,
        },
    };

    private partial class SliderRow : Container
    {
        private readonly Bindable<double> current;
        private readonly OsuSpriteText valueText;

        public SliderRow(string label, Bindable<double> source)
        {
            current = source.GetBoundCopy();
            RelativeSizeAxes = Axes.X;
            Height = 50;
            Children = new Drawable[]
            {
                new OsuSpriteText { Y = 6, Text = label, Font = AimModVisualStyle.BodyFont, Colour = AimModPalette.Text },
                valueText = new OsuSpriteText
                {
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.TopRight,
                    Y = 6,
                    Font = AimModVisualStyle.BodyStrongFont,
                    Colour = AimModPalette.Muted,
                },
                // osu!'s own slider keeps its native drag, keyboard and tooltip behaviour.
                new AccentSliderBar
                {
                    RelativeSizeAxes = Axes.X,
                    Y = 28,
                    DisplayAsPercentage = true,
                    PlaySamplesOnAdjust = false,
                    Current = current,
                },
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            current.BindValueChanged(value => valueText.Text = $"{value.NewValue * 100:0}%", true);
        }
    }

    private partial class AccentSliderBar : RoundedSliderBar<double>
    {
        protected override void LoadComplete()
        {
            base.LoadComplete();
            // osu! picks its overlay accent while loading; use AimMod's selection colour instead.
            AccentColour = AimModPalette.Accent;
            BackgroundColour = AimModPalette.Border;
        }
    }

    internal partial class ToggleRow : AimModInteractiveSurface
    {
        private readonly Bindable<bool> current;
        private readonly Container track;
        private readonly Box trackFill;
        private readonly Container knob;

        internal Bindable<bool> Current => current;

        public ToggleRow(string label, Bindable<bool> source, string? detail = null)
        {
            current = source.GetBoundCopy();
            RelativeSizeAxes = Axes.X;
            Height = detail is null ? 36 : 44;
            BorderThickness = 0;
            BackgroundColour = AimModPalette.PanelRaised;
            Action = () =>
            {
                if (!current.Disabled)
                    current.Value = !current.Value;
            };
            Children = new Drawable[]
            {
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Vertical,
                    Spacing = new(0, 1),
                    Children = detail is null
                        ? new Drawable[] { new OsuSpriteText { Text = label, Font = AimModVisualStyle.BodyFont, Colour = AimModPalette.Text } }
                        : new Drawable[]
                        {
                            new OsuSpriteText { Text = label, Font = AimModVisualStyle.BodyFont, Colour = AimModPalette.Text },
                            new OsuSpriteText { Text = detail, Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted },
                        },
                },
                track = new Container
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Size = new(34, 18),
                    Masking = true,
                    CornerRadius = 9,
                    BorderThickness = 1,
                    Children = new Drawable[]
                    {
                        trackFill = new Box { RelativeSizeAxes = Axes.Both },
                        knob = new CircularContainer
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Size = new(12),
                            Masking = true,
                            Child = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Text },
                        },
                    },
                },
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            current.BindValueChanged(value => updateState(value.NewValue, true), true);
        }

        private void updateState(bool on, bool animate)
        {
            double duration = animate ? AimModVisualStyle.FastTransition : 0;
            trackFill.FadeColour(on ? AimModPalette.Accent : AimModPalette.Panel, duration);
            track.BorderColour = on ? AimModPalette.Accent : AimModPalette.Border;
            knob.MoveToX(on ? 19 : 3, duration, Easing.OutQuint);
            knob.FadeColour(on ? AimModPalette.Canvas : AimModPalette.Muted, duration);
        }
    }

    private partial class CloseButton : AimModInteractiveSurface
    {
        public CloseButton(Action close)
        {
            Size = new(AimModVisualStyle.CompactControlHeight);
            Action = close;
            BackgroundColour = AimModPalette.PanelRaised;
            BorderThickness = 0;
            Child = new SpriteIcon
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Size = new(11),
                Icon = FontAwesome.Solid.Times,
                Colour = AimModPalette.Muted,
            };
        }
    }
}
