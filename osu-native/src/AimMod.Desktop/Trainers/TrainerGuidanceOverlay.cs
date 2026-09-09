using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osuTK;

namespace AimMod.Desktop.Trainers;

/// <summary>A small, non-interactive cue strip. Gameplay input passes through unchanged.</summary>
public partial class TrainerGuidanceOverlay : Container
{
    private readonly OsuSpriteText stage;
    private readonly OsuTextFlowContainer message;
    private readonly Container example;
    private readonly Circle marker;
    private bool spinning;

    public TrainerGuidanceOverlay()
    {
        Anchor = Anchor.TopCentre; Origin = Anchor.TopCentre;
        RelativeSizeAxes = Axes.X; Width = .8f; Height = 78; Y = 68;
        Masking = true; CornerRadius = 8;
        var labels = new FillFlowContainer { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical, Spacing = new(5), Padding = new MarginPadding { Left = 80, Right = 14, Top = 10 } };
        labels.Add(stage = new OsuSpriteText { Font = new(size: 12, weight: "Bold"), Colour = AimModPalette.Accent });
        labels.Add(message = new OsuTextFlowContainer(t => { t.Font = new(size: 15); t.Colour = AimModPalette.Text; })
            { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y });
        example = new Container { Position = new(40, 39), Origin = Anchor.Centre, Size = new(54) };
        example.Add(new Circle { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Accent.Opacity(.15f),
            BorderColour = AimModPalette.Accent.Opacity(.6f), BorderThickness = 2 });
        example.Add(marker = new Circle { Anchor = Anchor.Centre, Origin = Anchor.Centre, Size = new(7), Colour = AimModPalette.Accent });
        Children = [new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas.Opacity(.92f) }, example, labels];
    }

    public void SetCue(TrainerGuideCue cue, bool spinner, double? rpm = null)
    {
        Alpha = cue.Visible ? 1 : 0;
        spinning = spinner;
        stage.Text = rpm is {} speed ? $"{cue.Stage}   {speed:0} RPM" : cue.Stage;
        message.Text = cue.Text;
    }

    protected override void Update()
    {
        base.Update();
        // A slow technique illustration, never a timing target or an automated cursor.
        double angle = Time.Current / 1000 * Math.PI;
        marker.Position = spinning ? new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * 22 : Vector2.Zero;
        marker.Scale = new(spinning ? 1 : 1 + .2f * (float)Math.Sin(angle * 2));
    }
}
