using AimMod.Desktop.PpTargets;
using AimMod.Desktop.Visuals;
using osu.Framework.Extensions;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Effects;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;
using osuTK;

namespace AimMod.Desktop;

public partial class NativePpTargetsWorkspace
{
    /// <summary>
    /// Colour roles of the forecast visuals. Chances always carry the dice icon and the word "chance";
    /// predicted play statistics (accuracy, misses, timing, combo) are cyan; PP values are white, the target mint.
    /// </summary>
    internal static class ForecastColours
    {
        public static readonly Colour4 Chance = AimModPalette.Yellow;
        public static readonly Colour4 Performance = AimModPalette.Cyan;
        public static readonly Colour4 Pp = AimModPalette.Text;
        public static readonly Colour4 Target = AimModPalette.Accent;
        public static readonly IconUsage ChanceIcon = FontAwesome.Solid.Dice;
    }

    /// <summary>Consistent wording and number formats for forecast figures.</summary>
    internal static class ForecastText
    {
        public static string Chance(double probability) => !double.IsFinite(probability) ? "-"
            : probability >= .995 ? ">99%" : probability < .005 ? "<1%" : $"{probability * 100:0}%";

        public static string Accuracy(double accuracy) => $"{Math.Clamp(accuracy, 0, 1) * 100:0.0}%";

        public static string AccuracyRange(PpForecastAccuracy accuracy) =>
            $"{Math.Clamp(accuracy.Low, 0, 1) * 100:0.0}–{Math.Clamp(accuracy.High, 0, 1) * 100:0.0}%";

        public static string Misses(int count) => count == 1 ? "1 miss" : $"{count} misses";

        public static string MissRange(PpForecastMisses misses) => misses.Low == misses.High
            ? $"~{Misses(misses.Low)}" : $"{misses.Low}–{Misses(misses.High)}";

        public static string Offset(double meanMs) => Math.Abs(meanMs) < .5 ? "centred timing"
            : $"{Math.Abs(meanMs):0} ms {(meanMs < 0 ? "early" : "late")}";

        public static string Pp(double pp) => $"{Math.Max(0, pp):0}";

        public static string AccountGain(double? gain) => gain switch
        {
            null => "Account gain unverified",
            < .05 => "No account gain expected",
            { } value => $"+{value:0.0} account pp per try",
        };

        /// <summary>The compact predicted-performance strip of a row, e.g. "≈97.1% acc · ~2 misses · 12 ms early".</summary>
        public static string PerformanceSummary(PpTargetForecastBreakdown? breakdown)
        {
            if (breakdown is null) return string.Empty;
            var parts = new List<string> { $"≈{Accuracy(breakdown.Accuracy.Median)} acc", MissRange(breakdown.Misses) };
            if (breakdown.Timing is { } timing) parts.Add(Offset(timing.MeanOffsetMs));
            return string.Join("  ·  ", parts);
        }

        public static string Confidence(PpTargetConfidence confidence) => confidence switch
        {
            PpTargetConfidence.High => "High confidence",
            PpTargetConfidence.Medium => "Medium confidence",
            PpTargetConfidence.Low => "Low confidence",
            _ => "Limited evidence",
        };

        public static Colour4 ConfidenceColour(PpTargetConfidence confidence) => confidence switch
        {
            PpTargetConfidence.High => AimModPalette.Success,
            PpTargetConfidence.Medium => AimModPalette.Cyan,
            _ => AimModPalette.Yellow,
        };
    }

    /// <summary>The difficulty colour, or osu!'s gold difficulty text colour where the spectrum is too dark to read on a panel.</summary>
    internal static Colour4 ReadableDifficultyColour(double starRating)
    {
        Colour4 colour = AimModVisualStyle.DifficultyColour(starRating);
        double luminance = .2126 * colour.R + .7152 * colour.G + .0722 * colour.B;
        return luminance >= .22 ? colour : starRating >= 6.5 ? AimModVisualStyle.DifficultyTextColour(starRating) : AimModPalette.Text;
    }

    /// <summary>A chance figure: dice icon, the percentage and the word "chance", then a muted qualifier.</summary>
    internal static FillFlowContainer ChanceLabel(double probability, string qualifier, float size = 11, bool wrap = false)
    {
        var flow = new FillFlowContainer
        {
            AutoSizeAxes = Axes.Both,
            Direction = wrap ? FillDirection.Vertical : FillDirection.Horizontal,
            Spacing = new(4, 1),
        };
        flow.Add(new FillFlowContainer
        {
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Horizontal,
            Spacing = new(4, 0),
            Children = new Drawable[]
            {
                new SpriteIcon
                {
                    Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft,
                    Icon = ForecastColours.ChanceIcon, Size = new(Math.Max(10, size - 1)), Colour = ForecastColours.Chance,
                },
                text($"{ForecastText.Chance(probability)} chance", size, ForecastColours.Chance, "Bold")
                    .With(d => { d.Anchor = Anchor.CentreLeft; d.Origin = Anchor.CentreLeft; }),
            },
        });
        if (qualifier.Length > 0)
            flow.Add(text(qualifier, size, AimModPalette.Muted, "SemiBold"));
        return flow;
    }

    /// <summary>
    /// The visual forecast: predicted performance, how it becomes PP, the chances and the evidence. Used for the
    /// row popover and, without the header, in the details pane. Tiles use two columns when there is room.
    /// </summary>
    internal sealed partial class PpForecastCard : FillFlowContainer
    {
        private const float gap = 8;
        private readonly List<Container> tiles = [];
        private readonly List<Container> cells = [];
        private float laidOutWidth = -1;

        public PpForecastCard(PpTargetCandidate target, bool header)
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            Direction = FillDirection.Vertical;
            Spacing = new(0, 5);
            var forecast = target.Forecast;
            var breakdown = forecast?.Breakdown;
            if (header)
            {
                Add(new TruncatingSpriteText
                {
                    RelativeSizeAxes = Axes.X, Text = target.Title, Font = new FontUsage(size: 14, weight: "Bold"), Colour = AimModPalette.Text,
                });
                Add(new TruncatingSpriteText
                {
                    RelativeSizeAxes = Axes.X,
                    Text = $"[{target.Difficulty}]  {AimModVisualStyle.FormatStarRating(target.StarRating)}  ·  {target.ReadinessLabel}",
                    Font = new FontUsage(size: 11, weight: "Bold"), Colour = ReadableDifficultyColour(target.StarRating),
                });
            }

            if (breakdown is not null && forecast is not null)
            {
                Add(heading(FontAwesome.Solid.Crosshairs, "Predicted performance", "if the attempt passes"));
                Add(tileFlow(accuracyTile(breakdown), missesTile(breakdown), timingTile(breakdown), comboTile(breakdown)));
                Add(heading(FontAwesome.Solid.LongArrowAltRight, "Leads to PP", null));
                Add(chain(breakdown, forecast));
                Add(ppStrip(breakdown, forecast));
            }
            else
                Add(caption(target.Estimate is null ? "PP calculation pending. Predicted accuracy and misses appear once it finishes."
                    : "Predicted accuracy and misses need comparable completed plays.", AimModPalette.Muted));

            Add(heading(ForecastColours.ChanceIcon, "Chances", null, ForecastColours.Chance));
            Add(tileFlow(passTile(target), targetTile(target)));

            Add(heading(FontAwesome.Solid.Lightbulb, "Why we think this", "skill fit by pattern"));
            Add(evidence(target));
            Add(accountGain(target));
        }

        protected override void Update()
        {
            base.Update();
            if (DrawWidth == laidOutWidth) return;
            laidOutWidth = DrawWidth;
            // Tiles pair up from sidebar width; the labelled skill-fit cells need a little more room.
            arrange(tiles, DrawWidth >= 290);
            arrange(cells, DrawWidth >= 380);
        }

        private static void arrange(IEnumerable<Container> items, bool twoColumns)
        {
            foreach (var item in items)
            {
                int index = item.Parent is FillFlowContainer flow ? flow.IndexOf(item) : 0;
                item.Width = twoColumns ? .5f : 1;
                item.Padding = twoColumns
                    ? new MarginPadding { Left = index % 2 == 1 ? gap / 2 : 0, Right = index % 2 == 0 ? gap / 2 : 0 }
                    : new MarginPadding();
            }
        }

        private FillFlowContainer tileFlow(params Drawable[] contents)
        {
            var flow = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(0, gap),
            };
            foreach (var content in contents)
            {
                var wrapper = new Container
                {
                    RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Width = .5f,
                    Child = new Container
                    {
                        RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Masking = true, CornerRadius = AimModVisualStyle.ControlRadius,
                        Children = new[]
                        {
                            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                            content,
                        },
                    },
                };
                tiles.Add(wrapper);
                flow.Add(wrapper);
            }
            return flow;
        }

        private static FillFlowContainer tileBody(IconUsage icon, string label, string value, Colour4 valueColour, params Drawable[] rows)
        {
            var body = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(0, 4),
                Padding = new MarginPadding { Horizontal = 8, Vertical = 7 },
            };
            body.Add(new Container
            {
                RelativeSizeAxes = Axes.X, Height = 18,
                Children = new Drawable[]
                {
                    new SpriteIcon { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Icon = icon, Size = new(11), Colour = AimModPalette.Muted },
                    text(label, 11, AimModPalette.Muted, "Bold").With(d => { d.Anchor = Anchor.CentreLeft; d.Origin = Anchor.CentreLeft; d.X = 16; }),
                    text(value, 15, valueColour, "Bold").With(d => { d.Anchor = Anchor.CentreRight; d.Origin = Anchor.CentreRight; }),
                },
            });
            body.AddRange(rows);
            return body;
        }

        private static Drawable accuracyTile(PpTargetForecastBreakdown breakdown)
        {
            var accuracy = breakdown.Accuracy;
            double span = Math.Max(.004, accuracy.High - accuracy.Low);
            double minimum = Math.Max(0, accuracy.Low - span * 1.5 - .003), maximum = Math.Min(1, accuracy.High + span * 1.5 + .003);
            var strip = new AimModRangeStrip(minimum, maximum,
                [new AimModStripBand(accuracy.Low, accuracy.High, ForecastColours.Performance, .55f)],
                [new AimModStripMarker(accuracy.Median, AimModPalette.Text)]);
            return tileBody(FontAwesome.Solid.Bullseye, "Accuracy", ForecastText.Accuracy(accuracy.Median), ForecastColours.Performance,
                strip, edgeLabels(ForecastText.Accuracy(minimum), ForecastText.Accuracy(maximum)),
                caption($"Usually {ForecastText.AccuracyRange(accuracy)}", AimModPalette.Muted));
        }

        private static Drawable missesTile(PpTargetForecastBreakdown breakdown)
        {
            var misses = breakdown.Misses;
            string[] labels = ["0", "1", "2", "3", "4", "5+"];
            var columns = labels.Select((label, i) => (label, i < misses.Buckets.Count ? misses.Buckets[i] : 0)).ToArray();
            string range = misses.Low == misses.High ? $"{misses.Low}" : $"{misses.Low}–{misses.High}";
            return tileBody(FontAwesome.Solid.TimesCircle, "Misses", range, ForecastColours.Performance,
                new AimModProbabilityBars(columns, ForecastColours.Performance, misses.MostLikely, 24),
                caption($"Most likely {labels[misses.MostLikely]} · average {misses.Expected:0.#}", AimModPalette.Muted),
                ChanceLabel(misses.FullComboChance, "of a full combo"));
        }

        private static Drawable timingTile(PpTargetForecastBreakdown breakdown)
        {
            if (breakdown.Timing is not { } timing)
                return tileBody(FontAwesome.Solid.Stopwatch, "Timing", "-", AimModPalette.Muted,
                    caption("Timing needs analysed replays on similar maps.", AimModPalette.Muted));
            double deviation = timing.UnstableRate / 10;
            double limit = Math.Clamp(Math.Ceiling((Math.Abs(timing.MeanOffsetMs) + deviation + 4) / 10) * 10, 20, 80);
            var strip = new AimModRangeStrip(-limit, limit,
                [new AimModStripBand(timing.MeanOffsetMs - deviation, timing.MeanOffsetMs + deviation, ForecastColours.Performance, .4f)],
                [new AimModStripMarker(0, AimModPalette.Muted, Tick: true), new AimModStripMarker(timing.MeanOffsetMs, AimModPalette.Text)]);
            return tileBody(FontAwesome.Solid.Stopwatch, "Timing", ForecastText.Offset(timing.MeanOffsetMs), ForecastColours.Performance,
                strip, edgeLabels($"early {limit:0} ms", $"late {limit:0} ms"),
                caption($"UR ≈ {timing.UnstableRate:0}  ·  {timing.Plays} replay{(timing.Plays == 1 ? "" : "s")}", AimModPalette.Muted));
        }

        private static Drawable comboTile(PpTargetForecastBreakdown breakdown)
        {
            var combo = breakdown.Combo;
            var strip = new AimModRangeStrip(0, Math.Max(1, combo.Maximum),
                [new AimModStripBand(0, combo.Typical, ForecastColours.Performance, .55f)],
                [new AimModStripMarker(combo.Maximum, AimModPalette.Muted, Tick: true)]);
            return tileBody(FontAwesome.Solid.Link, "Max combo", $"~{combo.Typical:N0}x", ForecastColours.Performance,
                strip, edgeLabels("0x", $"{combo.Maximum:N0}x max"),
                caption($"Typical with {ForecastText.Misses(breakdown.TypicalMisses)}", AimModPalette.Muted));
        }

        /// <summary>
        /// The explicit chain from a likely play to the headline PP. It ends at the first try PP itself (the average
        /// over likely plays), so one PP number is shown; calculator and calibration details stay in a muted line.
        /// </summary>
        private static Drawable chain(PpTargetForecastBreakdown breakdown, PpTargetForecast forecast)
        {
            var flow = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(6, 4),
            };
            flow.Add(new AimModChip($"{ForecastText.Accuracy(breakdown.TypicalAccuracy)} acc", ForecastColours.Performance));
            flow.Add(new AimModChip(ForecastText.Misses(breakdown.TypicalMisses), ForecastColours.Performance));
            flow.Add(new AimModChip($"{breakdown.TypicalCombo:N0}x", ForecastColours.Performance));
            flow.Add(new SpriteIcon { Icon = FontAwesome.Solid.LongArrowAltRight, Size = new(14), Colour = AimModPalette.Muted, Margin = new MarginPadding { Top = 2 } });
            flow.Add(new AimModChip($"{(forecast.PreviousTries > 0 ? "Next" : "First")} try {ForecastText.Pp(forecast.PpIfPass)} pp", ForecastColours.Pp, backgroundAlpha: .12f));
            var adjustments = new List<string> { "Average of the official calculator over your likely plays" };
            if (Math.Abs(breakdown.CalibrationFactor - 1) >= .005) adjustments.Add($"×{breakdown.CalibrationFactor:0.00} to match your recorded PP");
            if (Math.Abs(breakdown.LearningAdjustment - 1) >= .005) adjustments.Add($"×{breakdown.LearningAdjustment:0.00} retry trend");
            return new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(0, 3),
                Children = new Drawable[] { flow, caption(string.Join(" · ", adjustments), AimModPalette.Muted) },
            };
        }

        private static Drawable ppStrip(PpTargetForecastBreakdown breakdown, PpTargetForecast forecast)
        {
            var pp = breakdown.Pp;
            double ceiling = pp.FullComboCeiling > 0 ? pp.FullComboCeiling : pp.P90;
            double maximum = Math.Max(ceiling, forecast.TargetPp ?? 0);
            double minimum = Math.Max(0, pp.P10 - (maximum - pp.P10) * .15);
            var markers = new List<AimModStripMarker> { new(pp.Mean, ForecastColours.Pp) };
            if (forecast.TargetPp is { } target) markers.Add(new(target, ForecastColours.Target));
            if (pp.FullComboCeiling > 0) markers.Add(new(pp.FullComboCeiling, AimModPalette.Muted, Tick: true));
            var strip = new AimModRangeStrip(minimum, maximum * 1.01,
                [new AimModStripBand(pp.P10, pp.P90, ForecastColours.Pp, .16f), new AimModStripBand(pp.P25, pp.P75, ForecastColours.Pp, .38f)],
                markers, 10);
            var legend = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(12, 2),
            };
            legend.Add(legendItem(ForecastColours.Pp, forecast.PreviousTries > 0 ? "Next try" : "First try"));
            if (forecast.TargetPp is { } targetPp) legend.Add(legendItem(ForecastColours.Target, $"Target {ForecastText.Pp(targetPp)} pp"));
            legend.Add(legendItem(ForecastColours.Pp, $"Usually {ForecastText.Pp(pp.P10)}–{ForecastText.Pp(pp.P90)} pp", .38f));
            if (pp.FullComboCeiling > 0) legend.Add(legendItem(AimModPalette.Muted, $"FC {ForecastText.Pp(pp.FullComboCeiling)} pp"));
            return new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(0, 4),
                Children = new Drawable[] { strip, legend },
            };
        }

        private static Drawable passTile(PpTargetCandidate target)
        {
            if (target.PassEstimate is not { } pass)
                return tileBody(ForecastColours.ChanceIcon, "Pass", "-", AimModPalette.Muted,
                    caption("Pass chance unknown: more comparable pass and fail history is needed.", AimModPalette.Muted));
            var strip = new AimModRangeStrip(0, 1,
                [new AimModStripBand(0, pass.Probability, ForecastColours.Chance, .75f), new AimModStripBand(pass.Lower, pass.Upper, ForecastColours.Chance, .3f)],
                [new AimModStripMarker(pass.Probability, AimModPalette.Text)]);
            return tileBody(ForecastColours.ChanceIcon, "Pass", $"{ForecastText.Chance(pass.Probability)} chance", ForecastColours.Chance,
                strip, caption($"Per attempt · range {ForecastText.Chance(pass.Lower)}–{ForecastText.Chance(pass.Upper)}", AimModPalette.Muted));
        }

        private static Drawable targetTile(PpTargetCandidate target)
        {
            if (target.Forecast is not { TargetPp: { } pp, ReachProbability: { } reach } forecast)
                return tileBody(FontAwesome.Solid.FlagCheckered, "Target", "-", AimModPalette.Muted,
                    caption("No target: passing within a few tries is unlikely or unknown.", AimModPalette.Muted));
            var strip = new AimModRangeStrip(0, 1, [new AimModStripBand(0, reach, ForecastColours.Chance, .75f)],
                [new AimModStripMarker(PpTargetForecastModel.TargetReach, AimModPalette.Muted, Tick: true)]);
            string tries = $"~{forecast.Tries} {(forecast.PreviousTries > 0 ? "more " : "")}tries";
            return tileBody(FontAwesome.Solid.FlagCheckered, "Target", $"{ForecastText.Pp(pp)} pp", ForecastColours.Target,
                strip, ChanceLabel(reach, $"in {tries} (best score)"));
        }

        private Drawable evidence(PpTargetCandidate target)
        {
            var flow = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(0, 5),
            };
            var fits = target.Estimate?.PatternPrediction?.PatternFits ?? [];
            if (fits.Count == 0)
                flow.Add(caption("Skill fit unmeasured: more analysed replays are needed.", AimModPalette.Muted));
            else
            {
                var grid = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(0, 3),
                };
                foreach (var fit in fits.Take(6))
                {
                    var cell = new Container
                    {
                        RelativeSizeAxes = Axes.X, Width = .5f, Height = 16,
                        Child = new Container
                        {
                            RelativeSizeAxes = Axes.Both,
                            Children = new Drawable[]
                            {
                                new TruncatingSpriteText
                                {
                                    Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Text = fit.Pattern, MaxWidth = 88,
                                    Font = new FontUsage(size: 11, weight: "SemiBold"), Colour = AimModPalette.Text,
                                },
                                new Container
                                {
                                    Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, RelativeSizeAxes = Axes.X, Height = 8,
                                    Padding = new MarginPadding { Left = 92, Right = 40 },
                                    Child = new AimModRangeStrip(0, 1, fit.Fit is { } value ? [new AimModStripBand(0, value, AimModPalette.CyanDark)] : null, trackHeight: 8)
                                    { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft },
                                },
                                text(fit.Fit is { } shown ? $"{shown:P0}" : "n/a", 11, fit.Fit is null ? AimModPalette.Muted : AimModPalette.Text, "SemiBold")
                                    .With(d => { d.Anchor = Anchor.CentreRight; d.Origin = Anchor.CentreRight; }),
                            },
                        },
                    };
                    cells.Add(cell);
                    grid.Add(cell);
                }
                flow.Add(grid);
            }
            var badges = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(6, 4), Margin = new MarginPadding { Top = 2 },
            };
            var confidence = target.Forecast?.Confidence ?? target.RecommendationConfidence;
            badges.Add(new AimModChip(ForecastText.Confidence(confidence), ForecastText.ConfidenceColour(confidence), FontAwesome.Solid.ShieldAlt));
            if (target.Forecast is { } forecast)
            {
                badges.Add(new AimModChip($"{forecast.EffectiveSamples:0} weighted passes · {forecast.EvidenceMaps:N0} maps", AimModPalette.Muted, backgroundAlpha: .1f));
                badges.Add(new AimModChip(forecast.CalibrationSamples > 0
                    ? $"Calibrated {forecast.CalibrationFactor:0.00}× · {forecast.CalibrationSamples} PP scores" : "Not calibrated yet",
                    AimModPalette.Muted, FontAwesome.Solid.SlidersH, .1f));
                if (forecast.LearningSessions > 0)
                    badges.Add(new AimModChip($"Retry trend {forecast.LearningAdjustment:0.00}× · {forecast.LearningSessions} sessions", AimModPalette.Muted, backgroundAlpha: .1f));
                if (forecast.CrossMode)
                    badges.Add(new AimModChip("Includes your other scoring mode", AimModPalette.Yellow, backgroundAlpha: .1f));
            }
            if (target.PassEstimate is { } pass)
                badges.Add(new AimModChip(pass.SameMap ? $"{pass.Attempts} attempts on this map" : $"{pass.Attempts} attempts · {pass.Maps} similar maps",
                    AimModPalette.Muted, backgroundAlpha: .1f));
            flow.Add(badges);
            return flow;
        }

        private static Drawable accountGain(PpTargetCandidate target) => new Container
        {
            RelativeSizeAxes = Axes.X, Height = 24, Masking = true, CornerRadius = AimModVisualStyle.ControlRadius,
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                text("Account gain", 11, AimModPalette.Muted, "Bold").With(d => { d.Anchor = Anchor.CentreLeft; d.Origin = Anchor.CentreLeft; d.X = 8; }),
                text(target.EstimatedAccountGainPp is { } gain && gain >= .05 ? $"+{gain:0.0} pp per try" : ForecastText.AccountGain(target.EstimatedAccountGainPp),
                    target.EstimatedAccountGainPp is >= .05 ? 14 : 11, target.EstimatedAccountGainPp is >= .05 ? AimModPalette.Success : AimModPalette.Muted, "Bold")
                    .With(d => { d.Anchor = Anchor.CentreRight; d.Origin = Anchor.CentreRight; d.X = -8; }),
            },
        };

        private static Drawable heading(IconUsage icon, string title, string? qualifier, Colour4? colour = null) => new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Horizontal, Spacing = new(6, 0),
            Margin = new MarginPadding { Top = 3 },
            Children = new Drawable[]
            {
                new SpriteIcon { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Icon = icon, Size = new(12), Colour = colour ?? AimModPalette.Cyan },
                text(title, 13, AimModPalette.Text, "Bold").With(d => { d.Anchor = Anchor.CentreLeft; d.Origin = Anchor.CentreLeft; }),
                text(qualifier ?? string.Empty, 11, AimModPalette.Muted).With(d => { d.Anchor = Anchor.CentreLeft; d.Origin = Anchor.CentreLeft; }),
            },
        };

        private static Drawable edgeLabels(string left, string right) => new Container
        {
            RelativeSizeAxes = Axes.X, Height = 13,
            Children = new Drawable[]
            {
                text(left, 11, AimModPalette.Muted),
                text(right, 11, AimModPalette.Muted).With(d => { d.Anchor = Anchor.TopRight; d.Origin = Anchor.TopRight; }),
            },
        };

        private static Drawable legendItem(Colour4 colour, string label, float alpha = 1) => new FillFlowContainer
        {
            AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(4, 0),
            Children = new Drawable[]
            {
                new Box { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Size = new(8, 8), Colour = colour, Alpha = alpha },
                text(label, 11, AimModPalette.Muted, "SemiBold").With(d => { d.Anchor = Anchor.CentreLeft; d.Origin = Anchor.CentreLeft; }),
            },
        };

        private static TextFlowContainer caption(string value, Colour4 colour) => new(sprite =>
        {
            sprite.Font = new FontUsage(size: AimModVisualStyle.MinReadableFontSize);
            sprite.Colour = colour;
        })
        {
            RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = value,
        };
    }

    /// <summary>
    /// Row popover hosted by the game's tooltip container, which draws above every workspace layer so no row,
    /// scroll mask or detail pane can cover it. Its position is clamped inside that container at any window size.
    /// </summary>
    internal sealed partial class PpForecastTooltip : VisibilityContainer, ITooltip<PpTargetCandidate>
    {
        public const float TooltipWidth = 410;
        private const float edge = 6;
        private readonly Container body;
        private PpTargetCandidate? shown;

        public PpTargetCandidate? ShownContent => shown;

        public PpForecastTooltip()
        {
            Width = TooltipWidth;
            AutoSizeAxes = Axes.Y;
            Masking = true;
            CornerRadius = AimModVisualStyle.CardRadius;
            BorderThickness = 1;
            BorderColour = AimModPalette.Border;
            EdgeEffect = new EdgeEffectParameters { Type = EdgeEffectType.Shadow, Colour = Colour4.Black.Opacity(.45f), Radius = 14, Offset = new(0, 4) };
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Header },
                body = new Container { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Padding = new MarginPadding(12) },
            };
        }

        public void SetContent(PpTargetCandidate content)
        {
            // Hovering the same row again, or the container refreshing, must not rebuild (and flicker) the card.
            if (ReferenceEquals(content, shown)) return;
            shown = content;
            body.Child = new PpForecastCard(content, true);
        }

        public void Move(Vector2 pos) => Position = Clamp(pos, DrawSize, Parent?.DrawSize);

        /// <summary>Keeps the whole popover inside its container, preferring the requested position.</summary>
        internal static Vector2 Clamp(Vector2 position, Vector2 size, Vector2? bounds)
        {
            if (bounds is not { } area) return position;
            float x = Math.Clamp(position.X, edge, Math.Max(edge, area.X - size.X - edge));
            float y = Math.Clamp(position.Y, edge, Math.Max(edge, area.Y - size.Y - edge));
            return new(x, y);
        }

        protected override void PopIn() => this.FadeIn(140, Easing.OutQuint);

        protected override void PopOut() => this.FadeOut(90, Easing.OutQuint);
    }
}
