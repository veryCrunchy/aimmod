using AimMod.Desktop.Trainers;
using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Home;

internal partial class NativeHomeDashboard
{
    /// <summary>What the player plays most, and where their accuracy is above or below their own average.</summary>
    internal partial class HomeSkillsetPanel : HomePanel
    {
        private const float name_width = 190, share_width = 170, accuracy_width = 240;
        private readonly HomeDashboardActions actions;
        private readonly OsuSpriteText detail;

        public HomeSkillsetPanel(HomeDashboardActions actions)
            : base("Your skillsets")
        {
            this.actions = actions;
            RelativeSizeAxes = Axes.X;
            HeaderActions.Add(detail = Text(string.Empty, AimModVisualStyle.CaptionFont, AimModPalette.Muted).With(t => { t.Anchor = Anchor.CentreRight; t.Origin = Anchor.CentreRight; }));
        }

        public HomeSkillsets? Shown { get; private set; }

        public void SetData(HomeSkillsets? skillsets)
        {
            Shown = skillsets;
            Clear();
            if (skillsets is null)
            {
                detail.Text = string.Empty;
                Add(paragraph("Your skillsets appear once a few of your recent replays have been analysed. They compare your accuracy on jumps, streams, bursts and sharp angles with your average."));
                return;
            }
            detail.Text = $"last {skillsets.Days} days · {skillsets.Plays:N0} analysed {(skillsets.Plays == 1 ? "play" : "plays")}";

            var tiles = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(AimModVisualStyle.RelatedSpacing) };
            if (skillsets.HasComparison && skillsets.Strongest is { } best)
                tiles.Add(new AimModTrainerKpi("STRONGEST", best.Label, versus(best), best.AccuracyVsAverage, AimModTrainerTrend.HigherIsBetter,
                    tooltip: "Accuracy on these notes compared with your accuracy on every note of the same plays."));
            if (skillsets.HasComparison && skillsets.Weakest is { } worst)
                tiles.Add(new AimModTrainerKpi("NEEDS WORK", worst.Label, versus(worst), worst.AccuracyVsAverage, AimModTrainerTrend.HigherIsBetter,
                    tooltip: "Accuracy on these notes compared with your accuracy on every note of the same plays."));
            if (skillsets.MostPlayed is { } most)
                tiles.Add(new AimModTrainerKpi("YOU PLAY MOST", most.Label, $"{most.PlayShare:P0} of your notes"));
            if (skillsets.Accuracy is { } accuracy)
                tiles.Add(new AimModTrainerKpi("YOUR AVERAGE", $"{accuracy:P1}", skillsets.UnstableRate is { } ur ? $"UR {ur:0}" : "accuracy on all notes"));
            Add(tiles);

            Add(row("PATTERN", "HOW MUCH YOU PLAY IT", "ACCURACY VS YOUR AVERAGE"));
            foreach (var skill in skillsets.Skills.OrderByDescending(s => s.PlayShare))
                Add(skillRow(skill));
            if (!skillsets.HasComparison)
                Add(paragraph("Play and analyse a few more maps with different patterns to see which ones you are strongest and weakest at."));

            var buttons = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(AimModVisualStyle.RelatedSpacing) };
            if (skillsets.HasComparison && skillsets.Weakest is { } weakest && actions.OpenTrainer is { } open)
                buttons.Add(new AimModButton($"Train {weakest.Label.ToLowerInvariant()}", () => open(weakest.Trainer), primary: true) { Height = AimModVisualStyle.CompactControlHeight });
            buttons.Add(new AimModButton("See statistics", actions.ShowStatistics) { Height = AimModVisualStyle.CompactControlHeight });
            Add(buttons);
        }

        internal static string versus(HomeSkill skill) => skill.AccuracyVsAverage is { } gap
            ? Math.Abs(gap) < .05 ? "same as your average" : $"{gap:+0.0;-0.0} points vs your average"
            : "not enough plays";

        private static Drawable skillRow(HomeSkill skill)
        {
            var name = new FillFlowContainer { Width = name_width, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(2),
                Children = [Text(skill.Label, AimModVisualStyle.BodyStrongFont, AimModPalette.Text), Text(skill.Description, AimModVisualStyle.CaptionFont, AimModPalette.Muted)] };
            var share = new FillFlowContainer { Width = share_width, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(4),
                Children =
                [
                    Text($"{skill.PlayShare:P0} of your notes", AimModVisualStyle.BodyFont, AimModPalette.Text),
                    new Container { Width = share_width - 24, Height = 6, Masking = true, CornerRadius = 3, Children =
                    [
                        new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                        new Box { RelativeSizeAxes = Axes.Both, Width = (float)Math.Clamp(skill.PlayShare, 0, 1), Colour = AimModPalette.Cyan },
                    ] },
                ] };
            Drawable accuracy;
            if (skill.Enough && skill.Accuracy is { } value)
            {
                Colour4 tone = skill.AccuracyVsAverage switch { >= .5 => AimModPalette.Success, <= -.5 => AimModPalette.Yellow, _ => AimModPalette.Muted };
                string misses = skill.MissesPer100 is { } m ? $"{m:0.0} misses per 100 notes" : "misses unknown";
                accuracy = new FillFlowContainer { Width = accuracy_width, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(2),
                    Children =
                    [
                        Text($"{value:P1}", AimModVisualStyle.BodyStrongFont, AimModPalette.Text),
                        Text(versus(skill), AimModVisualStyle.CaptionStrongFont, tone),
                        Text(skill.UnstableRate is { } ur ? $"{misses} · UR {ur:0}" : misses, AimModVisualStyle.CaptionFont, AimModPalette.Muted),
                    ] };
            }
            else accuracy = cell($"Not enough plays yet ({skill.Plays})", accuracy_width, AimModPalette.Muted);
            return new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(16, 6),
                Padding = new MarginPadding { Vertical = 4 },
                Children = [name, share, accuracy],
            };
        }

        private static Drawable row(params string[] cells)
        {
            float[] widths = [name_width, share_width, accuracy_width];
            var flow = new FillFlowContainer { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(16, 4) };
            for (int i = 0; i < cells.Length; i++)
                flow.Add(cell(cells[i], widths[i], AimModPalette.Muted, AimModVisualStyle.CaptionStrongFont));
            return flow;
        }

        private static Drawable cell(string value, float width, Colour4? colour = null, FontUsage? font = null) => new Container
        {
            Width = width, AutoSizeAxes = Axes.Y,
            Child = Text(value, font ?? AimModVisualStyle.BodyFont, colour ?? AimModPalette.Text),
        };

        private static Drawable paragraph(string value) => new osu.Game.Graphics.Containers.OsuTextFlowContainer(t =>
        {
            t.Font = AimModVisualStyle.BodyFont;
            t.Colour = AimModPalette.Muted;
        }) { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = value };
    }
}
