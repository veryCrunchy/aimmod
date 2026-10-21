using osu.Framework.Bindables;
using osu.Framework.Localisation;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Osu.Objects;

namespace AimMod.Desktop.Practice;

/// <summary>
/// The session's sections, progress and the drill the next attempt plays. Shared by reference between the
/// session and each player's mod clone. The session replaces <see cref="Drill"/> only between attempts.
/// </summary>
public sealed class AdaptiveAttemptPlan(IReadOnlyList<AdaptiveSection> sections, AdaptivePracticeProgress progress, AdaptiveDrill drill,
    IReadOnlyList<AdaptiveTiming>? timing = null)
{
    /// <summary>Map time of music before the drill's first object when the map has no usable timing.</summary>
    public const double LeadInMs = 3_000;
    /// <summary>A bar-snapped lead-in is at least this long, and falls back to <see cref="LeadInMs"/> past the maximum.</summary>
    public const double MinimumLeadInMs = 2_000, MaximumLeadInMs = 5_000;
    public IReadOnlyList<AdaptiveSection> Sections { get; } = sections;
    public AdaptivePracticeProgress Progress { get; set; } = progress;
    public AdaptiveDrill Drill { get; set; } = drill;
    public double StartMs => Drill.StartMs;
    public double EndMs => Drill.EndMs;
    public double LeadInStart => LeadIn(StartMs, timing ?? []);

    /// <summary>
    /// Where the music starts: the last bar line at least <see cref="MinimumLeadInMs"/> before the drill, so the
    /// player counts in on the downbeat. Very slow bars snap to a beat instead.
    /// </summary>
    public static double LeadIn(double startMs, IReadOnlyList<AdaptiveTiming> timing)
    {
        double latest = startMs - MinimumLeadInMs;
        var point = timing.LastOrDefault(t => t.Time <= latest) ?? timing.FirstOrDefault();
        if (point is null || !double.IsFinite(point.BeatLength) || point.BeatLength <= 0) return startMs - LeadInMs;
        foreach (double step in new[] { point.BeatLength * Math.Max(1, point.Meter), point.BeatLength })
        {
            double snapped = point.Time + Math.Floor((latest - point.Time) / step) * step;
            if (startMs - snapped <= MaximumLeadInMs) return snapped;
        }
        return startMs - LeadInMs;
    }
    /// <summary>One speed for the whole attempt, so nothing changes pace under the player mid-run.</summary>
    public double Rate => Drill.Speed * Progress.TargetRate;
}

/// <summary>A timing point reduced to what the lead-in needs: where it starts, its beat length and beats per bar.</summary>
public sealed record AdaptiveTiming(double Time, double BeatLength, int Meter);

/// <summary>
/// Plays only the drill's part of the original map, at the drill's speed. Derives from Half Time so the
/// ruleset accepts it and the pitch-preserving tempo adjustment is shared; its range also reaches full and
/// above-full speed.
/// </summary>
public sealed class AdaptiveSpeedMod : OsuModHalfTime, IApplicableToBeatmap
{
    public override string Name => "Adaptive Speed";
    public override LocalisableString Description => "Drills the part of the map you need at the speed you can clear.";
    public override bool Ranked => false;

    public override BindableNumber<double> SpeedChange { get; } = new BindableDouble(1) { MinValue = .25, MaxValue = 2, Precision = .01 };

    public AdaptiveAttemptPlan? Plan { get; init; }

    // The plan is session state, not a mod setting: share it with the player's clone instead of copying settings.
    public override Mod DeepClone() => new AdaptiveSpeedMod { Plan = Plan };

    public void ApplyToBeatmap(IBeatmap beatmap)
    {
        if (Plan is null) return;
        if (beatmap is Beatmap<OsuHitObject> osuBeatmap)
        {
            var remaining = osuBeatmap.HitObjects.Where(o => o.StartTime >= Plan.StartMs - .5 && o.StartTime <= Plan.EndMs + .5).ToList();
            if (remaining.Count > 0) osuBeatmap.HitObjects = remaining;
        }
        SpeedChange.Value = Plan.Rate;
    }
}
