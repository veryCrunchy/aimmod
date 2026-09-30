using System.Runtime.CompilerServices;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Coaching;

/// <summary>
/// Classifies the judgements of one replay analysis once, so repeated coaching builds never
/// re-scan or re-flatten the full judgement lists.
/// </summary>
internal sealed class ReplayJudgementDigest
{
    private static readonly ConditionalWeakTable<ReplayAnalysisResult, ReplayJudgementDigest> digests = new();

    private ReplayJudgementDigest(ReplayAnalysisResult analysis)
    {
        var misses = new List<ReplayObjectJudgement>();
        var classified = new List<ReplayObjectJudgement>();
        var timing = new List<double>();
        var tapTiming = new List<double>();
        var cursor = new List<double>();
        IReadOnlyList<ReplayObjectJudgement> judgements = analysis.Judgements ?? [];
        foreach (ReplayObjectJudgement judgement in judgements)
        {
            if (ReplayJudgementClassifier.IsMiss(judgement))
            {
                misses.Add(judgement);
                if (judgement.MissAnalysis is { Reason: not ReplayMissReason.Unknown })
                    classified.Add(judgement);
            }
            else if (judgement.ObjectPosition is not null && judgement.CursorPosition is not null)
            {
                double distance = ReplayJudgementClassifier.Distance(judgement.ObjectPosition, judgement.CursorPosition);
                if (double.IsFinite(distance))
                    cursor.Add(distance);
            }

            if (ReplayJudgementClassifier.IsTimingSample(judgement))
            {
                timing.Add(judgement.TimeOffsetMs);
                if (judgement.ObjectType.EndsWith("Circle", StringComparison.OrdinalIgnoreCase))
                    tapTiming.Add(judgement.TimeOffsetMs);
            }
        }

        double duration = judgements.Count == 0 ? 0 : judgements.Max(judgement => judgement.EndTimeMs);
        if (double.IsFinite(duration) && duration > 0)
        {
            foreach (ReplayObjectJudgement judgement in judgements)
            {
                if (!double.IsFinite(judgement.StartTimeMs))
                    continue;

                int segment = Math.Clamp((int)(judgement.StartTimeMs / duration * SegmentCount), 0, SegmentCount - 1);
                if (judgement.NestedPath is null)
                {
                    SegmentJudgements[segment]++;
                    if (ReplayJudgementClassifier.IsMiss(judgement))
                        SegmentMisses[segment]++;
                }

                if (ReplayJudgementClassifier.IsSliderBreak(judgement))
                    SegmentSliderBreaks[segment]++;
            }
        }

        JudgementCount = judgements.Count;
        Misses = misses.ToArray();
        ClassifiedMisses = classified.ToArray();
        TimingOffsets = timing.ToArray();
        TapTimingOffsets = tapTiming.ToArray();
        CursorDistances = cursor.ToArray();
    }

    public const int SegmentCount = 3;

    public int JudgementCount { get; }

    /// <summary>Primary (non-nested) judgements per opening/middle/closing third of the map.</summary>
    public int[] SegmentJudgements { get; } = new int[SegmentCount];

    public int[] SegmentMisses { get; } = new int[SegmentCount];

    public int[] SegmentSliderBreaks { get; } = new int[SegmentCount];

    public ReplayObjectJudgement[] Misses { get; }

    /// <summary>Misses whose classifier produced a reason other than <see cref="ReplayMissReason.Unknown"/>.</summary>
    public ReplayObjectJudgement[] ClassifiedMisses { get; }

    public double[] TimingOffsets { get; }

    public double[] TapTimingOffsets { get; }

    public double[] CursorDistances { get; }

    public static ReplayJudgementDigest For(ReplayAnalysisResult analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return digests.GetValue(analysis, static value => new ReplayJudgementDigest(value));
    }
}
