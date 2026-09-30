using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime.Contracts;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Replays;

/// <summary>Stacked judgement distribution with the count of each judgement beneath it.</summary>
public partial class ReplayJudgementBar : FillFlowContainer
{
    private readonly FillFlowContainer<Box> segments;
    private readonly FillFlowContainer counts;

    public ReplayJudgementBar()
    {
        RelativeSizeAxes = Axes.X;
        AutoSizeAxes = Axes.Y;
        Direction = FillDirection.Vertical;
        Spacing = new(0, 8);
        Children = new Drawable[]
        {
            new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = 8,
                Masking = true,
                CornerRadius = 4,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Border },
                    segments = new FillFlowContainer<Box> { RelativeSizeAxes = Axes.Both, Direction = FillDirection.Horizontal },
                },
            },
            counts = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Full,
                Spacing = new(14, 4),
            },
        };
    }

    public static IReadOnlyList<(string Label, int Count, Colour4 Colour)> Parts(ReplayJudgementSummary summary)
    {
        var parts = new List<(string, int, Colour4)>
        {
            ("300", summary.Great, ReplayJudgementTimeline.ColourFor(ReplayTimelineTone.Great)),
            ("100", summary.Ok, ReplayJudgementTimeline.ColourFor(ReplayTimelineTone.Ok)),
            ("50", summary.Meh, ReplayJudgementTimeline.ColourFor(ReplayTimelineTone.Meh)),
            ("miss", summary.Miss, ReplayMomentColours.For(ReplayMomentSeverity.Miss)),
        };
        if (summary.SliderBreaks > 0)
            parts.Add(("slider break", summary.SliderBreaks, ReplayMomentColours.For(ReplayMomentSeverity.SliderBreak)));
        return parts;
    }

    public void SetSummary(ReplayJudgementSummary summary)
    {
        segments.Clear();
        counts.Clear();
        IReadOnlyList<(string Label, int Count, Colour4 Colour)> parts = Parts(summary);
        // Slider breaks are counted separately from object judgements, so they are not a bar segment.
        int total = parts.Take(4).Sum(part => Math.Max(0, part.Count));
        foreach ((string label, int count, Colour4 colour) in parts)
        {
            if (total > 0 && count > 0 && label != "slider break")
            {
                segments.Add(new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    // A thin minimum keeps rare misses visible.
                    Width = Math.Max(0.012f, count / (float)total),
                    Colour = colour,
                });
            }

            counts.Add(new FillFlowContainer
            {
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Horizontal,
                Spacing = new(5, 0),
                Children = new Drawable[]
                {
                    new CircularContainer
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Size = new(7),
                        Masking = true,
                        Child = new Box { RelativeSizeAxes = Axes.Both, Colour = colour },
                    },
                    new OsuSpriteText
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Text = count.ToString("N0"),
                        Font = new FontUsage(size: 13, weight: "Bold"),
                        Colour = AimModPalette.Text,
                    },
                    new OsuSpriteText
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Text = label,
                        Font = AimModVisualStyle.CaptionFont,
                        Colour = AimModPalette.Muted,
                    },
                },
            });
        }

        // Normalise so rounding and minimum widths never overflow the bar.
        float sum = segments.Sum(segment => segment.Width);
        if (sum > 1)
        {
            foreach (Box segment in segments)
                segment.Width /= sum;
        }
    }
}
