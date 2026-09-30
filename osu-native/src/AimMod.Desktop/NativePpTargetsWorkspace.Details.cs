using AimMod.Desktop.PpTargets;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using osu.Framework.Extensions;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;

namespace AimMod.Desktop;

public partial class NativePpTargetsWorkspace
{
    /// <summary>What each displayed PP number means for this target, for the row tooltip and the detail pane.</summary>
    internal static IReadOnlyList<string> ForecastExplanation(PpTargetCandidate target)
    {
        if (target.Forecast is not { } forecast)
            return target.Estimate is { } pp
                ? [$"PP if you pass: {pp.ExpectedPp:0} (calculated score; the pass chance and target need more history)."]
                : [];
        string attempt = forecast.PreviousTries > 0 ? "next try" : "first try";
        var lines = new List<string>
        {
            $"{char.ToUpperInvariant(attempt[0])}{attempt[1..]} PP {forecast.PpIfPass:0} is the PP you can expect if this attempt passes "
            + $"(typically {forecast.PpIfPassRange.Minimum:0}-{forecast.PpIfPassRange.Maximum:0}). It is not reduced by the pass chance.",
            forecast.PassChance is { } chance
                ? $"Pass chance: {chance:P0} per attempt{(forecast.CrossMode ? ", partly from your other scoring mode" : "")}."
                : "Pass chance unknown: more comparable pass/fail history is needed.",
            forecast.TargetPp is { } targetPp
                ? $"Target PP {targetPp:0}: your best score within ~{forecast.Tries} {(forecast.PreviousTries > 0 ? "more " : "")}tries reaches it with about {forecast.ReachProbability:P0} chance. Failed tries earn nothing; capped at the full-combo ceiling."
                : "Target PP unavailable: passing within a few tries is too unlikely or the pass chance is unknown.",
            $"{forecast.Confidence} confidence: {forecast.EffectiveSamples:0.#} weighted passes on {forecast.EvidenceMaps} similar maps"
            + (forecast.CrossMode ? ", including plays from your other scoring mode" : "")
            + (forecast.UsesBestScores ? ", topped up with submitted best scores" : "") + ".",
            forecast.CalibrationSamples > 0
                ? $"Calibrated with {forecast.CalibrationSamples} recorded PP score{(forecast.CalibrationSamples == 1 ? "" : "s")}: {forecast.CalibrationFactor:0.00}x the calculator at the same statistics."
                : "Not calibrated: no comparable recorded PP scores yet.",
        };
        return lines;
    }

    internal sealed record DetailSection(string Heading, IReadOnlyList<string> Lines);

    internal const string PpPredictionHeading = "PP prediction";
    internal const string PassAndGainHeading = "Pass and account gain";
    internal const string PatternFitHeading = "Pattern demand and fit";

    /// <summary>Plain-text copy of the target details; the pane draws the forecast sections visually instead.</summary>
    internal static IReadOnlyList<string> TargetDetails(PpTargetCandidate target, OfficialBeatmapSet set, PpPatternProfile? profile) =>
        TargetDetailSections(target, set, profile)
            .SelectMany(section => section.Heading.Length == 0 ? section.Lines : section.Lines.Prepend(section.Heading)).ToArray();

    internal static IReadOnlyList<DetailSection> TargetDetailSections(PpTargetCandidate target, OfficialBeatmapSet set, PpPatternProfile? profile)
    {
        var difficulty = set.Difficulties.FirstOrDefault(d => d.BeatmapId == target.BeatmapId);
        var sections = new List<DetailSection>
        {
            new(string.Empty,
            [
                target.Title,
                $"{target.Artist} / {target.Creator}",
                $"[{target.Difficulty}]",
                $"{target.StarRating:0.00} stars / {target.Bpm:0} BPM / {Math.Max(0, target.TotalLengthSeconds) / 60}:{Math.Max(0, target.TotalLengthSeconds) % 60:00}",
                $"{set.Status} / {(target.SuggestedMods.Count == 0 ? "NM" : string.Join(" + ", target.SuggestedMods))}",
                $"AR {difficulty?.ApproachRate:0.#} / OD {difficulty?.OverallDifficulty:0.#} / CS {difficulty?.CircleSize:0.#} / HP {difficulty?.DrainRate:0.#}",
                target.MaximumCombo is { } combo ? $"Maximum combo: {combo:N0}x" : "Maximum combo unavailable",
            ]),
        };
        var prediction = new List<string> { target.ReadinessLabel };
        if (target.Estimate is { } pp)
        {
            string setup = ScoreMods.Configuration(target.SuggestedMods, pp.ModsJson ?? "", PpTargetMods.NormaliseForSkill);
            var sessionPatterns = profile?.SessionForm is { } form && form.ExpiresAt > DateTimeOffset.UtcNow
                ? form.Patterns.Where(p => p.Setup == setup && p.LegacyScore == pp.LegacyScore).ToArray() : [];
            prediction.AddRange(ForecastExplanation(target));
            if (target.Forecast is { LearningSessions: > 0 } learned)
                prediction.Add($"Retry trend: {learned.LearningSessions} recorded practice sessions on similar maps scale the next pass by {learned.LearningAdjustment:0.00}x. Failed tries are counted in the pass chance, not as zero PP.");
            prediction.Add(target.ExpectedEarnedPp is { } earned
                ? $"Expected earned per attempt (ranking only): {earned:0.0} PP, PP if you pass times pass chance."
                : "Expected earned PP needs more comparable completed and failed plays.");
            prediction.Add($"100% FC ceiling: {pp.RealisticMaximumPp:0.0} PP");
            if (target.Forecast?.Breakdown is { } breakdown)
                prediction.Add($"Predicted pass: {ForecastText.Accuracy(breakdown.Accuracy.Median)} accuracy (usually {ForecastText.AccuracyRange(breakdown.Accuracy)}), "
                    + $"usually {ForecastText.MissRange(breakdown.Misses)}, {ForecastText.Chance(breakdown.Misses.FullComboChance)} full-combo chance"
                    + (breakdown.Timing is { } timing ? $", {ForecastText.Offset(timing.MeanOffsetMs)} with UR {timing.UnstableRate:0}." : "."));
            else if (pp.Outcome is { } outcome)
                prediction.Add($"Fitted passes: {outcome.Distribution.MissMean:0.0} misses on average, {outcome.Distribution.HitAccuracy:P1} accuracy on hit objects, "
                    + $"{outcome.Distribution.FullComboEfficiency:P0} of max combo without misses. {outcome.Scenarios.Count} official PP scenarios.");
            prediction.Add(pp.PatternPrediction?.ExpectedAccuracy is { } accuracy
                ? $"Projected head accuracy: {accuracy:P1}" : target.PassEstimate?.ConditionalAccuracy is { } observed
                    ? $"Accuracy from comparable completed plays: {observed:P1}" : "General accuracy fallback; comparable score evidence is missing");
            sections.Add(new(PpPredictionHeading, prediction));
            if (sessionPatterns.Length > 0)
            {
                var session = sessionPatterns.Where(p => p.Pattern != "Overall" || sessionPatterns.Length == 1)
                    .Select(pattern => $"{pattern.Pattern}: {pattern.Observation}. Compared {pattern.Plays} recent plays across {pattern.Maps} maps with "
                        + (pattern.UsesSimilarMaps ? "similar patterns in your earlier plays." : "your earlier results on those maps.")).ToList();
                session.Add("Low-confidence session estimate. Relevant patterns make a small adjustment to projected accuracy and misses. Improving results may reflect warming up; the cause is uncertain. Adjustments expire after an hour without a comparable play.");
                sections.Add(new("Current session", session));
            }
            else sections.Add(new("Session form", [profile?.SessionForm?.StatusFor(setup, pp.LegacyScore, DateTimeOffset.UtcNow)
                ?? "Session form: recent replay results are not available yet. Your longer-term skill profile is used."]));
        }
        else
        {
            prediction.Add("PP calculation pending");
            sections.Add(new(PpPredictionHeading, prediction));
        }
        var skill = new List<string>();
        if (profile is null) skill.Add("Skill evidence loading");
        else
        {
            var support = PpTargetPatternModel.ScoreFit(profile, target.StarRating, target.SuggestedMods);
            skill.Add($"{support.Maps} comparable score-supported maps");
            skill.Add($"{profile.Evidence.Select(e => e.MapKey).Distinct().Count()} exact-replay maps in profile");
        }
        sections.Add(new("Skill evidence (30 days)", skill));
        var fits = new List<string>();
        var notes = new List<string>();
        if (target.Estimate?.PatternPrediction is { } patterns)
        {
            foreach (var pattern in patterns.PatternFits)
                fits.Add(pattern.Fit is { } fit
                    ? $"{pattern.Pattern}: {fit:P0} fit / {pattern.DistinctMaps} comparable maps"
                    : $"{pattern.Pattern}: unmeasured / {pattern.DistinctMaps} comparable maps");
            notes.AddRange(patterns.Strengths);
            notes.AddRange(patterns.Risks);
            notes.AddRange(patterns.CoverageNotes ?? []);
        }
        else fits.Add("Pattern fit unmeasured");
        sections.Add(new(PatternFitHeading, fits));
        if (notes.Count > 0) sections.Add(new("Pattern notes", notes));
        var pass = new List<string>
        {
            target.PassEstimate is { } estimate
                ? $"Estimated pass: {estimate.Probability:P0} ({estimate.Lower:P0}-{estimate.Upper:P0}); {estimate.Attempts} attempts {(estimate.SameMap ? "on this difficulty" : $"across {estimate.Maps} maps")}. {estimate.Confidence} confidence; {(estimate.DurationAdjusted ? "adjusted from shorter maps; stamina is unverified" : estimate.BroaderComparison ? "broader comparisons" : "matching mod setup")}."
                : "Pass chance unknown",
            target.EstimatedAccountGainPp is { } gain ? $"Expected account gain per attempt: +{gain:0.0} PP" : "Account gain unverified",
        };
        if (target.AccountGainPerMinute is { } perMinute) pass.Add($"Account gain per minute: +{perMinute:0.0} PP");
        sections.Add(new(PassAndGainHeading, pass));
        return sections;
    }

    private partial class PpTargetDetails : CompositeDrawable
    {
        private readonly AimModScrollContainer scroll;
        private readonly SpriteText actionStatus;
        private readonly ClickableContainer back;
        private readonly ClickableContainer saveButton;
        private readonly Container contentViewport;
        private readonly PpForecastCard forecastCard;
        private bool busy;
        public bool IsBusy => busy;
        public float ScrollPosition => (float)scroll.Current;
        public void SetBackVisible(bool visible)
        {
            back.Alpha = visible ? 1 : 0;
            contentViewport.Padding = new MarginPadding { Top = visible ? 38 : 0, Bottom = 80 };
        }

        public PpTargetDetails(PpTargetCandidate target, OfficialBeatmapSet set, PpPatternProfile? profile,
            Func<OfficialBeatmapSet, Task<OnlineBeatmapImportResult>> save,
            Func<int, CancellationToken, Task>? open, Action close, float scrollPosition, bool installed = false)
        {
            RelativeSizeAxes = Axes.Both;
            var body = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical,
                Spacing = new(0, 10), Padding = new MarginPadding(12),
            };
            if (target.CoverUrl is not null)
                body.Add(new Container { RelativeSizeAxes = Axes.X, Height = 72, Masking = true, CornerRadius = AimModVisualStyle.ControlRadius,
                    Child = new AimModOnlineArtworkHost(target.CoverUrl) });
            var sections = TargetDetailSections(target, set, profile);
            // A compact header keeps the forecast near the top of a short pane; the full copy stays in TargetDetails.
            var difficulty = set.Difficulties.FirstOrDefault(d => d.BeatmapId == target.BeatmapId);
            string mods = ScoreMods.Display(target.SuggestedMods, target.Estimate?.ModsJson);
            string length = $"{Math.Max(0, target.TotalLengthSeconds) / 60}:{Math.Max(0, target.TotalLengthSeconds) % 60:00}";
            (string Text, float Size, string Weight, Colour4 Colour)[] header =
            [
                (target.Title, 17, "Bold", AimModPalette.Text),
                ($"{target.Artist} / {target.Creator}", 12, "Regular", AimModPalette.Muted),
                ($"[{target.Difficulty}]  {target.StarRating:0.00}*  {target.Bpm:0} BPM  {length}  {(target.MaximumCombo is { } combo ? $"{combo:N0}x" : "-")}  {mods}",
                    12, "SemiBold", ReadableDifficultyColour(target.StarRating)),
                ($"AR {difficulty?.ApproachRate:0.#}  OD {difficulty?.OverallDifficulty:0.#}  CS {difficulty?.CircleSize:0.#}  HP {difficulty?.DrainRate:0.#}  ·  {set.Status}",
                    12, "Regular", AimModPalette.Muted),
            ];
            var headerFlow = new FillFlowContainer { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(0, 2) };
            foreach (var line in header)
                headerFlow.Add(new TextFlowContainer(sprite =>
                {
                    sprite.Font = new FontUsage(size: line.Size, weight: line.Weight);
                    sprite.Colour = line.Colour;
                }) { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = line.Text });
            body.Add(headerFlow);
            // Accuracy, misses, timing, PP, chances and skill fit are drawn; only notes without a visual stay as text.
            body.Add(forecastCard = new PpForecastCard(target, false));
            foreach (var section in sections.Skip(1).Where(s => s.Heading is not (PpPredictionHeading or PassAndGainHeading or PatternFitHeading)))
            {
                body.Add(new TextFlowContainer(sprite =>
                {
                    sprite.Font = new FontUsage(size: 13, weight: "Bold");
                    sprite.Colour = AimModPalette.Cyan;
                }) { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = section.Heading, Margin = new MarginPadding { Top = 8 } });
                foreach (string line in section.Lines)
                    body.Add(new TextFlowContainer(sprite =>
                    {
                        sprite.Font = new FontUsage(size: 12);
                        sprite.Colour = AimModPalette.Text;
                    }) { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = line });
            }
            InternalChildren =
            [
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                contentViewport = new Container
                {
                    RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Top = 38, Bottom = 80 },
                    Child = scroll = new AimModScrollContainer { RelativeSizeAxes = Axes.Both, Child = body },
                },
                back = button("Back to targets", FontAwesome.Solid.ArrowLeft, close, 12, 4, 160),
                new Container
                {
                    Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, RelativeSizeAxes = Axes.X, Height = 80,
                    Children =
                    [
                        // The selected target's pane carries the one primary action of this task area.
                        button("Open osu!", FontAwesome.Solid.Play,
                            open is null ? null : () => _ = perform(async () => { await open(target.BeatmapId, CancellationToken.None); return "Opened"; }), 12, 6, 132, primary: true),
                        saveButton = button(installed ? "Installed" : set.DownloadDisabled ? "Unavailable" : "Install", installed ? FontAwesome.Solid.Check : FontAwesome.Solid.Download,
                            installed || set.DownloadDisabled ? null : () => _ = perform(async () =>
                            {
                                var result = await save(set);
                                return result.Status == OnlineBeatmapImportStatus.Success ? "Added to osu!"
                                    : result.Status == OnlineBeatmapImportStatus.OsuInstallFailed ? "Saved locally; osu! import failed. Retry Save." : "Save failed. Try again.";
                            }), 156, 6, 132),
                        actionStatus = text(string.Empty, 11, AimModPalette.Muted).With(d => d.Position = new(12, 48)),
                    ],
                },
            ];
            Scheduler.AddDelayed(() => scroll.ScrollTo(scrollPosition, false), 50);
        }

        public void SetInstalled()
        {
            saveButton.Action = null;
            saveButton.Children.OfType<SpriteText>().Single().Text = "Installed";
            saveButton.Children.OfType<SpriteIcon>().Single().Icon = FontAwesome.Solid.Check;
        }

        private async Task perform(Func<Task<string>> action)
        {
            if (busy) return;
            busy = true;
            actionStatus.Text = "Working...";
            string message;
            try { message = await action().ConfigureAwait(false); }
            catch (Exception) { message = "Action failed. Try again."; }
            if (!IsDisposed) Schedule(() => { busy = false; actionStatus.Text = message; });
        }

        private static ClickableContainer button(string label, IconUsage icon, Action? action, float x, float y, float width, bool primary = false)
        {
            bool accent = primary && action is not null;
            Colour4 resting = accent ? AimModPalette.Accent : AimModPalette.PanelRaised;
            Colour4 foreground = action is null ? AimModPalette.Muted : accent ? AimModPalette.Canvas : AimModPalette.Text;
            return new DetailButton(resting, accent ? AimModPalette.Accent.Lighten(.15f) : AimModPalette.PanelHover)
            {
                Position = new(x, y), Size = new(width, 32), Action = action, Masking = true, CornerRadius = AimModVisualStyle.ControlRadius,
                Children =
                [
                    new Box { RelativeSizeAxes = Axes.Both, Colour = resting },
                    new SpriteIcon { Position = new(10, 9), Size = new(14), Icon = icon, Colour = action is null ? AimModPalette.Muted : accent ? AimModPalette.Canvas : AimModPalette.Cyan },
                    text(label, 11, foreground, accent ? "Bold" : "Regular").With(d => d.Position = new(32, 9)),
                ],
            };
        }

        private sealed partial class DetailButton(Colour4 resting, Colour4 hover) : ClickableContainer
        {
            protected override bool OnHover(osu.Framework.Input.Events.HoverEvent e)
            {
                if (Action is not null)
                    Children.OfType<Box>().First().FadeColour(hover, AimModVisualStyle.FastTransition);
                return Action is not null;
            }

            protected override void OnHoverLost(osu.Framework.Input.Events.HoverLostEvent e)
            {
                Children.OfType<Box>().First().FadeColour(resting, AimModVisualStyle.HoverTransition);
                base.OnHoverLost(e);
            }

            protected override bool OnMouseDown(osu.Framework.Input.Events.MouseDownEvent e)
            {
                if (Action is not null)
                    this.ScaleTo(0.97f, 80, Easing.OutQuint);
                return base.OnMouseDown(e);
            }

            protected override void OnMouseUp(osu.Framework.Input.Events.MouseUpEvent e)
            {
                this.ScaleTo(1, 160, Easing.OutQuint);
                base.OnMouseUp(e);
            }
        }
    }
}
