namespace AimMod.InGame;
// Scenario-relative early recovery classification from the desktop statistics.
static class WarmupAnalysis
{
 static double Median(double[] v){Array.Sort(v);return v.Length%2==1?v[v.Length/2]:(v[v.Length/2-1]+v[v.Length/2])/2;}
 public static HashSet<string> Classify(IEnumerable<Run> source){var result=new HashSet<string>(StringComparer.Ordinal);
 foreach(var group in source.Where(r=>double.IsFinite(r.Score)&&r.Duration>0&&HubHistory.Date(r.Timestamp)!=DateTimeOffset.MinValue).GroupBy(r=>r.Scenario,StringComparer.OrdinalIgnoreCase)){
 var runs=group.OrderBy(r=>HubHistory.Date(r.Timestamp)).ToArray();if(runs.Length<6)continue;
 var median=Median(runs.Select(r=>r.Score).ToArray());var mad=Median(runs.Select(r=>Math.Abs(r.Score-median)).ToArray());var scale=Math.Max(Math.Max(mad*1.4826,median*.06),1);
 for(int start=0;start<runs.Length;){int end=start+1;while(end<runs.Length&&HubHistory.Date(runs[end].Timestamp)-HubHistory.Date(runs[end-1].Timestamp)<=TimeSpan.FromHours(6))end++;
 if(end-start>=3){var z=runs[start..end].Select(r=>(r.Score-median)/scale).ToArray();var recovered=Array.FindIndex(z,n=>n>=-.15);var peak=z.Max();if(recovered>0&&peak>=0)for(int i=0;i<recovered;i++)if(z[i]<=-.25&&z[recovered]-z[i]>=.35&&peak-z[i]>=.5)result.Add(runs[start+i].Id);}
 start=end;}
 }return result;}
}
