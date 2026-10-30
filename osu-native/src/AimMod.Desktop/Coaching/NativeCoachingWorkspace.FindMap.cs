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

        // Unfinished adaptive practice comes first, so picking up where the player left off is one click.
        if (terms.Length == 0 && adaptiveMaps("").Where(m => !m.Progress.Completed).Take(2).ToArray() is { Length: > 0 } unfinished)
        {
            runList.Add(new SectionLine("Continue practising", "adaptive practice in progress"));
            foreach (var (run, progress) in unfinished)
                runList.Add(new AdaptiveMapRow(run, progress, () => openCoachingRun(run)));
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

        var bar = new Container { RelativeSizeAxes = Axes.X, Height = 10, Masking = true, CornerRadius = 5,
            Child = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised } };
        var legend = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(18, 8) };
        float offset = 0;
        foreach (CoachingSkillLine line in skills.Lines.Where(l => l.Share > 0))
        {
            bar.Add(new Box { RelativeSizeAxes = Axes.Both, RelativePositionAxes = Axes.X, X = offset, Width = (float)line.Share, Colour = FamilyColour(line.Family) });
            offset += (float)line.Share;
            legend.Add(legendItem(CoachingIssues.FamilyLabel(line.Family), $"{line.Share:P0}", FamilyColour(line.Family), line.Change));
        }
        var notes = new List<string>();
        if (skills.DriftShare is { } drift && drift > 0)
            notes.Add($"Stream drift in {drift:P0} of plays" + (skills.DriftChange is { } trend && Math.Abs(trend) >= .05
                ? $", {(trend < 0 ? "less" : "more")} often lately ({(trend < 0 ? "↓" : "↑")}{Math.Abs(trend):P0})" : ""));
        if (skills.MeanOffsetMs is { } mean && skills.SpreadMs is { } spread)
            notes.Add(Math.Abs(mean) < 1 ? $"taps on the beat, ±{spread:0} ms" : $"taps {Math.Abs(mean):0} ms {(mean < 0 ? "early" : "late")}, ±{spread:0} ms");

        var body = new FillFlowContainer<Drawable>
        {
            RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(10),
        };
        body.Add(new SectionLine("Where your misses come from", $"{skills.AnalysedPlays:N0} analysed plays"));
        body.Add(bar);
        body.Add(legend);
        if (notes.Count > 0)
        {
            string text = string.Join(" · ", notes);
            body.Add(flow(char.ToUpperInvariant(text[0]) + text[1..], 12, AimModPalette.Muted));
        }
        if (skills.Lines.Any(l => l.Family == CoachingIssueFamily.Tapping))
            body.Add(flow("Stream control counts misses inside streams of 8 or more notes, whatever went wrong on the note.", 11, AimModPalette.Muted));
        profileHost.Add(new CoachingCard(body, 16));
    }

    /// <summary>A coloured dot, the family, its share of misses and how that share changed recently.</summary>
    private static Drawable legendItem(string title, string share, Colour4 colour, double? change)
    {
        var item = new FillFlowContainer { AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(6) };
        item.Add(new Circle { Size = new(8), Colour = colour, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft });
        item.Add(label(title, 13, AimModPalette.Text).With(t => { t.Anchor = Anchor.CentreLeft; t.Origin = Anchor.CentreLeft; }));
        item.Add(label(share, 13, AimModPalette.Text, "SemiBold").With(t => { t.Anchor = Anchor.CentreLeft; t.Origin = Anchor.CentreLeft; }));
        if (change is { } delta && Math.Abs(delta) >= 0.05)
        {
            bool better = delta < 0;
            item.Add(label($"{(better ? "↓" : "↑")}{Math.Abs(delta) * 100:0}%", 11, better ? AimModPalette.Success : AimModPalette.Yellow, "SemiBold")
                .With(t => { t.Anchor = Anchor.CentreLeft; t.Origin = Anchor.CentreLeft; }));
        }
        return item;
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
        private const float chevron = 16, gain_width = 84, issue_width = 196, gap = 24, edge = 14;
        private readonly WorkspaceFocusRing focusRing;
        private readonly Container titleBlock;
        private readonly Drawable issue;
        private readonly Drawable gain;
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
                                    Text = $"{map.Attempts:N0} {(map.Attempts == 1 ? "play" : "plays")} · {relativeTime(latest.PlayedAt, now)} · "
                                        + (map.AccuracyGap >= .0005 ? $"{map.TypicalAccuracy:P1} typical, best {map.BestAccuracy:P1}" : $"{map.TypicalAccuracy:P1} typical"),
                                    Font = new FontUsage(size: 12), Colour = AimModPalette.Muted, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft,
                                },
                            ],
                        },
                    ],
                },
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
            // The issue goes first on narrow rows; the gain and title always stay.
            bool showIssue = DrawWidth >= 560;
            float x = edge + chevron + 12;
            gain.X = -x;
            x += gain_width + gap;
            issue.Alpha = showIssue ? 1 : 0;
            issue.X = -x;
            if (showIssue) x += issue_width + gap;
            titleBlock.Padding = new MarginPadding { Left = 18, Right = x };
            // The title truncates first so the difficulty name always stays visible next to it.
            titleText.MaxWidth = Math.Max(60, DrawWidth - 18 - x - difficultyText.DrawWidth - 6);
        }

        public void SetFocused(bool focused) => focusRing.SetVisible(focused);
    }
}
