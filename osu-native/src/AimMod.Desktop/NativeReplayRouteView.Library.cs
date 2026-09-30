using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime.Contracts;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop;

public partial class NativeReplayRouteView
{
    private readonly ILocalLibrarySource? source;
    private readonly IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses;
    private readonly Func<ILocalScorePpHydrationService?>? ppHydrator;
    private readonly Bindable<string> modSelection = new(ScoreMods.Any);
    private readonly Bindable<string> gameMode = new("All modes");
    private readonly Dictionary<string, ReplayGroupBlock> groupBlocks = new(StringComparer.Ordinal);
    private readonly HashSet<string> expandedReplayMaps = new(StringComparer.Ordinal);
    private AimModScrollContainer browserScroll = null!;
    private AimModInlineStatus browserStatus = null!;
    private AimModTextBox searchBox = null!;
    private ScoreModFilterDropdown modDropdown = null!;
    private AimModDropdown<string> modeDropdown = null!;
    private SpriteText replayCount = null!;
    private FillFlowContainer<Drawable> replayList = null!;
    private AimModLayout.ChangeTracker<float> browserStatusTracker;
    private (string Search, string Mods, string Ruleset)? loadedQuery;
    private bool browserLoadingShown;
    private bool browserLoaded;
    private CancellationTokenSource? loading;
    private ReplayBrowserSnapshot replayBrowser = ReplayBrowserSnapshot.Empty;

    private Drawable createLibraryContent() => new Container
    {
        RelativeSizeAxes = Axes.Both,
        Padding = new MarginPadding { Horizontal = 12, Top = 12 },
        Children = new Drawable[]
        {
            new AimModResetButton(() =>
            {
                searchBox!.Current.Value = string.Empty;
                gameMode.Value = "All modes";
                modSelection.Value = ScoreMods.Any;
                loadReplayBrowser(force: true);
            })
            {
                Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Width = 90, Height = 25,
            },
            replayCount = place(makeText("Loading local runs...", 12, AimModPalette.Muted, "SemiBold"), y: 5),
            searchBox = new AimModTextBox
            {
                RelativeSizeAxes = Axes.X,
                Height = AimModVisualStyle.ControlHeight,
                Y = 34,
                PlaceholderText = "Search maps, players or mods",
                FocusOnSearchShortcut = true,
            },
            // Mode and mod filters share one row; the row sits above the list so open menus cover it.
            new GridContainer
            {
                RelativeSizeAxes = Axes.X,
                Height = AimModVisualStyle.ControlHeight,
                Y = 78,
                Depth = -2,
                ColumnDimensions = new[]
                {
                    new Dimension(GridSizeMode.Relative, 0.44f),
                    new Dimension(GridSizeMode.Absolute, AimModVisualStyle.RelatedSpacing),
                    new Dimension(),
                },
                Content = new[]
                {
                    new Drawable[]
                    {
                        modeDropdown = new AimModDropdown<string> { RelativeSizeAxes = Axes.X, Items = new[] { "All modes", "osu!", "osu!taiko", "osu!catch", "osu!mania" }, Current = gameMode },
                        Empty(),
                        new StatisticsFilterBar { RelativeSizeAxes = Axes.X, Height = AimModVisualStyle.ControlHeight, Child = modDropdown = new ScoreModFilterDropdown(modSelection) },
                    },
                },
            },
            browserStatus = new AimModInlineStatus { Y = browser_list_top },
            browserScroll = new AimModScrollContainer
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = browser_list_top },
                Depth = 1,
                Child = replayList = new FillFlowContainer<Drawable>
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding { Bottom = 12, Right = 4 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(AimModVisualStyle.RelatedSpacing),
                },
            },
        },
    };

    private void updateBrowserStatusPadding()
    {
        float statusHeight = browserStatus.IsShowing ? browserStatus.DrawHeight + AimModVisualStyle.RelatedSpacing : 0;
        if (browserStatusTracker.Update(statusHeight))
            browserScroll.Padding = new MarginPadding { Top = browser_list_top + statusHeight };
    }

    private void loadReplayBrowser() => loadReplayBrowser(force: false);

    private void loadReplayBrowser(bool force)
    {
        if (source is null)
            return;

        string ruleset = gameMode.Value switch { "osu!" => "osu", "osu!taiko" => "taiko", "osu!catch" => "fruits", "osu!mania" => "mania", _ => "" };
        var query = (searchBox.Current.Value, modSelection.Value, ruleset);
        // Enter after a debounced edit, or a filter set to its current value, must not reload twice.
        if (!force && loadedQuery == query)
            return;
        loadedQuery = query;

        loading?.Cancel();
        loading?.Dispose();
        loading = new CancellationTokenSource();
        CancellationToken cancellationToken = loading.Token;
        browserLoadingShown = true;
        browserStatus.ShowLoading(browserLoaded ? "Updating replays..." : "Reading your local play history...", () =>
        {
            loading?.Cancel();
            loadedQuery = null;
            browserLoadingShown = false;
            browserStatus.ShowMessage("Loading was cancelled.", () => loadReplayBrowser(force: true));
        });
        _ = loadReplayBrowserAsync(query.Item1, query.Item2, ruleset, cancellationToken);
    }

    private async Task loadReplayBrowserAsync(string search, string mods, string ruleset, CancellationToken cancellationToken)
    {
        try
        {
            ILocalLibrarySource availableSource = source ?? throw new InvalidOperationException("The local replay library is not available.");
            ReplayBrowserSnapshot page = await ReplayBrowserModel.LoadAsync(
                availableSource,
                search,
                cancellationToken: cancellationToken, ruleset: ruleset, modSelection: mods).ConfigureAwait(false);
            if (!IsDisposed && !cancellationToken.IsCancellationRequested) { var initial = page; Schedule(() => { if (!cancellationToken.IsCancellationRequested) applyReplayBrowser(initial); }); }
            if (ppHydrator?.Invoke() is { } hydrator)
            {
                var hydrated = await hydrator.HydrateAsync(page.Maps.SelectMany(map => map.Attempts).Take(200).ToArray(), cancellationToken).ConfigureAwait(false);
                var byId = hydrated.Runs.ToDictionary(run => run.ScoreId);
                page = page with { Maps = page.Maps.Select(map => map with { Attempts = map.Attempts.Select(run => byId.GetValueOrDefault(run.ScoreId, run)).ToArray() }).ToArray() };
            }
            if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                Schedule(() => { if (!cancellationToken.IsCancellationRequested) applyReplayBrowser(page); });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!IsDisposed)
                Schedule(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;
                    browserLoadingShown = false;
                    loadedQuery = null;
                    replayCount.Text = "Local replays unavailable";
                    browserStatus.ShowError(error, "Loading local replays", () => loadReplayBrowser(force: true));
                    // Older results stay visible but read as out of date until a retry succeeds.
                    replayList.FadeTo(0.45f, AimModVisualStyle.HoverTransition);
                });
        }
    }

    private void applyReplayBrowser(ReplayBrowserSnapshot snapshot)
    {
        browserLoaded = true;
        modDropdown.SetChoices(snapshot.AvailableMods);
        replayBrowser = snapshot;
        if (selectedReplay is not null)
            expandedReplayMaps.Add(ReplayBrowserModel.MapKeyFor(selectedReplay));

        if (selectedReplay is { } selected)
        {
            var updated = snapshot.Maps.SelectMany(map => map.Attempts).FirstOrDefault(run => run.ScoreId == selected.ScoreId);
            if (updated?.PerformancePoints is { } pp) summaryPerformance.Text = $"{pp:0.#}pp";
        }
        renderReplayBrowser();
        replayList.FadeIn(AimModVisualStyle.HoverTransition);
        if (browserLoadingShown)
        {
            browserLoadingShown = false;
            browserStatus.Dismiss();
        }
    }

    private void renderReplayBrowser()
    {
        int shownMaps = replayBrowser.Maps.Count;
        string shownMapLabel = shownMaps == 1 ? "map" : "maps";
        string runs = $"{replayBrowser.TotalReplayCount:N0} {(replayBrowser.TotalReplayCount == 1 ? "run" : "runs")}";
        replayCount.Text = replayBrowser.TotalMapCount > shownMaps
            ? $"Newest {shownMaps:N0} {shownMapLabel} of {replayBrowser.TotalMapCount:N0}  ·  {runs}"
            : $"{shownMaps:N0} {shownMapLabel}  ·  {runs}";

        if (replayBrowser.Maps.Count == 0)
        {
            groupBlocks.Clear();
            replayList.Clear();
            bool searching = !string.IsNullOrWhiteSpace(searchBox.Current.Value) || gameMode.Value != "All modes" || modSelection.Value != ScoreMods.Any;
            replayList.Add(new ReplayBrowserEmptyState(
                searching ? "No matching replays" : "No saved replays",
                searching
                    ? "Try a title, artist, difficulty, player, or mod."
                    : "Play a map with replay recording enabled, then return here.",
                searching ? "Clear filters" : "Refresh",
                () =>
                {
                    if (searching)
                    {
                        searchBox.Current.Value = string.Empty;
                        gameMode.Value = "All modes";
                        modSelection.Value = ScoreMods.Any;
                    }

                    loadReplayBrowser(force: true);
                }));
            return;
        }

        // Keep unchanged map groups so expanding, selecting or refreshing preserves rows and scroll.
        if (groupBlocks.Count == 0)
            replayList.Clear();

        var incoming = replayBrowser.Maps.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        foreach (string stale in ListDiff.Stale(groupBlocks.Keys, incoming))
        {
            replayList.Remove(groupBlocks[stale], true);
            groupBlocks.Remove(stale);
        }

        for (int index = 0; index < replayBrowser.Maps.Count; index++)
        {
            ReplayBrowserMapGroup group = replayBrowser.Maps[index];
            string signature = ReplayGroupSignature(group);
            if (groupBlocks.TryGetValue(group.Key, out ReplayGroupBlock? existing) && existing.Signature != signature)
            {
                replayList.Remove(existing, true);
                groupBlocks.Remove(group.Key);
            }

            if (!groupBlocks.TryGetValue(group.Key, out ReplayGroupBlock? block))
            {
                string key = group.Key;
                block = new ReplayGroupBlock(group, signature, expandedReplayMaps.Contains(key), expanded =>
                {
                    if (expanded)
                        expandedReplayMaps.Add(key);
                    else
                        expandedReplayMaps.Remove(key);
                }, selectBrowserReplay);
                groupBlocks[key] = block;
                replayList.Add(block);
            }

            block.SetExpanded(expandedReplayMaps.Contains(group.Key));
            block.SetSelected(selectedReplay?.ScoreId);
            replayList.SetLayoutPosition(block, index);
        }
    }

    private void toggleReplayMap(string key)
    {
        if (!groupBlocks.TryGetValue(key, out ReplayGroupBlock? block))
            return;
        bool expanded = expandedReplayMaps.Add(key);
        if (!expanded)
            expandedReplayMaps.Remove(key);
        block.SetExpanded(expanded);
    }

    internal static string ReplayGroupSignature(ReplayBrowserMapGroup group) =>
        string.Join(',', group.Attempts.Select(replay => $"{replay.ScoreId:N}:{replay.PerformancePoints:0.#}"));

    private void selectBrowserReplay(LocalReplay replay)
    {
        if (replay.RulesetShortName == "osu")
            openReplay?.Invoke(replay);
        else
        {
            SuspendPlayback();
            SetReplaySummary(replay);
        }
    }

    internal static string FormatPlayedAt(DateTimeOffset playedAt)
    {
        DateTime local = playedAt.LocalDateTime;
        return local.Year == DateTime.Now.Year ? $"{local:MMM d, HH:mm}" : $"{local:MMM d yyyy}";
    }

    private partial class ReplayBrowserEmptyState : CompositeDrawable
    {
        public ReplayBrowserEmptyState(string title, string detail, string actionLabel, Action action)
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            Masking = true;
            CornerRadius = AimModVisualStyle.CardRadius;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised, Alpha = 0.6f },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding { Horizontal = 20, Vertical = 22 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(AimModVisualStyle.RelatedSpacing),
                    Children = new Drawable[]
                    {
                        new SpriteIcon
                        {
                            Anchor = Anchor.TopCentre,
                            Origin = Anchor.TopCentre,
                            Size = new(22),
                            Icon = FontAwesome.Solid.PlayCircle,
                            Colour = AimModPalette.Cyan,
                        },
                        truncatingText(title, 15, AimModPalette.Text, 220, "SemiBold", Anchor.TopCentre),
                        new WrappedLabel(detail, 12, AimModPalette.Muted, centred: true),
                        new AimModButton(actionLabel, action) { Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre, Margin = new MarginPadding { Top = 4 } },
                    },
                },
            };
        }
    }

    private partial class ReplayGroupBlock : FillFlowContainer<Drawable>
    {
        private readonly ReplayGroupHeader header;
        private readonly FillFlowContainer<Drawable> rows;
        private readonly ReplayBrowserMapGroup group;
        private readonly Action<LocalReplay> open;
        private bool expanded;
        private bool populated;

        public string Signature { get; }

        /// <summary>Attempt rows currently shown under the header.</summary>
        public int VisibleAttemptCount => expanded ? rows.Children.Count : 0;

        public ReplayGroupBlock(
            ReplayBrowserMapGroup group,
            string signature,
            bool expanded,
            Action<bool> expansionChanged,
            Action<LocalReplay> open)
        {
            Signature = signature;
            this.group = group;
            this.open = open;
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            Direction = FillDirection.Vertical;
            Spacing = new(4);
            Children = new Drawable[]
            {
                header = new ReplayGroupHeader(group, expanded, () =>
                {
                    SetExpanded(!this.expanded);
                    expansionChanged(this.expanded);
                }),
                rows = new FillFlowContainer<Drawable>
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    // Indented under the map card to show that attempts belong to it.
                    Padding = new MarginPadding { Left = 10 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(4),
                },
            };
            SetExpanded(expanded);
        }

        public void SetExpanded(bool value)
        {
            if (populated == value && expanded == value)
                return;
            expanded = value;
            header.SetExpanded(value);
            if (value && !populated)
            {
                // Attempt rows are built on first expansion only.
                populated = true;
                foreach (LocalReplay replay in group.Attempts)
                {
                    LocalReplay target = replay;
                    rows.Add(new ReplayBrowserRow(replay, false, () => open(target)));
                }
            }

            rows.Alpha = value ? 1 : 0;
            rows.AutoSizeAxes = value ? Axes.Y : Axes.None;
            if (!value)
                rows.Height = 0;
        }

        public void SetSelected(Guid? scoreId)
        {
            bool containsSelection = group.Attempts.Any(replay => replay.ScoreId == scoreId);
            header.SetContainsSelection(containsSelection);
            foreach (ReplayBrowserRow row in rows.OfType<ReplayBrowserRow>())
                row.SetSelected(row.ScoreId == scoreId);
        }
    }

    private partial class ReplayGroupHeader : AimModInteractiveSurface
    {
        private readonly TruncatingSpriteText title;
        private readonly TruncatingSpriteText detail;
        private readonly SpriteIcon chevron;
        private readonly AimModDifficultyPill difficulty;
        private bool expanded;
        private bool containsSelection;
        private AimModLayout.ChangeTracker<float> widthTracker;

        public ReplayGroupHeader(ReplayBrowserMapGroup group, bool expanded, Action action)
        {
            RelativeSizeAxes = Axes.X;
            Height = 66;
            Action = action;
            CornerRadius = AimModVisualStyle.CardRadius;
            LocalReplay latest = group.Attempts[0];
            double best = group.Attempts.Max(replay => replay.Accuracy);
            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = 3,
                    Colour = AimModVisualStyle.DifficultyColour(latest.StarRating),
                },
                title = new TruncatingSpriteText
                {
                    Text = group.Title,
                    Position = new(14, 9),
                    Font = new FontUsage(size: 14, weight: "Bold"),
                    Colour = AimModPalette.Text,
                    MaxWidth = 200,
                },
                detail = new TruncatingSpriteText
                {
                    Text = $"{group.Artist}  ·  {group.Difficulty}",
                    Position = new(14, 29),
                    Font = AimModVisualStyle.CaptionFont,
                    Colour = AimModPalette.Muted,
                    MaxWidth = 200,
                },
                place(makeText(
                    $"{group.Attempts.Count:N0} {(group.Attempts.Count == 1 ? "attempt" : "attempts")}  ·  best {formatAccuracy(best)}",
                    AimModVisualStyle.MinReadableFontSize,
                    AimModPalette.Cyan,
                    "SemiBold"), 14, 46),
                difficulty = new AimModDifficultyPill(latest.StarRating)
                {
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.TopRight,
                    Position = new(-10, 9),
                },
                chevron = new SpriteIcon
                {
                    Anchor = Anchor.BottomRight,
                    Origin = Anchor.BottomRight,
                    Position = new(-14, -12),
                    Size = new(11),
                    Colour = AimModPalette.Muted,
                },
            };
            SetExpanded(expanded);
        }

        public void SetExpanded(bool value)
        {
            expanded = value;
            chevron.Icon = value ? FontAwesome.Solid.ChevronUp : FontAwesome.Solid.ChevronDown;
            updateColours();
        }

        public void SetContainsSelection(bool value)
        {
            containsSelection = value;
            updateColours();
        }

        private void updateColours()
        {
            BackgroundColour = expanded ? AimModPalette.PanelRaised : AimModPalette.Panel;
            BorderColour = containsSelection ? AimModPalette.Accent.Opacity(0.45f) : AimModPalette.Border;
        }

        protected override void Update()
        {
            base.Update();
            if (widthTracker.Update(DrawWidth + difficulty.DrawWidth))
            {
                title.MaxWidth = Math.Max(80, DrawWidth - 14 - difficulty.DrawWidth - 20);
                detail.MaxWidth = Math.Max(80, DrawWidth - 28);
            }
        }
    }

    private partial class ReplayBrowserRow : AimModInteractiveSurface
    {
        private readonly Box accent;
        private readonly SpriteIcon playIcon;
        private readonly OsuSpriteText state;
        private readonly Colour4 difficultyColour;
        private readonly TruncatingSpriteText details;
        private readonly FillFlowContainer stateFlow;
        private AimModLayout.ChangeTracker<float> widthTracker;

        public Guid ScoreId { get; }

        protected override void Update()
        {
            base.Update();
            if (widthTracker.Update(DrawWidth - stateFlow.DrawWidth))
                details.MaxWidth = Math.Max(60, DrawWidth - stateFlow.DrawWidth - 14 - 20);
        }

        public ReplayBrowserRow(LocalReplay replay, bool selected, Action action)
        {
            ScoreId = replay.ScoreId;
            difficultyColour = AimModVisualStyle.DifficultyColour(replay.StarRating);
            RelativeSizeAxes = Axes.X;
            Height = 56;
            CornerRadius = AimModVisualStyle.ControlRadius;
            Action = action;
            bool clean = replay.MissCount == 0;
            Children = new Drawable[]
            {
                accent = new Box { RelativeSizeAxes = Axes.Y, Width = 3 },
                place(makeText(formatAccuracy(replay.Accuracy), 15, AimModPalette.Text, "Bold"), 14, 8),
                place(makeText(
                    clean ? "No misses" :$"{replay.MissCount:N0} {(replay.MissCount == 1 ? "miss" : "misses")}",
                    AimModVisualStyle.MinReadableFontSize,
                    clean ? AimModPalette.Success : AimModPalette.Pink,
                    "SemiBold"), 92, 12),
                place(makeText(
                    replay.PerformancePoints is { } pp ? $"{pp:0.#}pp" : "-- pp",
                    14,
                    replay.PerformancePoints is null ? AimModPalette.Muted : AimModPalette.Cyan,
                    "Bold"), -12, 8, Anchor.TopRight, Anchor.TopRight),
                details = truncatingText(
                    $"{FormatPlayedAt(replay.PlayedAt)}  ·  {ScoreMods.Display(replay)}",
                    AimModVisualStyle.MinReadableFontSize,
                    AimModPalette.Muted,
                    140),
                stateFlow = new FillFlowContainer
                {
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.TopRight,
                    Position = new(-12, 33),
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Horizontal,
                    Spacing = new(5, 0),
                    Children = new Drawable[]
                    {
                        playIcon = new SpriteIcon
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Size = new(9),
                            Icon = FontAwesome.Solid.Play,
                        },
                        state = new OsuSpriteText
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Font = AimModVisualStyle.CaptionStrongFont,
                        },
                    },
                },
            };
            details.Position = new(14, 33);
            SetSelected(selected);
        }

        public void SetSelected(bool selected)
        {
            BackgroundColour = selected ? AimModPalette.AccentMuted : AimModPalette.PanelRaised;
            BorderColour = selected ? AimModPalette.Accent.Opacity(0.5f) : AimModPalette.Border;
            accent.Colour = selected ? AimModPalette.Accent : difficultyColour;
            playIcon.Colour = state.Colour = selected ? AimModPalette.Accent : AimModPalette.Muted;
            state.Text = selected ? "Watching" : "Watch";
        }
    }
}
