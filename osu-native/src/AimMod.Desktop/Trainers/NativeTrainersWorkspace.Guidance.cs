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
    private AimModDropdown<TrainerSliderShape> sliderShapeSelector = null!;
    private AimModDropdown<TrainerSpinnerPattern> spinnerPatternSelector = null!;
    private Drawable sliderShapeControl = null!, spinnerPatternControl = null!;
    private AimModTrainerSwitch guideToggle = null!;

    private void buildObjectAndGuideControls(FillFlowContainer<Drawable> body, FillFlowContainer<Drawable> card)
    {
        objectControls = flow(); objectControls.Depth = -9.1f;
        objectControls.Add(sliderControl); objectControls.Add(sliderLengthControl);
        objectControls.Add(sliderShapeControl = selector("SLIDER SHAPE", new Dictionary<string, TrainerSliderShape> {
            ["Straight"] = TrainerSliderShape.Straight, ["Curved"] = TrainerSliderShape.Arc,
            ["S-curves"] = TrainerSliderShape.SCurve, ["Angular"] = TrainerSliderShape.Angular, ["Mixed shapes"] = TrainerSliderShape.Mixed },
            settings.SliderShape, shape => { settings = settings with { SliderShape = shape }; refreshHistory(); }, 180, d => sliderShapeSelector = d));
        objectControls.Add(spinnerFrequencyControl = selector("SPINNERS", new Dictionary<string, TrainerSpinnerFrequency>
        {
            ["No spinners"] = TrainerSpinnerFrequency.None,
            ["Occasional spinners"] = TrainerSpinnerFrequency.Occasional
        }, settings.Spinners, v => { settings = settings with { Spinners = v }; refreshObjectAndGuideControls(); refreshHistory(); }, 190, d => spinnerSelector = d));
        objectControls.Add(spinnerDurationControl = selector("SPIN DURATION", new[] { 2, 4, 6 }.Select(n => new KeyValuePair<string, int>($"{n} seconds", n)),
            settings.SpinnerSeconds, v => { settings = settings with { SpinnerSeconds = v }; refreshHistory(); }, 150, d => spinnerLengthSelector = d));
        body.Add(objectControls);
        objectControls.Add(spinnerPatternControl = selector("SPIN PATTERN", new Dictionary<string, TrainerSpinnerPattern> {
            ["Even duration"] = TrainerSpinnerPattern.Steady, ["Build duration"] = TrainerSpinnerPattern.BuildUp,
            ["Mixed lengths"] = TrainerSpinnerPattern.MixedLengths }, settings.SpinnerPattern,
            pattern => { settings = settings with { SpinnerPattern = pattern }; refreshHistory(); }, 180, d => spinnerPatternSelector = d));
        card.Add(guideToggle = new AimModTrainerSwitch("Practice guide", "", () =>
        {
            settings = settings with { GuidedCues = !settings.GuidedCues };
            preferences = preferences with { GuidedCues = settings.GuidedCues };
            saveTrainerPreferences(); refreshObjectAndGuideControls(); refreshHistory();
        }));
        refreshObjectAndGuideControls();
    }

    private void refreshObjectAndGuideControls()
    {
        if (guideToggle is null) return;
        guideToggle.SetValue(settings.GuidedCues);
        guideToggle.Hint = settings.Kind == TrainerKind.Spinner ? "Circle example and live RPM, then no prompts"
            : settings.Kind == TrainerKind.Reaction ? "A reminder after each response"
            : "Short cues while you play, then a final part without prompts";
        objectControls.Alpha = settings.Kind == TrainerKind.Reaction ? 0 : 1;
        sliderControl.Alpha = sliderLengthControl.Alpha = spinnerFrequencyControl.Alpha = settings.Kind == TrainerKind.Spinner ? 0 : 1;
        sliderLengthControl.Alpha = settings.Sliders != TrainerSliderStyle.None && settings.Kind != TrainerKind.Spinner ? 1 : 0;
        spinnerDurationControl.Alpha = settings.Spinners != TrainerSpinnerFrequency.None || settings.Kind == TrainerKind.Spinner ? 1 : 0;
        sliderShapeControl.Alpha = sliderLengthControl.Alpha;
        spinnerPatternControl.Alpha = spinnerDurationControl.Alpha;
        spinnerSelector.Current.Value = settings.Spinners;
        spinnerLengthSelector.Current.Value = settings.SpinnerSeconds;
        refreshAdaptiveVisibility();
    }
}
