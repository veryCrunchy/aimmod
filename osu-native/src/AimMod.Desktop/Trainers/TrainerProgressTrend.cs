using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;
using osuTK;

namespace AimMod.Desktop.Trainers;

/// <summary>One recorded session in a progress chart. Missing values stay missing, never zero.</summary>
public sealed record TrainerTrendPoint(DateTimeOffset At, double? Accuracy, double? SpreadMs, int? Bpm);

/// <summary>Accuracy and timing spread over recorded sessions, with least-squares trend lines and a tempo strip.</summary>
public partial class TrainerProgressTrend(IReadOnlyList<TrainerTrendPoint> points, string primaryTitle = "Accuracy", string primaryUnit = "%",
    string secondaryTitle = "Timing spread", string secondaryUnit = " ms", string secondaryNote = "lower is steadier") : Container
{
    private const float left = 44, right = 12, panel = 64, gap = 30, strip = 18;
    private float renderedWidth;
    public IReadOnlyList<TrainerTrendPoint> Points => points;

    protected override void LoadComplete()
    {
        base.LoadComplete();
        RelativeSizeAxes = Axes.X;
        Height = 22 + panel + gap + panel + 22 + strip + 20;
    }

    protected override void Update()
    {
        base.Update();
        if (Math.Abs(renderedWidth - DrawWidth) < .5f || DrawWidth < 160 || points.Count == 0) return;
        renderedWidth = DrawWidth;
        Clear();
        float width = DrawWidth - left - right;
        float x(int i) => left + (points.Count == 1 ? width / 2 : width * i / (points.Count - 1));
        float top = 22;
        series(points.Select(p => p.Accuracy).ToArray(), top, primaryTitle, primaryUnit, AimModPalette.Accent, x, higherIsBetter: true, note: "higher is better");
        float second = top + panel + gap;
        series(points.Select(p => p.SpreadMs).ToArray(), second, secondaryTitle, secondaryUnit, AimModPalette.Cyan, x, higherIsBetter: false,
            note: secondaryNote);
        float tempoTop = second + panel + 22;
        var tempos = points.Select(p => p.Bpm).ToArray();
        if (tempos.Any(t => t.HasValue))
        {
            int low = tempos.Where(t => t.HasValue).Min()!.Value, high = tempos.Where(t => t.HasValue).Max()!.Value;
            Add(label(low == high ? $"Tempo · {low} BPM" : $"Tempo · {low}–{high} BPM", 0, tempoTop - 16, AimModPalette.Muted));
            float bar = Math.Clamp(width / Math.Max(1, points.Count) * .55f, 2, 10);
            for (int i = 0; i < tempos.Length; i++)
            {
                if (tempos[i] is not { } bpm) continue;
                float h = high == low ? strip * .6f : 4 + (strip - 4) * (bpm - low) / (float)(high - low);
                Add(new Box { X = x(i), Y = tempoTop + strip, Origin = Anchor.BottomCentre, Width = bar, Height = h, Colour = AimModPalette.Muted.Opacity(.55f) });
            }
        }
        float axis = tempoTop + strip + 4;
        Add(label(points[0].At.LocalDateTime.ToString("d MMM"), left, axis, AimModPalette.Muted));
        var last = label(points[^1].At.LocalDateTime.ToString("d MMM"), 0, axis, AimModPalette.Muted);
        last.Anchor = Anchor.TopRight; last.Origin = Anchor.TopRight; last.X = -right;
        Add(last);
    }

    private void series(double?[] values, float top, string title, string unit, Colour4 colour, Func<int, float> x, bool higherIsBetter, string? note = null)
    {
        var present = values.Select((v, i) => (v, i)).Where(p => p.v.HasValue).Select(p => (Value: p.v!.Value, Index: p.i)).ToArray();
        var heading = new FillFlowContainer { Y = top - 20, AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(8) };
        heading.Add(label(title, 0, 0, AimModPalette.Text, "SemiBold"));
        if (note is not null) heading.Add(label(note, 0, 0, AimModPalette.Muted));
        Add(heading);
        Add(new Box { X = left, Y = top + panel, Width = DrawWidth - left - right, Height = 1, Colour = AimModPalette.Border });
        if (present.Length == 0) { Add(label("No data yet", left, top + panel / 2 - 7, AimModPalette.Muted)); return; }
        double min = present.Min(p => p.Value), max = present.Max(p => p.Value);
        double pad = Math.Max(1, (max - min) * .15);
        min = Math.Floor(min - pad); max = Math.Ceiling(max + pad);
        float y(double v) => top + panel - (float)((v - min) / Math.Max(1e-6, max - min)) * panel;
        Add(label($"{max:0}{unit}", 0, top - 5, AimModPalette.Muted));
        Add(label($"{min:0}{unit}", 0, top + panel - 9, AimModPalette.Muted));
        Add(new Box { X = left, Y = top, Width = DrawWidth - left - right, Height = 1, Colour = AimModPalette.Border.Opacity(.45f) });
        Vector2? previous = null;
        foreach (var (value, index) in present)
        {
            var at = new Vector2(x(index), y(value));
            if (previous is { } from) line(from, at, 1.5f, colour.Opacity(.35f));
            previous = at;
        }
        foreach (var (value, index) in present)
            Add(new Circle { Position = new(x(index), y(value)), Origin = Anchor.Centre, Size = new(6), Colour = colour });
        if (present.Length >= 3)
        {
            // Least-squares trend across session order.
            double meanX = present.Average(p => p.Index), meanY = present.Average(p => p.Value);
            double slope = present.Sum(p => (p.Index - meanX) * (p.Value - meanY)) / Math.Max(1e-9, present.Sum(p => Math.Pow(p.Index - meanX, 2)));
            double at(int i) => meanY + slope * (i - meanX);
            int first = present[0].Index, lastIndex = present[^1].Index;
            line(new(x(first), y(at(first))), new(x(lastIndex), y(at(lastIndex))), 2.5f, colour.Opacity(.85f));
            double change = at(lastIndex) - at(first);
            bool improving = higherIsBetter ? change > 0 : change < 0;
            string summary = Math.Abs(change) < .05 ? "Trend: flat" : $"Trend: {(change > 0 ? "+" : "")}{change:0.0}{unit}";
            var trend = label(summary, 0, top - 20, Math.Abs(change) < .05 ? AimModPalette.Muted : improving ? AimModPalette.Accent : AimModPalette.Yellow, "SemiBold");
            trend.Anchor = Anchor.TopRight; trend.Origin = Anchor.TopRight; trend.X = -right;
            Add(trend);
        }
    }

    private void line(Vector2 a, Vector2 b, float thickness, Colour4 colour)
    {
        var delta = b - a;
        if (delta.Length < .5f) return;
        Add(new Box { Position = a, Origin = Anchor.CentreLeft, Width = delta.Length, Height = thickness, EdgeSmoothness = new(1),
            Rotation = MathF.Atan2(delta.Y, delta.X) * 180 / MathF.PI, Colour = colour });
    }

    private static OsuSpriteText label(string text, float x, float y, Colour4 colour, string weight = "Regular") => new()
        { Text = text, X = x, Y = y, Font = new FontUsage(size: 11, weight: weight), Colour = colour };
}
