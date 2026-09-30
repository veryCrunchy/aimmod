using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics;
using AimMod.Desktop.Visuals;

namespace AimMod.Desktop.Trainers;

public partial class NativeTrainersWorkspace
{
    private AimModTrainerSwitch adaptiveToggle = null!;

    private TrainerSettings adaptiveSettings(TrainerSettings selected, IEnumerable<TrainerResult>? runs = null) =>
        TrainerAdaptiveDifficulty.Apply(selected with { AdaptiveDifficulty = preferences.AdaptiveDifficulty },
            runs ?? history().Load(), SkillEvidenceAccountId is > 0 && SkillEvidenceAccountId == CurrentSkillAccountId?.Invoke()
                ? SkillEvidence : [], DateTimeOffset.UtcNow);

    private void buildAdaptiveControls(FillFlowContainer<Drawable> body)
    {
        body.Add(adaptiveToggle = new AimModTrainerSwitch("Adaptive difficulty", "Recent runs set tempo, load and objects", () => {
            preferences = preferences with { AdaptiveDifficulty = !preferences.AdaptiveDifficulty };
            settings = settings with { AdaptiveDifficulty = preferences.AdaptiveDifficulty };
            saveTrainerPreferences(); updateInstruction(); refreshHistory();
        }));
        adaptiveToggle.TooltipText = "Turn off to choose an exact tempo, pattern and difficulty.";
        refreshAdaptiveSummary();
    }

    private void refreshAdaptiveSummary()
    {
        if (adaptiveToggle is null) return;
        adaptiveToggle.SetValue(preferences.AdaptiveDifficulty);
        adaptiveToggle.Hint = preferences.AdaptiveDifficulty ? "Recent runs set tempo, load and objects" : "Off · you choose tempo, patterns and difficulty";
        refreshSessionPlan();
        refreshAdaptiveVisibility();
    }

    private void refreshAdaptiveVisibility()
    {
        refreshStarVisibility();
        bool automatic = preferences.AdaptiveDifficulty;
        if (timingControls is not null) timingControls.Alpha = !automatic && settings.Kind != TrainerKind.Reaction && settings.Music != "song" ? 1 : 0;
        if (objectControls is not null) objectControls.Alpha = !automatic && settings.Kind != TrainerKind.Reaction ? 1 : 0;
        if (readingControls is not null) readingControls.Alpha = !automatic && settings.Kind == TrainerKind.Reading ? 1 : 0;
        if (practiceOptionsToggle is not null) practiceOptionsToggle.Alpha = !automatic && settings.Kind != TrainerKind.Spinner ? 1 : 0;
        if (automatic && practiceOptions is not null) practiceOptions.Hide();
    }
}
