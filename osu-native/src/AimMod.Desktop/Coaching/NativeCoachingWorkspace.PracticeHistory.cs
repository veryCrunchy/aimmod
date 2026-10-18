using AimMod.Desktop.Practice;
using AimMod.Desktop.Visuals;
namespace AimMod.Desktop.Coaching;
public partial class NativeCoachingWorkspace
{
    private readonly osu.Framework.Graphics.Containers.FillFlowContainer<osu.Framework.Graphics.Drawable> practiceHistoryHost;
    private readonly osu.Game.Graphics.UserInterface.OsuTextBox savedPracticeSearch;
    private readonly PracticeMapLibrary? practiceLibrary;
    private readonly Func<int> practiceAccountId;
    private IReadOnlyList<PracticeSetProgress> practiceSets=[];
    private int practiceRefreshEpoch;
    private bool practiceHistoryFailed;
    private string? practiceSetsFingerprint;
    private bool showArchivedPractice;
    private string automaticPracticeStatus = "";
    public void SetAutomaticPracticeStatus(string message) { automaticPracticeStatus = message; renderPracticeHistory(); }
    private readonly CancellationTokenSource practiceTrackingLifetime = new();
    private async Task watchPracticeScores(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15),token).ConfigureAwait(false);
                // The visible play list, selected map and saved sets must share the same merged history.
                if (!IsDisposed) Schedule(RefreshHistory);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested) { return; }
            catch(Exception)
            { if(!IsDisposed) Schedule(()=> { practiceHistoryFailed=true; renderPracticeHistory(); }); }
        }
    }
    private void refreshPracticeProgress()
    {
        if(practiceLibrary is null) return;
        int epoch=++practiceRefreshEpoch, account=practiceAccountId(); var runs=allReplays.ToArray();
        _=refreshPracticeProgressAsync(runs,epoch,account);
    }
    private async Task refreshPracticeProgressAsync(LocalLibrary.LocalReplay[] runs,int epoch,int account)
    {
        try
        {
            var result=await practiceLibrary!.RunAsync(()=>loadSavedPracticeSets(runs,account)).ConfigureAwait(false);
            string fingerprint=System.Text.Json.JsonSerializer.Serialize(result);
            if(!IsDisposed) Schedule(()=> {
                if(IsDisposed || epoch!=practiceRefreshEpoch || account!=practiceAccountId()) return;
                bool unchanged=fingerprint==practiceSetsFingerprint && !practiceHistoryFailed;
                practiceSets=result; practiceSetsFingerprint=fingerprint; practiceHistoryFailed=false;
                if(!unchanged) renderPracticeHistory();
            });
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { if(!IsDisposed) Schedule(()=> { practiceHistoryFailed=true; renderPracticeHistory(); }); }
    }
    private IReadOnlyList<PracticeSetProgress> loadSavedPracticeSets(IReadOnlyList<LocalLibrary.LocalReplay> runs, int account)
    {
        var tracked = practiceLibrary!.RefreshProgress(runs, account).ToDictionary(set => set.Map.Id);
        return practiceLibrary.List()
            .Where(map => map.Tracking is null || map.Tracking.AccountId == 0 || map.Tracking.AccountId == account)
            .OrderByDescending(map => map.CreatedAt)
            .Select(map => tracked.GetValueOrDefault(map.Id) ?? new PracticeSetProgress(map, PracticeProgress.Empty))
            .ToArray();
    }
    private void renderPracticeHistory()
    {
        practiceHistoryHost.Clear();
        if (!string.IsNullOrEmpty(automaticPracticeStatus)) practiceHistoryHost.Add(flow(automaticPracticeStatus, 13, AimModPalette.Muted));
        if (practiceHistoryFailed) practiceHistoryHost.Add(flow("Results could not be refreshed. Try again.",16,AimModPalette.Danger));
        string query = savedPracticeSearch.Current.Value.Trim();
        var adaptive = showArchivedPractice ? [] : adaptiveMaps(query);
        if (adaptive.Count > 0)
        {
            practiceHistoryHost.Add(new SectionLine("Adaptive practice", $"{adaptive.Count} {(adaptive.Count == 1 ? "map" : "maps")} · most recent first"));
            foreach (var (run, progress) in adaptive)
                practiceHistoryHost.Add(new AdaptiveMapRow(run, progress, () => openCoachingMap(null, run, returnPage: 3)));
        }
        var groups = practiceSets.Where(s => showArchivedPractice ? s.Map.RetiredAt is not null : s.Map.RetiredAt is null).GroupBy(set => CoachingMapKey(set.Map)).ToArray();
        var visible = groups.Where(group => group.Any(set =>
            (set.Map.Title + " " + set.Map.Difficulty + " " + string.Join(" ", set.Map.Tracking?.Difficulties.Select(d => d.Name) ?? []))
                .Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (groups.Length > 0 || adaptive.Count == 0)
            practiceHistoryHost.Add(new SectionLine(showArchivedPractice ? "Archived practice sets" : "Practice sets",
                $"{visible.Length} of {groups.Length} beatmaps · {visible.Sum(g => g.Count())} sets"));
        if (visible.Length == 0 && adaptive.Count == 0)
        {
            practiceHistoryHost.Add(flow(groups.Length == 0
                ? showArchivedPractice ? "No archived practice sets." : "Choose a map to start your first practice session. Your exercises and progress will stay together here."
                : "No beatmaps match your search.", 15, AimModPalette.Muted));
            if (query.Length > 0) practiceHistoryHost.Add(new CoachingButton("Clear search", () => savedPracticeSearch.Current.Value = "", compact: true));
            if (groups.Length == 0 && query.Length == 0 && !showArchivedPractice)
            {
                var choices = actionFlow();
                choices.Add(visualEntry("Choose a play", "Turn a difficult section into exercises.", PracticeSketchKind.Section, () => showCoachingPage(2)));
                if (openTrainers is not null)
                    choices.Add(visualEntry("Train a skill", "Practise timing, aim or reading first.", PracticeSketchKind.Timing, openTrainers));
                practiceHistoryHost.Add(choices);
            }
        }
        foreach (var group in visible)
        {
            var latest = group.First();
            int attempts = group.SelectMany(s => s.Progress.Attempts).Where(a => !a.Original).DistinctBy(a => a.ScoreId).Count();
            practiceHistoryHost.Add(new CoachingMapRow(latest.Map.Title, latest.Map.Difficulty,
                $"{group.Count()} {(group.Count() == 1 ? "set" : "sets")}  ·  {attempts} attempts  ·  Updated {latest.Map.CreatedAt.ToLocalTime():dd MMM}",
                () => openCoachingMap(group.Key)));
        }
        if (coachingMapId is not null || coachingMapRun is not null) renderCoachingMap();
    }

    /// <summary>Maps with saved adaptive practice that match the search, unfinished first, then by last practice.</summary>
    private IReadOnlyList<(LocalLibrary.LocalReplay Run, AdaptivePracticeProgress Progress)> adaptiveMaps(string query)
    {
        if (adaptiveProgress.Count == 0) return [];
        var runs = new Dictionary<string, LocalLibrary.LocalReplay>();
        foreach (var run in allReplays.Where(r => r.RulesetShortName == "osu").OrderByDescending(r => r.PlayedAt))
        {
            string key = AdaptivePracticeSetup.For(run).Key;
            if (adaptiveProgress.ContainsKey(key)) runs.TryAdd(key, run);
        }
        return adaptiveProgress.Values
            .Where(p => runs.ContainsKey(p.MapKey) && (p.Title + " " + p.Difficulty).Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Completed).ThenByDescending(p => p.UpdatedAt)
            .Select(p => (runs[p.MapKey], p)).ToArray();
    }

    /// <summary>A map with adaptive practice: where the player is with it, its section strip, and the map page on click.</summary>
    private sealed partial class AdaptiveMapRow : AimModInteractiveSurface
    {
        public AdaptiveMapRow(LocalLibrary.LocalReplay run, AdaptivePracticeProgress progress, Action open)
        {
            RelativeSizeAxes = osu.Framework.Graphics.Axes.X; Height = 84; Action = open;
            var strip = new AdaptiveSectionStrip(progress.Sections.Length) { RelativeSizeAxes = osu.Framework.Graphics.Axes.X, Height = 6, Y = 48 };
            strip.Set(progress, -1);
            string state = progress.Completed
                ? $"Cleared at full speed on {progress.CompletedAt!.Value.ToLocalTime():dd MMM}"
                : $"{progress.FullSpeedSections} of {progress.Sections.Length} sections at full speed · slowest {AdaptivePracticeCoach.Percent(progress.SlowestSpeed)}";
            Children = [
                new osu.Framework.Graphics.Containers.Container {
                    RelativeSizeAxes = osu.Framework.Graphics.Axes.Both, Padding = new osu.Framework.Graphics.MarginPadding { Horizontal = 14, Top = 10 },
                    Children = [
                        new osu.Framework.Graphics.Containers.Container {
                            RelativeSizeAxes = osu.Framework.Graphics.Axes.Both, Padding = new osu.Framework.Graphics.MarginPadding { Right = 40 },
                            Children = [
                                new osu.Game.Graphics.Sprites.TruncatingSpriteText { RelativeSizeAxes = osu.Framework.Graphics.Axes.X, Text = $"{run.Title} [{run.Difficulty}]",
                                    Font = new osu.Framework.Graphics.Sprites.FontUsage(size: 16, weight: "SemiBold"), Colour = AimModPalette.Text },
                                new osu.Game.Graphics.Sprites.TruncatingSpriteText { RelativeSizeAxes = osu.Framework.Graphics.Axes.X, Y = 24,
                                    Text = $"{state} · {progress.Attempts:N0} attempts · {progress.UpdatedAt.ToLocalTime():dd MMM}",
                                    Font = new osu.Framework.Graphics.Sprites.FontUsage(size: 12), Colour = progress.Completed ? AimModPalette.Success : AimModPalette.Muted },
                                strip,
                            ],
                        },
                        new osu.Framework.Graphics.Sprites.SpriteIcon { Icon = osu.Framework.Graphics.Sprites.FontAwesome.Solid.ChevronRight, Size = new(12),
                            Colour = AimModPalette.Muted, Anchor = osu.Framework.Graphics.Anchor.CentreRight, Origin = osu.Framework.Graphics.Anchor.CentreRight, Y = -5 },
                    ],
                },
            ];
        }
    }
}
