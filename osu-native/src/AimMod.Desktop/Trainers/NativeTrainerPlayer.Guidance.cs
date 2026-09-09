using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Objects.Drawables;

namespace AimMod.Desktop.Trainers;

public partial class NativeTrainerPlayer
{
    private TrainerGuidanceOverlay? guide;
    private OsuHitObject[] guideObjects = [];
    private readonly SpinnerPracticeMetrics spinMetrics = new();
    private Spinner? activeSpin;
    private double? previousSpinAngle;
    private double previousSpinTime, previousRotation;
    private double nextGuideUpdate;

    private void loadPracticeGuide()
    {
        guideObjects = GameplayState.Beatmap.HitObjects.OfType<OsuHitObject>().ToArray();
        if (settings.GuidedCues) AddInternal(guide = new TrainerGuidanceOverlay { Depth = -100 });
    }

    private void updatePracticeGuide()
    {
        double time = CurrentTime;
        var spinner = DrawableRuleset.Playfield.AllHitObjects.OfType<DrawableSpinner>()
            .FirstOrDefault(s => s.HitObject.StartTime <= time && s.HitObject.EndTime > time);
        if (spinner is not null)
        {
            if (!ReferenceEquals(activeSpin, spinner.HitObject))
            {
                activeSpin = spinner.HitObject; previousSpinAngle = null; spinMetrics.Begin();
                previousSpinTime = time; previousRotation = spinner.Result.TotalRotation;
            }
            var local = DrawableRuleset.Playfield.ScreenSpaceToGamefield(GetContainingInputManager().CurrentState.Mouse.Position);
            double angle = Math.Atan2(local.Y - 192, local.X - 256) * 180 / Math.PI;
            double delta = previousSpinAngle is {} last ? (angle - last + 540) % 360 - 180 : 0;
            previousSpinAngle = angle;
            double elapsed = time - previousSpinTime;
            double rpm = elapsed > 0 ? Math.Max(0, spinner.Result.TotalRotation - previousRotation) / 360 * 60000 / elapsed : 0;
            spinMetrics.Sample(elapsed, rpm, spinner.RotationTracker.Tracking, delta,
                Math.Sqrt(Math.Pow(local.X - 256, 2) + Math.Pow(local.Y - 192, 2)));
            previousSpinTime = time; previousRotation = spinner.Result.TotalRotation;
        }
        else { activeSpin = null; previousSpinAngle = null; }
        if (guide is null || time < nextGuideUpdate) return;
        nextGuideUpdate = time + 100;
        var cue = TrainerPracticeGuide.At(settings, guideObjects, time);
        if (spinner is not null && cue.Stage == "SPIN" && time > spinner.HitObject.StartTime + 700)
        {
            if (!spinner.RotationTracker.Tracking) cue = cue with { Text = $"Keep holding either {settings.Keys} while you circle the centre." };
            else if (spinner.Progress >= 1) cue = cue with { Text = "Cleared. Keep the same smooth motion until the spinner ends." };
        }
        if (cue.Stage == "RECOVER")
        {
            var recent = judgements.Where(j => j.HitObject is HitCircle && j.HitObject.StartTime > time - 5000).ToArray();
            if (recent.Length >= 3)
            {
                if (recent.Count(j => !j.IsHit) > 1) cue = cue with { Text = "Reset for the next group. Read its entry before moving." };
                else if (recent.Where(j => j.IsHit).Average(j => j.TimeOffset) < -20) cue = cue with { Text = "The last taps were early. Listen for the next entry before pressing." };
                else if (recent.Where(j => j.IsHit).Average(j => j.TimeOffset) > 20) cue = cue with { Text = "The last taps were late. Prepare for the next entry during this gap." };
            }
        }
        guide.SetCue(cue, spinner is not null || settings.Kind == TrainerKind.Spinner, spinner?.SpinsPerMinute.Value);
    }
}
