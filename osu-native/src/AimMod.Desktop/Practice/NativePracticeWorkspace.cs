using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Coaching;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Framework.Threading;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.Practice;

public partial class NativePracticeWorkspace : CompositeDrawable
{
    private readonly Func<PracticeMapCandidate, CancellationToken, Task<IReadOnlyList<PracticeSectionChoice>>> inspect;
    private readonly Func<PracticeMapGenerationRequest, CancellationToken, Task<PracticeMapGenerationResult>> generate;
    private readonly Func<SavedPracticeMap, CancellationToken, Task<LazerBeatmapInstallResult>> open;
    private readonly PracticeMapLibrary library;
    private readonly Func<LocalReplay, CancellationToken, Task>? openBeatmap;
    private readonly Container columns;
    private readonly AimModScrollContainer detailScroll;
    private readonly AimModLoadingOverlay loadingOverlay;
    private readonly PracticeMapList list = new() { RelativeSizeAxes = Axes.None };
    private readonly FillFlowContainer detail = flow();
    private readonly AimModTextBox search = new() { RelativeSizeAxes = Axes.X, Height = 40, PlaceholderText = "Search maps or difficulties" };
    private readonly Bindable<string> modSelection = new(ScoreMods.Any);
    private readonly ScoreModFilterDropdown modDropdown;
    private readonly Bindable<PracticeCandidateSort> sort = new(PracticeCandidateSort.WeakestFirst);
    private readonly Bindable<PracticeEvidenceFilter> evidence = new(PracticeEvidenceFilter.AnyEvidence);
    private readonly Bindable<PracticeLibrarySort> librarySort = new(PracticeLibrarySort.Newest);
    private readonly Bindable<int> savedScenario = new(-1);
    private readonly BindableBool favouriteFilter = new(false);
    private readonly BindableDouble minimumStars = new(0) { MinValue = 0, MaxValue = 10 };
    private readonly BindableDouble maximumStars = new(10) { MinValue = 0, MaxValue = 10 };
    private readonly Bindable<int> duration = new(60);
    private readonly Bindable<int> rounds = new(6);
    private readonly Bindable<int> leadIn = new(4);
    private readonly Bindable<int> recovery = new(3);
    private readonly Bindable<int> speed = new(100);
    private readonly Bindable<PracticeDrillType> scenario = new(PracticeDrillType.Mixed);
    private readonly Bindable<int> sectionIndex = new(0);
    private readonly Container sourceFilters;
    private readonly Container savedFilters;
    private readonly OsuSpriteText count;
    private readonly OsuSpriteText status;
    private readonly OsuSpriteText elapsed;
    private readonly Container filtersScroll;
    private readonly Container statusHost;
    private readonly FillFlowContainer statusText;
    private readonly Button cancel;
    private readonly Button createButton;
    private IReadOnlyList<PracticeMapCandidate> candidates = [];
    private IReadOnlyList<PracticeSectionChoice> sections = [];
    private PracticeMapCandidate? selected;
    private PracticeSectionChoice? choice;
    private CancellationTokenSource? operation;
    private ScheduledDelegate? refresh;
    private ScheduledDelegate? settingsSave;
    private PracticeWorkspaceSettings? pendingSettings;
    private PracticeWorkspaceSettings? persistedSettings;
    private IReadOnlyList<SavedPracticeMap> savedMaps = [];
    private bool libraryLoading;
    private int dataRevision;
    private (bool Saved, string Search, PracticeCandidateSort Sort, PracticeEvidenceFilter Evidence, double Min, double Max, PracticeLibrarySort LibrarySort, int Scenario, bool Favourites, int Revision, string Mods)? rendered;
    private bool savedView;
    private bool busy;
    private bool generating;
    private int epoch;
    private DateTimeOffset started;
    private int? tappingFirstObject;
    private bool advancedSettings;
    private readonly Action close;
    private bool guidedEditor;
    private bool createAllSections;
    private bool showIncludedSections;
    public bool IsShowingSavedPractice => savedView;
    private IReadOnlyList<LocalReplay> sourceHistory = [];
    public void SetSourceHistory(IReadOnlyList<LocalReplay> history) => sourceHistory = history;
    public void OpenSaved(SavedPracticeMap map) { Alpha = 1; showIncludedSections = false; guidedEditor = true; savedView = true; showSaved(map); }
    private readonly Drawable browserHeader;
    private readonly Container guidedHeader;
    private readonly OsuSpriteText guidedTitle;

    public NativePracticeWorkspace(
        Func<PracticeMapCandidate, CancellationToken, Task<IReadOnlyList<PracticeSectionChoice>>> inspect,
        Func<PracticeMapGenerationRequest, CancellationToken, Task<PracticeMapGenerationResult>> generate,
        Func<SavedPracticeMap, CancellationToken, Task<LazerBeatmapInstallResult>> open,
        PracticeMapLibrary library, Action close, Func<LocalReplay, CancellationToken, Task>? openBeatmap = null)
    {
        this.close = close;
        this.inspect = inspect;
        this.generate = generate;
        this.open = open;
        this.library = library;
        this.openBeatmap = openBeatmap;
        PracticeWorkspaceSettings settings = library.LoadSettings();
        persistedSettings = settings;
        search.Current.Value = settings.Search ?? "";
        sort.Value = Enum.IsDefined(settings.Sort) ? settings.Sort : PracticeCandidateSort.WeakestFirst;
        evidence.Value = Enum.IsDefined(settings.Evidence) ? settings.Evidence : PracticeEvidenceFilter.AnyEvidence;
        minimumStars.Value = double.IsFinite(settings.MinimumStars) ? Math.Clamp(settings.MinimumStars, 0, 10) : 0;
        maximumStars.Value = double.IsFinite(settings.MaximumStars) ? Math.Clamp(settings.MaximumStars, minimumStars.Value, 10) : 10;
        duration.Value = new[] { 30, 60, 90, 120 }.Contains(settings.DurationSeconds) ? settings.DurationSeconds : 60;
        rounds.Value = new[] { 2, 3, 4, 6, 8, 12, 16 }.Contains(settings.MinimumRounds) ? settings.MinimumRounds : 6;
        leadIn.Value = new[] { 2, 4, 6, 8 }.Contains(settings.LeadInSeconds) ? settings.LeadInSeconds : 4;
        recovery.Value = Math.Clamp(settings.PaddingSeconds, 1, 5);
        speed.Value = new[] { 75, 85, 90, 95, 100, 105, 110, 120 }.Contains(settings.SpeedPercent) ? settings.SpeedPercent : 100;
        RelativeSizeAxes = Axes.Both;
        Alpha = 0;
        Depth = -50;
        browserHeader = new GridContainer
        {
            RelativeSizeAxes = Axes.X, Height = 44,
            ColumnDimensions = [new Dimension(GridSizeMode.Relative, .4f), new Dimension(GridSizeMode.Relative, .3f), new Dimension(GridSizeMode.Relative, .3f)],
            Content = new[] { new Drawable[]
            {
                new Button("Coaching", FontAwesome.Solid.ArrowLeft, close),
                new Button("Find drills", FontAwesome.Solid.Crosshairs, () => switchView(false)),
                new Button("Saved drills", FontAwesome.Solid.Folder, () => switchView(true)),
            } },
        };
        guidedHeader = new Container { RelativeSizeAxes = Axes.X, Height = 60, Alpha = 0,
            Children = [new Button("Back to coaching", FontAwesome.Solid.ArrowLeft, close) { RelativeSizeAxes = Axes.None, Width = 190 },
                guidedTitle = text("Prepare your practice map", 24).With(t => { t.X = 220; t.Width = .6f; })] };
        sourceFilters = new Container { RelativeSizeAxes = Axes.X, Height = 42, Child = new GridContainer
        {
            RelativeSizeAxes = Axes.Both,
            ColumnDimensions = [new Dimension(GridSizeMode.Relative, .25f), new Dimension(GridSizeMode.Relative, .25f), new Dimension(GridSizeMode.Relative, .25f), new Dimension(GridSizeMode.Relative, .25f)],
            Content = new[] { new Drawable[]
            {
                new StatisticsFilterBar { RelativeSizeAxes=Axes.Both, Child=modDropdown=new ScoreModFilterDropdown(modSelection) },
                dropdown(sort, Enum.GetValues<PracticeCandidateSort>(), value => value switch
                {
                    PracticeCandidateSort.WeakestFirst => "Highest priority", PracticeCandidateSort.MostRepeated => "Repeated misses",
                    PracticeCandidateSort.MostExactMisses => "Most misses", PracticeCandidateSort.RecentlyPlayed => "Recently played",
                    PracticeCandidateSort.HardestFirst => "Hardest first", PracticeCandidateSort.EasiestFirst => "Easiest first", _ => "Title",
                }),
                dropdown(evidence, Enum.GetValues<PracticeEvidenceFilter>(), value => value switch
                {
                    PracticeEvidenceFilter.AnyEvidence => "Any evidence", PracticeEvidenceFilter.RepeatedAcrossAttempts => "Multiple attempts",
                    PracticeEvidenceFilter.HighConfidence => "High confidence", PracticeEvidenceFilter.ThreePlusMisses => "3+ misses", _ => "5+ misses",
                }),
                new AimModStarRatingFilter { RelativeSizeAxes = Axes.X, Height = 38, LowerBound = minimumStars, UpperBound = maximumStars },
            } },
        } };
        savedFilters = new Container { RelativeSizeAxes = Axes.X, Height = 42, Alpha = 0, Child = new GridContainer
        {
            RelativeSizeAxes = Axes.Both,
            Content = new[] { new Drawable[]
            {
                dropdown(librarySort, Enum.GetValues<PracticeLibrarySort>(), value => value.ToString()),
                dropdown(savedScenario, new[] { -1 }.Concat(Enum.GetValues<PracticeDrillType>().Select(value => (int)value)), value => value == -1 ? "Any scenario" : PracticeMapPlanner.Label((PracticeDrillType)value)),
                new OsuCheckbox { RelativeSizeAxes = Axes.X, LabelText = "Favourites", Current = favouriteFilter },
            } },
        } };
        var filterHost = new Container { RelativeSizeAxes = Axes.X, Height = 46, Children = [sourceFilters, savedFilters] };
        var filterBody = flow();
        filterBody.Add(search);
        filterBody.Add(filterHost);
        filterBody.Add(count = text("Practice", 14));
        filtersScroll = new Container { RelativeSizeAxes = Axes.X, Y = 54, Height = 124, Depth = -20, Child = filterBody };
        columns = new Container { RelativeSizeAxes = Axes.X, Y = 192 };
        columns.Add(list);
        columns.Add(detailScroll = new AimModScrollContainer { Child = detail });
        columns.Add(loadingOverlay = new AimModLoadingOverlay { RelativeSizeAxes = Axes.None, Depth = -30 });
        statusText = flow();
        statusText.Add(status = text("", 14));
        statusText.Add(elapsed = text("", 12, AimModPalette.Muted));
        cancel = new Button("Cancel", FontAwesome.Solid.Times, cancelOperation) { Width = 110, RelativeSizeAxes = Axes.None, Anchor = Anchor.TopRight, Origin = Anchor.TopRight };
        createButton = new Button("Create practice map", FontAwesome.Solid.Plus, create, true) { Width = 230, RelativeSizeAxes = Axes.None, Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Alpha = 0 };
        statusHost = new Container { RelativeSizeAxes = Axes.X, Height = 60, Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, Children = [statusText, cancel, createButton] };
        InternalChildren = [new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas }, browserHeader, guidedHeader, filtersScroll, columns, statusHost];
        search.Current.BindValueChanged(_ => scheduleRefresh());
        modSelection.BindValueChanged(_ => scheduleRefresh());
        sort.BindValueChanged(_ => refreshList());
        evidence.BindValueChanged(_ => refreshList());
        librarySort.BindValueChanged(_ => refreshList());
        savedScenario.BindValueChanged(_ => refreshList());
        favouriteFilter.BindValueChanged(_ => refreshList());
        minimumStars.BindValueChanged(_ => scheduleRefresh());
        maximumStars.BindValueChanged(_ => scheduleRefresh());
        duration.BindValueChanged(_ => saveSettings());
        rounds.BindValueChanged(_ => saveSettings());
        leadIn.BindValueChanged(_ => saveSettings());
        recovery.BindValueChanged(_ => saveSettings());
        speed.BindValueChanged(_ => saveSettings());
        showEmpty();
    }

    public void SetCandidates(IReadOnlyList<PracticeMapCandidate> values)
    {
        if (ReferenceEquals(candidates, values)) return;
        candidates = values;
        modDropdown.SetScores(values.Select(v=>v.SourceReplay));
        dataRevision++;
        if (Alpha > 0) refreshList();
    }

    public void Open(string? query = null)
    {
        Alpha = 1;
        if (query is not null) { search.Current.Value = query; savedView = false; }
        switchView(savedView);
    }

    public void OpenCandidate(PracticeMapCandidate candidate) { Open(candidate.SourceReplay.Title); select(candidate); guidedEditor = true; showIncludedSections = false; createAllSections = true; advancedSettings = false; detailScroll.ScrollTo(0, false); }
    public void OpenTappingPhrase(PracticeMapCandidate candidate, int firstObjectIndex, int playbackSpeed)
    {
        Open(candidate.SourceReplay.Title);
        select(candidate);
        tappingFirstObject = firstObjectIndex;
        guidedEditor = true; showIncludedSections = false; createAllSections = true; advancedSettings = false; detailScroll.ScrollTo(0, false);
        speed.Value = playbackSpeed;
        rounds.Value = 3;
    }

    private void switchView(bool saved)
    {
        guidedEditor = false;
        savedView = saved;
        sourceFilters.Alpha = saved ? 0 : 1;
        savedFilters.Alpha = saved ? 1 : 0;
        if (!busy) { selected = null; choice = null; detail.Clear(); showEmpty(); }
        refreshList();
        if (saved) _ = reloadLibraryAsync();
    }

    private void scheduleRefresh()
    {
        refresh?.Cancel();
        refresh = Scheduler.AddDelayed(refreshList, 180);
    }

    private void refreshList()
    {
        if (IsDisposed) return;
        saveSettings();
        var state = (savedView, search.Current.Value, sort.Value, evidence.Value, minimumStars.Value, maximumStars.Value,
            librarySort.Value, savedScenario.Value, favouriteFilter.Value, dataRevision, modSelection.Value);
        if (rendered == state) return;
        rendered = state;
        var rows = new List<PracticeMapListRow>();
        if (savedView)
        {
            try
            {
                var maps = PracticeMapLibrary.Search(savedMaps, search.Current.Value, savedScenario.Value == -1 ? null : (PracticeDrillType)savedScenario.Value, favouriteFilter.Value, librarySort.Value);
                count.Text = $"Saved drills / {maps.Count} maps{(favouriteFilter.Value ? " / Favourites" : "")}";
                foreach (SavedPracticeMap map in maps)
                    rows.Add(new(map.Id, map.Title, $"{map.Difficulty} / {PracticeMapPlanner.Label(map.Scenario)}",
                        $"{map.DurationMs / 1000:0}s / {map.Repetitions} rounds{(map.Favourite ? " / Favourite" : "")}", null, selectRow));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { status.Text = "Your saved drills could not be read. Try again."; }
        }
        else
        {
            PracticeCandidatePage page = PracticeMapCandidateSearch.Search(candidates.Where(c=>ScoreMods.Matches(c.SourceReplay,modSelection.Value)).ToArray(),
                new PracticeCandidateQuery(search.Current.Value, sort.Value, evidence.Value, minimumStars.Value, maximumStars.Value));
            count.Text = $"Find drills / {page.Total} matching maps / {candidates.Count} in coaching timeframe";
            foreach (PracticeMapCandidate candidate in page.Items)
                rows.Add(new(candidate.SourceReplay.ScoreId.ToString(), candidate.SourceReplay.Title, candidate.SourceReplay.Difficulty,
                    $"{candidate.SourceReplay.StarRating:0.00}* / {candidate.MissCount} misses / {candidate.AnalysedAttempts} attempts",
                    candidate.SourceReplay.BackgroundPath, selectRow));
        }
        list.SetRows(rows);
        if (rows.Count == 0) count.Text = libraryLoading && savedView ? "Loading saved drills..." : "No matching maps";
    }

    private void selectRow(string key)
    {
        if (savedView)
        {
            var map = savedMaps.FirstOrDefault(item => item.Id == key);
            if (map is not null) showSaved(map);
        }
        else
        {
            var candidate = candidates.FirstOrDefault(item => item.SourceReplay.ScoreId.ToString() == key);
            if (candidate is not null) select(candidate);
        }
    }

    private async Task reloadLibraryAsync(bool selectNewest = false)
    {
        if (libraryLoading || IsDisposed) return;
        libraryLoading = true;
        if (savedView && savedMaps.Count == 0) count.Text = "Loading saved drills...";
        try
        {
            var maps = await library.ListAsync().ConfigureAwait(false);
            if (!IsDisposed) Schedule(() =>
            {
                libraryLoading = false;
                savedMaps = maps;
                dataRevision++;
                refreshList();
                if (selectNewest && savedView && maps.FirstOrDefault() is { } map) showSaved(map);
            });
        }
        catch
        {
            if (!IsDisposed) Schedule(() => { libraryLoading = false; status.Text = "Your saved drills could not be read. Try again."; });
        }
    }

    private void saveSettings()
    {
        var settings = new PracticeWorkspaceSettings(search.Current.Value, sort.Value, evidence.Value, minimumStars.Value, maximumStars.Value,
            duration.Value, rounds.Value, leadIn.Value, recovery.Value, speed.Value);
        if (settings == pendingSettings || (pendingSettings is null && settings == persistedSettings)) return;
        pendingSettings = settings;
        settingsSave?.Cancel();
        settingsSave = Scheduler.AddDelayed(flushSettings, 500);
    }

    private void flushSettings()
    {
        if (pendingSettings is not { } settings) return;
        pendingSettings = null;
        persistedSettings = settings;
        _ = persistSettingsAsync(settings);
    }

    private async Task persistSettingsAsync(PracticeWorkspaceSettings settings)
    {
        try { await library.SaveSettingsAsync(settings).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void select(PracticeMapCandidate candidate)
    {
        if (generating) return;
        tappingFirstObject = null;
        selected = candidate;
        choice = null;
        int ticket = startOperation("Reading source patterns");
        detail.Clear();
        detail.Add(text(candidate.SourceReplay.Title, 20));
        detail.Add(text("Finding practice sections...", 14, AimModPalette.Cyan));
        _ = inspectAsync(candidate, ticket, operation!.Token);
    }

    private async Task inspectAsync(PracticeMapCandidate candidate, int ticket, CancellationToken token)
    {
        try
        {
            IReadOnlyList<PracticeSectionChoice> result = await inspect(candidate, token).ConfigureAwait(false);
            deliver(ticket, () =>
            {
                finishOperation($"{result.Count} practice sections available");
                sections = result;
                var target = tappingFirstObject is { } first
                    ? result.Where(s => s.Section.FirstObjectIndex <= first && s.Section.LastObjectIndex >= first)
                        .OrderBy(s => s.Section.LastObjectIndex - s.Section.FirstObjectIndex).FirstOrDefault()
                    : null;
                scenario.Value = target?.Scenario ?? result.FirstOrDefault()?.Scenario ?? PracticeDrillType.Mixed;
                sectionIndex.Value = target is null ? 0 : result.Where(s => s.Scenario == scenario.Value).ToList().IndexOf(target);
                showEditor();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { deliver(ticket, () => showSourceFailure(candidate, error)); }
    }

    private void showSourceFailure(PracticeMapCandidate candidate, Exception error)
    {
        finishOperation("Practice map could not be prepared");
        sections = []; choice = null;
        detail.Clear();
        detail.Add(text(candidate.SourceReplay.Title, 24));
        detail.Add(text(candidate.SourceReplay.Difficulty, 16, AimModPalette.Cyan));
        detail.Add(text("We could not load this map", 22));
        string help = error is ExternalLazerReplayOpenException sourceError ? sourceError.Code switch {
            "audio_file_missing" => "The map's audio is missing. Reinstall the map in osu!, then retry.",
            "beatmap_missing" or "beatmap_file_missing" or "beatmap_hash_invalid" => "This difficulty is unavailable locally. Install it in osu!, then retry.",
            "lazer_library_unavailable" => "Connect your osu! library in Settings, then retry.",
            "ruleset_unsupported" => "Choose an osu!standard play for practice.",
            _ => "Check that the map and its audio are installed in osu!, then retry."
        } : "Check that the map and its audio are installed in osu!, then retry.";
        detail.Add(text(help, 16, AimModPalette.Muted));
        int? target = tappingFirstObject;
        detail.Add(new Button("Retry loading this map", FontAwesome.Solid.Redo, () => {
            select(candidate); tappingFirstObject = target;
        }, true));
        detail.Add(new Button("Back to coaching", FontAwesome.Solid.ArrowLeft, close));
        detailScroll.ScrollTo(0, false);
    }

    private void showEditor()
    {
        detail.Clear();
        if (selected is null || sections.Count == 0) { detail.Add(text("No supported practice sections found", 16)); return; }
        detail.Add(text(selected.SourceReplay.Title, 20));
        detail.Add(text(selected.SourceReplay.Difficulty, 13, AimModPalette.Cyan));
        detail.Add(text("Original setup: " + ScoreMods.Display(selected.SourceReplay),14,AimModPalette.Muted));
        if (guidedEditor) detail.Add(text("Choose a comfortable speed and a short practice length.", 15, AimModPalette.Muted));
        LocalReplay sourceReplay = selected.SourceReplay;

        detail.Add(new Button(createAllSections ? "Practice set: all sections (click for one section)" : "Single section (click for a full practice set)",
            FontAwesome.Solid.LayerGroup, () => { createAllSections = !createAllSections; showEditor(); }));
        if (createAllSections)
            detail.Add(text("One mapset, with a separate difficulty for each practice section. Speed and length apply to each difficulty.", 13, AimModPalette.Muted));
        var scenarioControl = dropdown(new Bindable<PracticeDrillType>(scenario.Value), sections.Select(item => item.Scenario).Distinct(), PracticeMapPlanner.Label);
        scenarioControl.Current.BindValueChanged(change => { scenario.Value = change.NewValue; sectionIndex.Value = 0; showEditor(); });

        PracticeSectionChoice[] matches = sections.Where(item => item.Scenario == scenario.Value).ToArray();
        sectionIndex.Value = Math.Clamp(sectionIndex.Value, 0, matches.Length - 1);
        choice = matches[sectionIndex.Value];
        var sectionControl = dropdown(new Bindable<int>(sectionIndex.Value), Enumerable.Range(0, matches.Length), index =>
            $"{timestamp(matches[index].Section.SourceStartTimeMs)} - {timestamp(matches[index].Section.SourceEndTimeMs)}" + (matches[index].Section.WeakObjects.Count > 0 ? $" / {matches[index].Section.WeakObjects.Sum(item => item.MissCount)} misses" : ""));
        sectionControl.Current.BindValueChanged(change => { sectionIndex.Value = change.NewValue; showEditor(); });
        if (!createAllSections) detail.Add(optionPair(field("Pattern", scenarioControl), field("Section", sectionControl)));
        else
        {
            var included = sections.Where(s => s.Scenario == PracticeDrillType.Mixed)
                .DistinctBy(s => (s.Section.FirstObjectIndex, s.Section.LastObjectIndex)).ToArray();
            detail.Add(text($"{included.Length} {(included.Length == 1 ? "difficulty" : "difficulties")} in this practice set", 18, AimModPalette.Cyan));
            if (included.Length > 3)
                detail.Add(new Button(showIncludedSections ? "Hide included difficulties" : "Show included difficulties", FontAwesome.Solid.List,
                    () => { showIncludedSections = !showIncludedSections; showEditor(); }));
            if (showIncludedSections || included.Length <= 3) foreach (var item in included)
                detail.Add(text($"{PracticeMapPlanner.Label(item.Section.DrillType)} / {timestamp(item.Section.SourceStartTimeMs)} - {timestamp(item.Section.SourceEndTimeMs)}", 14, AimModPalette.Muted));
        }
        detail.Add(optionPair(field("Speed", dropdown(speed.GetBoundCopy(), new[] { 75, 85, 90, 95, 100, 105, 110, 120 }, value => $"{value}%")),
            field("Practice length", dropdown(duration.GetBoundCopy(), new[] { 30, 60, 90, 120 }, value => $"{value} seconds"))));

        if (!createAllSections)
        {
        detail.Add(text(choice.Section.WeaknessScore == 0
            ? $"{choice.Section.HitObjects.Count} objects / section practice"
            : $"{choice.Section.HitObjects.Count} objects / {choice.Section.WeakObjects.Count} missed locations", 13, AimModPalette.Cyan));
        if (choice.Section.WeaknessScore == 0)
            detail.Add(text("Choose a section you want to repeat. This selection is based on the map's patterns.", 13, AimModPalette.Muted));
        var reasons = choice.Section.WeakObjects.SelectMany(item => item.Reasons).GroupBy(item => item.Key)
            .Where(group => group.Key != AimMod.Osu.Runtime.Contracts.ReplayMissReason.Unknown)
            .OrderByDescending(group => group.Sum(item => item.Value)).Take(2);
        foreach (var reason in reasons)
            detail.Add(text($"{reasonLabel(reason.Key)} / {reason.Sum(item => item.Value)} observed misses", 12, AimModPalette.Pink));
        string focus = scenario.Value switch
        {
            PracticeDrillType.Streams => "Focus: hold an even tapping rhythm through the full run.",
            PracticeDrillType.Bursts => "Focus: clean burst starts and controlled final taps.",
            PracticeDrillType.LongJumps => "Focus: land on the target before tapping, then leave.",
            PracticeDrillType.SliderControl => "Focus: follow the slider through its end before moving on.",
            PracticeDrillType.RhythmChanges => "Focus: hear the subdivision change without rushing the next note.",
            _ => "Focus: repeat the difficult phrase with its original approach.",
        };
        detail.Add(text(focus, 12, AimModPalette.Muted));
        }
        else detail.Add(text("Practise each difficulty, then return to the original map to check your progress.", 13, AimModPalette.Muted));
        detail.Add(new Button(advancedSettings ? "Hide repetition & lead-in settings" : "Repetition & lead-in settings",
            advancedSettings ? FontAwesome.Solid.ChevronUp : FontAwesome.Solid.ChevronDown,
            () => { advancedSettings = !advancedSettings; showEditor(); }));
        if (advancedSettings)
        {
            detail.Add(optionPair(field("Minimum rounds", dropdown(rounds.GetBoundCopy(), new[] { 2, 3, 4, 6, 8, 12, 16 }, value => $"{value} rounds")),
                field("Lead-in", dropdown(leadIn.GetBoundCopy(), new[] { 2, 4, 6, 8 }, value => $"{value} seconds"))));
            detail.Add(field("Audio padding per side", dropdown(recovery.GetBoundCopy(), new[] { 1, 2, 3, 4, 5 }, value => $"{value} seconds")));
            detail.Add(text("Longer practice lengths can add rounds above your minimum.", 13, AimModPalette.Muted));
        }

        if (advancedSettings) detail.Add(new PatternPreview(choice.Section) { Height = 130 });
    }

    private void create()
    {
        if (selected is null || choice is null || busy) return;
        int ticket = startOperation("Preparing practice map");
        generating = true;
        var options = new PracticeMapOptions(scenario.Value, MaximumSections: 1, LeadInMs: leadIn.Value * 1000,
            AudioPaddingMs: recovery.Value * 1000, TargetDurationMs: duration.Value * 1000,
            MinimumRepetitions: rounds.Value, MaximumRepetitions: 24, FirstObjectIndex: choice.Section.FirstObjectIndex, PlaybackRate: speed.Value / 100d);
        var progress = new Progress<string>(message => deliver(ticket, () => { status.Text = message; loadingOverlay.ShowLoading("Creating practice map", message); }));
        _ = createAsync(new PracticeMapGenerationRequest(selected, scenario.Value, options, progress, createAllSections, sourceHistory), ticket, operation!.Token);
    }

    private async Task createAsync(PracticeMapGenerationRequest request, int ticket, CancellationToken token)
    {
        try
        {
            PracticeMapGenerationResult result = await generate(request, token).ConfigureAwait(false);
            deliver(ticket, () =>
            {
                finishOperation(result.Success ? "Practice map saved" : result.Message);
                if (result.Success)
                {
                    savedView = true; showIncludedSections = false;
                    search.Current.Value = "";
                    sourceFilters.Alpha = 0;
                    savedFilters.Alpha = 1;
                    _ = reloadLibraryAsync(selectNewest: true);
                }
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { deliver(ticket, () => finishOperation("Practice creation failed. Your source map is unchanged.")); }
    }

    private void showSaved(SavedPracticeMap map, bool confirmDelete = false)
    {
        if (busy) return;
        detail.Clear();
        detailScroll.ScrollTo(0, false);
        detail.Add(text(map.Title, 20));
        detail.Add(text(map.Difficulty, 13, AimModPalette.Cyan));
        if (map.Tracking is { } tracking)
        {
            detail.Add(text($"{tracking.Difficulties.Count} practice {(tracking.Difficulties.Count == 1 ? "difficulty" : "difficulties")}", 18));
            if (tracking.Difficulties.Count > 3) detail.Add(new Button(showIncludedSections ? "Hide difficulties" : "Show difficulties", FontAwesome.Solid.List,
                () => { showIncludedSections = !showIncludedSections; showSaved(map); }));
            if (showIncludedSections || tracking.Difficulties.Count <= 3)
                foreach (var difficulty in tracking.Difficulties) detail.Add(text(difficulty.Name, 14, AimModPalette.Cyan));
        }
        else detail.Add(text(PracticeMapPlanner.Label(map.Scenario), 18));
        if (map.SourceEndMs > 0 && (map.Tracking?.Difficulties.Count ?? 1) == 1) detail.Add(text($"Source: {timestamp(map.SourceStartMs)} - {timestamp(map.SourceEndMs)}", 14));
        detail.Add(text($"{map.DurationMs / 1000:0} seconds total / {map.ObjectCount} objects", 14));
        detail.Add(text($"{map.PlaybackRate * 100:0}% speed", 14, AimModPalette.Cyan));
        detail.Add(text($"Created {map.CreatedAt:dd MMM yyyy HH:mm}", 12, AimModPalette.Muted));
        detail.Add(new Button("Open practice map in osu!", FontAwesome.Solid.Play, () =>
        {
            if (busy) return;
            int ticket = startOperation("Opening in osu!");
            _ = openAsync(map, ticket, operation!.Token);
        }));
        if (guidedEditor)
        {
            detail.Add(text("After practice, play the original map again. Return to Coaching to review your results.", 15, AimModPalette.Muted));
            return;
        }
        detail.Add(new Button(map.Favourite ? "Remove favourite" : "Favourite", FontAwesome.Solid.Star, () =>
        {
            _ = updateSavedAsync(map with { Favourite = !map.Favourite });
        }));
        var rename = new AimModTextBox { RelativeSizeAxes = Axes.X, Height = 40, Current = new Bindable<string>(map.Title) };
        detail.Add(field("Library name", rename));
        detail.Add(new Button("Rename", FontAwesome.Solid.Pen, () =>
        {
            string name = rename.Current.Value.Trim();
            if (name.Length is < 1 or > 120) { status.Text = "Use a name between 1 and 120 characters."; return; }
            _ = updateSavedAsync(map with { Title = name });
        }));
        detail.Add(new Button(confirmDelete ? "Confirm delete from AimMod" : "Delete from AimMod", FontAwesome.Solid.Trash, () =>
        {
            if (!confirmDelete) { showSaved(map, true); return; }
            _ = updateSavedAsync(map, delete: true);
        }));
        if (confirmDelete) detail.Add(new Button("Keep drill", FontAwesome.Solid.Times, () => showSaved(map)));
    }

    private async Task updateSavedAsync(SavedPracticeMap map, bool delete = false)
    {
        if (busy) return;
        int ticket = startOperation(delete ? "Deleting saved drill" : "Saving drill");
        try
        {
            await library.RunAsync(() => { if (delete) library.Delete(map.Id); else library.Save(map); return true; }).ConfigureAwait(false);
            deliver(ticket, () =>
            {
                finishOperation(delete ? "Deleted from AimMod. Installed osu! copies are unchanged." : "Drill saved");
                savedMaps = delete ? savedMaps.Where(item => item.Id != map.Id).ToArray()
                    : savedMaps.Select(item => item.Id == map.Id ? map : item).ToArray();
                dataRevision++;
                refreshList();
                if (delete) { detail.Clear(); showEmpty(); } else showSaved(map);
            });
        }
        catch { deliver(ticket, () => finishOperation("The saved drill could not be updated. Try again.")); }
    }

    private async Task openAsync(SavedPracticeMap map, int ticket, CancellationToken token)
    {
        try
        {
            LazerBeatmapInstallResult result = await open(map, token).ConfigureAwait(false);
            deliver(ticket, () => finishOperation(result.Status is LazerBeatmapInstallStatus.Sent or LazerBeatmapInstallStatus.LazerStarted
                ? "Sent to osu!" : "Could not open osu!. Check your client preference and try again."));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { deliver(ticket, () => finishOperation("Could not open the saved practice map.")); }
    }

    private int startOperation(string message)
    {
        operation?.Cancel();
        operation?.Dispose();
        operation = new CancellationTokenSource();
        busy = true;
        started = DateTimeOffset.UtcNow;
        status.Text = message;
        loadingOverlay.ShowLoading(message, "Preparing your drill");
        return ++epoch;
    }

    private void finishOperation(string message)
    {
        // Progress<T> callbacks may arrive after the task's completion callback.
        // Retire the operation so those messages cannot reopen its loading overlay.
        epoch++;
        busy = generating = false;
        status.Text = message;
        loadingOverlay.HideLoading();
    }
    private void cancelOperation() { operation?.Cancel(); finishOperation("Cancelled"); }
    private void deliver(int ticket, Action action)
    {
        if (!IsDisposed) Schedule(() => { if (!IsDisposed && epoch == ticket) action(); });
    }
    private void showEmpty() => detail.Add(text(savedView ? "Select a saved drill" : "Select a map to inspect its practice sections", 18, AimModPalette.Muted));

    protected override void Update()
    {
        base.Update();
        browserHeader.Alpha = guidedEditor ? 0 : 1;
        guidedHeader.Alpha = guidedEditor ? 1 : 0;
        guidedTitle.Text = savedView ? "Your practice is ready" : createAllSections ? "Prepare your practice set" : "Prepare your practice map";
        filtersScroll.Alpha = guidedEditor ? 0 : 1;
        list.Alpha = guidedEditor ? 0 : 1;
        columns.Y = guidedEditor ? 76 : 192;
        float available = Math.Max(220, DrawHeight - (guidedEditor ? 150 : 264));
        columns.Height = available;
        bool narrow = DrawWidth < 820;
        float left = narrow ? DrawWidth : DrawWidth * .34f;
        list.Size = new(left - (narrow ? 0 : 16), narrow ? available * .36f : available);
        detailScroll.Position = new(narrow ? 0 : left, narrow ? available * .36f + 12 : 0);
        detailScroll.Size = new(narrow ? DrawWidth : DrawWidth - left, narrow ? available * .64f - 12 : available);
        if (guidedEditor)
        {
            float width = Math.Min(920, DrawWidth);
            detailScroll.Position = new((DrawWidth - width) / 2, 0);
            detailScroll.Size = new(width, available);
        }
        loadingOverlay.Position = detailScroll.Position;
        loadingOverlay.Size = detailScroll.Size;
        statusText.Width = Math.Max(100, DrawWidth - 244);
        statusText.RelativeSizeAxes = Axes.None;
        cancel.Alpha = busy ? 1 : 0;
        createButton.SetTitle(createAllSections ? "Create practice set" : "Create practice map");
        createButton.Alpha = !busy && !savedView && choice is not null ? 1 : 0;
        elapsed.Text = busy ? $"{(DateTimeOffset.UtcNow - started).TotalSeconds:0}s elapsed" : "";
    }

    protected override void Dispose(bool isDisposing)
    {
        operation?.Cancel(); operation?.Dispose(); refresh?.Cancel();
        settingsSave?.Cancel();
        flushSettings();
        base.Dispose(isDisposing);
    }

    private static string timestamp(double ms) => TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss");
    private static string reasonLabel(AimMod.Osu.Runtime.Contracts.ReplayMissReason reason) => reason switch
    {
        AimMod.Osu.Runtime.Contracts.ReplayMissReason.EarlyClick => "Early taps",
        AimMod.Osu.Runtime.Contracts.ReplayMissReason.LateClick => "Late taps",
        AimMod.Osu.Runtime.Contracts.ReplayMissReason.Undershoot => "Undershooting",
        AimMod.Osu.Runtime.Contracts.ReplayMissReason.Overshoot => "Overshooting",
        AimMod.Osu.Runtime.Contracts.ReplayMissReason.OnTargetNoClick => "On target without a tap",
        _ => "Aim deviation",
    };
    private static FillFlowContainer flow() => new() { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(10), Padding = new MarginPadding { Right = 10, Bottom = 12 } };
    private static OsuSpriteText text(string value, float size, Colour4? colour = null) => new TruncatingSpriteText
    { Text = value, Font = new osu.Framework.Graphics.Sprites.FontUsage("Torus", size), Colour = colour ?? AimModPalette.Text, RelativeSizeAxes = Axes.X };
    private Drawable optionPair(Drawable left, Drawable right) => new GridContainer
    {
        RelativeSizeAxes = Axes.X, Height = 84, Depth = -100 + detail.Children.Count,
        ColumnDimensions = [new Dimension(GridSizeMode.Relative, .5f), new Dimension(GridSizeMode.Relative, .5f)],
        Content = new[] { new[] { left, right } },
    };

    private static Drawable field(string name, Drawable control)
    {
        var body = flow(); body.Depth = -10; body.Add(text(name, 12, AimModPalette.Cyan)); body.Add(control); return body;
    }
    private static Dropdown<T> dropdown<T>(Bindable<T> current, IEnumerable<T> items, Func<T, string> label) where T : notnull =>
        new(label) { RelativeSizeAxes = Axes.X, Width = .98f, Items = items, Current = current, Depth = -10 };

    private partial class Dropdown<T>(Func<T, string> label) : AimModDropdown<T> where T : notnull
    {
        protected override LocalisableString GenerateItemText(T item) => label(item);
    }

    private partial class Button : ClickableContainer
    {
        private readonly OsuSpriteText caption;
        public void SetTitle(string title) => caption.Text = title;
        public Button(string title, IconUsage icon, Action action, bool primary = false)
        {
            RelativeSizeAxes = Axes.X; Height = 40; Action = action; Masking = true; CornerRadius = 4;
            Children = [new Box { RelativeSizeAxes = Axes.Both, Colour = primary ? Colour4.FromHex("38D9A9") : AimModPalette.PanelRaised },
                new SpriteIcon { Icon = icon, Size = new(16), Position = new(12, 12), Colour = primary ? AimModPalette.Canvas : AimModPalette.Cyan },
                new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Left = 38, Right = 12, Top = 11 }, Child = caption = text(title, 14, primary ? AimModPalette.Canvas : AimModPalette.Text) }];
        }
    }

    private partial class PatternPreview : CompositeDrawable
    {
        private readonly Container playfield = new() { Size = new(512, 384), Anchor = Anchor.Centre, Origin = Anchor.Centre };
        public PatternPreview(PracticeSourceSection section)
        {
            RelativeSizeAxes = Axes.X; Height = 210; Masking = true;
            InternalChildren = [new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas }, playfield];
            var points = section.HitObjects.Take(24).ToArray();
            for (int i = 0; i < points.Length; i++)
            {
                PracticeHitObject item = points[i];
                bool missed = section.WeakObjects.Any(weak => weak.ObjectIndex == item.SourceIndex);
                if (i > 0)
                {
                    PracticeHitObject previous = points[i - 1];
                    float dx = item.X - previous.X, dy = item.Y - previous.Y;
                    playfield.Add(new Box { Position = new(previous.X, previous.Y), Width = MathF.Sqrt(dx * dx + dy * dy), Height = 2,
                        Rotation = MathF.Atan2(dy, dx) * 180 / MathF.PI, Colour = AimModPalette.Muted, Alpha = .35f, Depth = 1 });
                }
                playfield.Add(new Circle { Position = new(item.X, item.Y), Origin = Anchor.Centre, Size = new(26), Colour = missed ? AimModPalette.Pink : AimModPalette.Cyan, Alpha = .8f });
                playfield.Add(new OsuSpriteText { Position = new(item.X, item.Y), Origin = Anchor.Centre, Text = (i + 1).ToString(), Font = new FontUsage("Torus", 16), Colour = AimModPalette.Canvas });
            }
        }
        protected override void Update() { base.Update(); playfield.Scale = new(Math.Min(DrawWidth / 540, DrawHeight / 410)); }
    }
}
