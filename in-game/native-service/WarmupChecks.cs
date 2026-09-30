namespace AimMod.InGame;
static class WarmupChecks
{
 public static void Run(){Run Sample(int i,double score,string scenario="Synthetic")=>new("w"+i,scenario,score,80,60,0,0,new DateTimeOffset(2026,1,1,12,0,0,TimeSpan.Zero).AddMinutes(i).ToString("O"),null,null,null,null,false);
 var runs=new[]{Sample(0,60),Sample(1,70),Sample(2,100),Sample(3,100),Sample(4,100),Sample(5,100)};
 var ids=WarmupAnalysis.Classify(runs);if(!ids.SetEquals(new[]{"w0","w1"}))throw new Exception("Early recovery not classified");
 if(WarmupAnalysis.Classify(runs.Take(5)).Count!=0)throw new Exception("Insufficient warmup sample accepted");
 if(WarmupAnalysis.Classify(Enumerable.Range(0,6).Select(i=>Sample(i,100))).Count!=0)throw new Exception("Stable early runs misclassified");
 if(WarmupAnalysis.Classify(runs.Select((r,i)=>r with{Scenario=i<2?"Other":"Synthetic"})).Count!=0)throw new Exception("Cross scenario scores compared");
 var report=StatsAnalysis.Build(runs,"Synthetic",new DateTimeOffset(2026,1,2,0,0,0,TimeSpan.Zero),TimeZoneInfo.Utc);var all=report.Periods.Single(p=>p.Key=="all");if(all.Warmup?.Runs!=2||all.Settled?.Runs!=4||all.Selected.Runs!=6)throw new Exception("Filtered statistics incomplete");
 Console.WriteLine("5 warmup checks passed.");}
}
