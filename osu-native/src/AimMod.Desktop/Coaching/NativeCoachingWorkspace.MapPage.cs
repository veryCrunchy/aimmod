using System.Globalization;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;

namespace AimMod.Desktop.Coaching;

/// <summary>
/// The map page leads with what the play is worth and where its pp went, ranks the sections to fix, and keeps
/// adaptive progress and the evidence one step down. Plays without a pp breakdown lead with practice instead.
/// </summary>
public partial class NativeCoachingWorkspace
{
    private const int fix_rows = 5, fallback_rows = 3;
    private bool mapDetailsOpen;

    private void renderMapOverview(FillFlowContainer<Drawable> host, LocalReplay run, IReadOnlyList<CoachingSection> problems,
        bool adaptive, string? drillsDetail, Action? prepareDrills)
    {
        bool analysed = analyses.ContainsKey(run.ScoreId);
        var report = analysed ? sectionPpReport(run) : null;
        var costly = report?.Pp is null ? [] : report.Costliest(fix_rows).ToArray();
        if (report?.Pp is { } pp)
        {
            host.Add(ppHeadline(run, report, pp, costly));
            renderFixList(host, run, costly, problems, report);
            if (adaptive) host.Add(adaptiveSummary(run, prepareDrills));
        }
        else
        {
            if (adaptive) host.Add(adaptiveNextStep(run, drillsDetail, prepareDrills));
            if (analysed && sectionPpLoading.Contains(run.ScoreId))
                host.Add(flow("Working out what each section of this play cost in pp…", 13, AimModPalette.Muted));
            else if (!analysed)
                host.Add(flow(run.HasReplayFile
                    ? "This play has not been analysed yet. What each section cost appears here after replay analysis."
                    : "This score has no saved replay, so its sections cannot be analysed.", 13, AimModPalette.Muted));
            else if (report is { Passed: false })
                host.Add(flow("This play failed, so it has no pp to break down. Its problem spots are below.", 13, AimModPalette.Muted));
            renderFixList(host, run, costly, problems, report);
        }
        renderMapDetails(host, run, problems, costly, report);
    }

    /// <summary>What the play is worth, what a full combo of it would be worth, and a strip of the map showing where pp was lost.</summary>
    private Drawable ppHeadline(LocalReplay run, SectionPpReport report, double pp, IReadOnlyList<SectionPpLine> costly)
    {
        string fullCombo = report.FullComboPp is { } fc && fc > pp + .05
            ? $"A full combo of the same play would be worth {points(fc)} (+{(fc - pp).ToString("N1", CultureInfo.InvariantCulture)})."
            : "This play is already a full combo.";
        if (report.SumOfBestPp is { } best && best > pp + .05)
            fullCombo += $" Your best of every section in one play: {points(best)}.";
        var text = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(2),
            Children = [label("This play is worth", 13, AimModPalette.Muted), label(points(pp), 30, AimModPalette.Text, "Bold"), flow(fullCombo, 13, AimModPalette.Muted)],
        };
        Drawable action = costly.Count > 0 && canStartAdaptive(run)
            ? new CoachingButton(costly.Count == 1 ? "Practise the costliest section" : $"Practise the {costly.Count} costliest sections",
                () => playAdaptiveFocused(run, costly.Select(c => c.Section.FirstObject).ToArray()), true)
            : new Container();
        var body = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(16) };
        body.Add(new CoachingActionRow(text, action));
        if (report.Sections.Count > 1)
        {
            double start = report.Sections[0].Section.StartMs, end = report.Sections[^1].Section.EndMs;
            body.Add(new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(6),
                Children =
                [
                    label($"Where the pp went · {AimModCoachTimeline.format(start)} to {AimModCoachTimeline.format(end)}", 12, AimModPalette.Muted),
                    new PpStrip(report.Sections, costly),
                ],
            });
        }
        return new CoachingCard(body, 18);
    }

    /// <summary>The sections to fix, worst first; without a pp breakdown, the problem spots of the latest analysed play.</summary>
    private void renderFixList(FillFlowContainer<Drawable> host, LocalReplay run, IReadOnlyList<SectionPpLine> costly,
        IReadOnlyList<CoachingSection> problems, SectionPpReport? report)
    {
        if (costly.Count == 0 && problems.Count == 0)
        {
            if (report?.Pp is not null) host.Add(flow("No section cost this play any pp.", 13, AimModPalette.Success));
            return;
        }
        var rows = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(8) };
        rows.Add(label("Fix these first", 17, AimModPalette.Text, "SemiBold"));
        if (costly.Count > 0)
        {
            double worst = costly.Max(c => c.Lost ?? 0);
            for (int i = 0; i < costly.Count; i++)
            {
                var line = costly[i];
                var issues = problemsIn(problems, line.Section);
                var notes = new List<string> { costSentence(line) };
                if (line.Best is { } best && line.GainFromBest is > 0.05)
                    notes.Add($"You have played it better: {describeBest(best.Tally)}.");
                rows.Add(new CoachingFixRow(i + 1, rankColour(i), sectionTime(line.Section),
                    issues.Select(p => (CoachingIssues.Label(p.Kind), IssueColour(p.Kind))).Distinct().ToArray(), notes,
                    worst > 0 ? (float)((line.Lost ?? 0) / worst) : 0, $"+{(line.Lost ?? 0).ToString("N1", CultureInfo.InvariantCulture)} pp",
                    fixActions(run, line.Section.FirstObject, line.Section.StartMs)));
            }
            if (report?.FullComboOkGoal is { } goal)
                rows.Add(flow($"Goal: a full combo with up to {goal:N0} hundreds is worth more than this play.", 13, AimModPalette.Muted)
                    .With(f => f.Margin = new MarginPadding { Top = 4, Left = 4 }));
        }
        else
        {
            var top = problems.Take(fallback_rows).ToArray();
            for (int i = 0; i < top.Length; i++)
            {
                var p = top[i];
                string plays = p.Observation.Plays > 1 ? $" In {p.Observation.Plays} plays." : "";
                rows.Add(new CoachingFixRow(i + 1, IssueColour(p.Kind), AimModCoachTimeline.format(p.TimeMs),
                    [(CoachingIssues.Label(p.Kind), IssueColour(p.Kind))], [capitalise(p.Summary) + "." + plays], null, null,
                    fixActions(run, p.FirstObjectIndex, p.TimeMs)));
            }
        }
        host.Add(rows);
    }

    /// <summary>Adaptive progress in one quiet row, once the pp breakdown leads the page.</summary>
    private Drawable adaptiveSummary(LocalReplay run, Action? prepareDrills)
    {
        var progress = adaptiveProgress.GetValueOrDefault(AdaptivePracticeSetup.For(run).Key);
        string title = progress switch
        {
            null => "Adaptive practice · not started",
            { Completed: true } => "Adaptive practice · cleared at full speed",
            _ => $"Adaptive practice · {progress.FullSpeedSections} of {progress.Sections.Length} sections at full speed",
        };
        string detail = progress is null ? "Play the whole map at your pace, slower only where you fail."
            : progress.Completed ? "Play it again to keep it consistent." : "Continue where you left off.";
        if (prepareDrills is not null) detail += " Or play generated drills instead.";
        var actions = new FillFlowContainer<Drawable> { AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(8) };
        if (prepareDrills is not null) actions.Add(new CoachingButton("Generated drills", prepareDrills, compact: true));
        actions.Add(new CoachingButton(progress is null ? "Start" : progress.Completed ? "Play again" : "Continue", () => playAdaptive(run), compact: true));
        var text = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(3),
            Children = [label(title, 14, AimModPalette.Text, "SemiBold"), flow(detail, 12, AimModPalette.Muted)],
        };
        return new CoachingCard(new CoachingActionRow(text, actions), 14);
    }

    /// <summary>Everything else, folded away: the map's numbers, the timeline, other problem spots and the calculator.</summary>
    private void renderMapDetails(FillFlowContainer<Drawable> host, LocalReplay run, IReadOnlyList<CoachingSection> problems,
        IReadOnlyList<SectionPpLine> costly, SectionPpReport? report)
    {
        host.Add(new CoachingButton(mapDetailsOpen ? "Hide details" : "Show details: timeline, other spots and numbers",
            () => { mapDetailsOpen = !mapDetailsOpen; renderCoachingMap(); }, compact: true));
        if (!mapDetailsOpen) return;
        if (mapNumbers(run) is { } numbers) host.Add(numbers);
        renderMapTimeline(host, run);
        var others = costly.Count == 0 ? problems.Skip(fallback_rows).ToArray()
            : problems.Where(p => !costly.Any(c => p.FirstObjectIndex >= c.Section.FirstObject && p.FirstObjectIndex <= c.Section.LastObject)).ToArray();
        if (others.Length > 0)
        {
            host.Add(new SectionLine("Other problem spots", "latest analysed play"));
            foreach (var p in others)
            {
                string plays = p.Observation.Plays > 1 ? $" In {p.Observation.Plays} plays." : "";
                host.Add(new CoachingFixRow(null, IssueColour(p.Kind), AimModCoachTimeline.format(p.TimeMs),
                    [(CoachingIssues.Label(p.Kind), IssueColour(p.Kind))], [capitalise(p.Summary) + "." + plays], null, null,
                    fixActions(run, p.FirstObjectIndex, p.TimeMs)));
            }
        }
        if (report?.Pp is not null)
            host.Add(flow($"pp comes from osu!'s own calculator, {report.Calculator}. Each section's number recalculates this play with only that section played clean, so they do not add up.",
                11, AimModPalette.Muted));
    }

    private Drawable fixActions(LocalReplay run, int firstObject, double timeMs)
    {
        var actions = new FillFlowContainer<Drawable> { AutoSizeAxes = Axes.Both, Direction = FillDirection.Vertical, Spacing = new(6) };
        if (canStartAdaptive(run) && firstObject >= 0)
            actions.Add(new CoachingButton("Practise", () => playAdaptive(run, firstObject), compact: true) { AutoSizeAxes = Axes.None, Width = 96 });
        if (run.HasReplayFile && openReplayMoment is not null)
            actions.Add(new CoachingButton($"Watch {AimModCoachTimeline.format(timeMs)}", () => openReplayMoment(run, timeMs), compact: true)
                { AutoSizeAxes = Axes.None, Width = 96 });
        return actions;
    }

    private static CoachingSection[] problemsIn(IReadOnlyList<CoachingSection> problems, AdaptiveSection section) =>
        problems.Where(p => p.FirstObjectIndex >= section.FirstObject && p.FirstObjectIndex <= section.LastObject).ToArray();

    private static string sectionTime(AdaptiveSection s) => $"{AimModCoachTimeline.format(s.StartMs)} – {AimModCoachTimeline.format(s.EndMs)}";

    private static Colour4 rankColour(int index) => index switch { 0 => AimModPalette.Danger, 1 or 2 => AimModPalette.Yellow, _ => AimModPalette.Muted };

    /// <summary>One plain sentence: what went wrong in the section and what each kind of mistake cost.</summary>
    internal static string costSentence(SectionPpLine line)
    {
        var t = line.Tally;
        string cost(double? value) => (value ?? 0).ToString("N1", CultureInfo.InvariantCulture) + " pp";
        var parts = new List<string>();
        parts.Add(t.Miss + t.SliderBreaks > 0 ? $"{capitalise(mistakePhrase(t))} cost {cost(line.LostToMisses)}." : "No misses.");
        if (t.Ok + t.Meh > 0) parts.Add($"{capitalise(hitPhrase(t))} cost {cost(line.LostToHits)}.");
        return string.Join(" ", parts);
    }

    private static string describeBest(SectionTally t) =>
        (t.Miss + t.SliderBreaks == 0 ? "no misses" : mistakePhrase(t)) + (t.Ok + t.Meh == 0 ? " and no hundreds" : ", " + hitPhrase(t));

    private static string mistakePhrase(SectionTally t)
    {
        var parts = new List<string>();
        if (t.Miss > 0) parts.Add(t.Miss == 1 ? "1 miss" : $"{t.Miss} misses");
        if (t.LargeTickMiss > 0) parts.Add(t.LargeTickMiss == 1 ? "a slider break" : $"{t.LargeTickMiss} slider breaks");
        if (t.SliderTailMiss > 0) parts.Add(t.SliderTailMiss == 1 ? "a dropped slider end" : $"{t.SliderTailMiss} dropped slider ends");
        return joinAnd(parts);
    }

    private static string hitPhrase(SectionTally t)
    {
        var parts = new List<string>();
        if (t.Ok > 0) parts.Add(t.Ok == 1 ? "1 hundred" : $"{t.Ok} hundreds");
        if (t.Meh > 0) parts.Add(t.Meh == 1 ? "1 fifty" : $"{t.Meh} fifties");
        return joinAnd(parts);
    }

    private static string joinAnd(IReadOnlyList<string> parts) =>
        parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

    private static string capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>
    /// The map from start to end, one bar per section: its height is the pp the section cost, and the costliest
    /// sections carry their rank in colour, with the rank and start time below.
    /// </summary>
    private sealed partial class PpStrip : Container
    {
        public PpStrip(IReadOnlyList<SectionPpLine> sections, IReadOnlyList<SectionPpLine> ranked)
        {
            RelativeSizeAxes = Axes.X;
            Height = 54;
            double start = sections[0].Section.StartMs, span = Math.Max(1, sections[^1].Section.EndMs - start);
            double worst = Math.Max(.01, sections.Max(s => s.Lost ?? 0));
            foreach (var line in sections)
            {
                int rank = ranked.ToList().FindIndex(r => r.Section.Index == line.Section.Index);
                float x = (float)((line.Section.StartMs - start) / span), width = (float)((line.Section.EndMs - line.Section.StartMs) / span);
                float height = (float)Math.Max(.08, (line.Lost ?? 0) / worst);
                Colour4 colour = rank >= 0 ? rankColour(rank) : (line.Lost ?? 0) > .05 ? AimModPalette.Muted.Opacity(.45f) : AimModPalette.Border;
                Add(new Container
                {
                    RelativePositionAxes = Axes.X, RelativeSizeAxes = Axes.X, X = x, Width = width, Height = 34,
                    Padding = new MarginPadding { Horizontal = 1.5f },
                    Child = new Container
                    {
                        RelativeSizeAxes = Axes.Both, Height = height, Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft,
                        Masking = true, CornerRadius = 3, Child = new Box { RelativeSizeAxes = Axes.Both, Colour = colour },
                    },
                });
                if (rank >= 0)
                    Add(label($"{rank + 1} · {AimModCoachTimeline.format(line.Section.StartMs)}", 11, colour, "SemiBold")
                        .With(t => { t.RelativePositionAxes = Axes.X; t.X = x; t.Y = 40; t.Padding = new MarginPadding { Left = 2 }; }));
            }
        }
    }

    /// <summary>
    /// One place to fix: a rank badge, the time and what went wrong in plain words, a bar against the worst section,
    /// the pp it would give back, and its actions on the right. Narrow rows put the gain and actions below.
    /// </summary>
    private sealed partial class CoachingFixRow : CompositeDrawable
    {
        private readonly FillFlowContainer text;
        private readonly Container textSlot;
        private readonly FillFlowContainer right;

        public CoachingFixRow(int? rank, Colour4 colour, string title, IReadOnlyList<(string Label, Colour4 Colour)> chips,
            IReadOnlyList<string> lines, float? share, string? gain, Drawable actions)
        {
            RelativeSizeAxes = Axes.X;
            Masking = true;
            CornerRadius = AimModVisualStyle.CardRadius;
            text = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(4),
                Children =
                [
                    new FillFlowContainer
                    {
                        AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(10),
                        Children =
                        [
                            label(title, 16, AimModPalette.Text, "SemiBold").With(t => { t.Anchor = Anchor.CentreLeft; t.Origin = Anchor.CentreLeft; }),
                            .. chips.Select(c => new AimModCoachIssueChip(c.Label, c.Colour).With(d => { d.Anchor = Anchor.CentreLeft; d.Origin = Anchor.CentreLeft; })),
                        ],
                    },
                    .. lines.Select((l, i) => flow(l, i == 0 ? 13 : 12, i == 0 ? AimModPalette.Text : AimModPalette.Muted)),
                ],
            };
            if (share is { } s)
                text.Add(new Container
                {
                    RelativeSizeAxes = Axes.X, Height = 4, Masking = true, CornerRadius = 2, Margin = new MarginPadding { Top = 6 },
                    Children =
                    [
                        new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                        new Box { RelativeSizeAxes = Axes.Both, Width = Math.Clamp(s, .02f, 1), Colour = colour },
                    ],
                });
            right = new FillFlowContainer { AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(18) };
            if (gain is not null)
                right.Add(new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both, Direction = FillDirection.Vertical, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft,
                    Children =
                    [
                        label(gain, 22, AimModPalette.Accent, "Bold").With(t => { t.Anchor = Anchor.TopRight; t.Origin = Anchor.TopRight; }),
                        label("if played clean", 11, AimModPalette.Muted).With(t => { t.Anchor = Anchor.TopRight; t.Origin = Anchor.TopRight; }),
                    ],
                });
            actions.Anchor = actions.Origin = Anchor.CentreLeft;
            right.Add(actions);
            var badge = rank is { } r
                ? new Container
                {
                    Size = new(28), Masking = true, CornerRadius = 14, Position = new(18, 16),
                    Children =
                    [
                        new Box { RelativeSizeAxes = Axes.Both, Colour = colour.Opacity(.18f) },
                        label(r.ToString(CultureInfo.InvariantCulture), 14, colour, "Bold").With(t => { t.Anchor = Anchor.Centre; t.Origin = Anchor.Centre; }),
                    ],
                }
                : (Drawable)new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = colour };
            InternalChildren =
            [
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                badge,
                textSlot = new Container { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Child = text },
                right,
            ];
        }

        protected override void Update()
        {
            base.Update();
            const float top = 16, left = 60, gap = 14;
            bool stacked = DrawWidth < 640;
            textSlot.Padding = new MarginPadding { Left = left, Top = top, Right = stacked ? 18 : right.DrawWidth + 40 };
            if (stacked)
            {
                right.Anchor = right.Origin = Anchor.TopLeft;
                right.Position = new(left, top + text.DrawHeight + gap);
                Height = top + text.DrawHeight + gap + right.DrawHeight + top;
            }
            else
            {
                right.Anchor = right.Origin = Anchor.CentreRight;
                right.Position = new(-18, 0);
                Height = top + Math.Max(text.DrawHeight, right.DrawHeight) + top;
            }
        }
    }
}
