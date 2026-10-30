using osu.Framework.Graphics;

namespace AimMod.Desktop.Trainers;

public partial class NativeTrainersWorkspace
{
    private Drawable? dtWorkspace;
    private void openDtTrainer()
    {
        if (preparing || sessionPending) return;
        if (DtTrainerFactory is null) return;
        Suspend();
        if (dtWorkspace is null)
        {
            dtWorkspace = DtTrainerFactory(showSkillTrainers);
            modPage.Add(dtWorkspace);
        }
        skillPage.Hide(); modPage.Show();
        warmupPage.Hide(); warmupEntry.SetSelected(false); hideLadder();
        dtEntry.SetSelected(true); skillEntry.SetSelected(false);
    }

    /// <summary>Opens the skill trainers on one exercise, for links from elsewhere in the app.</summary>
    public void ShowExercise(TrainerKind kind)
    {
        if (preparing || sessionPending || running) return;
        showSkillTrainers();
        SelectTrainer(kind);
    }

    private void showSkillTrainers()
    {
        if (preparing || sessionPending) return;
        warmupPage.Hide(); warmupEntry.SetSelected(false); hideLadder();
        modPage.Hide(); skillPage.Show();
        dtEntry.SetSelected(false); skillEntry.SetSelected(true);
    }
}
