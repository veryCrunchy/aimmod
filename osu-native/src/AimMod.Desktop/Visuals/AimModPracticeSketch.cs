using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osuTK;

namespace AimMod.Desktop.Visuals;

public enum PracticeSketchKind { Section, Timing, Aim, Combined, Original, Replay, Targets }

/// <summary>Illustrations of workflow choices, never player statistics or map previews.</summary>
public partial class AimModPracticeSketch(PracticeSketchKind kind) : Container
{
    private Vector2 renderedSize;

    protected override void Update()
    {
        base.Update();
        if (DrawWidth < 20 || renderedSize == DrawSize) return;
        renderedSize = DrawSize;
        Clear();
        Vector2 point(float x, float y) => new(10 + x * (DrawWidth - 20), 7 + y * (DrawHeight - 14));
        if (kind == PracticeSketchKind.Replay)
        {
            line(point(0, .5f), point(1, .5f), 2, AimModPalette.Muted.Opacity(.3f));
            for (int i = 0; i < 13; i++)
            {
                float x = i / 12f;
                float error = new[] { .05f, -.08f, .04f, .1f, -.12f, .04f, .35f, .3f, -.1f, .02f, -.04f, .1f, -.05f }[i];
                line(point(x, .5f), point(x, .5f + error), 2, AimModPalette.Accent.Opacity(.4f));
                Add(new Circle { Position = point(x, .5f + error), Origin = Anchor.Centre, Size = new(7),
                    Colour = i is 6 or 7 ? AimModPalette.Text : AimModPalette.Accent });
            }
            line(point(.5f, 0), point(.5f, 1), 1.5f, AimModPalette.Text.Opacity(.5f));
            return;
        }
        if (kind == PracticeSketchKind.Section)
        {
            for (int i = 0; i < 25; i++)
            {
                float height = .15f + .65f * MathF.Abs(MathF.Sin(i * 1.7f));
                line(point(i / 24f, .5f - height / 2), point(i / 24f, .5f + height / 2), 3,
                    i is >= 9 and <= 15 ? AimModPalette.Accent : AimModPalette.Muted.Opacity(.4f));
            }
            line(point(.34f, 0), point(.34f, 1), 2, AimModPalette.Text);
            line(point(.67f, 0), point(.67f, 1), 2, AimModPalette.Text);
            return;
        }
        if (kind == PracticeSketchKind.Targets)
        {
            for (int i = 0; i < 3; i++)
            {
                float y = .15f + i * .35f;
                line(point(0, y), point(1, y), 6, AimModPalette.Muted.Opacity(.15f));
                line(point(0, y), point(new[] { .85f, .6f, .38f }[i], y), 6, AimModPalette.Accent.Opacity(1 - i * .25f));
            }
            return;
        }
        bool timing = kind == PracticeSketchKind.Timing;
        bool aimOnly = kind == PracticeSketchKind.Aim;
        float spread = kind == PracticeSketchKind.Combined ? .45f : .85f;
        Vector2? previous = null;
        for (int i = 0; i < 5; i++)
        {
            var p = point(timing ? .2f + i / 4f * .6f : i / 4f, timing ? .35f + i % 2 * .12f : .5f + (i % 2 == 0 ? spread / 2 : -spread / 2));
            if (previous is {} from) line(from, p, 2, AimModPalette.Accent.Opacity(.35f));
            Add(new Circle { Position = p, Origin = Anchor.Centre, Size = new(13), Colour = AimModPalette.Accent });
            if (aimOnly) Add(new Circle { Position = p, Origin = Anchor.Centre, Size = new(9), Colour = AimModPalette.Panel });
            if (!aimOnly) Add(new OsuSpriteText { Position = p, Origin = Anchor.Centre, Text = $"{i + 1}",
                Font = new FontUsage(size: 9, weight: "Bold"), Colour = AimModPalette.Canvas });
            previous = p;
        }
    }

    private void line(Vector2 from, Vector2 to, float thickness, Colour4 colour)
    {
        var delta = to - from;
        Add(new Box { Position = from, Origin = Anchor.CentreLeft, Width = delta.Length, Height = thickness,
            Rotation = MathF.Atan2(delta.Y, delta.X) * 180 / MathF.PI, EdgeSmoothness = new(1), Colour = colour });
    }
}

public partial class AimModVisualChoiceContent : FillFlowContainer<Drawable>
{
    public AimModVisualChoiceContent(string title, string description, PracticeSketchKind sketch)
    {
        RelativeSizeAxes = Axes.X; AutoSizeAxes = Axes.Y; Direction = FillDirection.Vertical;
        Padding = new MarginPadding(12); Spacing = new(5);
        Add(new AimModPracticeSketch(sketch) { RelativeSizeAxes = Axes.X, Height = 42 });
        Add(copy(title, 16, AimModPalette.Text, "SemiBold"));
        Add(copy(description, 12, AimModPalette.Muted));
    }

    private static OsuTextFlowContainer copy(string text, float size, Colour4 colour, string weight = "Regular") => new(t =>
        { t.Font = new FontUsage(size: size, weight: weight); t.Colour = colour; })
        { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = text };
}
