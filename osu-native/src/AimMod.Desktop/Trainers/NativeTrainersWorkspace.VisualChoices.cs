using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Containers;

namespace AimMod.Desktop.Trainers;

public partial class NativeTrainersWorkspace
{
    private static OsuTextFlowContainer choiceText(string value, float size = 12) => new(t =>
        { t.Font = new(size: size); t.Colour = AimModPalette.Text; })
        { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = value };

    private AimModButton skillChoice(TrainerKind kind)
    {
        var button = new AimModButton(DisplayName(kind), () => SelectTrainer(kind)) { AutoSizeAxes = Axes.None, Width = 130 };
        var content = column(); content.Padding = new MarginPadding(10); content.Spacing = new(4);
        content.Add(new TrainerDrillPreview(new TrainerSettings(Kind: kind), true) { RelativeSizeAxes = Axes.X, Height = 32 });
        content.Add(choiceText(DisplayName(kind), 12));
        button.SetVisualContent(content, 78);
        return button;
    }

    private static Container modeContent(string caption, string description, IconUsage icon)
    {
        var labels = column(); labels.Padding = new MarginPadding { Left = 44, Top = 9, Right = 8 }; labels.Spacing = new(3);
        labels.Add(choiceText(caption, 12));
        var hint = choiceText(description, 10); hint.Colour = AimModPalette.Muted; labels.Add(hint);
        return new Container { RelativeSizeAxes = Axes.Both, Children = [
            new SpriteIcon { Icon = icon, Size = new(20), Position = new(13, 18), Colour = AimModPalette.Accent }, labels] };
    }
}
