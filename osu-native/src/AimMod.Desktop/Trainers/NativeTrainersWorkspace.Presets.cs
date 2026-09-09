using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Game.Graphics.Containers;

namespace AimMod.Desktop.Trainers;

public partial class NativeTrainersWorkspace
{
    private FillFlowContainer<Drawable> presetChoices = null!;
    private OsuTextFlowContainer presetSummary = null!;
    private readonly List<(TrainerPreset Preset, AimModButton Button)> presetButtons = [];

    private void buildPresetControls(FillFlowContainer<Drawable> body)
    {
        body.Add(text("2. Choose a drill", 16, AimModPalette.Text));
        body.Add(presetChoices = flow());
        body.Add(presetSummary = paragraph(""));
        rebuildPresets();
    }

    private void rebuildPresets()
    {
        if (presetChoices is null) return;
        presetChoices.Clear();
        presetButtons.Clear();
        foreach (var preset in TrainerPresets.For(settings.Kind))
        {
            var button = new AimModButton(preset.Title, () => ApplyPreset(preset));
            button.AutoSizeAxes = Axes.None;
            button.Width = 220;
            var description = new OsuTextFlowContainer(t => { t.Font = new(size: 12); t.Colour = AimModPalette.Muted; })
                { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = preset.Description };
            var content = column(); content.Padding = new MarginPadding(12); content.Spacing = new(6);
            content.Add(choiceText(preset.Title, 14));
            content.Add(new TrainerDrillPreview(preset.Apply(settings), tempo: () => settings.Bpm) { RelativeSizeAxes = Axes.X, Height = 48 });
            content.Add(description);
            button.SetVisualContent(content, 108);
            presetChoices.Add(button);
            presetButtons.Add((preset, button));
        }
        refreshPresetSummary();
    }

    internal void ApplyPreset(TrainerPreset preset)
    {
        if (running || preparing) return;
        TrainerSettings selected = preset.Apply(settings);
        selected.Validate();
        restorePatternControls(selected);
        durationSelector.Current.Value = selected.Seconds;
        aimStyleSelector.Current.Value = selected.AimStyle;
        aimSpacingSelector.Current.Value = selected.AimSpacing;
        circleSizeSelector.Current.Value = selected.CircleSize;
        settings = selected;
        preferences = preferences with { RandomizePatterns = false };
        saveTrainerPreferences();
        refreshPatternToggle();
        updateInstruction();
        refreshHistory();
    }

    private void refreshPresetSummary()
    {
        if (presetSummary is null) return;
        foreach (var (preset, button) in presetButtons) button.SetSelected(preset.Matches(settings));
        string title = presetButtons.FirstOrDefault(p => p.Preset.Matches(settings)).Preset?.Title ?? "Custom drill";
        presetSummary.Alpha = title == "Custom drill" ? 1 : 0;
        presetSummary.Text = settings.Kind == TrainerKind.Reaction ? $"{title} · {settings.Seconds} seconds"
            : $"{title} · {settings.Seconds} seconds · {settings.TempoDescription}";
    }
}
