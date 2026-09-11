using osu.Framework.Graphics;

namespace AimMod.Desktop.Trainers;

public partial class NativeTrainersWorkspace
{
    private Drawable? dtWorkspace;
    private void openDtTrainer()
    {
        if (DtTrainerFactory is null) return;
        Suspend();
        if (dtWorkspace is null)
        {
            dtWorkspace = DtTrainerFactory(showSkillTrainers);
            modPage.Add(dtWorkspace);
        }
        skillPage.Hide(); modPage.Show();
        dtEntry.SetSelected(true); skillEntry.SetSelected(false);
    }

    private void showSkillTrainers()
    {
        modPage.Hide(); skillPage.Show();
        dtEntry.SetSelected(false); skillEntry.SetSelected(true);
    }
}
