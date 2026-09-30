using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Replays;

/// <summary>Title row above the viewport: the selected map and attempt, plus page actions.</summary>
public partial class ReplayNowPlayingBar : Container
{
    public const float BarHeight = 44;

    private readonly AimModDifficultyPill difficulty;
    private readonly TruncatingSpriteText title;
    private readonly TruncatingSpriteText detail;
    private readonly FillFlowContainer stats;
    private readonly FillFlowContainer actions;
    private float leftWidth = -1;

    internal string TitleText => title.Text.ToString();

    public ReplayNowPlayingBar()
    {
        RelativeSizeAxes = Axes.X;
        Height = BarHeight;
        Children = new Drawable[]
        {
            difficulty = new AimModDifficultyPill(0)
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Alpha = 0,
            },
            title = new TruncatingSpriteText
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopLeft,
                Y = 3,
                Text = "No replay selected",
                Font = new FontUsage(size: 16, weight: "Bold"),
                Colour = AimModPalette.Text,
            },
            detail = new TruncatingSpriteText
            {
                Y = 25,
                Text = "Choose an attempt from the library to watch it here.",
                Font = AimModVisualStyle.CaptionFont,
                Colour = AimModPalette.Muted,
            },
            new FillFlowContainer
            {
                Anchor = Anchor.CentreRight,
                Origin = Anchor.CentreRight,
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Horizontal,
                Spacing = new(AimModVisualStyle.RelatedSpacing, 0),
                Children = new Drawable[]
                {
                    stats = new FillFlowContainer
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Horizontal,
                        Spacing = new(6, 0),
                    },
                    actions = new FillFlowContainer
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Horizontal,
                        Spacing = new(AimModVisualStyle.RelatedSpacing, 0),
                    },
                },
            },
        };
    }

    public void AddAction(Drawable action)
    {
        action.Anchor = Anchor.CentreLeft;
        action.Origin = Anchor.CentreLeft;
        actions.Add(action);
    }

    public void SetReplay(LocalReplay? replay)
    {
        stats.Clear();
        if (replay is null)
        {
            difficulty.Alpha = 0;
            title.Text = "No replay selected";
            detail.Text = "Choose an attempt from the library to watch it here.";
            leftWidth = -1;
            return;
        }

        difficulty.StarRating = replay.StarRating;
        difficulty.Alpha = 1;
        title.Text = replay.Title;
        detail.Text = $"{replay.Artist}  ·  {replay.Difficulty}  ·  {ScoreMods.Display(replay)}  ·  {replay.Player}  ·  {NativeReplayRouteView.FormatPlayedAt(replay.PlayedAt)}";
        stats.Add(new StatChip(double.IsFinite(replay.Accuracy) ? $"{replay.Accuracy * 100:0.00}%" : "--", AimModPalette.Text));
        stats.Add(new StatChip(replay.MissCount == 0 ? "No misses" : $"{replay.MissCount:N0} {(replay.MissCount == 1 ? "miss" : "misses")}",
            replay.MissCount == 0 ? AimModPalette.Success : AimModPalette.Pink));
        if (replay.PerformancePoints is { } pp)
            stats.Add(new StatChip($"{pp:0.#}pp", AimModPalette.Cyan));
        leftWidth = -1;
    }

    public void SetPerformance(double? pp)
    {
        if (pp is not { } value || stats.Count == 0)
            return;
        if (stats.Count >= 3)
            stats.Remove(stats[2], true);
        stats.Add(new StatChip($"{value:0.#}pp", AimModPalette.Cyan));
    }

    protected override void Update()
    {
        base.Update();
        float pill = difficulty.Alpha > 0 ? difficulty.DrawWidth + 10 : 0;
        float right = stats.DrawWidth + actions.DrawWidth + 24;
        float available = Math.Max(80, DrawWidth - pill - right);
        if (Math.Abs(available - leftWidth) < 0.5f)
            return;
        leftWidth = available;
        title.X = detail.X = pill;
        title.MaxWidth = detail.MaxWidth = available;
    }

    private partial class StatChip : CompositeDrawable
    {
        public StatChip(string text, Colour4 colour)
        {
            AutoSizeAxes = Axes.Both;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                new OsuSpriteText
                {
                    Margin = new MarginPadding { Horizontal = 10, Vertical = 6 },
                    Text = text,
                    Font = new FontUsage(size: 13, weight: "Bold"),
                    Colour = colour,
                },
            };
        }
    }
}
