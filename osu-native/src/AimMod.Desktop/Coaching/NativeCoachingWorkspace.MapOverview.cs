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
    /// <summary>Back link and map identity, with the play count and accuracy on one line.</summary>
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
                                label(map is null ? difficulty : $"{difficulty} · {map.Attempts:N0} {(map.Attempts == 1 ? "play" : "plays")} · {map.TypicalAccuracy:P1} typical, best {map.BestAccuracy:P1}", 13, AimModPalette.Muted)
                                    .With(t => { t.Anchor = Anchor.CentreLeft; t.Origin = Anchor.CentreLeft; }),
                            ],
                        },
                    ],
                },
            ],
        });
        return header;
    }

    /// <summary>This map's numbers against the player's usual level, for the page's details.</summary>
    private Drawable? mapNumbers(LocalReplay run)
    {
        if (candidateFor(run) is not { } map) return null;
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
        return new CoachingCard(strip, 14);
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


}
