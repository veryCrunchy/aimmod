using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Coaching;

/// <summary>
/// A keyboard focus outline that draws above a row without changing its layout.
/// </summary>
internal partial class WorkspaceFocusRing : CompositeDrawable
{
    private const float thickness = 2;

    public WorkspaceFocusRing()
    {
        RelativeSizeAxes = Axes.Both;
        Alpha = 0;
        AlwaysPresent = false;
        InternalChildren = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.X, Height = thickness, Colour = AimModPalette.Cyan },
            new Box { RelativeSizeAxes = Axes.X, Height = thickness, Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, Colour = AimModPalette.Cyan },
            new Box { RelativeSizeAxes = Axes.Y, Width = thickness, Colour = AimModPalette.Cyan },
            new Box { RelativeSizeAxes = Axes.Y, Width = thickness, Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Colour = AimModPalette.Cyan },
        };
    }

    public void SetVisible(bool visible) => this.FadeTo(visible ? 1 : 0, AimModVisualStyle.FastTransition, Easing.OutQuint);
}

internal partial class WorkspaceButton : ClickableContainer
{
    private readonly Box background;
    private readonly Colour4 resting;
    private readonly Colour4 hover;
    private readonly Container content;

    public WorkspaceButton(string label, Action? action, Colour4? accent = null)
    {
        Colour4 colour = accent ?? AimModPalette.Pink;
        resting = action is null ? AimModPalette.PanelHover : colour.Darken(0.35f);
        hover = colour;
        Action = action;
        AutoSizeAxes = Axes.Both;
        Masking = true;
        CornerRadius = AimModVisualStyle.ControlRadius;
        Alpha = action is null ? 0.5f : 1;
        Children = new Drawable[]
        {
            background = new Box { RelativeSizeAxes = Axes.Both, Colour = resting },
            content = new Container
            {
                AutoSizeAxes = Axes.Both,
                Padding = new MarginPadding { Horizontal = 14, Vertical = 8 },
                Child = new OsuSpriteText
                {
                    Text = label,
                    Font = new FontUsage(size: 12, weight: "SemiBold"),
                    Colour = AimModPalette.Text,
                },
            },
        };
    }

    protected override bool OnHover(HoverEvent e)
    {
        if (Action is not null)
            background.FadeColour(hover, AimModVisualStyle.FastTransition);
        return Action is not null;
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        background.FadeColour(resting, AimModVisualStyle.HoverTransition);
        base.OnHoverLost(e);
    }

    protected override bool OnMouseDown(MouseDownEvent e)
    {
        if (Action is not null)
            content.ScaleTo(0.97f, 120, Easing.OutQuint);
        return Action is not null;
    }

    protected override void OnMouseUp(MouseUpEvent e)
    {
        content.ScaleTo(1, 250, Easing.OutElastic);
        base.OnMouseUp(e);
    }
}

/// <summary>
/// A thin progress strip that is either determinate or a looping indeterminate sweep.
/// </summary>
internal partial class WorkspaceProgressStrip : CompositeDrawable
{
    private readonly Box fill;
    private float? value;
    private bool sweeping;
    private bool sweepPending;

    public WorkspaceProgressStrip(float height = 4)
    {
        RelativeSizeAxes = Axes.X;
        Height = height;
        Masking = true;
        CornerRadius = height / 2;
        InternalChildren = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Border },
            fill = new Box { RelativeSizeAxes = Axes.Both, Width = 0, Colour = AimModPalette.Cyan },
        };
    }

    public Colour4 FillColour
    {
        get => fill.Colour;
        set => fill.Colour = value;
    }

    /// <summary>Sets a determinate fraction, or null for an indeterminate sweep.</summary>
    public void SetProgress(float? fraction)
    {
        if (fraction is { } determinate)
        {
            float clamped = Math.Clamp(determinate, 0, 1);
            if (!sweeping && value == clamped)
                return;

            sweeping = false;
            sweepPending = false;
            value = clamped;
            fill.ClearTransforms();
            fill.RelativePositionAxes = Axes.None;
            fill.X = 0;
            fill.ResizeWidthTo(clamped, 180, Easing.OutQuint);
            return;
        }

        if (sweeping || sweepPending)
            return;

        value = null;
        if (!IsLoaded)
        {
            sweepPending = true;
            return;
        }

        startSweep();
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        if (sweepPending && value is null)
            startSweep();
        sweepPending = false;
    }

    private void startSweep()
    {
        sweeping = true;
        fill.ClearTransforms();
        fill.Width = 0.28f;
        fill.RelativePositionAxes = Axes.X;
        fill.X = -0.28f;
        fill.MoveToX(1, 1_100, Easing.InOutSine).Then().MoveToX(-0.28f).Loop();
    }
}

/// <summary>
/// Shimmering placeholder blocks shown while a workspace model is still being prepared.
/// </summary>
internal partial class WorkspaceSkeleton : CompositeDrawable
{
    private readonly List<Drawable> bars = new();

    public WorkspaceSkeleton(int rows, float rowHeight = 56, float spacing = AimModVisualStyle.RelatedSpacing)
    {
        RelativeSizeAxes = Axes.X;
        AutoSizeAxes = Axes.Y;
        var flow = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(spacing),
        };
        for (int i = 0; i < rows; i++)
        {
            var bar = new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = rowHeight,
                Masking = true,
                CornerRadius = AimModVisualStyle.ControlRadius,
                Child = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                Alpha = 0.3f,
            };
            flow.Add(bar);
            bars.Add(bar);
        }

        InternalChild = flow;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        for (int i = 0; i < bars.Count; i++)
            bars[i].Delay(i * 90).FadeTo(0.6f, 760, Easing.InOutSine).Then().FadeTo(0.3f, 760, Easing.InOutSine).Loop();
    }
}

/// <summary>
/// A friendly full-width message with an icon, optional progress and an optional retry style action.
/// </summary>
internal partial class WorkspaceStateCard : CompositeDrawable
{
    private readonly Box accentBar;
    private readonly SpriteIcon icon;
    private readonly OsuSpriteText title;
    private readonly OsuTextFlowContainer detail;
    private readonly WorkspaceProgressStrip progress;
    private readonly Container actionHost;
    private readonly FillFlowContainer body;

    public WorkspaceStateCard()
    {
        RelativeSizeAxes = Axes.X;
        AutoSizeAxes = Axes.Y;
        Masking = true;
        CornerRadius = AimModVisualStyle.CardRadius;
        Alpha = 0;
        InternalChildren = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
            accentBar = new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = AimModPalette.Cyan },
            body = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new(6),
                Padding = new MarginPadding { Left = 52, Right = 20, Vertical = 16 },
                Children = new Drawable[]
                {
                    title = new OsuSpriteText
                    {
                        Font = new FontUsage(size: 15, weight: "Bold"),
                        Colour = AimModPalette.Text,
                    },
                    detail = new OsuTextFlowContainer(sprite =>
                    {
                        sprite.Font = new FontUsage(size: 12);
                        sprite.Colour = AimModPalette.Muted;
                    })
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                    },
                    progress = new WorkspaceProgressStrip { Alpha = 0, Margin = new MarginPadding { Top = 4 } },
                    actionHost = new Container { AutoSizeAxes = Axes.Both, Margin = new MarginPadding { Top = 6 } },
                },
            },
            icon = new SpriteIcon
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.Centre,
                Position = new(27, 29),
                Size = new(18),
                Colour = AimModPalette.Cyan,
            },
        };
    }

    public bool IsShown => Alpha > 0;

    public void Show(IconUsage stateIcon, string heading, string description, Colour4? accent = null, string? actionLabel = null, Action? action = null)
    {
        Colour4 colour = accent ?? AimModPalette.Cyan;
        icon.Icon = stateIcon;
        icon.Colour = colour;
        accentBar.Colour = colour;
        progress.FillColour = colour;
        title.Text = heading;
        detail.Text = description;
        actionHost.Clear();
        if (actionLabel is not null && action is not null)
            actionHost.Add(new WorkspaceButton(actionLabel, action, colour));

        this.FadeIn(AimModVisualStyle.HoverTransition, Easing.OutQuint);
    }

    public void SetProgress(float? fraction, bool visible = true)
    {
        progress.Alpha = visible ? 1 : 0;
        if (visible)
            progress.SetProgress(fraction);
    }

    public void Dismiss() => this.FadeOut(AimModVisualStyle.HoverTransition, Easing.OutQuint);
}

/// <summary>
/// Estimates the remaining time of a long running pass from its observed throughput.
/// </summary>
internal sealed class WorkspaceProgressEstimator
{
    private readonly Func<DateTimeOffset> clock;
    private DateTimeOffset? started;
    private int startCompleted;

    public WorkspaceProgressEstimator(Func<DateTimeOffset>? clock = null)
    {
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Reset()
    {
        started = null;
        startCompleted = 0;
    }

    public void Report(int completed)
    {
        if (started is null)
        {
            started = clock();
            startCompleted = Math.Max(0, completed);
        }
    }

    public TimeSpan? Remaining(int completed, int total)
    {
        if (started is not { } begin || total <= 0 || completed >= total)
            return null;

        int done = completed - startCompleted;
        TimeSpan elapsed = clock() - begin;
        if (done < 1 || elapsed < TimeSpan.FromSeconds(3))
            return null;

        return TimeSpan.FromTicks((long)(elapsed.Ticks / (double)done * (total - completed)));
    }

    public static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining < TimeSpan.FromSeconds(45))
            return "less than a minute remaining";
        if (remaining < TimeSpan.FromMinutes(90))
            return $"about {Math.Max(1, (int)Math.Round(remaining.TotalMinutes))} min remaining";
        return $"about {Math.Max(1, (int)Math.Round(remaining.TotalHours))} h remaining";
    }
}
