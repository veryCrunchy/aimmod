using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Trainers;

public partial class NativeTrainersWorkspace
{
    private AimModTrainerSwitch autopilotToggle = null!;

    private void buildAutopilotControl(FillFlowContainer<Drawable> card)
    {
        card.Add(autopilotToggle = new AimModTrainerSwitch("Autopilot", "The cursor follows the notes · you only tap", () =>
        {
            settings = settings with { Autopilot = !settings.Autopilot };
            preferences = preferences with { Autopilot = settings.Autopilot };
            saveTrainerPreferences(); refreshAutopilotControl(); updateInstruction(); refreshHistory();
        }));
        autopilotToggle.TooltipText = "Uses osu!'s Autopilot mod. Autopilot runs are kept separate from your own-aim results and never shape skill levels or sync to your profile.";
        refreshAutopilotControl();
    }

    private void refreshAutopilotControl()
    {
        if (autopilotToggle is null) return;
        autopilotToggle.SetValue(settings.Autopilot);
        autopilotToggle.Alpha = TrainerAutopilot.Supports(settings.Kind) ? 1 : 0;
    }
}
