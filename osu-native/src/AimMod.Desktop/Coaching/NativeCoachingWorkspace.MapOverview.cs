using System.Globalization;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Coaching;

/// <summary>
/// The map page reads top to bottom: which map, the one next step, the sections worth working on, then the evidence.
/// Every section of the map appears once, with what went wrong there and what it cost.
/// </summary>
public partial class NativeCoachingWorkspace
{
    private const int focus_rows = 5;

    /// <summary>Back link, map identity and one compact strip of this map's numbers.</summary>
    private Drawable mapIdentity(PracticeSetProgress? latest, LocalReplay? run)
    {
        string title = latest?.Map.Title ?? run?.Title ?? "Beatmap coaching";
        string difficulty = latest?.Map.Difficulty ?? run?.Difficulty ?? "Select a beatmap to begin";
        double stars = run?.StarRating ?? 0;
        CoachingMapCandidate? map = run is null ? null : candidateFor(run);
        var header = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(10) };
        header.Add(new CoachingButton(detailReturnPage == 2 ? "‹ Find a map" : "‹ My coaching", () => showCoachingPage(detailReturnPage), compact: true));
        header.Add(new Container
        {
            RelativeSizeAxes = Axes.X, Height = 50,
            Children =
            [
                new Box { RelativeSizeAxes = Axes.Y, Width = 4, Colour = run is null ? coachingAccent : AimModVisualStyle.DifficultyColour(stars) },
                new Container
                {
                    RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Left = 16 },
                    Children =
                    [
                        new TruncatingSpriteText { RelativeSizeAxes = Axes.X, Text = title, Font = new FontUsage(size: 22, weight: "SemiBold"), Colour = AimModPalette.Text },
                        new FillFlowContainer
                        {
                            AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(8), Y = 29,
                            Children =
                            [
                                new AimModDifficultyPill(stars) { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Alpha = run is null ? 0 : 1 },
                                label(map is null ? difficulty : $"{difficulty} · {map.Attempts:N0} {(map.Attempts == 1 ? "play" : "plays")}", 13, AimModPalette.Muted)
                                    .With(t => { t.Anchor = Anchor.CentreLeft; t.Origin = Anchor.CentreLeft; }),
                            ],
                        },
                    ],
                },
            ],
        });
        if (map is null) return header;

        double? average = workspace?.Global.MedianAccuracy;
        var strip = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(36, 12) };
        strip.Add(summaryItem("Typical accuracy", $"{map.TypicalAccuracy:P1}", average is { } avg
            ? ($"{(map.TypicalAccuracy - avg) * 100:+0.0;-0.0;0.0} vs your usual", map.TypicalAccuracy >= avg ? AimModPalette.Success : AimModPalette.Yellow) : null));
        strip.Add(summaryItem("Best", $"{map.BestAccuracy:P1}", map.AccuracyGap >= .001 ? ($"{map.AccuracyGap * 100:0.0} above typical", AimModPalette.Muted) : null));
        strip.Add(summaryItem("Misses per play", $"{map.TypicalMisses:0.#}", ($"best {map.BestMisses}", AimModPalette.Muted)));
        if (map.TopIssue is { } issue)
            strip.Add(new FillFlowContainer
            {
                AutoSizeAxes = Axes.Both, Direction = FillDirection.Vertical, Spacing = new(4),
                Children =
                [
                    label("Main issue", 11, AimModPalette.Muted, "SemiBold"),
                    new AimModCoachIssueChip(CoachingIssues.Label(issue.Kind), IssueColour(issue.Kind)),
                    label(CoachingCandidateRow.IssueDetail(map, issue), 11, AimModPalette.Muted),
                ],
            });
        header.Add(new CoachingCard(strip, 14));
        return header;
    }

    private static Drawable summaryItem(string title, string value, (string Text, Colour4 Colour)? note) => new FillFlowContainer
    {
        AutoSizeAxes = Axes.Both, Direction = FillDirection.Vertical, Spacing = new(2),
        Children =
        [
            label(title, 11, AimModPalette.Muted, "SemiBold"),
            label(value, 18, AimModPalette.Text, "Bold"),
            label(note?.Text ?? string.Empty, 11, note?.Colour ?? AimModPalette.Muted),
        ],
    };

    /// <summary>
    /// The one thing to do next on this map: adaptive practice with its progress, and generated drills as the
    /// quieter alternative in the same card.
    /// </summary>
    private Drawable adaptiveNextStep(LocalReplay run, string? drillsDetail, Action? prepareDrills)
    {
        var setup = AdaptivePracticeSetup.For(run);
        var progress = adaptiveProgress.GetValueOrDefault(setup.Key);
        string mods = setup.Mods.Count == 0 && Math.Abs(setup.TargetRate - 1) < .001 ? "" : $" Full speed is your play's setup: {setup.Label}.";
        (string title, string detail, string action) = progress switch
        {
            null => ("Practise the whole map at your pace",
                "A section you fail repeats a little slower and speeds back up as you clear it, until you beat the map at full speed." + mods, "Start adaptive practice"),
            { Completed: true } => ("Cleared at full speed",
                $"You beat this map at full speed on {progress.CompletedAt!.Value.ToLocalTime():dd MMM}. Play it again to keep it consistent.", "Play again"),
            _ => ("Continue adaptive practice",
                $"{progress.FullSpeedSections} of {progress.Sections.Length} sections at full speed · slowest section {AdaptivePracticeCoach.Percent(progress.SlowestSpeed)}." + mods, "Continue"),
        };
        var body = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(14) };
        body.Add(new CoachingActionRow(
            new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(4),
                Children = [label("Next step", 11, coachingAccent, "SemiBold"), label(title, 18, AimModPalette.Text, "SemiBold"), flow(detail, 13, AimModPalette.Muted)],
            },
            new CoachingButton(action, () => playAdaptive(run), true)));
        if (progress is not null)
        {
            var strip = new AdaptiveSectionStrip(progress.Sections.Length) { RelativeSizeAxes = Axes.X, Height = 8 };
            strip.Set(progress, -1);
            body.Add(new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(6),
                Children = [strip, label("Section speeds: mint is full speed, amber to red still slowed down.", 11, AimModPalette.Muted)],
            });
        }
        if (drillsDetail is not null && prepareDrills is not null)
        {
            body.Add(new Box { RelativeSizeAxes = Axes.X, Height = 1, Colour = AimModPalette.Border });
            body.Add(new CoachingActionRow(flow(drillsDetail, 12, AimModPalette.Muted), new CoachingButton("Generated drills", prepareDrills, compact: true)));
        }
        return new CoachingCard(body, 18);
    }

    /// <summary>
    /// One list of the places worth practising. With a pp breakdown, the sections that cost the play the most pp
    /// lead, each with what went wrong there; problem sections it does not cover follow.
    /// </summary>
    private void renderFocusSections(FillFlowContainer<Drawable> host, LocalReplay run, IReadOnlyList<CoachingSection> problems)
    {
        bool hasAnalysis = analyses.ContainsKey(run.ScoreId);
        var report = sectionPpReport(run);
        var costly = report?.Pp is null ? [] : report.Costliest(focus_rows).ToArray();
        if (!hasAnalysis)
        {
            host.Add(new SectionLine("Sections to work on", ""));
            host.Add(flow(run.HasReplayFile
                ? "This play has not been analysed yet. The sections to work on appear here after replay analysis."
                : "This score has no saved replay, so its sections cannot be analysed.", 13, AimModPalette.Muted));
            return;
        }

        string detail = report?.Pp is { } pp
            ? $"this play {points(pp)}" + (report.SumOfBestPp is { } best ? $" · best of every section {points(best)}" : "")
            : sectionPpLoading.Contains(run.ScoreId) ? "working out pp per section…" : "latest analysed play";
        host.Add(new SectionLine("Sections to work on", detail));
        if (costly.Length > 1 && canStartAdaptive(run))
            host.Add(new CoachingButton($"Practise these {costly.Length} sections first",
                () => playAdaptiveFocused(run, costly.Select(c => c.Section.FirstObject).ToArray()), compact: true));

        var covered = new HashSet<CoachingSection>();
        foreach (var line in costly)
        {
            var here = problems.Where(p => p.FirstObjectIndex >= line.Section.FirstObject && p.FirstObjectIndex <= line.Section.LastObject).ToArray();
            covered.UnionWith(here);
            host.Add(costRow(run, line, here));
        }
        foreach (var problem in problems.Where(p => !covered.Contains(p)))
            host.Add(problemRow(run, problem));

        if (costly.Length == 0 && problems.Count == 0)
            host.Add(flow(report?.Pp is not null ? "No section cost this play any pp." : "Nothing stands out in this play.", 13, AimModPalette.Success));
        if (report?.Pp is not null)
        {
            var notes = new List<string>();
            if (report.FullComboOkGoal is { } goal) notes.Add($"Goal: a full combo with at most {goal:N0} × 100 is worth more than this play.");
            notes.Add($"pp from {report.Calculator}. Each number recalculates this play with only that section changed, so they do not add up.");
            host.Add(flow(string.Join(" ", notes), 11, AimModPalette.Muted).With(f => f.Margin = new MarginPadding { Left = 4 }));
        }
    }

    private Drawable costRow(LocalReplay run, SectionPpLine line, IReadOnlyList<CoachingSection> issues)
    {
        var t = line.Tally;
        var lines = new List<string>();
        var parts = new List<string>();
        if (t.Miss + t.SliderBreaks > 0) parts.Add($"{mistakes(t)} {gain(line.LostToMisses ?? 0)}");
        if (t.Ok + t.Meh > 0) parts.Add($"{hundreds(t)} {gain(line.LostToHits ?? 0)}");
        if (parts.Count > 0) lines.Add(string.Join(" · ", parts));
        foreach (var issue in issues) lines.Add(issue.Summary);
        if (line.Best is { } best && line.GainFromBest is > 0.05)
            lines.Add($"Your best here: {describe(best.Tally)}, worth {gain(line.GainFromBest.Value)} to this play.");
        var chips = issues.Select(i => (CoachingIssues.Label(i.Kind), IssueColour(i.Kind))).Distinct().ToArray();
        Colour4 accent = issues.Count > 0 ? IssueColour(issues[0].Kind) : coachingAccent;
        return new CoachingFocusRow(accent, $"Section {line.Section.Index + 1}",
            $"{AimModCoachTimeline.format(line.Section.StartMs)}–{AimModCoachTimeline.format(line.Section.EndMs)}", chips, lines,
            (gain(line.Lost ?? 0), "lost here"), focusActions(run, line.Section.FirstObject, line.Section.StartMs, null));
    }

    private Drawable problemRow(LocalReplay run, CoachingSection section)
    {
        string plays = section.Observation.Plays > 1 ? $" · in {section.Observation.Plays} plays" : string.Empty;
        return new CoachingFocusRow(IssueColour(section.Kind), CoachingIssues.Label(section.Kind), AimModCoachTimeline.format(section.TimeMs), [],
            [section.Summary + plays], null, focusActions(run, section.FirstObjectIndex, section.TimeMs, section), visualFor(section));
    }

    /// <summary>Practise first, then watch; generated drills stay available for problem sections.</summary>
    private Drawable focusActions(LocalReplay run, int firstObject, double timeMs, CoachingSection? section)
    {
        var actions = new FillFlowContainer<Drawable> { AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(8) };
        if (run.HasReplayFile && openReplayMoment is not null)
            actions.Add(new CoachingButton($"Watch {AimModCoachTimeline.format(timeMs)}", () => openReplayMoment(run, timeMs), compact: true));
        if (section is not null && practiceWorkspace is not null && firstObject >= 0)
            actions.Add(new CoachingButton("Drills", () => practiceWorkspace.OpenBreakdown(
                new PracticeMapCandidate(run, section.Observation.ScoreIds, Math.Max(1, section.Observation.Plays), run.MissCount, 0), firstObject), compact: true));
        if (canStartAdaptive(run) && firstObject >= 0)
            actions.Add(new CoachingButton("Practise", () => playAdaptive(run, firstObject), compact: true));
        return actions;
    }

    /// <summary>Text on the left and one action on the right; the action drops below the text on narrow pages.</summary>
    private sealed partial class CoachingActionRow : CompositeDrawable
    {
        private readonly Container textSlot;
        private readonly Drawable action;

        public CoachingActionRow(Drawable text, Drawable action)
        {
            this.action = action;
            RelativeSizeAxes = Axes.X;
            InternalChildren = [textSlot = new Container { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Child = text }, action];
        }

        protected override void Update()
        {
            base.Update();
            bool stacked = DrawWidth < 560;
            action.Anchor = action.Origin = stacked ? Anchor.TopLeft : Anchor.CentreRight;
            if (stacked)
            {
                textSlot.Padding = new MarginPadding();
                textSlot.Anchor = textSlot.Origin = Anchor.TopLeft;
                action.Y = textSlot.DrawHeight + 12;
                Height = textSlot.DrawHeight + 12 + action.DrawHeight;
            }
            else
            {
                textSlot.Padding = new MarginPadding { Right = action.DrawWidth + 24 };
                textSlot.Anchor = textSlot.Origin = Anchor.CentreLeft;
                action.Y = 0;
                Height = Math.Max(textSlot.DrawHeight, action.DrawHeight);
            }
        }
    }

    /// <summary>
    /// A place on the map to work on: what and where on the left, an optional cost and plot, and its actions aligned
    /// on the right. Narrow rows put the actions under the text.
    /// </summary>
    private sealed partial class CoachingFocusRow : CompositeDrawable
    {
        private readonly FillFlowContainer text;
        private readonly FillFlowContainer right;
        private readonly Container? visualSlot;

        public CoachingFocusRow(Colour4 accent, string title, string time, IReadOnlyList<(string Label, Colour4 Colour)> chips,
            IReadOnlyList<string> lines, (string Value, string Caption)? metric, Drawable actions, Drawable? visual = null)
        {
            RelativeSizeAxes = Axes.X;
            Masking = true;
            CornerRadius = AimModVisualStyle.CardRadius;
            var heading = new FillFlowContainer
            {
                AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(8),
                Children =
                [
                    label(title, 15, AimModPalette.Text, "SemiBold").With(t => { t.Anchor = Anchor.CentreLeft; t.Origin = Anchor.CentreLeft; }),
                    label(time, 13, accent, "SemiBold").With(t => { t.Anchor = Anchor.CentreLeft; t.Origin = Anchor.CentreLeft; }),
                    .. chips.Select(c => new AimModCoachIssueChip(c.Label, c.Colour).With(d => { d.Anchor = Anchor.CentreLeft; d.Origin = Anchor.CentreLeft; })),
                ],
            };
            text = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(4),
                Children = [heading, .. lines.Select(l => flow(l, 12, AimModPalette.Muted))],
            };
            right = new FillFlowContainer { AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(16) };
            if (metric is { } m)
                right.Add(new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both, Direction = FillDirection.Vertical, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft,
                    Children =
                    [
                        label(m.Value, 18, AimModPalette.Accent, "Bold").With(t => { t.Anchor = Anchor.TopRight; t.Origin = Anchor.TopRight; }),
                        label(m.Caption, 11, AimModPalette.Muted).With(t => { t.Anchor = Anchor.TopRight; t.Origin = Anchor.TopRight; }),
                    ],
                });
            actions.Anchor = actions.Origin = Anchor.CentreLeft;
            right.Add(actions);
            InternalChildren =
            [
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = accent },
                new Container { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Padding = new MarginPadding { Left = 20, Top = 14 }, Child = text },
                right,
            ];
            if (visual is not null)
            {
                visual.Anchor = visual.Origin = Anchor.CentreRight;
                AddInternal(visualSlot = new Container { AutoSizeAxes = Axes.Both, Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, Child = visual });
            }
        }

        protected override void Update()
        {
            base.Update();
            bool stacked = DrawWidth < 620;
            bool showVisual = visualSlot is not null && DrawWidth >= 760;
            if (visualSlot is not null) visualSlot.Alpha = showVisual ? 1 : 0;
            float reserved = stacked ? 16 : right.DrawWidth + 40 + (showVisual ? visualSlot!.DrawWidth + 24 : 0);
            ((Container)text.Parent!).Padding = new MarginPadding { Left = 20, Top = 14, Right = reserved };
            if (stacked)
            {
                right.Anchor = right.Origin = Anchor.TopLeft;
                right.Position = new(20, 14 + text.DrawHeight + 12);
                Height = 14 + text.DrawHeight + 12 + right.DrawHeight + 14;
            }
            else
            {
                right.Anchor = right.Origin = Anchor.CentreRight;
                right.Position = new(-16, 0);
                if (visualSlot is not null) visualSlot.X = -(right.DrawWidth + 40);
                Height = 14 + Math.Max(text.DrawHeight, right.DrawHeight) + 14;
            }
        }
    }
}
