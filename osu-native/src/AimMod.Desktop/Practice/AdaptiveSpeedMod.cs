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
public sealed class AdaptiveAttemptPlan(IReadOnlyList<AdaptiveSection> sections, AdaptivePracticeProgress progress, AdaptiveDrill drill)
{
    /// <summary>Map time of music before the drill's first object. Long enough to settle into the rhythm.</summary>
    public const double LeadInMs = 3_000;
    public IReadOnlyList<AdaptiveSection> Sections { get; } = sections;
    public AdaptivePracticeProgress Progress { get; set; } = progress;
    public AdaptiveDrill Drill { get; set; } = drill;
    public double StartMs => Drill.StartMs;
    public double EndMs => Drill.EndMs;
    public double LeadInStart => StartMs - LeadInMs;
    /// <summary>One speed for the whole attempt, so nothing changes pace under the player mid-run.</summary>
    public double Rate => Drill.Speed * Progress.TargetRate;
}

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
