using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics;
using AimMod.Desktop.Visuals;

namespace AimMod.Desktop.Trainers;

public partial class NativeTrainersWorkspace
{
    private AimModButton adaptiveToggle = null!;
    private osu.Game.Graphics.Containers.OsuTextFlowContainer adaptiveSummary = null!;

    private TrainerSettings adaptiveSettings(TrainerSettings selected, IEnumerable<TrainerResult>? runs = null) =>
        TrainerAdaptiveDifficulty.Apply(selected with { AdaptiveDifficulty = preferences.AdaptiveDifficulty },
            runs ?? history().Load(), SkillEvidenceAccountId is > 0 && SkillEvidenceAccountId == CurrentSkillAccountId?.Invoke()
                ? SkillEvidence : [], DateTimeOffset.UtcNow);

    private void buildAdaptiveControls(FillFlowContainer<Drawable> body)
    {
        body.Add(adaptiveToggle = new AimModButton("", () => {
            preferences = preferences with { AdaptiveDifficulty = !preferences.AdaptiveDifficulty };
            settings = settings with { AdaptiveDifficulty = preferences.AdaptiveDifficulty };
            saveTrainerPreferences(); updateInstruction(); refreshHistory();
        }));
        body.Add(adaptiveSummary = paragraph(""));
        refreshAdaptiveSummary();
    }

    private void refreshAdaptiveSummary()
    {
        if (adaptiveSummary is null) return;
        adaptiveToggle.SetCaption(preferences.AdaptiveDifficulty ? "Adaptive session: On" : "Manual session: On");
        adaptiveToggle.SetSelected(preferences.AdaptiveDifficulty);
        adaptiveSummary.Text = preferences.AdaptiveDifficulty ? TrainerAdaptiveDifficulty.Describe(adaptiveSettings(settings))
            : settings.RandomizePatterns ? "Choose your exact settings below. The skill randomizer is on; turn it off to keep the exact pattern."
            : "Choose your tempo, patterns, sliders and difficulty below. Fixed drills keep your chosen rhythm over the song.";
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
