using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime.Contracts;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;

namespace AimMod.Desktop.Replays;

/// <summary>
/// Seek bar with the exact judgement timeline drawn along it. Misses and slider breaks are
/// clickable markers that jump to the lead-in of the mistake.
/// </summary>
public partial class ReplayTimelineScrubber : CompositeDrawable, IHasTooltip
{
    public const float ScrubberHeight = 34;
    private const float track_y = 25;
    private const float track_height = 4;

    private readonly Func<double> currentTime;
    private readonly Func<double> duration;
    private readonly Action<double> seek;
    private readonly Action<ReplayMoment> jump;
    private readonly Container judgementMarks;
    private readonly Container momentMarkers;
    private readonly Box progress;
    private readonly Container handle;
    private readonly Box hoverLine;
    private IReadOnlyList<ReplayTimelineMark> marks = Array.Empty<ReplayTimelineMark>();
    private IReadOnlyList<ReplayMoment> moments = Array.Empty<ReplayMoment>();
    private double laidOutDuration = -1;
    private double hoverTime = -1;
    private bool dragging;

    public LocalisableString TooltipText => hoverTime >= 0 && duration() > 0 ? ReplayTimeFormat.Clock(hoverTime) : string.Empty;

    internal int MomentMarkerCount => momentMarkers.Count;

    public ReplayTimelineScrubber(Func<double> currentTime, Func<double> duration, Action<double> seek, Action<ReplayMoment> jump)
    {
        this.currentTime = currentTime;
        this.duration = duration;
        this.seek = seek;
        this.jump = jump;
        RelativeSizeAxes = Axes.X;
        Height = ScrubberHeight;
        InternalChildren = new Drawable[]
        {
            // Generous transparent hit area; the visible track is thin.
            new Box { RelativeSizeAxes = Axes.Both, Alpha = 0, AlwaysPresent = true },
            judgementMarks = new Container { RelativeSizeAxes = Axes.X, Height = track_y - 3 },
            new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = track_height,
                Y = track_y,
                Origin = Anchor.CentreLeft,
                Masking = true,
                CornerRadius = track_height / 2,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Border },
                    progress = new Box { RelativeSizeAxes = Axes.Both, Width = 0, Colour = AimModPalette.Accent },
                },
            },
            hoverLine = new Box
            {
                RelativePositionAxes = Axes.X,
                Width = 1,
                Height = ScrubberHeight,
                Colour = AimModPalette.Text,
                Alpha = 0,
            },
            momentMarkers = new Container { RelativeSizeAxes = Axes.Both },
            handle = new CircularContainer
            {
                RelativePositionAxes = Axes.X,
                Y = track_y,
                Origin = Anchor.Centre,
                Size = new(14),
                Masking = true,
                BorderThickness = 3,
                BorderColour = AimModPalette.Accent,
                Child = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Text },
            },
        };
    }

    public void SetAnalysis(ReplayAnalysisResult? result, IReadOnlyList<ReplayMoment> replayMoments)
    {
        marks = result is null ? Array.Empty<ReplayTimelineMark>() : ReplayTimelineSampler.Sample(result);
        moments = replayMoments;
        laidOutDuration = -1;
    }

    protected override void Update()
    {
        base.Update();
        double total = duration();
        if (Math.Abs(total - laidOutDuration) > 0.5)
            layoutMarks(total);

        float position = total > 0 ? (float)Math.Clamp(currentTime() / total, 0, 1) : 0;
        if (progress.Width != position)
        {
            progress.Width = position;
            handle.X = position;
        }
    }

    private void layoutMarks(double total)
    {
        laidOutDuration = total;
        judgementMarks.Clear();
        momentMarkers.Clear();
        if (total <= 0)
            return;

        foreach (ReplayTimelineMark mark in marks)
        {
            if (mark.Tone is ReplayTimelineTone.Miss or ReplayTimelineTone.SliderBreak)
                continue;

            judgementMarks.Add(new Box
            {
                RelativePositionAxes = Axes.X,
                X = (float)Math.Clamp(mark.TimeMilliseconds / total, 0, 1),
                Anchor = Anchor.BottomLeft,
                Origin = Anchor.BottomCentre,
                Size = new(1, mark.Tone == ReplayTimelineTone.Great ? 6 : 11),
                Colour = ReplayJudgementTimeline.ColourFor(mark.Tone),
                Alpha = mark.Tone == ReplayTimelineTone.Great ? 0.45f : 0.85f,
            });
        }

        foreach (ReplayMoment moment in moments)
            momentMarkers.Add(new MomentMarker(moment, jump) { X = (float)Math.Clamp(moment.TimeMs / total, 0, 1) });
    }

    protected override bool OnHover(HoverEvent e)
    {
        hoverLine.FadeTo(0.35f, AimModVisualStyle.FastTransition);
        return true;
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        hoverTime = -1;
        hoverLine.FadeOut(AimModVisualStyle.FastTransition);
        base.OnHoverLost(e);
    }

    protected override bool OnMouseMove(MouseMoveEvent e)
    {
        float relative = DrawWidth > 0 ? Math.Clamp(e.MousePosition.X / DrawWidth, 0, 1) : 0;
        double total = duration();
        hoverTime = total > 0 ? relative * total : -1;
        hoverLine.X = relative;
        return base.OnMouseMove(e);
    }

    protected override bool OnMouseDown(MouseDownEvent e) => duration() > 0;

    protected override bool OnClick(ClickEvent e)
    {
        seekTo(e.MousePosition.X);
        return true;
    }

    protected override bool OnDragStart(DragStartEvent e)
    {
        dragging = duration() > 0;
        return dragging;
    }

    protected override void OnDrag(DragEvent e) => seekTo(e.MousePosition.X);

    protected override void OnDragEnd(DragEndEvent e)
    {
        dragging = false;
        base.OnDragEnd(e);
    }

    internal bool IsDragging => dragging;

    private void seekTo(float localX)
    {
        double total = duration();
        if (total <= 0 || DrawWidth <= 0)
            return;

        seek(Math.Clamp(localX / DrawWidth, 0, 1) * total);
    }

    private partial class MomentMarker : CompositeDrawable, IHasTooltip
    {
        private readonly ReplayMoment moment;
        private readonly Action<ReplayMoment> jump;
        private readonly Container dot;

        public LocalisableString TooltipText => Describe(moment);

        /// <summary>Hover text for a timeline marker: when, which object, and the measured cause.</summary>
        internal static string Describe(ReplayMoment moment) =>
            $"{moment.SeverityLabel} at {ReplayTimeFormat.Precise(moment.TimeMs)} · {moment.ObjectLabel}\n{moment.Detail}";

        public MomentMarker(ReplayMoment moment, Action<ReplayMoment> jump)
        {
            this.moment = moment;
            this.jump = jump;
            RelativePositionAxes = Axes.X;
            Origin = Anchor.TopCentre;
            // Only the marker head is a jump target; the rest of the bar still scrubs.
            Size = new(12, 14);
            Colour4 colour = ReplayMomentColours.For(moment.Severity);
            InternalChildren = new Drawable[]
            {
                new Box
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Y = 4,
                    Size = new(2, track_y - 2),
                    Colour = colour,
                },
                dot = new CircularContainer
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.Centre,
                    Y = 5,
                    Size = new(8),
                    Masking = true,
                    Child = new Box { RelativeSizeAxes = Axes.Both, Colour = colour },
                },
            };
        }

        protected override bool OnHover(HoverEvent e)
        {
            dot.ScaleTo(1.5f, AimModVisualStyle.FastTransition, Easing.OutQuint);
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            dot.ScaleTo(1, AimModVisualStyle.FastTransition, Easing.OutQuint);
            base.OnHoverLost(e);
        }

        protected override bool OnClick(ClickEvent e)
        {
            jump(moment);
            return true;
        }
    }
}

internal static class ReplayMomentColours
{
    public static Colour4 For(ReplayMomentSeverity severity) => severity == ReplayMomentSeverity.Miss
        ? ReplayJudgementTimeline.ColourFor(ReplayTimelineTone.Miss)
        : AimModPalette.Yellow;
}

public static class ReplayTimeFormat
{
    /// <summary>Transport clock at tenth-of-a-second precision.</summary>
    public static string Clock(double milliseconds)
    {
        TimeSpan time = TimeSpan.FromMilliseconds(Math.Max(0, double.IsFinite(milliseconds) ? milliseconds : 0));
        return $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds / 100}";
    }

    /// <summary>Millisecond precision for judgement times.</summary>
    public static string Precise(double milliseconds)
    {
        TimeSpan time = TimeSpan.FromMilliseconds(Math.Max(0, double.IsFinite(milliseconds) ? milliseconds : 0));
        return $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}";
    }
}
