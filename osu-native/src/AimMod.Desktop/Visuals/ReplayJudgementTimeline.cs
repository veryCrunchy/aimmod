using AimMod.Osu.Runtime.Contracts;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;

namespace AimMod.Desktop.Visuals;

public enum ReplayTimelineTone
{
    Great,
    Ok,
    Meh,
    Miss,
    SliderBreak,
}

public sealed record ReplayTimelineMark(double Position, ReplayTimelineTone Tone, double TimeMilliseconds);

public static class ReplayTimelineSampler
{
    public const int MaximumMarks = 240;

    public static IReadOnlyList<ReplayTimelineMark> Sample(ReplayAnalysisResult result, int maximum = MaximumMarks)
    {
        ArgumentNullException.ThrowIfNull(result);
        maximum = Math.Clamp(maximum, 1, MaximumMarks);

        ReplayObjectJudgement[] ordered = result.Judgements
                                                   .Where(judgement => double.IsFinite(judgement.StartTimeMs) && judgement.StartTimeMs >= 0)
                                                   .OrderBy(judgement => judgement.StartTimeMs)
                                                   .ToArray();
        if (ordered.Length == 0)
            return Array.Empty<ReplayTimelineMark>();

        double duration = Math.Max(1, ordered.Max(judgement => Math.Max(judgement.StartTimeMs, judgement.EndTimeMs)));
        ReplayObjectJudgement[] notable = ordered.Where(judgement => tone(judgement) is ReplayTimelineTone.Miss or ReplayTimelineTone.SliderBreak)
                                                   .Take(maximum)
                                                   .ToArray();
        int ordinaryBudget = maximum - notable.Length;
        ReplayObjectJudgement[] ordinary = ordered.Where(judgement => tone(judgement) is not (ReplayTimelineTone.Miss or ReplayTimelineTone.SliderBreak))
                                                   .ToArray();
        int stride = ordinaryBudget == 0 ? int.MaxValue : Math.Max(1, (int)Math.Ceiling((double)ordinary.Length / ordinaryBudget));

        return notable.Concat(ordinary.Where((_, index) => index % stride == 0).Take(ordinaryBudget))
                      .DistinctBy(judgement => (judgement.ObjectIndex, judgement.StartTimeMs, judgement.Result))
                      .OrderBy(judgement => judgement.StartTimeMs)
                      .Select(judgement => new ReplayTimelineMark(
                          Math.Clamp(judgement.StartTimeMs / duration, 0, 1),
                          tone(judgement),
                          judgement.StartTimeMs))
                      .ToArray();
    }

    private static ReplayTimelineTone tone(ReplayObjectJudgement judgement) => judgement.Result switch
    {
        "Miss" => ReplayTimelineTone.Miss,
        "LargeTickMiss" or "SmallTickMiss" or "SliderTailMiss" => ReplayTimelineTone.SliderBreak,
        "Meh" => ReplayTimelineTone.Meh,
        "Ok" => ReplayTimelineTone.Ok,
        _ => ReplayTimelineTone.Great,
    };
}

public partial class ReplayJudgementTimeline : CompositeDrawable
{
    private readonly Container marks;

    public ReplayJudgementTimeline()
    {
        RelativeSizeAxes = Axes.X;
        Height = 42;
        InternalChildren = new Drawable[]
        {
            new Box
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                RelativeSizeAxes = Axes.X,
                Height = 2,
                Colour = AimModPalette.Border,
            },
            marks = new Container { RelativeSizeAxes = Axes.Both },
        };
    }

    public void SetResult(ReplayAnalysisResult result)
    {
        marks.Clear();
        foreach (ReplayTimelineMark mark in ReplayTimelineSampler.Sample(result))
        {
            bool critical = mark.Tone is ReplayTimelineTone.Miss or ReplayTimelineTone.SliderBreak;
            marks.Add(new Box
            {
                RelativePositionAxes = Axes.X,
                X = (float)mark.Position,
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.Centre,
                Size = new(critical ? 3 : 1, critical ? 34 : mark.Tone == ReplayTimelineTone.Great ? 13 : 21),
                Colour = colour(mark.Tone),
            });
        }
    }

    public void ClearResult() => marks.Clear();

    private static Colour4 colour(ReplayTimelineTone tone) => tone switch
    {
        ReplayTimelineTone.Miss => AimModPalette.Pink,
        ReplayTimelineTone.SliderBreak => Colour4.FromHex("FF9C55"),
        ReplayTimelineTone.Meh => Colour4.FromHex("FFD45A"),
        ReplayTimelineTone.Ok => AimModPalette.Cyan,
        _ => AimModPalette.Success,
    };
}
