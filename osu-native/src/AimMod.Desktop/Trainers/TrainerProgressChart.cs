using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;
using osuTK;

namespace AimMod.Desktop.Trainers;

/// <summary>Completed, comparable sessions only; missing spread values are excluded by the caller.</summary>
public partial class TrainerProgressChart(double[] values) : Container
{
    private float renderedWidth;

    protected override void LoadComplete()
    {
        base.LoadComplete();
        RelativeSizeAxes = Axes.X;
        Height = 94;
    }

    protected override void Update()
    {
        base.Update();
        if (Math.Abs(renderedWidth - DrawWidth) < .5f || DrawWidth < 100) return;
        renderedWidth = DrawWidth;
        Clear();
        double maximum = Math.Max(10, Math.Ceiling(values.Max() / 10) * 10);
        Add(new Box { X = 50, Y = 70, Width = DrawWidth - 62, Height = 1, Colour = AimModPalette.Border });
        Add(new OsuSpriteText { Text = $"{maximum:0} ms", Font = new FontUsage(size: 11), Colour = AimModPalette.Muted, Y = 7 });
        Add(new OsuSpriteText { Text = "0", Font = new FontUsage(size: 11), Colour = AimModPalette.Muted, Y = 61 });
        Vector2? previous = null;
        for (int i = 0; i < values.Length; i++)
        {
            var position = new Vector2(52 + (DrawWidth - 68) * i / (values.Length - 1), 70 - 58 * (float)(values[i] / maximum));
            if (previous is {} start)
            {
                var delta = position - start;
                Add(new Box { Position = start, Origin = Anchor.CentreLeft, Width = delta.Length, Height = 2,
                    Rotation = MathF.Atan2(delta.Y, delta.X) * 180 / MathF.PI, Colour = AimModPalette.Accent.Opacity(.7f) });
            }
            Add(new Circle { Position = position, Origin = Anchor.Centre, Size = new(6), Colour = AimModPalette.Accent });
            previous = position;
        }
        Add(new OsuSpriteText { Text = "Earlier", X = 50, Y = 78, Font = new FontUsage(size: 11), Colour = AimModPalette.Muted });
        Add(new OsuSpriteText { Text = "Latest", Anchor = Anchor.TopRight, Origin = Anchor.TopRight, X = -12, Y = 78, Font = new FontUsage(size: 11), Colour = AimModPalette.Muted });
    }
}
