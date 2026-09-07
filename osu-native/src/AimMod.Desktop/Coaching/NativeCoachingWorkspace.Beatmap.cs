using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;

namespace AimMod.Desktop.Coaching;

public partial class NativeCoachingWorkspace
{
    private readonly FillFlowContainer<Drawable> mapDetailHost;
    private string? coachingMapId;
    private LocalReplay? coachingMapRun;
    private int coachingMapSection;
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
        mapDetailHost.Clear();
        mapDetailHost.Spacing = new(10);
        mapDetailHost.Padding = new MarginPadding { Right = 12, Bottom = 12 };
        if (coachingMapId is null && coachingMapRun is { } selected)
            coachingMapId = practiceSets.FirstOrDefault(s => s.Map.Tracking is { } t && PracticeProgressTracker.SameSource(selected,t) && PracticeProgressTracker.SamePlayer(selected,t.Player)) is { } matched
                ? CoachingMapKey(matched.Map) : null;
        var sets = practiceSets.Where(s => CoachingMapKey(s.Map) == coachingMapId).ToArray();
        var latest = sets.FirstOrDefault();
        var run = coachingMapRun ?? (latest?.Map.Tracking is { } tracking
            ? allReplays.FirstOrDefault(r => eligibleForCoaching(r) && PracticeProgressTracker.SameSource(r,tracking) && PracticeProgressTracker.SamePlayer(r,tracking.Player)) : null);
        var heading=new Container {RelativeSizeAxes=Axes.X,Height=66,Children=[
            new Box {RelativeSizeAxes=Axes.Y,Width=3,Colour=coachingAccent},
            new Container {RelativeSizeAxes=Axes.X,Width=.76f,Padding=new MarginPadding{Left=14},Children=[
                flow(latest?.Map.Title ?? run?.Title ?? "Beatmap coaching",23,AimModPalette.Text),
                flow(latest?.Map.Difficulty ?? run?.Difficulty ?? "Select a beatmap to begin",14,coachingAccent).With(d=>d.Y=32)
            ]},
            new CoachingButton("‹ My coaching",()=>showCoachingPage(3),compact:true){Anchor=Anchor.TopRight,Origin=Anchor.TopRight,Y=4}
        ]};
        mapDetailHost.Add(heading);
        var tabs = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(8) };
        foreach (var (name,index) in new[] { "Overview", "Sections", "Practice sets", "Original map" }.Select((name,index)=>(name,index)))
        {
            var button = new CoachingButton(name,()=> { coachingMapSection=index;renderCoachingMap(); }, compact: true);
            button.SetSelected(index == coachingMapSection); tabs.Add(button);
        }
        mapDetailHost.Add(tabs);
        if (practiceHistoryFailed) mapDetailHost.Add(flow("Results could not be refreshed. Use Refresh results in My coaching to retry.",16,AimModPalette.Danger));
        if (coachingMapSection is 0 or 1)
        {
            var attempts=sets.SelectMany(s=>s.Progress.Attempts).Where(a=>!a.Original).DistinctBy(a=>a.ScoreId).ToArray();
            int sections=sets.Sum(s=>s.Map.Tracking?.Difficulties.Count ?? 0);
            int practised=sets.Sum(s=>s.Map.Tracking?.Difficulties.Count(d=>s.Progress.Attempts.Any(a=>!a.Original && a.Difficulty==d.Name)) ?? 0);
            mapDetailHost.Add(metricStrip(("PRACTICE SETS",sets.Length.ToString()),("SECTIONS PRACTISED",$"{practised} / {sections}"),
                ("ATTEMPTS",attempts.Length.ToString()),("COMPLETED",$"{attempts.Count(a=>a.Passed)} / {attempts.Length}")));
            var actions=new FillFlowContainer<Drawable>{RelativeSizeAxes=Axes.X,AutoSizeAxes=Axes.Y,Direction=FillDirection.Full,Spacing=new(8)};
            if(latest is { Map.PayloadRemoved: false } && practiceWorkspace is not null)actions.Add(new CoachingButton("Continue practice",()=>practiceWorkspace.OpenSaved(latest.Map),true,true));
            if(run is not null)actions.Add(new CoachingButton(sets.Length==0?"Prepare practice set":"New practice set",()=>{
                coachingTargetScoreId=run.ScoreId;renderSession(workspace ?? buildWorkspace());showCoachingPage(0);
            },sets.Length==0,true));
            mapDetailHost.Add(actions);
            renderSectionTable(sets);
            if(coachingMapSection==0 && latest?.Map.Tracking is not null)
            {
                mapDetailHost.Add(flow("ORIGINAL MAP",12,AimModPalette.Muted));
                mapDetailHost.Add(new CoachingCard(flow(PracticeProgressTracker.DescribeTransfer(latest),14,AimModPalette.Text),12));
            }
        }
        else if (coachingMapSection == 2)
        {
            foreach(var set in sets)
            {
                var body=denseFlow();
                body.Add(flow($"Practice set · {set.Map.CreatedAt.ToLocalTime():dd MMM yyyy HH:mm}",16,AimModPalette.Text));
                body.Add(flow($"{set.Map.PlaybackRate*100:0}% speed · {set.Map.Tracking?.Difficulties.Count ?? 1} difficulties",16,AimModPalette.Muted));
                foreach(var difficulty in set.Map.Tracking?.Difficulties ?? [])body.Add(flow(difficulty.Name,16,AimModPalette.Text));
                if(set.Map.RetiredAt is not null)body.Add(flow(set.Map.PayloadRemoved ? "Archived · generated files cleaned up · history retained" : "Archived · history retained",12,AimModPalette.Muted));
                if(set.Map.Tracking is null)body.Add(flow("This older set has no linked practice history.",16,AimModPalette.Muted));
                if(practiceWorkspace is not null && !set.Map.PayloadRemoved)body.Add(new CoachingButton("Open practice set",()=>practiceWorkspace.OpenSaved(set.Map),true,true));
                mapDetailHost.Add(new CoachingCard(body,12));
            }
            if(sets.Length==0)mapDetailHost.Add(flow("No practice sets yet. Start from Overview.",18,AimModPalette.Muted));
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
                    body.Add(flow($"{attempt.PlayedAt.ToLocalTime():dd MMM HH:mm} · {attempt.Accuracy:P2} · {attempt.Misses} misses{(attempt.Passed?"":" · Unfinished")}",15,AimModPalette.Muted));
                mapDetailHost.Add(new CoachingCard(body,12));
            }
            if(!sets.Any(s=>s.Map.Tracking is not null))mapDetailHost.Add(flow("Create a practice set first to save a starting point for comparison.",18,AimModPalette.Muted));
        }
    }
}
