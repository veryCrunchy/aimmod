using AimMod.Desktop.Coaching;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Replays;

public enum ReplayMomentSeverity
{
    Miss,
    SliderBreak,
}

/// <summary>A reviewable mistake in the selected replay.</summary>
public sealed record ReplayMoment(
    double TimeMs,
    ReplayMomentSeverity Severity,
    string ObjectLabel,
    string Detail)
{
    public string SeverityLabel => Severity == ReplayMomentSeverity.Miss ? "Miss" : "Slider break";

    public ReplayTimelineTone Tone => Severity == ReplayMomentSeverity.Miss ? ReplayTimelineTone.Miss : ReplayTimelineTone.SliderBreak;

    /// <summary>Where playback starts so the lead-in to the mistake is visible.</summary>
    public double SeekTimeMs => Math.Max(0, TimeMs - ReplayTransportController.MomentPreRollMs);

    public static IReadOnlyList<ReplayMoment> From(ReplayAnalysisResult result, int maximum = 200)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Judgements
                     .Where(judgement => double.IsFinite(judgement.StartTimeMs) && judgement.StartTimeMs >= 0)
                     .Where(judgement => ReplayJudgementClassifier.IsMiss(judgement) || ReplayJudgementClassifier.IsSliderBreak(judgement))
                     .OrderBy(judgement => judgement.StartTimeMs)
                     // Nested slider misses share a head time with their object; keep one moment per object.
                     .DistinctBy(judgement => (judgement.ObjectIndex, Math.Round(judgement.StartTimeMs)))
                     .Take(Math.Max(0, maximum))
                     .Select(judgement => new ReplayMoment(
                         judgement.StartTimeMs,
                         ReplayJudgementClassifier.IsMiss(judgement) ? ReplayMomentSeverity.Miss : ReplayMomentSeverity.SliderBreak,
                         judgement.ObjectIndex is { } index ? $"Object {index + 1:N0}" : judgement.ObjectType,
                         ReplayMissInsightPresenter.Describe(judgement)))
                     .ToArray();
    }
}

/// <summary>
/// Transport commands shared by AimMod's controls and keyboard shortcuts. All playback changes go
/// through the official replay player's gameplay clock.
/// </summary>
internal sealed class ReplayTransportController
{
    public const double MomentPreRollMs = 1500;
    public const double SeekStepMs = 5000;
    public const double FineSeekStepMs = 1000;

    public static readonly double[] PlaybackRates = { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2 };

    private IReadOnlyList<ReplayMoment> moments = Array.Empty<ReplayMoment>();
    private double analysisDuration;

    public NativeReplayPlayer? Player { get; private set; }

    public IReadOnlyList<ReplayMoment> Moments => moments;

    public bool HasPlayer => Player is not null;

    public bool IsReady => Player?.IsTransportReady.Value == true;

    public double CurrentTime => Math.Max(0, Player?.CurrentTime.Value ?? 0);

    /// <summary>The player's timeline, or the analysed length before the player is ready.</summary>
    public double Duration => Player?.Duration.Value is { } playerDuration && playerDuration > 0 ? playerDuration : analysisDuration;

    public bool IsPaused => Player?.IsPaused.Value != false;

    public double PlaybackRate => Player?.PlaybackRate.Value ?? 1;

    public void Attach(NativeReplayPlayer? player) => Player = player;

    public void SetAnalysis(ReplayAnalysisResult? result)
    {
        moments = result is null ? Array.Empty<ReplayMoment>() : ReplayMoment.From(result);
        analysisDuration = result?.Judgements
                                 .Where(judgement => double.IsFinite(judgement.EndTimeMs))
                                 .Select(judgement => Math.Max(judgement.StartTimeMs, judgement.EndTimeMs))
                                 .DefaultIfEmpty(0)
                                 .Max() ?? 0;
    }

    public bool TogglePause() => Player?.TogglePause() == true;

    public bool SeekTo(double time) => double.IsFinite(time) && Player?.SeekTo(Math.Max(0, time)) == true;

    public bool SeekBy(double offset) => SeekTo(CurrentTime + offset);

    public bool StepFrame(int direction) => Player?.StepReplayFrame(direction) == true;

    public bool SetPlaybackRate(double rate) => Player?.SetPlaybackRate(rate) == true;

    public bool JumpToMoment(int direction)
    {
        ReplayMoment? target = FindAdjacentMoment(moments, CurrentTime, direction);
        return target is not null && SeekTo(target.SeekTimeMs);
    }

    public bool JumpToMoment(ReplayMoment moment) => SeekTo(moment.SeekTimeMs);

    /// <summary>
    /// Finds the previous or next moment relative to playback. Moments are compared by their seek
    /// position, with a short tolerance so repeated presses keep moving instead of re-selecting the
    /// moment that was just jumped to.
    /// </summary>
    internal static ReplayMoment? FindAdjacentMoment(IReadOnlyList<ReplayMoment> moments, double now, int direction)
    {
        if (direction == 0 || moments.Count == 0)
            return null;

        const double tolerance = 400;
        return direction > 0
            ? moments.FirstOrDefault(moment => moment.SeekTimeMs > now + tolerance)
            : moments.LastOrDefault(moment => moment.SeekTimeMs < now - tolerance);
    }

    public static string FormatRate(double rate) => $"{rate:0.##}x";

    public static double NearestRate(double rate) =>
        PlaybackRates.OrderBy(candidate => Math.Abs(candidate - rate)).First();
}
