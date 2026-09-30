using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.Coaching;

public partial class NativeCoachingWorkspace
{
    private readonly FillFlowContainer<Drawable> profileHost;
    private readonly CoachingFindLayout findLayout;
    private readonly Drawable findReset;
    private readonly object rankingGate = new();
    private (NativeCoachingWorkspaceModel Model, int Analyses, CoachingMapRanking Ranking)? rankingCache;
    private CoachingMapRanking mapRanking = CoachingMapRanking.Empty;
    private int shownMapCount = visible_map_page;
    private (CoachingSkillSummary Skills, int Columns)? renderedProfile;

    internal static Colour4 FamilyColour(CoachingIssueFamily family) => family switch
    {
        CoachingIssueFamily.Aim => AimModPalette.Pink,
        CoachingIssueFamily.Timing => AimModPalette.Yellow,
        CoachingIssueFamily.NoPress => AimModPalette.Muted,
        _ => AimModPalette.Cyan,
    };

    internal static Colour4 IssueColour(CoachingIssueKind kind) => FamilyColour(CoachingIssues.Family(kind));

    private CoachingMapRanking? cachedRanking(NativeCoachingWorkspaceModel model, int analysisCount)
    {
        lock (rankingGate)
            return rankingCache is { } cached && ReferenceEquals(cached.Model, model) && cached.Analyses == analysisCount ? cached.Ranking : null;
    }

    private void rememberRanking(NativeCoachingWorkspaceModel model, int analysisCount, CoachingMapRanking ranking)
    {
        lock (rankingGate)
            rankingCache = (model, analysisCount, ranking);
        if (!ReferenceEquals(mapRanking, ranking)) shownMapCount = visible_map_page;
        mapRanking = ranking;
    }

    private CoachingMapRanking rankMaps(NativeCoachingWorkspaceModel model, CancellationToken token) =>
        CoachingMapRanker.Rank(model.History.Where(eligibleForCoaching).ToArray(), analyses, DateTimeOffset.UtcNow, token);

    private static Container createFindFilters(OsuTextBox search, ScoreModFilterDropdown mods, Bindable<CoachingTimeRange> range, out Drawable reset)
    {
        var row = new CoachingFilterRow(search,
            new StatisticsFilterBar { RelativeSizeAxes = Axes.X, Height = AimModVisualStyle.ControlHeight, Child = mods },
            new TimeRangeDropdown { RelativeSizeAxes = Axes.X, Items = Enum.GetValues<CoachingTimeRange>(), Current = range },
            reset = new AimModResetButton(() => { }) { Height = AimModVisualStyle.ControlHeight, Alpha = 0 })
        { Depth = -20 };
        return row;
    }

    /// <summary>Any filter away from the page defaults; the reset button only appears then.</summary>
    private bool findChanged => search.Current.Value.Length > 0 || modSelection.Value != ScoreMods.Any || !coachingTimeRange.IsDefault;

    /// <summary>Anything that hides plays, including the default period, so an empty list can offer to widen it.</summary>
    private bool findFiltered => search.Current.Value.Length > 0 || modSelection.Value != ScoreMods.Any || coachingTimeRange.Value != CoachingTimeRange.All;

    private void resetFindFilters() => resetFindFilters(allTime: false);

    private void resetFindFilters(bool allTime)
    {
        search.Current.Value = string.Empty;
        modSelection.Value = ScoreMods.Any;
        if (allTime) coachingTimeRange.Value = CoachingTimeRange.All;
        else coachingTimeRange.SetDefault();
    }

    private void refreshRunList()
    {
        scheduledRunListRefresh?.Cancel();
        scheduledRunListRefresh = null;
        runList.Clear();
        runRows.Clear();
        focusedRun = -1;
        ((AimModResetButton)findReset).Action = resetFindFilters;
        findReset.FadeTo(findChanged ? 1 : 0, AimModVisualStyle.FastTransition);
        renderProfile();

        if (workspace is null)
        {
            runList.Add(new WorkspaceSkeleton(5, 64));
            return;
        }

        string[] terms = search.Current.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        CoachingMapCandidate[] matching = mapRanking.Maps.Where(map => terms.All(term => searchable(map.Latest).Any(value =>
            value.Contains(term, StringComparison.OrdinalIgnoreCase)))).ToArray();

        if (matching.Length == 0)
        {
            var empty = new WorkspaceStateCard();
            runList.Add(empty);
            if (allReplays.Count == 0)
                empty.Show(FontAwesome.Solid.Music, "No plays yet",
                    "Play a map in osu!, or connect your osu! installation and account in Settings.");
            else if (findFiltered)
                empty.Show(FontAwesome.Solid.Filter, "No maps match these filters",
                    "Try a wider date range, another mod filter or a different search.", actionLabel: "Show all time", action: () => resetFindFilters(allTime: true));
            else
                empty.Show(FontAwesome.Solid.InfoCircle, "No completed plays to coach yet",
                    "Coaching uses passed plays with at least 70% accuracy.");
            if (openTrainers is not null)
                runList.Add(visualEntry("Train a skill", "Practise now, even without saved plays.", PracticeSketchKind.Timing, openTrainers));
            return;
        }

        runList.Add(new SectionLine("Maps to practise", matching.Length == 1 ? "1 map" : $"{matching.Length:N0} maps · most to gain first"));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (CoachingMapCandidate map in matching.Take(shownMapCount))
        {
            var row = new CoachingCandidateRow(map, now, () => openCoachingRun(map.Target));
            runRows.Add(row);
            runList.Add(row);
        }
        if (matching.Length > shownMapCount)
            runList.Add(new CoachingButton($"Show {Math.Min(visible_map_page, matching.Length - shownMapCount)} more", () =>
            {
                shownMapCount += visible_map_page;
                refreshRunList();
            }, compact: true));
    }

    private static IEnumerable<string> searchable(LocalReplay run)
    {
        yield return run.Title ?? string.Empty;
        yield return run.Artist ?? string.Empty;
        yield return run.Difficulty ?? string.Empty;
        foreach (string mod in run.Mods ?? Array.Empty<string>()) yield return mod;
    }

    private void renderProfile()
    {
        CoachingSkillSummary skills = mapRanking.Skills;
        int columns = findLayout.ProfileColumns;
        bool stacked = columns > 1;
        if (renderedProfile is { } rendered && ReferenceEquals(rendered.Skills, skills) && rendered.Columns == columns) return;
        renderedProfile = (skills, columns);
        profileHost.Clear();
        findLayout.ShowSide = skills.AnalysedPlays > 0;
        if (skills.AnalysedPlays == 0) return;

        var items = new FillFlowContainer<Drawable>
        {
            RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(0, 10),
        };
        void addBar(Drawable bar)
        {
            int column = items.Count % columns;
            items.Add(new Container
            {
                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Width = 1f / columns,
                Padding = new MarginPadding { Right = column < columns - 1 ? AimModVisualStyle.SectionSpacing : 0 }, Child = bar,
            });
        }
        foreach (CoachingSkillLine line in skills.Lines)
            addBar(new AimModCoachSkillBar(CoachingIssues.FamilyLabel(line.Family), $"{line.Share:P0} of misses", (float)line.Share,
                FamilyColour(line.Family), line.Change));
        if (skills.DriftShare is { } drift && drift > 0)
            addBar(new AimModCoachSkillBar(CoachingIssues.FamilyLabel(CoachingIssueFamily.Tapping), $"drift in {drift:P0} of plays", (float)drift,
                FamilyColour(CoachingIssueFamily.Tapping), skills.DriftChange));

        var body = new FillFlowContainer<Drawable>
        {
            RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(AimModVisualStyle.RelatedSpacing),
        };
        body.Add(new SectionLine("Where your misses come from", $"{skills.AnalysedPlays:N0} analysed plays"));
        body.Add(items);
        if (!stacked && skills.MeanOffsetMs is { } mean && skills.SpreadMs is { } spread)
        {
            body.Add(new Container
            {
                RelativeSizeAxes = Axes.X, Height = 18, Margin = new MarginPadding { Top = 6 },
                Children =
                [
                    label("Tap timing", 13, AimModPalette.Text),
                    label(Math.Abs(mean) < 1 ? $"on beat · ±{spread:0} ms" : $"{Math.Abs(mean):0} ms {(mean < 0 ? "early" : "late")} · ±{spread:0} ms", 11,
                        AimModPalette.Text, "SemiBold").With(t => { t.Anchor = Anchor.TopRight; t.Origin = Anchor.TopRight; }),
                ],
            });
            body.Add(new AimModCoachOffsetGauge(mean, spread));
        }
        profileHost.Add(new CoachingCard(body, 16));
    }

    private static string relativeTime(DateTimeOffset when, DateTimeOffset now)
    {
        TimeSpan age = now - when;
        if (age.TotalMinutes < 60) return "just now";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours} h ago";
        if (age.TotalDays < 14) return $"{(int)age.TotalDays} d ago";
        return when.ToLocalTime().ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Search, mod and period filters on one row, wrapping below the search on narrow pages.</summary>
    private sealed partial class CoachingFilterRow : Container
    {
        private const float mods_width = 190, range_width = 160, reset_width = 104, gap = AimModVisualStyle.RelatedSpacing;
        private readonly Drawable search, mods, range, reset;
        private float layoutWidth = -1;

        public CoachingFilterRow(Drawable search, Drawable mods, Drawable range, Drawable reset)
        {
            this.search = search; this.mods = mods; this.range = range; this.reset = reset;
            RelativeSizeAxes = Axes.X;
            Height = AimModVisualStyle.ControlHeight;
            foreach (var child in new[] { search, mods, range, reset }) child.RelativeSizeAxes = Axes.None;
            reset.Width = reset_width;
            Children = [reset, range, mods, search];
            ChangeChildDepth(mods, -2);
            ChangeChildDepth(range, -1);
        }

        protected override void Update()
        {
            base.Update();
            if (DrawWidth == layoutWidth) return;
            layoutWidth = DrawWidth;
            bool wrap = DrawWidth < 700;
            float controls = mods_width + range_width + reset_width + gap * 3;
            search.Width = wrap ? DrawWidth : DrawWidth - controls;
            search.Position = new(0, 0);
            float y = wrap ? AimModVisualStyle.ControlHeight + gap : 0;
            float x = wrap ? 0 : search.Width + gap;
            float modsWidth = wrap ? (DrawWidth - reset_width - gap * 2) * 0.55f : mods_width;
            float rangeWidth = wrap ? DrawWidth - reset_width - gap * 2 - modsWidth : range_width;
            mods.Width = modsWidth; mods.Position = new(x, y);
            range.Width = rangeWidth; range.Position = new(x + modsWidth + gap, y);
            reset.Position = new(x + modsWidth + rangeWidth + gap * 2, y);
            Height = y + AimModVisualStyle.ControlHeight;
        }
    }

    /// <summary>Ranked map list with the player's profile as a side column, or above the list on narrow pages.</summary>
    private sealed partial class CoachingFindLayout : Container
    {
        private const float side_width = 320, gap = AimModVisualStyle.SectionSpacing, stack_below = 1180;
        private readonly Drawable side;
        private readonly Drawable main;
        private (float Width, float SideHeight, bool Show) layout = (-1, -1, false);

        /// <summary>Profile bar columns: 1 in the side column, 2 or 4 when the profile sits above the list.</summary>
        public int ProfileColumns { get; private set; } = 1;

        public bool ShowSide { get; set; }

        public Action? StackedChanged { get; set; }

        public CoachingFindLayout(Drawable side, Drawable main)
        {
            this.side = side; this.main = main;
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            Children = [main, side];
        }

        protected override void Update()
        {
            base.Update();
            var next = (DrawWidth, side.DrawHeight, ShowSide);
            if (next == layout) return;
            layout = next;
            bool stacked = DrawWidth < stack_below;
            side.Alpha = ShowSide ? 1 : 0;
            if (stacked)
            {
                side.Width = 1;
                side.Position = new(0, 0);
                main.Width = 1;
                main.Y = ShowSide ? side.DrawHeight + AimModVisualStyle.RelatedSpacing * 2 : 0;
            }
            else
            {
                float mainWidth = ShowSide ? DrawWidth - side_width - gap : DrawWidth;
                main.Width = mainWidth / DrawWidth;
                main.Y = 0;
                side.Width = side_width / DrawWidth;
                side.Position = new(mainWidth + gap, 0);
            }
            int columns = !stacked ? 1 : DrawWidth >= 1_000 ? 4 : 2;
            if (columns != ProfileColumns)
            {
                ProfileColumns = columns;
                StackedChanged?.Invoke();
            }
        }
    }

    /// <summary>One difficulty: accuracy room, attempts and the main recurring issue. The whole row opens its coaching.</summary>
    private sealed partial class CoachingCandidateRow : AimModInteractiveSurface
    {
        private const float chevron = 16, gain_width = 84, issue_width = 196, range_width = 170, gap = 20, edge = 14;
        private readonly WorkspaceFocusRing focusRing;
        private readonly Container titleBlock;
        private readonly Drawable range;
        private readonly Drawable issue;
        private readonly Drawable gain;
        private readonly OsuSpriteText compactAccuracy;
        private readonly TruncatingSpriteText titleText;
        private readonly OsuSpriteText difficultyText;
        private (float Width, float Difficulty) layoutWidth = (-1, -1);

        public CoachingMapCandidate Map { get; }

        /// <summary>Why the map ranks where it does, as one number the player can check: accuracy between typical and best.</summary>
        internal static (string Value, string Caption) Gain(CoachingMapCandidate map) =>
            map.AccuracyGap * 100 >= 0.05
                ? ($"+{map.AccuracyGap * 100:0.0}%", "to your best")
                : map.TopIssue is { PerPlay: >= 0.3 } top && CoachingIssues.Family(top.Kind) != CoachingIssueFamily.Tapping
                    ? ($"~{top.PerPlay:0.#}", "misses per play")
                    : ("—", "at your best");

        internal static string IssueDetail(CoachingMapCandidate map, CoachingIssue top) =>
            CoachingIssues.Family(top.Kind) == CoachingIssueFamily.Tapping
                ? $"{top.PerPlay:P0} of plays · {AimModCoachTimeline.format(top.TimeMs)}"
                : $"{top.PerPlay:0.#} misses/play · {AimModCoachTimeline.format(top.TimeMs)}";

        public CoachingCandidateRow(CoachingMapCandidate map, DateTimeOffset now, Action open)
        {
            Map = map;
            RelativeSizeAxes = Axes.X;
            Height = 64;
            Action = open;
            LocalReplay latest = map.Latest;
            (string gainValue, string gainCaption) = Gain(map);
            Anchor right = Anchor.CentreRight;
            range = new FillFlowContainer
            {
                Width = range_width, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(6),
                Anchor = right, Origin = right,
                Children =
                [
                    new Container
                    {
                        RelativeSizeAxes = Axes.X, Height = 16,
                        Children =
                        [
                            new FillFlowContainer
                            {
                                AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(4),
                                Children =
                                [
                                    new OsuSpriteText { Text = $"{map.TypicalAccuracy:P1}", Font = new FontUsage(size: 13, weight: "SemiBold"), Colour = AimModPalette.Text },
                                    new OsuSpriteText { Text = "typical", Font = new FontUsage(size: 11), Colour = AimModPalette.Muted, Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft },
                                ],
                            },
                            new OsuSpriteText
                            {
                                Text = $"best {map.BestAccuracy:P1}", Font = new FontUsage(size: 11, weight: "SemiBold"), Colour = AimModPalette.Accent,
                                Anchor = Anchor.BottomRight, Origin = Anchor.BottomRight,
                            },
                        ],
                    },
                    new AimModCoachRangeBar(map.TypicalAccuracy, map.BestAccuracy),
                ],
            };
            issue = map.TopIssue is { } top
                ? new FillFlowContainer
                {
                    Width = issue_width, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(3), Anchor = right, Origin = right,
                    Children =
                    [
                        new AimModCoachIssueChip(CoachingIssues.Label(top.Kind), IssueColour(top.Kind)),
                        new OsuSpriteText
                        {
                            Text = IssueDetail(map, top), Font = new FontUsage(size: 11), Colour = AimModPalette.Muted, Margin = new MarginPadding { Left = 4 },
                        },
                    ],
                }
                : new Container
                {
                    Width = issue_width, Height = 16, Anchor = right, Origin = right,
                    Child = new OsuSpriteText
                    {
                        Text = map.AnalysedAttempts == 0 ? "No replay analysis yet" : "No repeated issue",
                        Font = new FontUsage(size: 11), Colour = AimModPalette.Muted,
                    },
                };
            gain = new FillFlowContainer
            {
                Width = gain_width, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(1), Anchor = right, Origin = right,
                Children =
                [
                    new OsuSpriteText
                    {
                        Text = gainValue, Font = new FontUsage(size: 18, weight: "Bold"), Anchor = Anchor.TopRight, Origin = Anchor.TopRight,
                        Colour = gainValue == "—" ? AimModPalette.Muted : AimModPalette.Accent,
                    },
                    new OsuSpriteText
                    {
                        Text = gainCaption, Font = new FontUsage(size: 11), Colour = AimModPalette.Muted, Anchor = Anchor.TopRight, Origin = Anchor.TopRight,
                    },
                ],
            };
            Children =
            [
                new Box { RelativeSizeAxes = Axes.Y, Width = 4, Colour = AimModVisualStyle.DifficultyColour(latest.StarRating) },
                titleBlock = new Container
                {
                    RelativeSizeAxes = Axes.X, Height = 46, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft,
                    Children =
                    [
                        new FillFlowContainer
                        {
                            AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(6),
                            Children =
                            [
                                titleText = new TruncatingSpriteText
                                {
                                    Text = latest.Title, Font = new FontUsage(size: 16, weight: "SemiBold"), Colour = AimModPalette.Text,
                                },
                                difficultyText = new OsuSpriteText
                                {
                                    Text = $"[{latest.Difficulty}]", Font = new FontUsage(size: 15), Colour = AimModPalette.Muted,
                                    Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft,
                                },
                            ],
                        },
                        new FillFlowContainer
                        {
                            AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(8), Y = 26,
                            Children =
                            [
                                new AimModDifficultyPill(latest.StarRating) { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft },
                                new OsuSpriteText
                                {
                                    Text = $"{map.Attempts:N0} {(map.Attempts == 1 ? "play" : "plays")} · {relativeTime(latest.PlayedAt, now)}",
                                    Font = new FontUsage(size: 12), Colour = AimModPalette.Muted, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft,
                                },
                                compactAccuracy = new OsuSpriteText
                                {
                                    Text = $"· typical {map.TypicalAccuracy:P1}", Font = new FontUsage(size: 12), Colour = AimModPalette.Muted,
                                    Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Alpha = 0,
                                },
                            ],
                        },
                    ],
                },
                range,
                issue,
                gain,
                new SpriteIcon
                {
                    Icon = FontAwesome.Solid.ChevronRight, Size = new(12), Colour = AimModPalette.Muted,
                    Anchor = right, Origin = right, X = -edge,
                },
                focusRing = new WorkspaceFocusRing(),
            ];
        }

        protected override void Update()
        {
            base.Update();
            if (layoutWidth == (DrawWidth, difficultyText.DrawWidth)) return;
            layoutWidth = (DrawWidth, difficultyText.DrawWidth);
            // Drop the least essential column first: the accuracy bar, then the issue; the gain and title always stay.
            bool showRange = DrawWidth >= 720, showIssue = DrawWidth >= 500;
            float x = edge + chevron + 12;
            gain.X = -x;
            x += gain_width + gap;
            issue.Alpha = showIssue ? 1 : 0;
            issue.X = -x;
            if (showIssue) x += issue_width + gap;
            range.Alpha = showRange ? 1 : 0;
            range.X = -x;
            if (showRange) x += range_width + gap;
            compactAccuracy.Alpha = showRange ? 0 : 1;
            titleBlock.Padding = new MarginPadding { Left = 18, Right = x };
            // The title truncates first so the difficulty name always stays visible next to it.
            titleText.MaxWidth = Math.Max(60, DrawWidth - 18 - x - difficultyText.DrawWidth - 6);
        }

        public void SetFocused(bool focused) => focusRing.SetVisible(focused);
    }
}
