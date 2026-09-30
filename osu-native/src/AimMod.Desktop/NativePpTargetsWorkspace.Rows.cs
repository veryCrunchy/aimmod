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
        private readonly Container actionHost;

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
                actionHost = new Container
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.TopCentre,
                    Position = new(0, 66),
                    AutoSizeAxes = Axes.Both,
                },
            };
        }

        public void ShowState(IconUsage stateIcon, string heading, string description, string? actionLabel = null, Action? action = null)
        {
            icon.Icon = stateIcon;
            title.Text = heading;
            detail.Text = description;
            actionHost.Clear();
            if (actionLabel is not null && action is not null)
                actionHost.Add(new AimMod.Desktop.Coaching.WorkspaceButton(actionLabel, action, AimModPalette.Accent)
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                });
            this.FadeIn(150, Easing.OutQuint);
        }

        public void HideState() => this.FadeOut(120, Easing.OutQuint);

        private float layoutWidth = -1;

        protected override void Update()
        {
            base.Update();
            if (DrawWidth == layoutWidth) return;
            layoutWidth = DrawWidth;
            float maxWidth = Math.Clamp(DrawWidth - 80, 260, 560);
            title.MaxWidth = maxWidth;
            detail.MaxWidth = maxWidth;
        }
    }

    private partial class PpTargetRow : AimModInteractiveSurface, IHasCustomTooltip<PpTargetCandidate>
    {
        public PpTargetCandidate TooltipContent { get; }
        public ITooltip<PpTargetCandidate> GetCustomTooltip() => new PpForecastTooltip();
        private readonly OfficialBeatmapSet set;
        private readonly Func<OfficialBeatmapSet, Task<OnlineBeatmapImportResult>> import;
        private readonly SpriteText saveText;
        private readonly Box saveBackground;
        private readonly FillFlowContainer details;
        private readonly TruncatingSpriteText title;
        private readonly TruncatingSpriteText artist;
        private readonly TruncatingSpriteText mapDetails;
        private readonly TruncatingSpriteText performanceDetails;
        private readonly TruncatingSpriteText confidenceDetails;
        private readonly Container artwork;
        private readonly Container expectedMetric;
        private readonly Container maximumMetric;
        private readonly AimMod.Desktop.Coaching.WorkspaceFocusRing focusRing;
        private bool importing;
        private bool installed;

        public string Key { get; }

        public OfficialBeatmapSet Set => set;

        public bool Installed => installed;

        public void SetFocused(bool focused) => focusRing.SetVisible(focused);

        public PpTargetRow(
            PpTargetCandidate candidate,
            OfficialBeatmapSet set,
            Func<OfficialBeatmapSet, Task<OnlineBeatmapImportResult>> import,
            Func<int, CancellationToken, Task>? openBeatmap,
            TargetSort ordering, bool installed = false, string key = "")
        {
            Key = key;
            TooltipContent = candidate;
            this.installed = installed;
            this.set = set;
            this.import = import;
            PpPatternPrediction? pattern = candidate.Estimate?.PatternPrediction;
            PpTargetForecastBreakdown? breakdown = candidate.Forecast?.Breakdown;
            RelativeSizeAxes = Axes.X;
            Height = 112;
            CornerRadius = AimModVisualStyle.ControlRadius;
            BackgroundColour = AimModPalette.Panel;

            Colour4 difficultyColour = AimModVisualStyle.DifficultyColour(candidate.StarRating);
            string expected = candidate.DisplayedExpectedPp is { } expectedPp ? ForecastText.Pp(expectedPp) : "-";
            string maximum = candidate.Estimate is null ? "-" : ForecastText.Pp(candidate.Estimate.RealisticMaximumPp);
            string mods = ScoreMods.Display(candidate.SuggestedMods, candidate.Estimate?.ModsJson);
            string combo = candidate.MaximumCombo is > 0 ? $"{candidate.MaximumCombo:N0}x" : "-";
            // Least important last, so a narrow row truncates the skill fit before the account gain.
            string evidence = string.Join(" · ", new[]
            {
                candidate.ReadinessLabel,
                ForecastText.AccountGain(candidate.EstimatedAccountGainPp),
                pattern?.Fit is { } fit ? $"{fit:P0} skill fit" : "skill fit unmeasured",
            });
            string performance = breakdown is not null ? ForecastText.PerformanceSummary(breakdown)
                : candidate.Estimate is null ? "PP calculation pending"
                : "Predicted accuracy needs comparable completed plays";

            Drawable expectedDetail = candidate.PassEstimate is { } pass
                ? ChanceLabel(pass.Probability, "to pass", wrap: true)
                : text(candidate.Estimate is null ? "pending" : "if you pass", 11, AimModPalette.Muted, "SemiBold");
            (string priorityCaption, string priorityValue, Colour4 priorityColour, Drawable priorityDetail) = ordering switch
            {
                TargetSort.AccountGain => ("ACCOUNT GAIN", candidate.EstimatedAccountGainPp is { } account ? $"+{account:0.0}" : "-", AimModPalette.Success,
                    (Drawable)text("pp per try", 11, AimModPalette.Muted, "SemiBold")),
                TargetSort.GainPerMinute => ("ACCOUNT GAIN", candidate.AccountGainPerMinute is { } efficient ? $"+{efficient:0.0}" : "-", AimModPalette.Success,
                    text("pp per minute", 11, AimModPalette.Muted, "SemiBold")),
                TargetSort.PassProbability => ("PASS CHANCE", candidate.PassEstimate is { } chance ? ForecastText.Chance(chance.Probability) : "-", ForecastColours.Chance,
                    text(candidate.PassEstimate is { } range ? $"range {ForecastText.Chance(range.Lower)}–{ForecastText.Chance(range.Upper)}" : "more history needed",
                        11, AimModPalette.Muted, "SemiBold")),
                _ when candidate.Forecast is { TargetPp: { } target, ReachProbability: { } reach } forecast => ("TARGET PP", ForecastText.Pp(target), ForecastColours.Target,
                    ChanceLabel(reach, $"in ~{forecast.Tries} {(forecast.PreviousTries > 0 ? "more " : "")}tries", wrap: true)),
                _ => ("FC CEILING", maximum, AimModPalette.Muted, text(candidate.Estimate is null ? "pending" : "full-combo SS", 11, AimModPalette.Muted, "SemiBold")),
            };

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
                    Position = new(158, 9),
                    Width = 450,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new(2),
                    Children = new Drawable[]
                    {
                        title = new TruncatingSpriteText { Text = candidate.Title, Font = new FontUsage(size: 15, weight: "Bold"), Colour = AimModPalette.Text, MaxWidth = 450 },
                        artist = new TruncatingSpriteText { Text = $"{candidate.Artist}  /  mapped by {candidate.Creator}", Font = new FontUsage(size: 11, weight: "SemiBold"), Colour = AimModPalette.Muted, MaxWidth = 450 },
                        mapDetails = truncatingText($"[{candidate.Difficulty}]   {candidate.StarRating:0.00}*   {candidate.Bpm:0} BPM   {formatLength(candidate.TotalLengthSeconds)}   {combo}   {mods}", 11, ReadableDifficultyColour(candidate.StarRating), "Bold"),
                        // Predicted play statistics: cyan on a tinted strip, distinct from the amber chance figures.
                        new Container
                        {
                            AutoSizeAxes = Axes.Both,
                            Masking = true,
                            CornerRadius = 4,
                            Margin = new MarginPadding { Vertical = 2 },
                            Children = new Drawable[]
                            {
                                new Box { RelativeSizeAxes = Axes.Both, Colour = ForecastColours.Performance, Alpha = breakdown is null ? .05f : .1f },
                                new SpriteIcon
                                {
                                    Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, X = 6, Size = new(10),
                                    Icon = FontAwesome.Solid.Crosshairs, Colour = breakdown is null ? AimModPalette.Muted : ForecastColours.Performance,
                                },
                                performanceDetails = truncatingText(performance, 11, breakdown is null ? AimModPalette.Muted : ForecastColours.Performance, "SemiBold").With(d =>
                                {
                                    d.Margin = new MarginPadding { Left = 21, Right = 7, Vertical = 1 };
                                }),
                            },
                        },
                        confidenceDetails = truncatingText(evidence, 11, candidate.EvidenceTier == 2 ? AimModPalette.Success : AimModPalette.Muted, "SemiBold"),
                    },
                },
                expectedMetric = metric(candidate.ExpectedPpCaption, expected, ForecastColours.Pp, expectedDetail),
                maximumMetric = metric(priorityCaption, priorityValue, priorityColour, priorityDetail),
                new Container
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Margin = new MarginPadding { Right = 12 },
                    Size = new(104, 76),
                    Children = new Drawable[]
                    {
                        // Neutral row actions: the selected target's detail pane holds the primary action.
                        actionButton(
                            FontAwesome.Solid.Play,
                            "Open osu!",
                            AimModPalette.PanelRaised,
                            openBeatmap is null ? () => { } : () => _ = openBeatmap(candidate.BeatmapId, CancellationToken.None),
                            disabled: openBeatmap is null),
                        actionButton(installed ? FontAwesome.Solid.Check : FontAwesome.Solid.Download, installed ? "Installed" : set.DownloadDisabled ? "Unavailable" : "Install", AimModPalette.PanelRaised, beginImport, 41, installed || set.DownloadDisabled,
                            out saveBackground, out saveText),
                    },
                },
                focusRing = new AimMod.Desktop.Coaching.WorkspaceFocusRing(),
            };
        }

        private float layoutWidth = -1;

        protected override void Update()
        {
            base.Update();
            if (DrawWidth == layoutWidth) return;
            layoutWidth = DrawWidth;
            const float actionColumn = 128;
            float metricWidth = DrawWidth < 880 ? 96 : 112;
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
            performanceDetails.MaxWidth = Math.Max(60, detailWidth - 28);
            confidenceDetails.MaxWidth = detailWidth;
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

        private static Container metric(string caption, string value, Colour4 colour, Drawable detail) => new()
        {
            Size = new(112, 112),
            Children = new Drawable[]
            {
                truncatingText(caption, 11, AimModPalette.Muted, "Bold").With(drawable => { drawable.Position = new(0, 16); drawable.MaxWidth = 96; }),
                truncatingText(value, 24, colour, "Bold").With(drawable => { drawable.Position = new(0, 31); drawable.MaxWidth = 96; }),
                detail.With(drawable => drawable.Position = new(0, 64)),
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
                // A border keeps neutral actions visible on the raised selected row.
                BorderThickness = 1,
                BorderColour = AimModPalette.Border,
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
