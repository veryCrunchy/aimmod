using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Trainers;

public partial class NativeTrainersWorkspace
{
    private FillFlowContainer<Drawable> patternControls = null!, geometryControls = null!, reactionControls = null!;
    private AimModDropdown<TrainerPattern> patternSelector = null!;
    private AimModDropdown<TrainerNoteSpeed> noteSpeedSelector = null!;
    private AimModDropdown<TrainerSliderStyle> sliderSelector = null!;
    private AimModDropdown<int> sliderLengthSelector = null!, approachSelector = null!;
    private AimModDropdown<TrainerPathStyle> pathSelector = null!;
    private AimModDropdown<TrainerReactionDelay> reactionDelaySelector = null!;
    private AimModButton randomizeToggle = null!;
    private Drawable pathControl = null!;
    private TrainerWorkspacePreferences preferences = new();
    public IReadOnlyList<TrainerSkillEvidence> SkillEvidence { get; set; } = [];
    public int? SkillEvidenceAccountId { get; set; }
    public Func<int?>? CurrentSkillAccountId { get; set; }
    private osu.Game.Graphics.Containers.OsuTextFlowContainer skillSummary = null!;
    private TrainerSkillLimits currentSkillLimits() => TrainerSkillProfile.Build(settings.Kind,history().Load(),
        SkillEvidenceAccountId is > 0 && SkillEvidenceAccountId==CurrentSkillAccountId?.Invoke() ? SkillEvidence : [],DateTimeOffset.UtcNow);
    private void refreshSkillSummary()
    {
        if(skillSummary is null) return;
        skillSummary.Alpha=settings.RandomizePatterns && settings.Kind!=TrainerKind.Reaction ? 1:0;
        if(skillSummary.Alpha==0) return;
        var limits=currentSkillLimits();
        string focus=settings.Kind is TrainerKind.Aim or TrainerKind.Reading ? $"{limits.MaxJumpDistance:0} px jumps"
            : settings.Kind==TrainerKind.Bursts ? $"up to {limits.MaxBurst}-note bursts"
            : settings.Kind is TrainerKind.Alternating or TrainerKind.Rhythm ? $"up to {limits.MaxChain}-note groups" : "steady pace";
        skillSummary.Text=$"Skill randomizer · up to {limits.MaxNps:0.#} taps/s · {focus} · AR {limits.MaxApproachRate} max. "
            +(limits.EvidenceCount==0 ? "Starting gently; clean runs build your level." : $"Matched to {limits.EvidenceCount} recent plays. Clean runs raise the challenge gradually.");
    }
    private void saveTrainerPreferences() { try { history().SavePreferences(preferences); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private void buildPatternControls(FillFlowContainer<Drawable> body)
    {
        patternControls = flow(); patternControls.Depth = -9.7f;
        patternControls.Add(selector("PATTERN", TrainerPatterns.Choices(settings.Kind), settings.Pattern,
            v => { settings = settings with { Pattern = v }; updateInstruction(); refreshHistory(); }, 230, d => patternSelector = d));
        ((TrainerDropdown<TrainerPattern>)patternSelector).Label = v => TrainerPatterns.Choices(settings.Kind).FirstOrDefault(p => p.Value == v).Key ?? "Choose a pattern";
        patternControls.Add(selector("NOTES PER BEAT", new Dictionary<string, TrainerNoteSpeed> {
            ["Default for this drill"] = TrainerNoteSpeed.Default, ["1 per beat"] = TrainerNoteSpeed.OnePerBeat,
            ["2 per beat"] = TrainerNoteSpeed.TwoPerBeat, ["4 per beat"] = TrainerNoteSpeed.FourPerBeat },
            settings.NoteSpeed, v => { settings = settings with { NoteSpeed = v }; refreshHistory(); }, 180, d => noteSpeedSelector = d));
        sliderControl = selector("SLIDERS", new Dictionary<string, TrainerSliderStyle> { ["No sliders"] = TrainerSliderStyle.None,
            ["Circles + sliders"] = TrainerSliderStyle.Mixed, ["Slider focus"] = TrainerSliderStyle.SlidersOnly, ["Back and forth"] = TrainerSliderStyle.BackAndForth },
            settings.Sliders, v => { settings = settings with { Sliders = v }; refreshObjectAndGuideControls(); refreshHistory(); }, 185, d => sliderSelector = d);
        sliderLengthControl = selector("SLIDER LENGTH", new[] {1,2,4}.Select(v => new KeyValuePair<string,int>($"{v} beat{(v==1?"":"s")}",v)),
            1, v => { settings = settings with { SliderBeats = v }; refreshHistory(); }, 140, d => sliderLengthSelector = d);
        randomizeToggle = new AimModButton("", () => {
            settings = settings with { RandomizePatterns = !settings.RandomizePatterns };
            preferences = preferences with { RandomizePatterns = settings.RandomizePatterns }; saveTrainerPreferences(); refreshPatternToggle(); refreshHistory();
        }) { Margin = new MarginPadding { Top = selectorLabelSpacing } };
        patternControls.Add(randomizeToggle); refreshPatternToggle();
        body.Add(patternControls);
        body.Add(skillSummary=paragraph(""));
        geometryControls = flow(); geometryControls.Depth = -9.4f;
        geometryControls.Add(pathControl = selector("MOVEMENT", new Dictionary<string, TrainerPathStyle> { ["Figure eight"] = TrainerPathStyle.FigureEight,
            ["Curved streams"] = TrainerPathStyle.Arc, ["Zigzags"] = TrainerPathStyle.Zigzag, ["Scattered"] = TrainerPathStyle.Random },
            settings.PathStyle, v => { settings = settings with { PathStyle = v }; refreshHistory(); }, 170, d => pathSelector = d));
        geometryControls.Add(selector("APPROACH RATE", Enumerable.Range(3,8).Select(v => new KeyValuePair<string,int>($"AR {v}",v)),
            7, v => { settings = settings with { ApproachRate = v }; refreshHistory(); }, 150, d => approachSelector = d));
        body.Add(geometryControls);
        reactionControls = flow(); reactionControls.Depth = -9.4f;
        reactionControls.Add(selector("CUE DELAY", new Dictionary<string, TrainerReactionDelay> {
            ["Standard · 1.3–3.2 s"] = TrainerReactionDelay.Standard, ["Short · 0.7–1.6 s"] = TrainerReactionDelay.Short,
            ["Long · 2.5–5 s"] = TrainerReactionDelay.Long, ["Unpredictable · 0.7–5 s"] = TrainerReactionDelay.Wide },
            settings.ReactionDelay, v => { settings = settings with { ReactionDelay = v }; refreshHistory(); }, 250, d => reactionDelaySelector = d));
        body.Add(reactionControls);
        buildSpecializedControls(body);
    }
    private void refreshPatternToggle()
    {
        randomizeToggle.SetCaption($"Skill randomizer: {(settings.RandomizePatterns ? "On" : "Off")}");
        randomizeToggle.SetSelected(settings.RandomizePatterns);
        refreshSkillSummary();
    }
    private void restorePatternControls(TrainerSettings s)
    {
        patternSelector.Current.Value = s.Pattern; noteSpeedSelector.Current.Value = s.NoteSpeed;
        sliderSelector.Current.Value = s.Sliders; sliderLengthSelector.Current.Value = s.SliderBeats;
        pathSelector.Current.Value = s.PathStyle; approachSelector.Current.Value = s.ApproachRate;
        reactionDelaySelector.Current.Value = s.ReactionDelay;
        readingComplexitySelector.Current.Value = s.ReadingComplexity; readingLengthSelector.Current.Value = s.ReadingGroupSize; readingHiddenSelector.Current.Value = s.ReadingHidden;
        reactionModeSelector.Current.Value = s.ReactionMode; reactionWindowSelector.Current.Value = s.ReactionWindowMs;
        settings = settings with { Spinners = s.Spinners, SpinnerSeconds = s.SpinnerSeconds, GuidedCues = s.GuidedCues };
        refreshObjectAndGuideControls();
        settings = settings with { RandomizePatterns = s.RandomizePatterns }; refreshPatternToggle();
    }
}
