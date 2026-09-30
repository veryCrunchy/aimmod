using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Visuals;

/// <summary>A labelled on/off switch. The whole row is clickable; the state is shown by the switch, not by caption text.</summary>
public partial class AimModTrainerSwitch : ClickableContainer, IHasTooltip
{
    private readonly Box background;
    private readonly OsuSpriteText title;
    private readonly OsuTextFlowContainer hint;
    private readonly CircularContainer track;
    private readonly Box trackFill;
    private readonly Circle knob;

    private string hintText = string.Empty;
    public bool Value { get; private set; }
    public LocalisableString TooltipText { get; set; }

    public AimModTrainerSwitch(string title, string hint, Action action)
    {
        Action = action;
        RelativeSizeAxes = Axes.X; AutoSizeAxes = Axes.Y;
        Masking = true; CornerRadius = AimModVisualStyle.ControlRadius;
        Children = [
            background = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel, Alpha = 0 },
            new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical,
                Spacing = new(2), Padding = new MarginPadding { Left = 10, Right = 64, Vertical = 8 }, Children = [
                    this.title = new OsuSpriteText { Font = new FontUsage(size: 14, weight: "SemiBold"), Colour = AimModPalette.Text },
                    this.hint = new OsuTextFlowContainer(t => { t.Font = new FontUsage(size: 12); t.Colour = AimModPalette.Muted; })
                        { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y },
                ] },
            track = new CircularContainer { Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, X = -10, Size = new(38, 22),
                Masking = true, BorderThickness = 1.5f, Children = [
                    trackFill = new Box { RelativeSizeAxes = Axes.Both },
                    knob = new Circle { Anchor = Anchor.CentreLeft, Origin = Anchor.Centre, Size = new(14) },
                ] },
        ];
        Title = title; Hint = hint;
        SetValue(false, false);
    }

    public string Title { get => title.Text.ToString(); set => title.Text = value; }

    public string Hint
    {
        get => hintText;
        set { hintText = value; hint.Text = value; hint.Alpha = string.IsNullOrWhiteSpace(value) ? 0 : 1; }
    }

    public void SetValue(bool on, bool animate = true)
    {
        Value = on;
        double duration = animate && IsLoaded ? AimModVisualStyle.FastTransition : 0;
        trackFill.FadeColour(on ? AimModPalette.Accent : AimModPalette.PanelRaised, duration);
        track.BorderColour = on ? AimModPalette.Accent : AimModPalette.Border;
        knob.FadeColour(on ? AimModPalette.Canvas : AimModPalette.Muted, duration);
        knob.MoveToX(on ? 27 : 11, duration, Easing.OutQuint);
    }

    protected override bool OnHover(HoverEvent e) { background.FadeTo(1, AimModVisualStyle.FastTransition); background.Colour = AimModPalette.PanelHover; return true; }
    protected override void OnHoverLost(HoverLostEvent e) { background.FadeTo(0, AimModVisualStyle.FastTransition); base.OnHoverLost(e); }
}
