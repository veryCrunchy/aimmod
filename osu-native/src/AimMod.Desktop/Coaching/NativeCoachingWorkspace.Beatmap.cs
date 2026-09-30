using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Coaching;

public partial class NativeCoachingWorkspace
{
    private readonly FillFlowContainer<Drawable> mapDetailHost;
    private string? coachingMapId;
    private LocalReplay? coachingMapRun;
    private int coachingMapSection;
    private int detailReturnPage = 3;
    private readonly Dictionary<string, string> viewedPracticeSets = [];
    private readonly HashSet<string> expandedCoachingSections = [];

    internal static string CoachingMapKey(SavedPracticeMap map)
    {
        var t = map.Tracking;
        if (t is null) return "saved:" + map.Id;
        string source = t.OnlineBeatmapId > 0 ? "online:" + t.OnlineBeatmapId
            : !string.IsNullOrEmpty(t.SourceHash) ? "hash:" + t.SourceHash.ToLowerInvariant()
            : t.SourceBeatmapId != Guid.Empty ? "local:" + t.SourceBeatmapId : "saved:" + map.Id;
        return t.AccountId + ":" + t.Player + ":" + source;
    }

    private void openCoachingMap(string? id, LocalReplay? run = null)
    {
        coachingMapId = id;
        coachingMapRun = run;
        // A chosen play came from Find a map; a saved practice map came from My coaching.
        detailReturnPage = run is null ? 3 : 2;
        if (run is not null)
            coachingMapId = practiceSets.FirstOrDefault(s => s.Map.Tracking is { } t && PracticeProgressTracker.SameSource(run,t) && PracticeProgressTracker.SamePlayer(run,t.Player)) is { } set
                ? CoachingMapKey(set.Map) : null;
        coachingMapSection = 0;
        renderCoachingMap();
        showCoachingPage(4);
        coachingPages[4].ScrollTo(0,false);
    }

    private void renderCoachingMap()
    {
        trainerRunsCache = null;
        mapDetailHost.Clear();
        mapDetailHost.Spacing = new(12);
        mapDetailHost.Padding = new MarginPadding { Right = 12, Bottom = 12 };
        if (coachingMapId is null && coachingMapRun is { } selected)
            coachingMapId = practiceSets.FirstOrDefault(s => s.Map.Tracking is { } t && PracticeProgressTracker.SameSource(selected,t) && PracticeProgressTracker.SamePlayer(selected,t.Player)) is { } matched
                ? CoachingMapKey(matched.Map) : null;
        var sets = practiceSets.Where(s => CoachingMapKey(s.Map) == coachingMapId).ToArray();
        var latest = sets.FirstOrDefault(s => s.Map.Id == viewedPracticeSets.GetValueOrDefault(coachingMapId ?? "")) ?? sets.FirstOrDefault();
        var run = coachingMapRun ?? (latest?.Map.Tracking is { } tracking
            ? allReplays.FirstOrDefault(r => eligibleForCoaching(r) && PracticeProgressTracker.SameSource(r,tracking) && PracticeProgressTracker.SamePlayer(r,tracking.Player)) : null);
        mapDetailHost.Add(mapHeader(latest, run));

        // Tabs only help once there is practice history to switch between.
        if (sets.Length == 0) coachingMapSection = 0;
        else
        {
            var tabs = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(8) };
            foreach (var (name,index) in new[] { "Overview", "Sections", "Practice sets", "Original map" }.Select((name,index)=>(name,index)))
            {
                var button = new CoachingButton(name,()=> { coachingMapSection=index;renderCoachingMap(); }, compact: true);
                button.SetSelected(index == coachingMapSection); tabs.Add(button);
            }
            mapDetailHost.Add(tabs);
        }
        if (coachingMapSection == 0 && latest is not null) renderPracticeSession(mapDetailHost, latest, run);
        else if (practiceRunStatus.Length > 0) mapDetailHost.Add(flow(practiceRunStatus, 14, coachingAccent));
        if (practiceHistoryFailed) mapDetailHost.Add(flow("Results could not be refreshed. Use Refresh results in My coaching to retry.",14,AimModPalette.Danger));
        if (coachingMapSection is 0 or 1)
        {
            var attempts=sets.SelectMany(s=>s.Progress.Attempts).Where(a=>!a.Original).DistinctBy(a=>a.ScoreId).ToArray();
            int sections=sets.Sum(s=>s.Map.Tracking?.Difficulties.Count ?? 0);
            int practised=sets.Sum(s=>s.Map.Tracking?.Difficulties.Count(d=>s.Progress.Attempts.Any(a=>!a.Original && a.Difficulty==d.Name)) ?? 0);
            // Progress numbers only appear once there is progress; before that the page shows the first step instead.
            if (attempts.Length > 0)
                mapDetailHost.Add(new CoachingTileRow().With(row =>
                {
                    row.Add(statTile("Exercises practised", $"{practised} of {sections}", sections > practised ? ($"{sections - practised} still to try", AimModPalette.Muted) : ("all tried", AimModPalette.Success)));
                    row.Add(statTile("Section attempts", attempts.Length.ToString("N0"), ($"{attempts.Count(a => a.Passed):N0} completed", AimModPalette.Muted)));
                    if (attempts.Where(a => a.Passed).OrderBy(a => a.PlayedAt).ToArray() is { Length: >= 2 } done)
                        row.Add(statTile("Latest section run", $"{done[^1].Accuracy:P1}",
                            ($"{(done[^1].Accuracy - done[0].Accuracy) * 100:+0.0;-0.0;0.0} since first", done[^1].Accuracy >= done[0].Accuracy ? AimModPalette.Success : AimModPalette.Yellow)));
                }));
            IReadOnlyList<CoachingSection> found = run is null ? [] : sectionsFor(run);
            // The start step practises the map's main issue, the same section the header names and the timeline shades first.
            CoachingIssue? main = run is null ? null : evidenceFor(run).MainIssue;
            CoachingIssue? focus = main is { FirstObjectIndex: >= 0 } ? main
                : found.FirstOrDefault() is { } section ? new CoachingIssue(section.Kind, section.Count, 1, section.TimeMs, section.EndMs, section.FirstObjectIndex) : null;
            void prepare()
            {
                if (run is null) return;
                if (practiceWorkspace is not null)
                {
                    int? first = focus?.FirstObjectIndex ?? TappingCoaching.Build(analyses.GetValueOrDefault(run.ScoreId))?.FirstObjectIndex;
                    practiceWorkspace.OpenBreakdown(new PracticeMapCandidate(run, [run.ScoreId], 1, run.MissCount, 0), first);
                    return;
                }
                coachingTargetScoreId=run.ScoreId;renderSession();showCoachingPage(0);
            }
            if (sets.Length == 0)
                mapDetailHost.Add(run is null
                    ? startStep("Choose a play first", "Pick a map from Find a map to turn its hardest section into exercises.",
                        new CoachingButton("Find a map", () => showCoachingPage(2), true, true))
                    : startStep("Start: prepare a practice set", focus is { } top
                            ? $"Turn {CoachingIssues.Label(top.Kind).ToLowerInvariant()} at {AimModCoachTimeline.format(top.TimeMs)} into tapping and aim exercises."
                            : "Pick a section, practise tapping and aim separately, then retest the full map.",
                        new CoachingButton("Prepare practice set", prepare, true, true)));
            else
            {
                var actions=new FillFlowContainer<Drawable>{RelativeSizeAxes=Axes.X,AutoSizeAxes=Axes.Y,Direction=FillDirection.Full,Spacing=new(8)};
                if(coachingMapSection == 1 && latest is { Map.PayloadRemoved: false } && practiceWorkspace is not null)actions.Add(new CoachingButton("Open practice set",()=>practiceWorkspace.OpenSaved(latest.Map),true,true));
                if(run is not null)actions.Add(new CoachingButton("New practice set",prepare,false,true));
                mapDetailHost.Add(actions);
            }
            if (coachingMapSection == 0 && run is not null)
            {
                renderMapTimeline(mapDetailHost, run);
                renderReplayObservations(mapDetailHost, run, found);
            }
            if (coachingMapSection == 1) renderSectionTable(sets);
            if(coachingMapSection==0 && latest?.Map.Tracking is not null)
            {
                mapDetailHost.Add(new SectionLine("Original map", "since your first practice set"));
                mapDetailHost.Add(new CoachingCard(flow(PracticeProgressTracker.DescribeTransfer(latest),13,AimModPalette.Text),12));
            }
        }
        else if (coachingMapSection == 2)
        {
            foreach(var set in sets)
            {
                var body=denseFlow();
                body.Add(flow($"Practice set · {set.Map.CreatedAt.ToLocalTime():dd MMM yyyy HH:mm}",16,AimModPalette.Text));
                body.Add(flow($"{set.Map.PlaybackRate*100:0}% speed · {set.Map.Tracking?.Difficulties.Count ?? 1} difficulties",13,AimModPalette.Muted));
                foreach(var difficulty in set.Map.Tracking?.Difficulties ?? [])body.Add(flow(difficulty.Name,13,AimModPalette.Text));
                if(set.Map.RetiredAt is not null)body.Add(flow(set.Map.PayloadRemoved ? "Archived · generated files cleaned up · history retained" : "Archived · history retained",12,AimModPalette.Muted));
                if(set.Map.Tracking is null)body.Add(flow("This older set has no linked practice history.",13,AimModPalette.Muted));
                if(practiceWorkspace is not null && !set.Map.PayloadRemoved)body.Add(new CoachingButton("Open practice set",()=>practiceWorkspace.OpenSaved(set.Map),true,true));
                mapDetailHost.Add(new CoachingCard(body,12));
            }
        }
        else
        {
            mapDetailHost.Add(flow("Same mods and speed · completed original-map plays",13,AimModPalette.Muted));
            if(run is not null && openBeatmap is not null)mapDetailHost.Add(new OpenBeatmapButton(()=>run,openBeatmap));
            foreach(var set in sets.Where(s=>s.Map.Tracking is not null))
            {
                var body=denseFlow();
                body.Add(flow($"After practice set · {set.Map.CreatedAt.ToLocalTime():dd MMM HH:mm}",16,AimModPalette.Text));
                var baseline = set.Map.Tracking!.Baseline.Where(a=>a.Passed).ToArray();
                var recent = set.Progress.Attempts.Where(a=>a.Original && a.Passed).OrderBy(a=>a.PlayedAt).TakeLast(5).ToArray();
                body.Add(metricStrip(
                    ($"BASELINE ACC · {baseline.Length} PLAYS",baseline.Length==0?"—":$"{PracticeProgressTracker.Median(baseline.Select(a=>a.Accuracy)):P2}"),
                    ($"RECENT ACC · {recent.Length} PLAYS",recent.Length==0?"—":$"{PracticeProgressTracker.Median(recent.Select(a=>a.Accuracy)):P2}"),
                    ("BASELINE MISSES",baseline.Length==0?"—":$"{PracticeProgressTracker.Median(baseline.Select(a=>(double)a.Misses)):0.#}"),
                    ("RECENT MISSES",recent.Length==0?"—":$"{PracticeProgressTracker.Median(recent.Select(a=>(double)a.Misses)):0.#}")));
                body.Add(flow(PracticeProgressTracker.DescribeTransfer(set),13,AimModPalette.Muted));
                foreach(var attempt in set.Progress.Attempts.Where(a=>a.Original).OrderByDescending(a=>a.PlayedAt).Take(8))
                    body.Add(flow($"{attempt.PlayedAt.ToLocalTime():dd MMM HH:mm} · {attempt.Accuracy:P2} · {attempt.Misses} misses{(attempt.Passed?"":" · Unfinished")}",13,AimModPalette.Muted));
                mapDetailHost.Add(new CoachingCard(body,12));
            }
            if(!sets.Any(s=>s.Map.Tracking is not null))mapDetailHost.Add(flow("Create a practice set first to save a starting point for comparison.",14,AimModPalette.Muted));
        }
    }

    private CoachingMapCandidate? candidateFor(LocalReplay run)
    {
        string key = CoachingIssues.DifficultyKey(run);
        return mapRanking.Maps.FirstOrDefault(m => m.Key == key)
               ?? CoachingMapRanker.Rank(allReplays.Where(r => eligibleForCoaching(r) && CoachingIssues.DifficultyKey(r) == key).ToArray(),
                   analyses, DateTimeOffset.UtcNow).Maps.FirstOrDefault();
    }

    /// <summary>Map identity, where to go back to, and this map's numbers against the player's usual level.</summary>
    private Drawable mapHeader(PracticeSetProgress? latest, LocalReplay? run)
    {
        string title = latest?.Map.Title ?? run?.Title ?? "Beatmap coaching";
        string difficulty = latest?.Map.Difficulty ?? run?.Difficulty ?? "Select a beatmap to begin";
        double stars = run?.StarRating ?? 0;
        CoachingMapCandidate? map = run is null ? null : candidateFor(run);
        var identity = new FillFlowContainer
        {
            AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(8),
            Children =
            [
                new AimModDifficultyPill(stars) { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Alpha = run is null ? 0 : 1 },
                label(map is null ? difficulty : $"{difficulty} · {map.Attempts:N0} {(map.Attempts == 1 ? "play" : "plays")}", 13, AimModPalette.Muted)
                    .With(t => { t.Anchor = Anchor.CentreLeft; t.Origin = Anchor.CentreLeft; }),
            ],
        };
        var header = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(12) };
        header.Add(new Container
        {
            RelativeSizeAxes = Axes.X, Height = 52,
            Children =
            [
                new Box { RelativeSizeAxes = Axes.Y, Width = 4, Colour = run is null ? coachingAccent : AimModVisualStyle.DifficultyColour(stars) },
                new Container
                {
                    RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Left = 16, Right = 150 },
                    Children =
                    [
                        new TruncatingSpriteText { RelativeSizeAxes = Axes.X, Text = title, Font = new FontUsage(size: 22, weight: "SemiBold"), Colour = AimModPalette.Text },
                        identity.With(d => d.Y = 30),
                    ],
                },
                new CoachingButton(detailReturnPage == 2 ? "‹ Find a map" : "‹ My coaching", () => showCoachingPage(detailReturnPage), compact: true)
                    { Anchor = Anchor.TopRight, Origin = Anchor.TopRight },
            ],
        });
        if (map is null) return header;

        double? average = workspace?.Global.MedianAccuracy;
        var tiles = new CoachingTileRow();
        tiles.Add(statTile("Typical accuracy", $"{map.TypicalAccuracy:P1}", average is { } avg
            ? ($"{(map.TypicalAccuracy - avg) * 100:+0.0;-0.0;0.0} vs your usual {avg:P1}", map.TypicalAccuracy >= avg ? AimModPalette.Success : AimModPalette.Yellow)
            : null));
        tiles.Add(statTile("Best", $"{map.BestAccuracy:P1}", map.AccuracyGap >= 0.001
            ? ($"{map.AccuracyGap * 100:0.0} above typical", AimModPalette.Accent) : null));
        tiles.Add(statTile("Misses per play", $"{map.TypicalMisses:0.#}", ($"typical · best {map.BestMisses}", AimModPalette.Muted)));
        if (map.TopIssue is { } issue)
            tiles.Add(new CoachingCard(new FillFlowContainer
            {
                AutoSizeAxes = Axes.Both, Direction = FillDirection.Vertical, Spacing = new(4),
                Children =
                [
                    label("Main issue", 11, AimModPalette.Muted, "SemiBold"),
                    new AimModCoachIssueChip(CoachingIssues.Label(issue.Kind), IssueColour(issue.Kind)),
                    label(CoachingCandidateRow.IssueDetail(map, issue), 11, AimModPalette.Muted),
                ],
            }, 10) { RelativeSizeAxes = Axes.None, AutoSizeAxes = Axes.None, Size = new(210, 76) });
        header.Add(tiles);
        return header;
    }

    private static Drawable statTile(string title, string value, (string Text, Colour4 Colour)? note) => new CoachingCard(new FillFlowContainer
    {
        AutoSizeAxes = Axes.Both, Direction = FillDirection.Vertical, Spacing = new(2),
        Children =
        [
            label(title, 11, AimModPalette.Muted, "SemiBold"),
            label(value, 19, AimModPalette.Text, "Bold"),
            label(note?.Text ?? string.Empty, 11, note?.Colour ?? AimModPalette.Muted),
        ],
    }, 10) { RelativeSizeAxes = Axes.None, AutoSizeAxes = Axes.None, Size = new(210, 76) };

    /// <summary>Equal-width stat tiles on one row, two per row when the page is narrow.</summary>
    private sealed partial class CoachingTileRow : FillFlowContainer<Drawable>
    {
        private (float Width, int Count) layout = (-1, -1);

        public CoachingTileRow()
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            Direction = FillDirection.Full;
            Spacing = new(AimModVisualStyle.RelatedSpacing);
        }

        protected override void Update()
        {
            base.Update();
            if (layout == (DrawWidth, Count) || Count == 0) return;
            layout = (DrawWidth, Count);
            int columns = DrawWidth / Count >= 170 ? Count : Math.Min(2, Count);
            float width = (DrawWidth - Spacing.X * (columns - 1)) / columns - 0.5f;
            foreach (var tile in Children) tile.Width = width;
        }
    }

    /// <summary>A single numbered next step with its one primary action.</summary>
    private static Drawable startStep(string title, string detail, CoachingButton action)
    {
        action.Anchor = Anchor.CentreRight;
        action.Origin = Anchor.CentreRight;
        return new CoachingCard(new Container
        {
            RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y,
            Children =
            [
                new Container
                {
                    Size = new(28), Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Masking = true, CornerRadius = 14,
                    Children =
                    [
                        new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.AccentMuted },
                        label("1", 13, AimModPalette.Accent, "Bold").With(t => { t.Anchor = Anchor.Centre; t.Origin = Anchor.Centre; }),
                    ],
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(3),
                    Padding = new MarginPadding { Left = 42, Right = 200 }, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft,
                    Children = [label(title, 15, AimModPalette.Text, "SemiBold"), flow(detail, 12, AimModPalette.Muted)],
                },
                action,
            ],
        }, 14);
    }
}
