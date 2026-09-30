using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Desktop.ScoreHistory;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Framework.Threading;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop;

public partial class NativePpTargetsWorkspace
{
    private sealed partial class PpTargetDropdown<T> : AimMod.Desktop.Coaching.BoundedShearedDropdown<T>
        where T : notnull
    {
        private readonly Func<T, string> formatter;

        public PpTargetDropdown(Func<T, string> formatter) : base(string.Empty)
        {
            this.formatter = formatter;
            if (Header is ShearedDropdownHeader header)
            {
                // Empty external labels otherwise collapse the native 30-unit header.
                header.LabelContainer.AutoSizeAxes = Axes.X;
                header.LabelContainer.Height = 30;
            }
        }

        protected override LocalisableString GenerateItemText(T item) => formatter(item);
    }

    private sealed partial class PpTargetWorkspaceState : Container
    {
        private readonly SpriteIcon icon;
        private readonly TruncatingSpriteText title;
        private readonly TruncatingSpriteText detail;

        public PpTargetWorkspaceState()
        {
            RelativeSizeAxes = Axes.Both;
            Depth = -10;
            Alpha = 0;
            Children = new Drawable[]
            {
                icon = new SpriteIcon
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.BottomCentre,
                    Position = new(0, -24),
                    Size = new(34),
                    Colour = AimModPalette.Accent,
                },
                title = new TruncatingSpriteText
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Position = new(0, 12),
                    Font = new FontUsage(size: 21, weight: "Bold"),
                    Colour = AimModPalette.Text,
                },
                detail = new TruncatingSpriteText
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Position = new(0, 43),
                    Font = new FontUsage(size: 12, weight: "SemiBold"),
                    Colour = AimModPalette.Muted,
                },
            };
        }

        public void ShowState(IconUsage stateIcon, string heading, string description)
        {
            icon.Icon = stateIcon;
            title.Text = heading;
            detail.Text = description;
            this.FadeIn(150, Easing.OutQuint);
        }

        public void HideState() => this.FadeOut(120, Easing.OutQuint);

        protected override void Update()
        {
            base.Update();
            float maxWidth = Math.Clamp(DrawWidth - 80, 260, 560);
            title.MaxWidth = maxWidth;
            detail.MaxWidth = maxWidth;
        }
    }

    private partial class PpTargetRow : AimModInteractiveSurface, IHasTooltip
    {
        public LocalisableString TooltipText { get; }
        private readonly OfficialBeatmapSet set;
        private readonly Func<OfficialBeatmapSet, Task<OnlineBeatmapImportResult>> import;
        private readonly SpriteText saveText;
        private readonly Box saveBackground;
        private readonly FillFlowContainer details;
        private readonly TruncatingSpriteText title;
        private readonly TruncatingSpriteText artist;
        private readonly TruncatingSpriteText mapDetails;
        private readonly TruncatingSpriteText mechanicsDetails;
        private readonly TruncatingSpriteText confidenceDetails;
        private readonly TruncatingSpriteText patternDetails;
        private readonly Container artwork;
        private readonly Container expectedMetric;
        private readonly Container maximumMetric;
        private bool importing;
        private bool installed;

        public PpTargetRow(
            PpTargetCandidate candidate,
            OfficialBeatmapSet set,
            Func<OfficialBeatmapSet, Task<OnlineBeatmapImportResult>> import,
            Func<int, CancellationToken, Task>? openBeatmap,
            TargetSort ordering, bool installed = false)
        {
            this.installed = installed;
            this.set = set;
            this.import = import;
            PpPatternPrediction? pattern = candidate.Estimate?.PatternPrediction;
            string skillLabel = pattern?.Fit is { } fit ? $"{fit:P0} skill fit" : "Pattern fit unmeasured";
            string patternSummary = pattern?.Risks.FirstOrDefault()
                ?? pattern?.Strengths.FirstOrDefault() ?? "More recent replay evidence needed";
            string passDetails = candidate.PassEstimate is { } pass
                ? $"Estimated pass: {pass.Probability:P0} ({pass.Lower:P0}-{pass.Upper:P0}). {pass.Attempts} recent attempts {(pass.SameMap ? "on this difficulty" : $"across {pass.Maps} comparable maps")}. {pass.Confidence} confidence; {(pass.DurationAdjusted ? "adjusted from shorter maps; stamina is unverified" : pass.BroaderComparison ? "broader comparisons" : "matching mod setup")}."
                : "Pass chance unknown: more comparable recent pass/fail results needed.";
            string gainDetails = candidate.EstimatedAccountGainPp is { } gainPp
                ? $"Estimated account gain per attempt: +{gainPp:0.0}pp, weighted by pass chance. Uses known best plays; excludes bonus PP and unknown scores."
                : "Account gain unverified: comparable completed plays and pass evidence are needed.";
            TooltipText = string.Join("\n", new[] { candidate.ReadinessLabel, passDetails, gainDetails }
                .Concat(pattern?.Risks.Take(2) ?? []));
            if (candidate.Learning is { } retry)
                TooltipText += $"\nEstimated first/next try: {retry.FirstTryPp:0} PP. Target: {retry.TargetPp:0} PP in about {retry.LikelyTries} tries ({retry.ReachProbability:P0} chance). Low confidence; {retry.Sessions} recorded sessions on {retry.Maps} similar-pattern maps.";
            RelativeSizeAxes = Axes.X;
            Height = 112;
            CornerRadius = AimModVisualStyle.ControlRadius;
            BackgroundColour = AimModPalette.Panel;

            Colour4 difficultyColour = AimModVisualStyle.DifficultyColour(candidate.StarRating);
            string expected = candidate.DisplayedExpectedPp is { } expectedPp ? $"{expectedPp:0}" : "-";
            string maximum = candidate.Estimate is null ? "-" : $"{candidate.Estimate.RealisticMaximumPp:0}";
            bool calculated = candidate.Estimate?.Method.StartsWith("Official osu! ruleset", StringComparison.Ordinal) == true;
            string confidence = calculated ? "PP ready" : "PP pending";
            string recommendationConfidence = candidate.RecommendationConfidence switch
            {
                PpTargetConfidence.High => "high evidence",
                PpTargetConfidence.Medium => "medium evidence",
                PpTargetConfidence.Low => "low evidence",
                _ => "limited evidence",
            };
            string gain = candidate.EstimatedAccountGainPp is { } expectedGain
                ? $"   /   +{expectedGain:0.0} account pp / try"
                : string.Empty;
            string personalPass = candidate.PassEstimate is { } predictedPass ? $"{predictedPass.Probability:P0} est. pass" : "Pass unverified";
            (string priorityCaption, string priorityValue, string priorityDetail) = ordering switch
            {
                TargetSort.AccountGain => ("ACCOUNT PP GAIN", candidate.EstimatedAccountGainPp is { } account ? $"+{account:0.0}" : "-", personalPass),
                TargetSort.GainPerMinute => ("ACCOUNT PP / MIN", candidate.AccountGainPerMinute is { } efficient ? $"+{efficient:0.0}" : "-", personalPass),
                TargetSort.PassProbability => ("EST. PASS", candidate.PassEstimate is { } chance ? $"{chance.Probability:P0}" : "-",
                    candidate.PassEstimate is { } range ? $"{range.Lower:P0}-{range.Upper:P0} range" : "More history needed"),
                _ when candidate.Learning is { } forecast => ("TARGET PP", $"{forecast.TargetPp:0}", $"~{forecast.LikelyTries} {(forecast.PreviousTries > 0 ? "more " : "")}tries"),
                _ => ("MAX PP", maximum, candidate.Estimate is null ? "pending" : "100% FC ceiling"),
            };
            string mods = ScoreMods.Display(candidate.SuggestedMods, candidate.Estimate?.ModsJson);
            OfficialBeatmapDifficulty? difficulty = set.Difficulties.FirstOrDefault(item => item.BeatmapId == candidate.BeatmapId);
            double passRate = difficulty is { PlayCount: > 0 } ? (double)difficulty.PassCount / difficulty.PlayCount : 0;
            string combo = candidate.MaximumCombo is > 0 ? $"{candidate.MaximumCombo:N0}x" : "-";

            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Y, Width = 4, Colour = difficultyColour },
                artwork = new Container
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = 136,
                    X = 4,
                    Masking = true,
                    Child = candidate.CoverUrl is null
                        ? new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised }
                        : new AimModOnlineArtworkHost(candidate.CoverUrl),
                },
                details = new FillFlowContainer
                {
                    Position = new(158, 8),
                    Width = 450,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new(1),
                    Children = new Drawable[]
                    {
                        title = new TruncatingSpriteText { Text = candidate.Title, Font = new FontUsage(size: 15, weight: "Bold"), Colour = AimModPalette.Text, MaxWidth = 450 },
                        artist = new TruncatingSpriteText { Text = $"{candidate.Artist}  /  mapped by {candidate.Creator}", Font = new FontUsage(size: 10, weight: "SemiBold"), Colour = AimModPalette.Muted, MaxWidth = 450 },
                        mapDetails = truncatingText($"[{candidate.Difficulty}]   {candidate.StarRating:0.00}*   {candidate.Bpm:0} BPM   {formatLength(candidate.TotalLengthSeconds)}   {combo}   {mods}", 10, difficultyColour, "Bold"),
                        mechanicsDetails = truncatingText(
                            $"AR {difficulty?.ApproachRate:0.#}   OD {difficulty?.OverallDifficulty:0.#}   CS {difficulty?.CircleSize:0.#}   HP {difficulty?.DrainRate:0.#}   " +
                            $"{set.Status.ToUpperInvariant()}   {set.PlayCount:N0} plays   {(passRate > 0 ? $"{passRate:P0} global pass" : "global pass rate -")}",
                            9, AimModPalette.Muted, "SemiBold"),
                        confidenceDetails = truncatingText(
                            $"{candidate.ReadinessLabel}{(candidate.PassEstimate is null ? string.Empty : $"   /   {personalPass}")}{gain}",
                            9, candidate.EvidenceTier == 2 ? AimModPalette.Success : AimModPalette.Muted, "SemiBold"),
                        patternDetails = truncatingText(candidate.IsConditionalPp
                            ? "PP for your projected score if passed. Pass chance needs more history."
                            : $"{skillLabel} / {patternSummary}", 10, AimModPalette.Muted),
                    },
                },
                expectedMetric = metric(candidate.ExpectedPpCaption, expected, AimModPalette.Cyan,
                    candidate.Learning is not null ? "low confidence" : candidate.Estimate is null ? "pending" : candidate.IsConditionalPp ? "projected score" : "per attempt"),
                maximumMetric = metric(priorityCaption, priorityValue, Colour4.FromHex("FFD45A"), priorityDetail),
                new Container
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Margin = new MarginPadding { Right = 12 },
                    Size = new(104, 76),
                    Children = new Drawable[]
                    {
                        actionButton(
                            FontAwesome.Solid.Play,
                            "Open osu!",
                            AimModPalette.Accent,
                            openBeatmap is null ? () => { } : () => _ = openBeatmap(candidate.BeatmapId, CancellationToken.None),
                            disabled: openBeatmap is null),
                        actionButton(installed ? FontAwesome.Solid.Check : FontAwesome.Solid.Download, installed ? "Installed" : set.DownloadDisabled ? "Unavailable" : "Install", AimModPalette.PanelRaised, beginImport, 41, installed || set.DownloadDisabled,
                            out saveBackground, out saveText),
                    },
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            const float actionColumn = 128;
            float metricWidth = DrawWidth < 880 ? 88 : 106;
            bool compact = DrawWidth < 980;
            artwork.Width = compact ? 86 : 136;
            details.X = compact ? 106 : 158;
            float expectedX = DrawWidth - actionColumn - metricWidth * 2;
            expectedMetric.X = expectedX;
            expectedMetric.Width = metricWidth;
            maximumMetric.X = expectedX + metricWidth;
            maximumMetric.Width = metricWidth;
            float detailWidth = Math.Max(100, expectedX - details.X - 18);
            details.Width = detailWidth;
            title.MaxWidth = detailWidth;
            artist.MaxWidth = detailWidth;
            mapDetails.MaxWidth = detailWidth;
            mechanicsDetails.MaxWidth = detailWidth;
            confidenceDetails.MaxWidth = detailWidth;
            patternDetails.MaxWidth = detailWidth;
        }

        private void beginImport()
        {
            if (importing || installed || set.DownloadDisabled)
                return;
            importing = true;
            saveText.Text = "Installing...";
            saveBackground.Colour = AimModPalette.PanelHover;
            _ = importAsync();
        }

        private async Task importAsync()
        {
            OnlineBeatmapImportResult result = await import(set).ConfigureAwait(false);
            if (!IsDisposed)
            {
                Schedule(() =>
                {
                    importing = false;
                    if (result.Status == OnlineBeatmapImportStatus.Success) { SetInstalled(); return; }
                    saveText.Text = result.Status == OnlineBeatmapImportStatus.Success ? "Installed"
                        : result.Status == OnlineBeatmapImportStatus.OsuInstallFailed ? "Retry osu!" : "Try again";
                    saveBackground.Colour = AimModPalette.PanelRaised;
                });
            }
        }

        public void SetInstalled()
        {
            installed = true;
            saveText.Text = "Installed";
            saveBackground.Colour = AimModPalette.PanelHover;
            if (saveText.Parent is Container parent)
                foreach (var icon in parent.Children.OfType<SpriteIcon>()) icon.Icon = FontAwesome.Solid.Check;
        }

        private static Container metric(string caption, string value, Colour4 colour, string detail) => new()
        {
            Size = new(106, 112),
            Children = new Drawable[]
            {
                truncatingText(caption, 8, AimModPalette.Muted, "Bold").With(drawable => { drawable.Position = new(0, 22); drawable.MaxWidth = 82; }),
                truncatingText(value, 23, colour, "Bold").With(drawable => { drawable.Position = new(0, 38); drawable.MaxWidth = 82; }),
                truncatingText(detail, 8, AimModPalette.Muted).With(drawable =>
                {
                    drawable.Position = new(0, 70);
                    drawable.MaxWidth = 82;
                }),
            },
        };

        private static ClickableContainer actionButton(IconUsage icon, string label, Colour4 colour, Action action, float y = 0, bool disabled = false) =>
            actionButton(icon, label, colour, action, y, disabled, out _, out _);

        private static ClickableContainer actionButton(
            IconUsage icon,
            string label,
            Colour4 colour,
            Action action,
            float y,
            bool disabled,
            out Box background,
            out SpriteText labelText)
        {
            background = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = disabled ? AimModPalette.PanelHover : colour,
                Alpha = disabled ? 0.55f : 1,
            };
            Colour4 foreground = disabled ? AimModPalette.Muted : colour == AimModPalette.PanelRaised ? AimModPalette.Text : AimModPalette.Canvas;
            labelText = text(label, 9, foreground, "Bold").With(drawable =>
            {
                drawable.Anchor = Anchor.CentreLeft;
                drawable.Origin = Anchor.CentreLeft;
                drawable.X = 31;
            });
            return new ClickableContainer
            {
                Position = new(0, y),
                Size = new(104, AimModVisualStyle.CompactControlHeight),
                Action = disabled ? null : action,
                Masking = true,
                CornerRadius = AimModVisualStyle.ControlRadius,
                Children = new Drawable[]
                {
                    background,
                    new SpriteIcon
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Position = new(12, 0),
                        Size = new(11),
                        Icon = icon,
                        Colour = foreground,
                    },
                    labelText,
                },
            };
        }

        private static string formatLength(int seconds) => $"{Math.Max(0, seconds) / 60}:{Math.Max(0, seconds) % 60:00}";
    }
}
