using AimMod.Desktop.Visuals;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Trainers;

public partial class NativeTrainersWorkspace
{
    private readonly BindableDouble targetMinimum = new(3) { MinValue = 0, MaxValue = 10 };
    private readonly BindableDouble targetMaximum = new(4) { MinValue = 0, MaxValue = 10 };
    private FillFlowContainer<Drawable> starControls = null!, starRange = null!;
    private AimModButton starTargetToggle = null!;
    private bool targetStarsEnabled;
    private osu.Framework.Threading.ScheduledDelegate? pendingStarSave;

    private void buildStarControls(FillFlowContainer<Drawable> body)
    {
        targetStarsEnabled = preferences.TargetStarsEnabled;
        targetMinimum.Value = double.IsFinite(preferences.MinimumStars) ? Math.Clamp(preferences.MinimumStars, 0, 10) : 3;
        targetMaximum.Value = double.IsFinite(preferences.MaximumStars) ? Math.Clamp(preferences.MaximumStars, targetMinimum.Value, 10) : Math.Max(4, targetMinimum.Value);
        starControls = column(); starControls.Depth = -9.8f;
        starControls.Add(starTargetToggle = new AimModButton("", () => { targetStarsEnabled = !targetStarsEnabled; updateStarTarget(); }));
        starRange = column();
        starRange.Add(new AimModStarRatingFilter { RelativeSizeAxes = Axes.X, Height = 38, LowerBound = targetMinimum, UpperBound = targetMaximum });
        starRange.Add(paragraph("Each map is checked before play. Adaptive fits the song, spacing and note speed to your target and recent performance. Manual note rates stay fixed. If this setup cannot reach the range, you can adjust it before starting."));
        starControls.Add(starRange); body.Add(starControls);
        targetMinimum.BindValueChanged(_ => updateStarTarget());
        targetMaximum.BindValueChanged(_ => updateStarTarget());
        updateStarTarget();
    }

    private void updateStarTarget()
    {
        settings = settings with { MinimumStars = targetStarsEnabled ? Math.Min(targetMinimum.Value, targetMaximum.Value) : null,
            MaximumStars = targetStarsEnabled ? Math.Max(targetMinimum.Value, targetMaximum.Value) : null, MeasuredStars = null };
        starTargetToggle.SetCaption(targetStarsEnabled ? $"Target: {settings.MinimumStars:0.0}–{settings.MaximumStars:0.0} stars" : "Target star range: Off");
        starTargetToggle.SetSelected(targetStarsEnabled);
        starRange.Alpha = targetStarsEnabled ? 1 : 0;
        preferences = preferences with { TargetStarsEnabled = targetStarsEnabled, MinimumStars = Math.Min(targetMinimum.Value, targetMaximum.Value), MaximumStars = Math.Max(targetMinimum.Value, targetMaximum.Value) };
        if (IsLoaded) { pendingStarSave?.Cancel(); pendingStarSave = Scheduler.AddDelayed(() => { saveTrainerPreferences(); refreshAdaptiveSummary(); }, 300); }
        refreshStarVisibility();
    }

    private void refreshStarVisibility()
    {
        if (starControls is not null) starControls.Alpha = settings.Kind is not (TrainerKind.Reaction or TrainerKind.Spinner)
            && practiceIntent == PracticeIntent.Quick ? 1 : 0;
    }
}
