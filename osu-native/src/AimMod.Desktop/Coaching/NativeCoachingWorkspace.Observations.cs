using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Coaching;

public partial class NativeCoachingWorkspace
{
    private (LocalReplay Run, IReadOnlyList<LocalReplay> History, int Analyses, CoachingMapRanking Ranking,
        IReadOnlyList<CoachingSection> Sections, CoachingMapTimeline Timeline, CoachingIssue? MainIssue)? observationCache;

    private (IReadOnlyList<CoachingSection> Sections, CoachingMapTimeline Timeline, CoachingIssue? MainIssue) evidenceFor(LocalReplay run)
    {
        if (observationCache is not { } cached || !ReferenceEquals(cached.Run, run) || !ReferenceEquals(cached.History, allReplays)
            || cached.Analyses != analyses.Count || !ReferenceEquals(cached.Ranking, mapRanking))
        {
            var observations = CoachingReplayObservations.Build(run, allReplays, analyses);
            CoachingIssue? main = candidateFor(run)?.TopIssue;
            observationCache = cached = (run, allReplays, analyses.Count, mapRanking, CoachingSections.Build(run, observations, analyses, main),
                CoachingMapTimeline.Build(run, allReplays, analyses), main);
        }
        return (cached.Sections, cached.Timeline, cached.MainIssue);
    }

    private IReadOnlyList<CoachingSection> sectionsFor(LocalReplay run) => evidenceFor(run).Sections;

    private static IconUsage familyIcon(CoachingIssueFamily family) => family switch
    {
        CoachingIssueFamily.Aim => FontAwesome.Solid.Crosshairs,
        CoachingIssueFamily.Timing => FontAwesome.Solid.Stopwatch,
        CoachingIssueFamily.NoPress => FontAwesome.Solid.HandPointer,
        _ => FontAwesome.Solid.Music,
    };

    /// <summary>The whole map with problem counts from every analysed attempt, so recurring sections stand out.</summary>
    private void renderMapTimeline(FillFlowContainer<Drawable> host, LocalReplay run)
    {
        var (sections, timeline, main) = evidenceFor(run);
        if (timeline.AnalysedPlays == 0 || timeline.LengthMs <= 0) return;
        var families = Enum.GetValues<CoachingIssueFamily>();
        var present = timeline.Markers.Select(m => CoachingIssues.Family(m.Kind)).Distinct().OrderBy(f => f).ToArray();
        var legend = new FillFlowContainer { AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(14) };
        foreach (var family in present) legend.Add(new AimModCoachLegendItem(CoachingIssues.FamilyLabel(family), FamilyColour(family)));
        bool canWatch = run.HasReplayFile && openReplayMoment is not null;
        var body = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(10) };
        body.Add(new SectionLine("Where it goes wrong",
            $"{timeline.AnalysedPlays:N0} analysed {(timeline.AnalysedPlays == 1 ? "play" : "plays")}{(canWatch ? " · click a bar to watch" : string.Empty)}"));
        body.Add(legend);
        var marks = timeline.Markers.Select(m => new AimModCoachTimelineMark(m.TimeMs, (int)CoachingIssues.Family(m.Kind))).ToArray();
        var colours = families.Select(FamilyColour).ToArray();
        var names = families.Select(f => f switch
        {
            CoachingIssueFamily.Aim => "aim",
            CoachingIssueFamily.Timing => "timing",
            CoachingIssueFamily.NoPress => "no press",
            _ => "stream drift",
        }).ToArray();
        var bands = bandsFor(sections, main);
        Action<double>? watch = canWatch ? time => openReplayMoment!(run, time) : null;
        var full = new AimModCoachTimeline(timeline.LengthMs, marks, colours, names, timeline.AnalysedPlays, bands, watch);
        // The main section gets its own zoomed, auto-scaled view so one spike on a long map is still readable.
        Drawable? zoom = null;
        if (main is { FirstObjectIndex: >= 0 } && timeline.LengthMs > 40_000)
        {
            double from = Math.Max(0, main.TimeMs - 8_000), to = Math.Min(timeline.LengthMs, Math.Max(main.EndMs, main.TimeMs + 1_000) + 8_000);
            zoom = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(4),
                Children =
                [
                    label($"{CoachingIssues.Label(main.Kind)} · {AimModCoachTimeline.format(from)}–{AimModCoachTimeline.format(to)}", 11, IssueColour(main.Kind), "SemiBold"),
                    new AimModCoachTimeline(to, marks, colours, names, timeline.AnalysedPlays, bands, watch, from),
                ],
            };
        }
        body.Add(new CoachingTimelinePair(full, zoom) { Margin = new MarginPadding { Top = 4 } });
        host.Add(new CoachingCard(body, 16));
    }

    /// <summary>Shaded ranges for the main issue and each problem section, so bars and cards read as the same places.</summary>
    private static AimModCoachTimelineBand[] bandsFor(IReadOnlyList<CoachingSection> sections, CoachingIssue? main)
    {
        var bands = new List<AimModCoachTimelineBand>();
        if (main is { FirstObjectIndex: >= 0 })
            bands.Add(new(main.TimeMs, Math.Max(main.EndMs, main.TimeMs + 1_000), IssueColour(main.Kind), CoachingIssues.Label(main.Kind)));
        foreach (var section in sections)
        {
            double end = Math.Max(section.EndMs, section.TimeMs + 1_000);
            if (bands.Any(b => section.TimeMs <= b.EndMs && end >= b.StartMs)) continue;
            bands.Add(new(section.TimeMs, end, IssueColour(section.Kind), CoachingIssues.Label(section.Kind)));
        }
        return bands.ToArray();
    }

    private void renderReplayObservations(FillFlowContainer<Drawable> host, LocalReplay run) => renderReplayObservations(host, run, sectionsFor(run));

    private void renderReplayObservations(FillFlowContainer<Drawable> host, LocalReplay run, IReadOnlyList<CoachingSection> sections)
    {
        if (sections.Count == 0)
        {
            if (!analyses.ContainsKey(run.ScoreId))
                host.Add(flow(run.HasReplayFile
                    ? "This play has not been analysed yet. Problem sections appear here after replay analysis."
                    : "This score has no saved replay, so problem sections are unavailable.", 13, AimModPalette.Muted));
            return;
        }
        host.Add(new SectionLine("Problem sections", "latest analysed play"));
        foreach (var section in sections)
        {
            var observation = section.Observation;
            var actions = new FillFlowContainer<Drawable> { AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(8) };
            if (run.HasReplayFile && openReplayMoment is not null)
                actions.Add(new CoachingButton($"Watch {AimModCoachTimeline.format(section.TimeMs)}", () => openReplayMoment(run, section.TimeMs), compact: true));
            if (practiceWorkspace is not null)
                actions.Add(new CoachingButton("Practise", () => practiceWorkspace.OpenBreakdown(
                    new PracticeMapCandidate(run, observation.ScoreIds, Math.Max(1, observation.Plays), run.MissCount, 0),
                    section.FirstObjectIndex), compact: true));
            host.Add(new CoachingSectionCard(section, visualFor(section), actions));
        }
        if (sections.Count == 1)
            host.Add(flow("Other sections look fine in this play.", 12, AimModPalette.Muted).With(f => f.Margin = new MarginPadding { Left = 4 }));
    }

    private static Drawable visualFor(CoachingSection section)
    {
        Colour4 colour = IssueColour(section.Kind);
        return CoachingIssues.Family(section.Kind) switch
        {
            CoachingIssueFamily.Tapping => new AimModCoachTapDrift(section.TapOffsetsMs, colour),
            CoachingIssueFamily.Timing => new AimModCoachTimingAxis(section.PressOffsetsMs, colour),
            _ => new AimModCoachPressScatter(section.PressPoints, colour),
        };
    }

    /// <summary>Whole-map timeline with the main section's zoom beside it, or below it on narrow pages.</summary>
    private sealed partial class CoachingTimelinePair : Container
    {
        private readonly Drawable full;
        private readonly Drawable? zoom;
        private (float Width, float Full, float Zoom) layout = (-1, -1, -1);

        public CoachingTimelinePair(Drawable full, Drawable? zoom)
        {
            this.full = full;
            this.zoom = zoom;
            RelativeSizeAxes = Axes.X;
            Add(full);
            if (zoom is not null) Add(zoom);
        }

        protected override void Update()
        {
            base.Update();
            var next = (DrawWidth, full.DrawHeight, zoom?.DrawHeight ?? 0);
            if (next == layout) return;
            layout = next;
            if (zoom is null) { full.Width = 1; Height = full.DrawHeight; return; }
            bool side = DrawWidth >= 820;
            full.Width = side ? 0.66f : 1;
            zoom.Width = side ? 0.3f : 1;
            zoom.X = side ? DrawWidth * 0.7f : 0;
            // Bottom-align both charts so their time axes share a baseline.
            float height = side ? Math.Max(full.DrawHeight, zoom.DrawHeight) : full.DrawHeight + AimModVisualStyle.SectionSpacing + zoom.DrawHeight;
            full.Y = side ? height - full.DrawHeight : 0;
            zoom.Y = height - zoom.DrawHeight;
            Height = height;
        }
    }

    /// <summary>Issue icon, one-line measurement, a small plot of that measurement and the section's actions.</summary>
    private sealed partial class CoachingSectionCard : CompositeDrawable
    {
        private readonly Drawable visual;
        private readonly Container visualSlot;
        private readonly Container textSlot;
        private float layoutWidth = -1;

        public CoachingSectionCard(CoachingSection section, Drawable visual, Drawable actions)
        {
            this.visual = visual;
            RelativeSizeAxes = Axes.X;
            Height = 76;
            Masking = true;
            CornerRadius = AimModVisualStyle.CardRadius;
            Colour4 colour = IssueColour(section.Kind);
            visual.Anchor = Anchor.CentreLeft;
            visual.Origin = Anchor.CentreLeft;
            actions.Anchor = Anchor.CentreRight;
            actions.Origin = Anchor.CentreRight;
            string plays = section.Observation.Plays > 1 ? $" · in {section.Observation.Plays} plays" : string.Empty;
            InternalChildren =
            [
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = colour },
                new Container
                {
                    Size = new(34), Position = new(18, 0), Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Masking = true, CornerRadius = 17,
                    Children =
                    [
                        new Box { RelativeSizeAxes = Axes.Both, Colour = colour.Opacity(0.16f) },
                        new SpriteIcon { Icon = familyIcon(CoachingIssues.Family(section.Kind)), Size = new(15), Colour = colour, Anchor = Anchor.Centre, Origin = Anchor.Centre },
                    ],
                },
                textSlot = new Container
                {
                    RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Left = 66 },
                    Child = new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(4),
                        Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft,
                        Children =
                        [
                            new FillFlowContainer
                            {
                                AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(8),
                                Children =
                                [
                                    label(CoachingIssues.Label(section.Kind), 15, AimModPalette.Text, "SemiBold"),
                                    label(AimModCoachTimeline.format(section.TimeMs), 13, colour, "SemiBold").With(t => { t.Anchor = Anchor.BottomLeft; t.Origin = Anchor.BottomLeft; }),
                                ],
                            },
                            new TruncatingSpriteText
                            {
                                RelativeSizeAxes = Axes.X, Text = section.Summary + plays, Font = new FontUsage(size: 12), Colour = AimModPalette.Muted,
                            },
                        ],
                    },
                },
                visualSlot = new Container { RelativeSizeAxes = Axes.Y, AutoSizeAxes = Axes.X, Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, Child = visual },
                new Container { RelativeSizeAxes = Axes.Y, AutoSizeAxes = Axes.X, Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, Padding = new MarginPadding { Right = 14 }, Child = actions },
            ];
            this.actions = actions;
        }

        private readonly Drawable actions;

        protected override void Update()
        {
            base.Update();
            float width = DrawWidth + actions.DrawWidth;
            if (width == layoutWidth) return;
            layoutWidth = width;
            // Plot sits between the text and the actions; narrow cards drop it before squeezing the text.
            bool showVisual = DrawWidth >= 700;
            visualSlot.Alpha = showVisual ? 1 : 0;
            float right = actions.DrawWidth + 14 + 24;
            visualSlot.X = -right;
            textSlot.Padding = new MarginPadding { Left = 66, Right = right + (showVisual ? visual.DrawWidth + 24 : 0) };
        }
    }
}
