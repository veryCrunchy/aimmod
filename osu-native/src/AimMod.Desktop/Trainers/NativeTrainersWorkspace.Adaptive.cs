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
            saveTrainerPreferences(); refreshHistory();
        }));
        body.Add(adaptiveSummary = paragraph(""));
        refreshAdaptiveSummary();
    }

    private void refreshAdaptiveSummary()
    {
        if (adaptiveSummary is null) return;
        adaptiveToggle.SetCaption(preferences.AdaptiveDifficulty ? "Difficulty: Adaptive" : "Difficulty: Manual");
        adaptiveToggle.SetSelected(preferences.AdaptiveDifficulty);
        adaptiveSummary.Text = preferences.AdaptiveDifficulty ? TrainerAdaptiveDifficulty.Describe(adaptiveSettings(settings))
            : settings.RandomizePatterns ? "Your skill randomizer still limits the generated patterns. Turn it off for fully manual difficulty."
            : "Your selected difficulty stays fixed. Turn on Adaptive to follow your recent practice.";
    }
}
