using AimMod.InGame.Analysis;
using System.Globalization;
using System.Text;

namespace AimMod.InGame;

sealed record FlickPath(double Time, double End, double Efficiency, double[][] Recorded, double[][] Ideal);
static class PracticeReplay
{
    public static FlickPath[] Build(NativeReplay replay)
    {
        var input = ReplayAnalysis.Build(replay);
        var path = ViewPath.Resample(input.View, input.Duration);
        return FlickAnalysis.Analyze(input, path).Flicks.Take(2000).Select(f => {
            int first = path.IndexAt(f.OnsetT), last = path.IndexAt(f.ClickT);
            TargetSample goal = default;
            for (int i = last; i >= first; i--) {
                goal = input.Targets[i].FirstOrDefault(t => t.Id == f.Id);
                if (goal.Id != 0) break;
            }
            var dx = goal.Id == 0 ? path.Yaw[last] - path.Yaw[first] : Angles.Wrap(goal.Yaw - path.Yaw[first]);
            var dy = goal.Id == 0 ? path.Pitch[last] - path.Pitch[first] : goal.Pitch - path.Pitch[first];
            var recorded = Enumerable.Range(0, Math.Min(65, last-first+1)).Select(k => {
                int i = first + (int)Math.Round(k * (last-first) / (double)Math.Max(1, Math.Min(64,last-first)));
                return new[] { path.Yaw[i]-path.Yaw[first], path.Pitch[i]-path.Pitch[first] };
            }).ToArray();
            var ideal = Enumerable.Range(0, 33).Select(k => { var u = MinimumJerk.Position(k/32.0); return new[] {dx*u,dy*u}; }).ToArray();
            return new FlickPath(f.OnsetT, f.ClickT, double.IsFinite(f.Efficiency) ? f.Efficiency : 0, recorded, ideal);
        }).ToArray();
    }
    public static string Encode(string id, double duration, FlickPath[] paths)
    {
        static string N(double n) => n.ToString("R", CultureInfo.InvariantCulture);
        var text = new StringBuilder("AIMMOD_FLICKS_1\t" + id + "\t" + N(duration) + "\n");
        foreach (var f in paths) {
            text.Append(N(f.Time)).Append('\t').Append(N(f.End)).Append('\t').Append(N(f.Efficiency));
            foreach (var points in new[] { f.Recorded, f.Ideal }) {
                text.Append('\t').Append(string.Join(';', points.Select(p => N(p[0])+","+N(p[1]))));
            }
            text.Append('\n');
        }
        return text.ToString();
    }
}
