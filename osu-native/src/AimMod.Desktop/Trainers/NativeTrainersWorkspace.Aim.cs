using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Trainers;

public partial class NativeTrainersWorkspace
{
    private FillFlowContainer<Drawable> aimControls = null!;
    private AimMod.Desktop.Visuals.AimModDropdown<TrainerAimStyle> aimStyleSelector = null!;
    private AimMod.Desktop.Visuals.AimModDropdown<int> aimSpacingSelector = null!, circleSizeSelector = null!;
    private bool freshAimLayout = true;
    private Drawable aimStyleControl = null!;
    private void buildAimControls(FillFlowContainer<Drawable> body)
    {
        aimControls = flow(); aimControls.Depth = -9.5f;
        aimControls.Add(aimStyleControl = selector("AIM TYPE", new Dictionary<string, TrainerAimStyle>
        {
            ["Balanced jumps"] = TrainerAimStyle.Balanced, ["Wide jumps"] = TrainerAimStyle.WideJumps,
            ["Flowing aim"] = TrainerAimStyle.Flow, ["Small corrections"] = TrainerAimStyle.SmallCorrections,
            ["Direction changes"] = TrainerAimStyle.DirectionChanges,
        }, settings.AimStyle, style => { settings = settings with { AimStyle = style }; updateInstruction(); refreshHistory(); }, 205, d => aimStyleSelector = d));
        aimControls.Add(selector("SPACING", new[] { 70, 85, 100, 120, 140 }.Select(v => new KeyValuePair<string, int>($"{v}%", v)),
            100, v => { settings = settings with { AimSpacing = v }; refreshHistory(); }, 130, d => aimSpacingSelector = d));
        aimControls.Add(selector("CIRCLE SIZE", new[] { 3, 4, 5, 6 }.Select(v => new KeyValuePair<string, int>($"CS {v}", v)),
            4, v => { settings = settings with { CircleSize = v }; refreshHistory(); }, 130, d => circleSizeSelector = d));
        aimControls.Add(selector("LAYOUT", new Dictionary<string, bool> { ["Fresh each run"] = true, ["Keep this layout"] = false },
            freshAimLayout, v => { freshAimLayout = v; preferences = preferences with { FreshLayout = v }; saveTrainerPreferences(); }, 170));
        body.Add(aimControls);
    }
}
