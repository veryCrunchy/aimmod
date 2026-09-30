using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using AimMod.Desktop.ScoreHistory;
using AimMod.Desktop.Practice;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Framework.Threading;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.Coaching;

public partial class NativeCoachingWorkspace
{
    private partial class AnalysisProgressBanner : CompositeDrawable
    {
        private readonly Box accent;
        private readonly Box progressFill;
        private readonly SpriteIcon icon;
        private readonly OsuSpriteText phase;
        private readonly TruncatingSpriteText title;
        private readonly TruncatingSpriteText detail;

        public AnalysisProgressBanner()
        {
            RelativeSizeAxes = Axes.X;
            Height = 64;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                accent = new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = AimModPalette.Cyan },
                icon = new SpriteIcon
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Position = new(16, -1),
                    Size = new(16),
                    Icon = FontAwesome.Solid.ChartLine,
                    Colour = AimModPalette.Cyan,
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    AutoSizeAxes = Axes.Y,
                    RelativeSizeAxes = Axes.X,
                    Width = 0.56f,
                    Margin = new MarginPadding { Left = 42 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(2),
                    Children = new Drawable[]
                    {
                        phase = label("LOADING HISTORY", 9, AimModPalette.Cyan, "Bold"),
                        title = truncatingLabel("Building your global profile", 14, AimModPalette.Text, 520, "SemiBold"),
                    },
                },
                detail = truncatingLabel("Reading local and submitted plays", 11, AimModPalette.Muted, 460).With(text =>
                {
                    text.Anchor = Anchor.CentreRight;
                    text.Origin = Anchor.CentreRight;
                    text.Margin = new MarginPadding { Right = 18 };
                }),
                new Container
                {
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomLeft,
                    RelativeSizeAxes = Axes.X,
                    Height = 3,
                    Children = new Drawable[]
                    {
                        new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Border },
                        progressFill = new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Width = 0.12f,
                            Colour = AimModPalette.Cyan,
                        },
                    },
                },
            };
        }

        public void ShowHistoryLoading() => set(
            "LOADING HISTORY",
            "Building your global profile",
            "Reading local and submitted plays",
            0.12f,
            AimModPalette.Cyan,
            FontAwesome.Solid.ChartLine);

        public void ShowStarting(int cached) => set(
            "ANALYSING REPLAYS",
            "Preparing the next replay",
            $"{Math.Max(0, cached):N0} replay analyses already available",
            0.04f,
            AimModPalette.Cyan,
            FontAwesome.Solid.CircleNotch);

        public void ShowAnalysing(int completed, int total, string currentTitle, int cached)
        {
            float progress = total <= 0 ? 0 : Math.Clamp(completed / (float)total, 0, 1);
            set(
                "ANALYSING REPLAYS",
                string.IsNullOrWhiteSpace(currentTitle) ? "Reading replay judgements" : currentTitle,
                AnalysisProgressDetail(completed, total, cached),
                progress,
                AimModPalette.Cyan,
                FontAwesome.Solid.CircleNotch);
        }

        public void ShowReady(GlobalCoachingProfile profile, int merged, int submitted, string period)
        {
            if (merged == 0)
            {
                bool allTime = string.Equals(period, "All time", StringComparison.Ordinal);
                set(
                    "NO PLAY HISTORY",
                    allTime ? "No osu!standard plays found" : $"No osu!standard plays in the {period.ToLowerInvariant()}",
                    allTime
                        ? "Play a map or connect an osu! account to begin coaching."
                        : "Choose a longer profile period or complete a new play.",
                    0,
                    AimModPalette.Pink,
                    FontAwesome.Solid.ExclamationCircle);
                return;
            }

            int cached = profile.Coverage.AnalysedRunCount;
            set(
                cached > 0 ? "GLOBAL PROFILE READY" : "REPLAY ANALYSIS READY",
                cached > 0
                    ? $"{cached:N0} replays analysed across {profile.Coverage.AnalysedMapCount:N0} maps"
                    : $"{merged:N0} plays loaded for coaching",
                cached > 0
                    ? $"{ConfidenceLabel(profile.Coverage.Confidence)} confidence  //  {ProfileCoverageValue(profile)} replay coverage  //  {period}"
                    : $"{profile.Coverage.ReplayAvailableRunCount:N0} saved replays  //  {submitted:N0} submitted scores  //  {period}",
                cached > 0 ? 1 : 0,
                cached > 0 ? AimModPalette.Success : AimModPalette.Yellow,
                cached > 0 ? FontAwesome.Solid.CheckCircle : FontAwesome.Solid.Clock);
        }

        public void ShowComplete(GlobalCoachingProfile profile, int completed, int failed) => set(
            "GLOBAL PROFILE UPDATED",
            completed > 0
                ? $"Added {completed:N0} new replay {(completed == 1 ? "analysis" : "analyses")}"
                : "Replay analysis is up to date",
            AnalysisCompletionDetail(profile.Coverage.AnalysedRunCount, failed),
            1,
            failed > 0 && completed == 0 ? AimModPalette.Yellow : AimModPalette.Success,
            failed > 0 && completed == 0 ? FontAwesome.Solid.ExclamationCircle : FontAwesome.Solid.CheckCircle);

        public void ShowWarning(string titleText, string detailText) => set(
            "LIMITED DATA",
            titleText,
            detailText,
            1,
            AimModPalette.Yellow,
            FontAwesome.Solid.ExclamationCircle);

        public void ShowError(string titleText, string detailText) => set(
            "ANALYSIS PAUSED",
            titleText,
            detailText,
            1,
            AimModPalette.Pink,
            FontAwesome.Solid.ExclamationCircle);

        private void set(string phaseText, string titleText, string detailText, float progress, Colour4 colour, IconUsage iconUsage)
        {
            phase.Text = phaseText;
            phase.Colour = colour;
            title.Text = titleText;
            detail.Text = detailText;
            accent.Colour = colour;
            progressFill.Colour = colour;
            progressFill.ResizeWidthTo(Math.Clamp(progress, 0, 1), 180, Easing.OutQuint);
            icon.Icon = iconUsage;
            icon.Colour = colour;
        }

        protected override void Update()
        {
            base.Update();
            title.MaxWidth = Math.Max(180, DrawWidth * 0.5f - 64);
            detail.MaxWidth = Math.Max(160, DrawWidth * 0.4f - 28);
        }
    }

    private partial class GlobalSkillProfileGrid : CompositeDrawable
    {
        public GlobalSkillProfileGrid(GlobalCoachingProfile profile)
        {
            RelativeSizeAxes = Axes.X;
            Height = 126;
            GlobalMissReasonShare? miss = profile.MissReasons.FirstOrDefault();
            GlobalSkillAreaEvidence? focusArea = profile.MeasuredSkillAreas.FirstOrDefault();
            InternalChild = new GridContainer
            {
                RelativeSizeAxes = Axes.Both,
                RowDimensions = new[]
                {
                    new Dimension(GridSizeMode.Relative, 0.5f),
                    new Dimension(GridSizeMode.Relative, 0.5f),
                },
                ColumnDimensions = new[]
                {
                    new Dimension(GridSizeMode.Relative, 0.333f),
                    new Dimension(GridSizeMode.Relative, 0.334f),
                    new Dimension(GridSizeMode.Relative, 0.333f),
                },
                Content = new[]
                {
                    new Drawable[]
                    {
                        new ProfileMetric("TIMING", profile.TimingTendency, profile.TimingDetail, AimModPalette.Cyan),
                        new ProfileMetric("AIM", profile.AimTendency, profile.AimDetail, AimModPalette.Pink),
                        new ProfileMetric(
                            "MISS CAUSE",
                            miss is null ? "Collecting" : ReplayMissInsightPresenter.Label(miss.Reason),
                            miss is null
                                ? "No classified misses yet"
                                : $"{miss.Count:N0} classified  //  {miss.Share:P0}  //  {ConfidenceLabel(miss.Confidence)} confidence",
                            AimModPalette.Yellow),
                    },
                    new Drawable[]
                    {
                        new ProfileMetric(
                            "COVERAGE",
                            ProfileCoverageValue(profile),
                            $"{profile.Coverage.AnalysedRunCount:N0} of {profile.Coverage.ReplayAvailableRunCount:N0} saved replays",
                            AimModPalette.Success),
                        new ProfileMetric(
                            "CONFIDENCE",
                            ConfidenceLabel(profile.Coverage.Confidence),
                            $"{profile.Coverage.AnalysedMapCount:N0} analysed maps",
                            AimModPalette.Yellow),
                        new ProfileMetric(
                            "FOCUS AREA",
                            focusArea?.Label ?? "Collecting",
                            focusArea is null
                                ? $"{profile.Coverage.JudgementCount:N0} exact judgements"
                                : $"{focusArea.EvidenceCount:N0} misses  //  {focusArea.MapCount:N0} maps  //  {ConfidenceLabel(focusArea.Confidence)}",
                            AimModPalette.Cyan),
                    },
                },
            };
        }
    }

    private partial class ProfileMetric : CompositeDrawable
    {
        private readonly TruncatingSpriteText value;
        private readonly TruncatingSpriteText detail;

        public ProfileMetric(string titleText, string valueText, string detailText, Colour4 accentColour)
        {
            RelativeSizeAxes = Axes.Both;
            Padding = new MarginPadding(3);
            InternalChild = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Masking = true,
                CornerRadius = AimModVisualStyle.ControlRadius,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                    new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = accentColour },
                    label(titleText, 8, AimModPalette.Muted, "Bold").With(text => text.Position = new(11, 6)),
                    value = truncatingLabel(valueText, 13, accentColour, 160, "Bold").With(text => text.Position = new(11, 20)),
                    detail = truncatingLabel(detailText, 10, AimModPalette.Muted, 160).With(text => text.Position = new(11, 39)),
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            value.MaxWidth = detail.MaxWidth = Math.Max(60, DrawWidth - 28);
        }
    }

    private partial class InsightRow : CompositeDrawable
    {
        public InsightRow(string title, string detail, string value, Colour4 accent)
        {
            RelativeSizeAxes = Axes.X;
            Height = 73;
            InternalChildren = new Drawable[]
            {
                new Box
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Size = new(3, 48),
                    Colour = accent,
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding { Left = 14, Right = 102, Top = 7 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(4),
                    Children = new Drawable[]
                    {
                        label(title, 13, AimModPalette.Text, "SemiBold"),
                        flow(detail, 10, AimModPalette.Muted),
                    },
                },
                label(value, 13, accent, "Bold").With(text =>
                {
                    text.Anchor = Anchor.TopRight;
                    text.Origin = Anchor.TopRight;
                    text.Margin = new MarginPadding { Top = 8, Right = 2 };
                }),
                new Box
                {
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomLeft,
                    RelativeSizeAxes = Axes.X,
                    Height = 1,
                    Colour = AimModPalette.Border,
                    Alpha = 0.65f,
                },
            };
        }
    }

    private partial class SelectedRunCard : CompositeDrawable
    {
        public SelectedRunCard(LocalReplay run, CoachingAccuracyPrediction? prediction, Action showGlobal, Action? open)
        {
            RelativeSizeAxes = Axes.X;
            Height = 48;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            InternalChildren = new Drawable[]
            {
                new AimModLocalArtwork(run.BackgroundPath),
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = ColourInfo.GradientHorizontal(AimModPalette.Canvas.Opacity(0.92f), AimModPalette.Panel.Opacity(0.82f)),
                },
                new Box
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = 5,
                    Colour = AimModVisualStyle.DifficultyColour(run.StarRating),
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Width = 1,
                    Margin = new MarginPadding { Left = 14 },
                    Padding = new MarginPadding { Right = 260 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(2),
                    Children = new Drawable[]
                    {
                        truncatingLabel($"{run.Title} [{run.Difficulty}]", 13, AimModPalette.Text, 520, "Bold"),
                        truncatingLabel($"{run.Accuracy:P2}  //  {run.MissCount:N0} misses  //  {run.PlayedAt:MMM d, yyyy}{formatPpSuffix(run.PerformancePoints)}", 10, AimModPalette.Cyan, 520, "SemiBold"),
                    },
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    AutoSizeAxes = Axes.Both,
                    Margin = new MarginPadding { Right = 9 },
                    Direction = FillDirection.Horizontal,
                    Spacing = new(6),
                    Children = new Drawable[]
                    {
                        new AimModDifficultyPill(run.StarRating),
                        new ActionButton("Global", showGlobal),
                        new ActionButton(open is null ? "No replay" : "Replay", open),
                    },
                },
            };
        }
    }

    private partial class GlobalEvidenceStrip : CompositeDrawable
    {
        public GlobalEvidenceStrip(GlobalCoachingProfile profile)
        {
            RelativeSizeAxes = Axes.X;
            Height = 66;

            GlobalMissReasonShare[] reasons = profile.MissReasons.Take(4).ToArray();
            var bar = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                Height = 8,
                Direction = FillDirection.Horizontal,
                Spacing = new(2),
            };
            if (reasons.Length == 0)
            {
                bar.Add(new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Border });
            }
            else
            {
                Colour4[] colours = { AimModPalette.Pink, AimModPalette.Cyan, AimModPalette.Yellow, AimModPalette.Success };
                double visibleTotal = reasons.Sum(item => item.Share);
                for (int i = 0; i < reasons.Length; i++)
                {
                    bar.Add(new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Width = visibleTotal <= 0 ? 1f / reasons.Length : (float)(reasons[i].Share / visibleTotal),
                        Colour = colours[i % colours.Length],
                    });
                }
            }

            string missSummary = ProfileEvidenceSummary(profile);
            InternalChildren = new Drawable[]
            {
                bar,
                truncatingLabel(missSummary, 10, AimModPalette.Muted, 760).With(text => text.Y = 14),
                truncatingLabel(ProfileTendencySummary(profile), 11, AimModPalette.Text, 760, "SemiBold")
                    .With(text => text.Y = 38),
            };
        }
    }

    private partial class SectionLine : AimModSubsectionHeader
    {
        public SectionLine(string titleText, string detailText)
            : base(titleText, detailText)
        {
        }

        public void SetDetail(string value) => Detail = value;
    }

    private sealed partial class PracticeDropdown<T> : AimModDropdown<T>
        where T : struct, Enum
    {
        private readonly Func<T, string> formatter;

        public PracticeDropdown(Func<T, string> formatter)
        {
            this.formatter = formatter;
        }

        protected override LocalisableString GenerateItemText(T item) => formatter(item);
    }

    private sealed partial class TimeRangeDropdown : AimModDropdown<CoachingTimeRange>
    {
        protected override LocalisableString GenerateItemText(CoachingTimeRange item) => TimeRangeLabel(item);
    }

    internal static string TimeRangeLabel(CoachingTimeRange item) => item switch
    {
        CoachingTimeRange.Days7 => "Last 7 days",
        CoachingTimeRange.Days30 => "Last 30 days",
        CoachingTimeRange.Days90 => "Last 90 days",
        CoachingTimeRange.Year => "Last year",
        _ => "All time",
    };

    private static string formatPpSuffix(double? pp) =>
        pp is { } value && double.IsFinite(value) && value > 0 ? $"  //  {value:0.0}pp" : string.Empty;

    private partial class RecommendationCard : CompositeDrawable
    {
        public RecommendationCard(CoachingRecommendation recommendation, Action? open)
        {
            RelativeSizeAxes = Axes.X;
            Height = 146;
            Masking = true;
            CornerRadius = AimModVisualStyle.CardRadius;
            InternalChildren = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = ColourInfo.GradientHorizontal(AimModPalette.PanelRaised, AimModPalette.PinkDark),
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding(14),
                    Direction = FillDirection.Vertical,
                    Spacing = new(5),
                    Children = new Drawable[]
                    {
                        label(recommendation.Intent.ToUpperInvariant(), 9, AimModPalette.Pink, "Bold"),
                        truncatingLabel($"{recommendation.Title} [{recommendation.Difficulty}]", 15, AimModPalette.Text, 440, "Bold"),
                        flow(recommendation.Reason, 10, AimModPalette.Muted),
                    },
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.BottomRight,
                    Origin = Anchor.BottomRight,
                    AutoSizeAxes = Axes.Both,
                    Margin = new MarginPadding { Right = 12, Bottom = 11 },
                    Direction = FillDirection.Horizontal,
                    Spacing = new(9),
                    Children = new Drawable[]
                    {
                        label(recommendation.ExpectedAccuracy is { } expected ? $"Expected {expected:P1}" : confidenceText(recommendation.Confidence), 10, AimModPalette.Cyan, "SemiBold"),
                        new ActionButton(open is null ? "Run unavailable" : "Open run", open),
                    },
                },
            };
        }

        private static string confidenceText(CoachingConfidence confidence) => confidence switch
        {
            CoachingConfidence.High => "High confidence",
            CoachingConfidence.Medium => "Medium confidence",
            CoachingConfidence.Low => "Low confidence",
            _ => "More plays needed",
        };
    }

    private partial class RunPickerRow : ClickableContainer
    {
        private readonly Action select;
        private readonly Box background;
        private readonly Colour4 restingColour;

        public RunPickerRow(CoachingRecentRun run, bool selected, Action select)
        {
            this.select = select;
            restingColour = selected ? AimModPalette.PanelRaised : AimModPalette.Panel;
            RelativeSizeAxes = Axes.X;
            Height = 64;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            Children = new Drawable[]
            {
                background = new Box { RelativeSizeAxes = Axes.Both, Colour = restingColour },
                new Box
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = selected ? 4 : 3,
                    Colour = AimModVisualStyle.DifficultyColour(run.StarRating),
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Width = 1,
                    Margin = new MarginPadding { Left = 17 },
                    Padding = new MarginPadding { Right = 280 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(2),
                    Children = new Drawable[]
                    {
                        truncatingLabel($"{run.Title} [{run.Difficulty}]", 14, AimModPalette.Text, 520, "SemiBold"),
                        truncatingLabel($"{run.Artist}  //  {run.PlayedAt:MMM d, HH:mm}  //  {formatMods(run.Mods)}", 10, AimModPalette.Muted, 520),
                    },
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    AutoSizeAxes = Axes.Both,
                    Margin = new MarginPadding { Right = 16 },
                    Direction = FillDirection.Horizontal,
                    Spacing = new(12),
                    Children = new Drawable[]
                    {
                        label($"{run.Accuracy:P2}", 13, AimModPalette.Cyan, "Bold"),
                        label($"{run.MissCount:N0} miss", 11, run.MissCount == 0 ? AimModPalette.Success : AimModPalette.Muted),
                        new AimModDifficultyPill(run.StarRating),
                        label(selected ? "Selected" : "Inspect", 11, selected ? AimModPalette.Pink : AimModPalette.Muted, "SemiBold"),
                    },
                },
            };
        }

        protected override bool OnClick(ClickEvent e)
        {
            select();
            return true;
        }

        protected override bool OnHover(HoverEvent e)
        {
            background.FadeColour(AimModPalette.PanelHover, 100);
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            background.FadeColour(restingColour, AimModVisualStyle.HoverTransition);
            base.OnHoverLost(e);
        }

        private static string formatMods(IReadOnlyList<string> mods) => mods.Count == 0 ? "No Mod" : string.Join(' ', mods);
    }

    private partial class ActionButton : ClickableContainer
    {
        private readonly Action? action;
        private readonly Box background;

        public ActionButton(string text, Action? action)
        {
            this.action = action;
            AutoSizeAxes = Axes.Both;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            Alpha = action is null ? 0.5f : 1;
            Children = new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = action is null ? AimModPalette.PanelHover : AimModPalette.PinkDark,
                },
                new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Horizontal = 11, Vertical = 6 },
                    Child = label(text, 10, AimModPalette.Text, "SemiBold"),
                },
            };
        }

        protected override bool OnClick(ClickEvent e)
        {
            action?.Invoke();
            return action is not null;
        }

        protected override bool OnHover(HoverEvent e)
        {
            if (action is not null)
                background.FadeColour(AimModPalette.Pink, 90);
            return action is not null;
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            background.FadeColour(action is null ? AimModPalette.PanelHover : AimModPalette.PinkDark, 90);
            base.OnHoverLost(e);
        }
    }

    private partial class PracticeCandidateRow : CompositeDrawable
    {
        private readonly TruncatingSpriteText title;
        private readonly TruncatingSpriteText evidence;
        private readonly TruncatingSpriteText source;

        public PracticeCandidateRow(PracticeMapCandidate candidate, Action<PracticeMapCandidate, PracticeDrillType> create)
        {
            RelativeSizeAxes = Axes.X;
            Height = 124;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                new Box
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = 3,
                    Colour = AimModPalette.Pink,
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding { Left = 13, Right = 72, Top = 9 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(3),
                    Children = new Drawable[]
                    {
                        title = truncatingLabel($"{candidate.SourceReplay.Title} [{candidate.SourceReplay.Difficulty}]", 13, AimModPalette.Text, 520, "SemiBold"),
                        evidence = truncatingLabel(
                            PracticeEvidenceSummary(candidate),
                            11,
                            AimModPalette.Cyan,
                            520,
                            "SemiBold"),
                        source = truncatingLabel(
                            PracticeSourceSummary(candidate),
                            11,
                            AimModPalette.Muted,
                            520),
                    },
                },
                new AimModDifficultyPill(candidate.SourceReplay.StarRating)
                {
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.TopRight,
                    Margin = new MarginPadding { Top = 9, Right = 9 },
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomLeft,
                    AutoSizeAxes = Axes.Both,
                    Margin = new MarginPadding { Left = 13, Bottom = 9 },
                    Direction = FillDirection.Horizontal,
                    Spacing = new(6),
                    Children = new Drawable[]
                    {
                        new ActionButton("Configure drill", () => create(candidate, PracticeDrillType.Mixed)),
                    },
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            title.MaxWidth = Math.Max(120, DrawWidth - 98);
            evidence.MaxWidth = source.MaxWidth = Math.Max(120, DrawWidth - 30);
        }
    }

    private partial class PracticeStatusRow : CompositeDrawable
    {
        public PracticeStatusRow(
            string titleText,
            string detailText,
            Colour4 accentColour,
            IconUsage iconUsage,
            string? actionLabel = null,
            Action? action = null)
        {
            RelativeSizeAxes = Axes.X;
            Height = 64;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            var children = new List<Drawable>
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = accentColour },
                new SpriteIcon
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Position = new(15, 0),
                    Size = new(15),
                    Icon = iconUsage,
                    Colour = accentColour,
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding { Left = 40, Right = action is null ? 14 : 118, Top = 8 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(3),
                    Children = new Drawable[]
                    {
                        label(titleText, 12, AimModPalette.Text, "SemiBold"),
                        truncatingLabel(detailText, 11, AimModPalette.Muted, 680),
                    },
                },
            };
            if (action is not null && actionLabel is not null)
            {
                children.Add(new ActionButton(actionLabel, action)
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Margin = new MarginPadding { Right = 12 },
                });
            }

            InternalChildren = children.ToArray();
        }
    }

    private partial class PracticeEmptyState : CompositeDrawable
    {
        public PracticeEmptyState(string titleText, string detailText)
        {
            RelativeSizeAxes = Axes.X;
            Height = 132;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas, Alpha = 0.25f },
                new FillFlowContainer
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Y = 42,
                    Padding = new MarginPadding { Horizontal = 24 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(4),
                    Children = new Drawable[]
                    {
                        label(titleText, 13, AimModPalette.Text, "SemiBold").With(text =>
                        {
                            text.Anchor = Anchor.TopCentre;
                            text.Origin = Anchor.TopCentre;
                        }),
                        flow(detailText, 11, AimModPalette.Muted).With(text => text.TextAnchor = Anchor.TopCentre),
                    },
                },
            };
        }
    }

    private partial class CoachingTrendChart : CompositeDrawable
    {
        private readonly LineGraph graph;
        private readonly Container markers;
        private readonly Container missBars;
        private readonly OsuSpriteText upperLabel;
        private readonly OsuSpriteText lowerLabel;
        private readonly OsuSpriteText timeRange;

        public CoachingTrendChart()
        {
            RelativeSizeAxes = Axes.X;
            Height = 220;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas, Alpha = 0.38f },
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Top = 16, Bottom = 58, Left = 38, Right = 16 },
                    Children = new Drawable[]
                    {
                        new Box { RelativeSizeAxes = Axes.X, Height = 1, Colour = AimModPalette.Border },
                        new Box { RelativeSizeAxes = Axes.X, RelativePositionAxes = Axes.Y, Y = 0.5f, Height = 1, Colour = AimModPalette.Border },
                        new Box { RelativeSizeAxes = Axes.X, Anchor = Anchor.BottomLeft, Height = 1, Colour = AimModPalette.Border },
                        graph = new LineGraph
                        {
                            RelativeSizeAxes = Axes.Both,
                            LineColour = AimModPalette.Pink,
                            DefaultValueCount = NativeCoachingWorkspaceModel.MaximumTrendRuns,
                        },
                        markers = new Container { RelativeSizeAxes = Axes.Both },
                    },
                },
                upperLabel = label("100%", 9, AimModPalette.Muted).With(text => text.Position = new(4, 8)),
                lowerLabel = label("80%", 9, AimModPalette.Muted).With(text =>
                {
                    text.Anchor = Anchor.BottomLeft;
                    text.Origin = Anchor.BottomLeft;
                    text.Position = new(4, -58);
                }),
                label("MISS", 8, AimModPalette.Muted, "Bold").With(text =>
                {
                    text.Anchor = Anchor.BottomLeft;
                    text.Origin = Anchor.BottomLeft;
                    text.Position = new(4, -28);
                }),
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomLeft,
                    Y = -24,
                    Height = 20,
                    Padding = new MarginPadding { Left = 38, Right = 16 },
                    Child = missBars = new Container { RelativeSizeAxes = Axes.Both },
                },
                timeRange = label("No recent plays", 9, AimModPalette.Muted).With(text =>
                {
                    text.Anchor = Anchor.BottomRight;
                    text.Origin = Anchor.BottomRight;
                    text.Margin = new MarginPadding { Right = 7, Bottom = 3 };
                }),
            };
        }

        public void SetRuns(IReadOnlyList<LocalReplay> runs, Guid? selectedScoreId, Action<Guid> select)
        {
            LocalReplay[] chronological = runs.Where(run => double.IsFinite(run.Accuracy))
                                              .OrderBy(run => run.PlayedAt)
                                              .TakeLast(NativeCoachingWorkspaceModel.MaximumTrendRuns)
                                              .ToArray();
            markers.Clear();
            missBars.Clear();
            if (chronological.Length == 0)
            {
                graph.Alpha = 0;
                timeRange.Text = "No recent plays";
                return;
            }

            double minimum = Math.Max(0, Math.Floor((chronological.Min(run => run.Accuracy * 100) - 2) / 5) * 5);
            double maximum = Math.Min(100, Math.Ceiling((chronological.Max(run => run.Accuracy * 100) + 1) / 5) * 5);
            if (maximum - minimum < 5)
                minimum = Math.Max(0, maximum - 5);

            graph.MinValue = (float)minimum;
            graph.MaxValue = (float)maximum;
            graph.DefaultValueCount = Math.Max(2, chronological.Length);
            graph.Values = chronological.Select(run => (float)(run.Accuracy * 100)).ToArray();
            graph.FadeIn(120);
            upperLabel.Text = $"{maximum:0}%";
            lowerLabel.Text = $"{minimum:0}%";
            timeRange.Text = chronological.Length == 1
                ? chronological[0].PlayedAt.ToString("MMM d, HH:mm")
                : $"{chronological[0].PlayedAt:MMM d} to {chronological[^1].PlayedAt:MMM d}";

            int maximumMisses = Math.Max(1, chronological.Max(run => run.MissCount));
            for (int i = 0; i < chronological.Length; i++)
            {
                LocalReplay run = chronological[i];
                float x = chronological.Length == 1 ? 0.5f : (float)i / (chronological.Length - 1);
                float y = (float)(1 - (run.Accuracy * 100 - minimum) / (maximum - minimum));
                markers.Add(new TrendPoint(
                    run.ScoreId == selectedScoreId,
                    AimModVisualStyle.DifficultyColour(run.StarRating),
                    () => select(run.ScoreId))
                {
                    RelativePositionAxes = Axes.Both,
                    Position = new(x, Math.Clamp(y, 0, 1)),
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.Centre,
                });

                float barX = chronological.Length == 1 ? 0.5f : (float)i / (chronological.Length - 1);
                missBars.Add(new Box
                {
                    RelativePositionAxes = Axes.X,
                    X = barX,
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomCentre,
                    Width = Math.Clamp(150f / chronological.Length, 3, 8),
                    Height = run.MissCount == 0 ? 1 : 2 + 9f * run.MissCount / maximumMisses,
                    Colour = run.MissCount == 0 ? AimModPalette.Success : AimModPalette.Pink,
                    Alpha = run.ScoreId == selectedScoreId ? 1 : 0.62f,
                });
            }
        }
    }

    private partial class WorkspacePanel : Container
    {
        private readonly Container content;

        protected override Container<Drawable> Content => content;

        public WorkspacePanel(MarginPadding padding)
        {
            RelativeSizeAxes = Axes.Both;
            Masking = true;
            CornerRadius = AimModVisualStyle.CardRadius;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                content = new Container { RelativeSizeAxes = Axes.Both, Padding = padding },
            };
        }
    }

    private partial class TrendPoint : ClickableContainer
    {
        private readonly Action action;
        private readonly CircularContainer circle;

        public TrendPoint(bool selected, Colour4 colour, Action action)
        {
            this.action = action;
            Size = new(selected ? 15 : 10);
            circle = new CircularContainer
            {
                RelativeSizeAxes = Axes.Both,
                Masking = true,
                BorderThickness = selected ? 2 : 0,
                BorderColour = AimModPalette.Text,
                Child = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = colour,
                },
            };
            Child = circle;
        }

        protected override bool OnClick(ClickEvent e)
        {
            action();
            return true;
        }

        protected override bool OnHover(HoverEvent e)
        {
            circle.ScaleTo(1.35f, 80);
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            circle.ScaleTo(1, 80);
            base.OnHoverLost(e);
        }
    }
}
