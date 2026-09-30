using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Game.Graphics.Sprites;
using osuTK;

namespace AimMod.Desktop.Trainers;

/// <summary>A small sparkline of the latest recorded runs that jumps to the full progress section.</summary>
public partial class TrainerRecentRunsLink : ClickableContainer, IHasTooltip
{
    private const float sparkWidth = 54, sparkHeight = 16;
    private readonly Container spark;
    private readonly OsuSpriteText label;
    private readonly SpriteIcon arrow;
    public LocalisableString TooltipText => "Show progress";

    public TrainerRecentRunsLink(Action open)
    {
        Action = open; AutoSizeAxes = Axes.Both; Alpha = 0;
        Child = new FillFlowContainer { AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(8), Children = [
            spark = new Container { Size = new(sparkWidth, sparkHeight), Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft },
            label = new OsuSpriteText { Font = new FontUsage(size: 12, weight: "SemiBold"), Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft },
            arrow = new SpriteIcon { Icon = FontAwesome.Solid.ChevronRight, Size = new(9), Colour = AimModPalette.Muted, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft },
        ] };
    }

    /// <param name="values">Oldest first. Missing values are skipped.</param>
    public void Show(double?[] values, string metric)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        spark.Clear();
        if (present.Length < 2) { Alpha = 0; return; }
        Alpha = 1;
        double min = present.Min(), max = present.Max(), change = present[^1] - present[0];
        Vector2 at(int i) => new(sparkWidth * i / (present.Length - 1), sparkHeight - 2 - (float)((present[i] - min) / Math.Max(1e-6, max - min)) * (sparkHeight - 4));
        for (int i = 1; i < present.Length; i++)
        {
            var delta = at(i) - at(i - 1);
            spark.Add(new Box { Position = at(i - 1), Origin = Anchor.CentreLeft, Width = delta.Length, Height = 1.5f, EdgeSmoothness = new(1),
                Rotation = MathF.Atan2(delta.Y, delta.X) * 180 / MathF.PI, Colour = AimModPalette.Accent.Opacity(.8f) });
        }
        spark.Add(new Circle { Position = at(present.Length - 1), Origin = Anchor.Centre, Size = new(5), Colour = AimModPalette.Accent });
        bool flat = Math.Abs(change) < .05;
        label.Text = $"Last {present.Length} runs · {(flat ? "steady" : $"{change:+0.0;-0.0}%")} {metric}";
        label.Colour = flat ? AimModPalette.Muted : change > 0 ? AimModPalette.Accent : AimModPalette.Yellow;
    }

    protected override bool OnHover(HoverEvent e) { arrow.FadeColour(AimModPalette.Text, 100); return true; }
    protected override void OnHoverLost(HoverLostEvent e) { arrow.FadeColour(AimModPalette.Muted, 100); base.OnHoverLost(e); }
}
