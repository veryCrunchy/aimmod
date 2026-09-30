using AimMod.Desktop.Practice;
using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;

namespace AimMod.Desktop.Coaching;

public partial class NativeCoachingWorkspace
{
    private static FillFlowContainer<Drawable> denseFlow() => new() {
        RelativeSizeAxes=Axes.X, AutoSizeAxes=Axes.Y, Direction=FillDirection.Vertical, Spacing=new(6)
    };

    private static Drawable metricStrip(params (string Label,string Value)[] metrics)
    {
        var grid=new GridContainer {RelativeSizeAxes=Axes.X,Height=70,
            ColumnDimensions=metrics.Select(_=>new Dimension(GridSizeMode.Relative,1f/metrics.Length)).ToArray()};
        grid.Content=new[] { metrics.Select(metric=> (Drawable)new Container {
            RelativeSizeAxes=Axes.Both,Padding=new MarginPadding{Right=8},Child=new CoachingCard(new Container {
                RelativeSizeAxes=Axes.X,Height=43,Children=[
                    label(metric.Label,11,AimModPalette.Muted,"SemiBold"),
                    label(metric.Value,24,AimModPalette.Text,"Bold").With(d=>d.Y=17)
                ]},10)
        }).ToArray() };
        return grid;
    }

    private static Drawable tableRow(Drawable[] cells, bool heading=false)
    {
        float[] widths=[.24f,.10f,.10f,.11f,.10f,.11f,.10f,.14f];
        var grid=new GridContainer {RelativeSizeAxes=Axes.X,Height=heading?26:54,
            ColumnDimensions=widths.Select(w=>new Dimension(GridSizeMode.Relative,w)).ToArray()};
        grid.Content=new[] {cells.Select(cell=>(Drawable)new Container {RelativeSizeAxes=Axes.Both,
            Padding=new MarginPadding{Right=7,Top=heading?5:9},Child=cell}).ToArray()};
        return new Container {RelativeSizeAxes=Axes.X,Height=heading?26:54,Children=[
            new Box{RelativeSizeAxes=Axes.Both,Colour=heading?AimModPalette.Canvas:AimModPalette.Panel},
            new Container{RelativeSizeAxes=Axes.Both,Padding=new MarginPadding{Horizontal=10},Child=grid}
        ]};
    }

    private static Drawable trendBars(PracticeAttempt[] attempts)
    {
        var completed=attempts.Where(a=>a.Passed).OrderBy(a=>a.PlayedAt).TakeLast(10).ToArray();
        var chart=new Container{RelativeSizeAxes=Axes.X,Height=32};
        if(completed.Length==0){chart.Add(label("—",14,AimModPalette.Muted));return chart;}
        for(int i=0;i<completed.Length;i++)
            chart.Add(new Box {RelativeSizeAxes=Axes.Both,RelativePositionAxes=Axes.X,
                X=(float)i/10,Width=.075f,Height=(float)Math.Clamp(completed[i].Accuracy,0,1),
                Anchor=Anchor.BottomLeft,Origin=Anchor.BottomLeft,Colour=coachingAccent});
        return chart;
    }

    private void renderSectionTable(PracticeSetProgress[] sets)
    {
        mapDetailHost.Add(flow("SECTION PROGRESS · completed plays · Δ first 3 vs latest 3 · trend 0–100%",12,AimModPalette.Muted));
        mapDetailHost.Add(tableRow(new[]{"SECTION / SETUP","ATTEMPTS","LATEST ACC","MISSES","Δ ACC","SPREAD¹","TREND","DETAILS"}
            .Select(t=>(Drawable)label(t,10,AimModPalette.Muted,"SemiBold")).ToArray(),true));
        int rows=0;
        foreach(var set in sets)
            foreach(var difficulty in set.Map.Tracking?.Difficulties ?? [])
            {
                var attempts=set.Progress.Attempts.Where(a=>!a.Original && a.Difficulty==difficulty.Name).ToArray();
                var groups=attempts.GroupBy(a=>a.Setup).Select(g=>(Setup:g.Key,Attempts:g.ToArray())).ToArray();
                if(groups.Length==0)groups=[("Not practised",Array.Empty<PracticeAttempt>())];
                foreach(var group in groups)
                {
                    rows++;
                    var completed=group.Attempts.Where(a=>a.Passed).OrderBy(a=>a.PlayedAt).ToArray();
                    var recent=completed.TakeLast(3).ToArray();
                    var latest=completed.LastOrDefault();
                    string key=set.Map.Id+":"+difficulty.Name+":"+group.Setup;
                    bool expanded=expandedCoachingSections.Contains(key);
                    var title=denseFlow(); title.Spacing=new(3);
                    title.Add(flow($"{difficulty.Skill} · {TimeSpan.FromMilliseconds(difficulty.SourceStartMs):m\\:ss}–{TimeSpan.FromMilliseconds(difficulty.SourceEndMs):m\\:ss}",13,AimModPalette.Text));
                    title.Add(flow($"{set.Map.PlaybackRate*100:0}% · {string.Join(" / ",group.Setup.Split(" / ").Take(2))}",11,AimModPalette.Muted));
                    double delta=completed.Length<6?0:(PracticeProgressTracker.Median(recent.Select(a=>a.Accuracy))-PracticeProgressTracker.Median(completed.Take(3).Select(a=>a.Accuracy)))*100;
                    double mean=recent.Length==0?0:recent.Average(a=>a.Accuracy);
                    double spread=recent.Length<3?0:Math.Sqrt(recent.Average(a=>Math.Pow(a.Accuracy-mean,2)))*100;
                    mapDetailHost.Add(tableRow([
                        title,label(group.Attempts.Length.ToString(),16,AimModPalette.Text),
                        label(latest is null?"—":$"{latest.Accuracy:P2}",15,coachingAccent),
                        label(latest?.Misses.ToString()??"—",15,AimModPalette.Text),
                        label(completed.Length<6?"—":$"{delta:+0.00;-0.00;0.00}pt",13,delta<0?AimModPalette.Danger:coachingAccent),
                        label(recent.Length<3?"—":$"{spread:0.00}pt",13,AimModPalette.Text),trendBars(group.Attempts),
                        new CoachingButton(expanded?"Hide":"View",()=>{if(!expandedCoachingSections.Add(key))expandedCoachingSections.Remove(key);renderCoachingMap();},compact:true)
                    ]));
                    if(expanded)
                    {
                        var detail=denseFlow();
                        detail.Add(flow($"{difficulty.Name} · Set {set.Map.CreatedAt.ToLocalTime():dd MMM HH:mm}",13,coachingAccent));
                        detail.Add(flow(PracticeProgressTracker.DescribePractice(group.Attempts),13,AimModPalette.Muted));
                        foreach(var attempt in group.Attempts.OrderByDescending(a=>a.PlayedAt).Take(8))
                            detail.Add(flow($"{attempt.PlayedAt.ToLocalTime():dd MMM HH:mm}    {attempt.Accuracy:P2}    {attempt.Misses} misses    {(attempt.Passed?"Completed":"Unfinished")}",12,AimModPalette.Text));
                        if(practiceWorkspace is not null && !set.Map.PayloadRemoved)detail.Add(new CoachingButton("Open practice set",()=>practiceWorkspace.OpenSaved(set.Map),compact:true));
                        mapDetailHost.Add(new CoachingCard(detail,12));
                    }
                }
            }
        if(rows==0)mapDetailHost.Add(flow("No section results yet. Create a practice set to begin.",14,AimModPalette.Muted));
        mapDetailHost.Add(flow("¹ Accuracy spread across the latest 3 completed plays; lower is steadier. Δ needs 6 completed plays. Values are percentage points.",11,AimModPalette.Muted));
    }
}
