using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Trainers;

public partial class NativeTrainersWorkspace
{
    private Drawable sliderControl = null!, sliderLengthControl = null!, spinnerFrequencyControl = null!, spinnerDurationControl = null!;
    private FillFlowContainer<Drawable> objectControls = null!;
    private AimModDropdown<TrainerSpinnerFrequency> spinnerSelector = null!;
    private AimModDropdown<int> spinnerLengthSelector = null!;
    private AimModButton guideToggle = null!;
    private osu.Game.Graphics.Containers.OsuTextFlowContainer guideDescription = null!;

    private void buildObjectAndGuideControls(FillFlowContainer<Drawable> body)
    {
        objectControls = flow(); objectControls.Depth = -9.1f;
        objectControls.Add(sliderControl); objectControls.Add(sliderLengthControl);
        objectControls.Add(spinnerFrequencyControl = selector("SPINNERS", new Dictionary<string, TrainerSpinnerFrequency>
        {
            ["No spinners"] = TrainerSpinnerFrequency.None,
            ["Occasional spinners"] = TrainerSpinnerFrequency.Occasional
        }, settings.Spinners, v => { settings = settings with { Spinners = v }; refreshObjectAndGuideControls(); refreshHistory(); }, 190, d => spinnerSelector = d));
        objectControls.Add(spinnerDurationControl = selector("SPIN DURATION", new[] { 2, 4, 6 }.Select(n => new KeyValuePair<string, int>($"{n} seconds", n)),
            settings.SpinnerSeconds, v => { settings = settings with { SpinnerSeconds = v }; refreshHistory(); }, 150, d => spinnerLengthSelector = d));
        body.Add(objectControls);
        body.Add(guideToggle = new AimModButton("", () =>
        {
            settings = settings with { GuidedCues = !settings.GuidedCues };
            preferences = preferences with { GuidedCues = settings.GuidedCues };
            saveTrainerPreferences(); refreshObjectAndGuideControls(); refreshHistory();
        }));
        body.Add(guideDescription = paragraph(""));
        refreshObjectAndGuideControls();
    }

    private void refreshObjectAndGuideControls()
    {
        if (guideToggle is null) return;
        guideToggle.SetCaption($"Practice guide: {(settings.GuidedCues ? "On" : "Off")}");
        guideToggle.SetSelected(settings.GuidedCues);
        guideDescription.Text = settings.GuidedCues
            ? settings.Kind == TrainerKind.Spinner ? "Follow the circle example, check your live RPM, then finish without prompts. Use a comfortable circle size."
                : settings.Kind == TrainerKind.Reaction ? "Get a reminder after each response. Cues still arrive at unpredictable times."
                : "Get short cues as you practise, then finish the last part without prompts."
            : "Turn on the practice guide for cues during the exercise.";
        objectControls.Alpha = settings.Kind == TrainerKind.Reaction ? 0 : 1;
        sliderControl.Alpha = sliderLengthControl.Alpha = spinnerFrequencyControl.Alpha = settings.Kind == TrainerKind.Spinner ? 0 : 1;
        sliderLengthControl.Alpha = settings.Sliders != TrainerSliderStyle.None && settings.Kind != TrainerKind.Spinner ? 1 : 0;
        spinnerDurationControl.Alpha = settings.Spinners != TrainerSpinnerFrequency.None || settings.Kind == TrainerKind.Spinner ? 1 : 0;
        spinnerSelector.Current.Value = settings.Spinners;
        spinnerLengthSelector.Current.Value = settings.SpinnerSeconds;
    }
}
