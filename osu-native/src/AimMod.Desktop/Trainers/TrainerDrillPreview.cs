using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;
using osuTK;

namespace AimMod.Desktop.Trainers;

/// <summary>Small schematics. Timing previews use the same beat pattern as the drill generator.</summary>
public partial class TrainerDrillPreview(TrainerSettings settings, bool compact = false, Func<int>? tempo = null) : Container
{
    private float renderedWidth;
    private readonly List<(Circle Dot, double Beat)> notes = [];
    protected override void Update()
    {
        base.Update();
        if (DrawWidth < 20) return;
        if (Math.Abs(renderedWidth - DrawWidth) > .5f)
        {
            renderedWidth = DrawWidth;
            Clear(); notes.Clear();
            if (settings.Kind == TrainerKind.Spinner) spinner();
            else if (settings.Kind == TrainerKind.Reaction) reaction();
            else if (settings.Kind is TrainerKind.Aim or TrainerKind.Reading || compact && settings.Kind == TrainerKind.Alternating) movement();
            else rhythm();
        }
        // A gentle playhead follows musical time. No audio or training state is changed.
        double beat = Time.Current / (60000.0 / (tempo?.Invoke() ?? settings.Bpm)) % 8;
        foreach (var (dot, at) in notes)
        {
            double distance = (beat - at + 8) % 8;
            dot.Colour = distance < .35 ? AimModPalette.Text : AimModPalette.Accent;
            dot.Scale = new((float)(distance < .35 ? 1.3 : 1));
        }
    }

    private void line(Vector2 a, Vector2 b, float thickness = 2, float opacity = .3f)
    {
        Vector2 delta = b - a;
        Add(new Box { Position = a, Origin = Anchor.CentreLeft, Width = delta.Length, Height = thickness,
            Rotation = MathF.Atan2(delta.Y, delta.X) * 180 / MathF.PI, EdgeSmoothness = new(1), Colour = AimModPalette.Accent.Opacity(opacity) });
    }

    private void dot(Vector2 position, float diameter, double beat, string? label = null)
    {
        var circle = new Circle { Position = position, Origin = Anchor.Centre, Size = new(diameter), Colour = AimModPalette.Accent };
        Add(circle); notes.Add((circle, beat));
        if (label is not null) Add(new OsuSpriteText { Position = position, Origin = Anchor.Centre,
            Text = label, UseFullGlyphHeight = false, Font = new FontUsage(size: 10, weight: "Bold"), Colour = AimModPalette.Canvas });
    }

    private void rhythm()
    {
        float left = 8, width = DrawWidth - 16, y = DrawHeight * .48f;
        line(new(left, y), new(left + width, y), 1.5f);
        for (int beat = 0; beat < 8; beat++)
        {
            float x = left + width * beat / 8;
            line(new(x, y - 13), new(x, y + 13), 1, .14f);
        }
        for (int bar = 0; bar < 2; bar++)
            foreach (var note in TrainerPatterns.Bar(settings, bar))
            {
                double beat = bar * 4 + note.Beat;
                dot(new(left + width * (float)beat / 8, y), compact ? 4 : 5, beat);
            }
        if (!compact) Add(new OsuSpriteText { Text = "2 bars", Font = new FontUsage(size: 10),
            Colour = AimModPalette.Muted, Anchor = Anchor.BottomRight, Origin = Anchor.BottomRight });
    }

    private void movement()
    {
        if (settings.Kind == TrainerKind.Reading)
        {
            var samples = Enumerable.Range(0, settings.ReadingGroupSize).Select(i => new TrainerNote(i * 500, 0, TrainerPatterns.PatternAt(settings, i / 4))).ToArray();
            var positions = TrainerReadingPatterns.Create(settings, samples);
            Vector2? last = null;
            for (int i = 0; i < positions.Length; i++)
            {
                var p = new Vector2(8 + (positions[i].X - 64) / 384 * (DrawWidth - 16), 6 + (positions[i].Y - 64) / 256 * (DrawHeight - 12));
                if (last is {} from) line(from, p);
                dot(p, compact ? 12 : 14, i * 8.0 / positions.Length, $"{i + 1}");
                last = p;
            }
            if (settings.ReadingHidden) Alpha = .6f;
            return;
        }
        int count = settings.Kind == TrainerKind.Alternating ? 12 : settings.Pattern == TrainerPattern.JumpFill ? 9 : 5;
        Vector2? previous = null;
        for (int i = 0; i < count; i++)
        {
            float t = (float)i / (count - 1);
            float y = settings.AimStyle == TrainerAimStyle.Flow || settings.Kind == TrainerKind.Alternating
                ? .5f + .29f * MathF.Sin(t * MathF.PI * 2)
                : settings.Kind == TrainerKind.Reading ? new[] { .65f, .2f, .75f, .3f, .5f }[i % 5]
                : i % 2 == 0 ? .7f : .2f;
            if (settings.Pattern == TrainerPattern.Scattered) t = new[] { .1f, .75f, .4f, .95f, .2f }[i % 5];
            Vector2 p = new(10 + (DrawWidth - 20) * t, 7 + (DrawHeight - 14) * y);
            if (previous is {} from) line(from, p, settings.Sliders != TrainerSliderStyle.None ? 5 : 1.5f);
            dot(p, settings.Kind == TrainerKind.Alternating ? 5 : settings.Kind == TrainerKind.Reading ? 14 : compact ? 9 : 14, i * 8.0 / count,
                settings.Kind == TrainerKind.Reading ? $"{i + 1}" : null);
            previous = p;
        }
    }

    private void spinner()
    {
        var centre = new Vector2(DrawWidth / 2, DrawHeight / 2);
        float radius = Math.Min(DrawHeight / 2 - 5, 20);
        for (int i = 0; i < 24; i++)
        {
            float a = i / 24f * MathF.Tau, b = (i + 1) / 24f * MathF.Tau;
            line(centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius,
                centre + new Vector2(MathF.Cos(b), MathF.Sin(b)) * radius, 2, .5f);
            if (i % 6 == 0) dot(centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius, 5, i / 3.0);
        }
    }

    private void reaction()
    {
        if (!compact && settings.ReactionMode != ReactionMode.Simple)
        {
            string[] labels = settings.ReactionMode == ReactionMode.Choice ? settings.Keys.Split(" / ") : ["GO", "STOP"];
            for (int i = 0; i < 2; i++)
                Add(new OsuSpriteText { Text = labels[i], Font = new FontUsage(size: 23, weight: "Bold"),
                    Colour = i == 0 ? AimModPalette.Accent : AimModPalette.Text, Origin = Anchor.Centre,
                    Position = new(DrawWidth * (i == 0 ? .28f : .72f), DrawHeight / 2) });
            return;
        }
        var centre = new Vector2(DrawWidth / 2, DrawHeight / 2);
        Add(new Circle { Position = centre, Origin = Anchor.Centre, Size = new(Math.Min(DrawHeight - 4, 34)),
            Colour = AimModPalette.Accent.Opacity(.16f), BorderThickness = 2, BorderColour = AimModPalette.Accent });
        dot(centre, 8, 3);
        line(centre + new Vector2(-28, 0), centre + new Vector2(-21, 0));
        line(centre + new Vector2(21, 0), centre + new Vector2(28, 0));
    }
}
