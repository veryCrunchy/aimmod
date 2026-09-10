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
            if(!IsDisposed) Schedule(()=> {
                if(IsDisposed || epoch!=practiceRefreshEpoch || account!=practiceAccountId()) return;
                practiceSets=result; practiceHistoryFailed=false; renderPracticeHistory();
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
        var groups = practiceSets.Where(s => showArchivedPractice ? s.Map.RetiredAt is not null : s.Map.RetiredAt is null).GroupBy(set => CoachingMapKey(set.Map)).ToArray();
        var visible = groups.Where(group => group.Any(set =>
            (set.Map.Title + " " + set.Map.Difficulty + " " + string.Join(" ", set.Map.Tracking?.Difficulties.Select(d => d.Name) ?? []))
                .Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        practiceHistoryHost.Add(flow($"{visible.Length} of {groups.Length} beatmaps · {visible.Sum(g => g.Count())} sets",12,AimModPalette.Muted));
        if (visible.Length == 0)
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
                "Open session", () => openCoachingMap(group.Key)));
        }
        if (coachingMapId is not null || coachingMapRun is not null) renderCoachingMap();
    }
}
