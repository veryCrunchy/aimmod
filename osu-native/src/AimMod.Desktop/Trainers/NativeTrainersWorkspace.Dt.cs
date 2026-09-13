using osu.Framework.Graphics;

namespace AimMod.Desktop.Trainers;

public partial class NativeTrainersWorkspace
{
    private Drawable? dtWorkspace;
    private void openDtTrainer()
    {
        if (preparing || pendingWarmup is not null) return;
        if (DtTrainerFactory is null) return;
        Suspend();
        if (dtWorkspace is null)
        {
            dtWorkspace = DtTrainerFactory(showSkillTrainers);
            modPage.Add(dtWorkspace);
        }
        skillPage.Hide(); modPage.Show();
        warmupPage.Hide(); warmupEntry.SetSelected(false);
        dtEntry.SetSelected(true); skillEntry.SetSelected(false);
    }

    private void showSkillTrainers()
    {
        if (preparing || pendingWarmup is not null) return;
        warmupPage.Hide(); warmupEntry.SetSelected(false);
        modPage.Hide(); skillPage.Show();
        dtEntry.SetSelected(false); skillEntry.SetSelected(true);
    }
}
