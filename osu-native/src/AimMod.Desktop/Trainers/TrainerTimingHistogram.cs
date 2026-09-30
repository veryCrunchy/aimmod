using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Trainers;

/// <summary>
/// Where taps landed relative to each note. Uses the recorded 5 ms histogram when available; older runs only
/// stored their mean and spread, so those are shown as a band instead of an invented distribution.
/// </summary>
public partial class TrainerTimingHistogram(TrainerResult result, TrainerResult? previous) : Container
{
    private const float range = TrainerResult.HistogramRangeMs, plot = 96;
    private float renderedWidth;

    protected override void LoadComplete()
    {
        base.LoadComplete();
        RelativeSizeAxes = Axes.X;
        Height = plot + 72;
    }

    protected override void Update()
    {
        base.Update();
        if (Math.Abs(renderedWidth - DrawWidth) < .5f || DrawWidth < 160) return;
        renderedWidth = DrawWidth;
        Clear();
        float width = DrawWidth;
        float x(double ms) => (float)((Math.Clamp(ms, -range, range) + range) / (2 * range)) * width;
        float top = 20;
        Add(label("Where your taps landed", 0, 0, AimModPalette.Text, "SemiBold"));
        var legend = label(result.OffsetHistogram is null ? "older run · average ± spread only" : "share of taps per 5 ms · grey = outside ±55 ms", 0, 0, AimModPalette.Muted);
        legend.Anchor = Anchor.TopRight; legend.Origin = Anchor.TopRight; Add(legend);
        // The ±25 ms window counted as "on time".
        Add(new Box { X = x(-25), Y = top, Width = x(25) - x(-25), Height = plot, Colour = AimModPalette.AccentMuted.Opacity(.55f) });
        Add(new Box { X = x(0), Y = top, Width = 1, Height = plot, Origin = Anchor.TopCentre, Colour = AimModPalette.Muted.Opacity(.6f) });
        Add(new Box { Y = top + plot, Width = width, Height = 1, Colour = AimModPalette.Border });
        if (result.OffsetHistogram is { Length: > 2 } bins && bins.Sum() > 0)
        {
            // Heights are shares of taps, so runs with different note counts compare fairly.
            double[] share(int[] counts) => counts.Select(c => c / (double)Math.Max(1, counts.Sum())).ToArray();
            var current = share(bins);
            var before = previous?.OffsetHistogram is { Length: > 2 } old && old.Length == bins.Length && old.Sum() > 0 ? share(old) : null;
            // The outer bins also hold every tap beyond the range, so they never set the scale.
            double peak = Math.Max(1e-6, current.Skip(1).SkipLast(1).Concat(before?.Skip(1).SkipLast(1) ?? []).DefaultIfEmpty(0).Max());
            float binWidth = width / bins.Length;
            for (int i = 0; i < bins.Length; i++)
            {
                bool overflow = i == 0 || i == bins.Length - 1;
                double centre = -range + (i + .5) * TrainerResult.HistogramBinMs;
                if (bins[i] > 0)
                {
                    float h = Math.Clamp(plot * (float)(current[i] / peak), 2, plot);
                    Add(new Box { X = i * binWidth + 1, Y = top + plot, Origin = Anchor.BottomLeft, Width = Math.Max(1, binWidth - 2), Height = h,
                        Colour = overflow ? AimModPalette.Muted.Opacity(.35f) : Math.Abs(centre) <= 25 ? AimModPalette.Accent : AimModPalette.Cyan.Opacity(.8f) });
                    if (overflow)
                    {
                        var count = label($"{(i == 0 ? "<" : ">")}{(i == 0 ? -range + TrainerResult.HistogramBinMs : range - TrainerResult.HistogramBinMs):+0;-0} · {bins[i]}",
                            i * binWidth + binWidth / 2, top + plot - h - 14, AimModPalette.Muted);
                        count.Origin = Anchor.TopCentre; count.X = Math.Clamp(count.X, 24, width - 24); Add(count);
                    }
                }
                if (before is not null && before[i] > 0 && !overflow)
                    // Previous run as a faint outline: the top edge of each of its bins.
                    Add(new Box { X = i * binWidth + 1, Y = top + plot - Math.Clamp(plot * (float)(before[i] / peak), 1, plot), Width = Math.Max(1, binWidth - 2), Height = 2,
                        Colour = AimModPalette.Text.Opacity(.55f) });
            }
            if (before is not null)
            {
                var key = new FillFlowContainer { AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(5), Y = top + plot + 6, Children = [
                    new Box { Width = 12, Height = 2, Colour = AimModPalette.Text.Opacity(.55f), Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft },
                    label("previous run", 0, 0, AimModPalette.Muted) ] };
                Add(key);
            }
        }
        else if (result.MeanMs is { } mean && result.SpreadMs is { } spread)
        {
            band(mean, spread, top + plot * .35f, plot * .3f, AimModPalette.Cyan.Opacity(.75f), x);
            if (previous?.MeanMs is { } previousMean && previous.SpreadMs is { } previousSpread)
            {
                float y = top + plot + 8;
                band(previousMean, previousSpread, y, 5, AimModPalette.Muted.Opacity(.5f), x);
                Add(label("previous run", x(previousMean + previousSpread) + 6, y - 4, AimModPalette.Muted));
            }
        }
        if (result.MeanMs is { } average)
        {
            Add(new Box { X = x(average), Y = top - 4, Width = 2, Height = plot + 4, Origin = Anchor.TopCentre, Colour = AimModPalette.Text });
            var avg = label($"avg {average:+0.0;-0.0;0} ms", x(average), top + plot + 20, AimModPalette.Text, "SemiBold");
            avg.Origin = Anchor.TopCentre; avg.X = Math.Clamp(avg.X, 40, width - 40); Add(avg);
        }
        float axis = top + plot + 36;
        Add(label($"{-range:0} ms · early", 0, axis, AimModPalette.Muted));
        var centreLabel = label("±25 ms on time", x(0), axis, AimModPalette.Accent); centreLabel.Origin = Anchor.TopCentre; Add(centreLabel);
        var late = label($"late · +{range:0} ms", 0, axis, AimModPalette.Muted); late.Anchor = Anchor.TopRight; late.Origin = Anchor.TopRight; Add(late);
    }

    private void band(double mean, double spread, float y, float height, Colour4 colour, Func<double, float> x)
    {
        float from = x(mean - spread), to = x(mean + spread);
        Add(new Box { X = from, Y = y, Width = Math.Max(2, to - from), Height = height, Colour = colour });
    }

    private static OsuSpriteText label(string text, float x, float y, Colour4 colour, string weight = "Regular") => new()
        { Text = text, X = x, Y = y, Font = new FontUsage(size: 11, weight: weight), Colour = colour };
}
