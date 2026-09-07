using System.Text.Json;
using AimMod.Desktop.LocalLibrary;
namespace AimMod.Desktop.Coaching;

public sealed record CoachingTrainingPlan(
    string Focus, string Why, string Cue, Guid TargetScoreId, string TargetTitle, string Difficulty,
    string Setup, string ModLabel, string Player, int BaselineCount, double BaselineAccuracy,
    int BaselineMisses, double TargetAccuracy, int TargetMisses, DateTimeOffset? StartedAt = null);
public sealed record CoachingTrainingReview(int Attempts, int SuccessfulAttempts, bool Complete, string Message);

public static class CoachingTrainingPlanner {
    public static CoachingTrainingPlan? Build(NativeCoachingWorkspaceModel model) {
        var candidates = model.History.Where(r => r.Passed && ScoreMods.IsManualPlay(r) && r.Accuracy is >= .7 and <= 1
            && double.IsFinite(r.Accuracy) && r.MissCount >= 0 && r.PlayedAt <= DateTimeOffset.UtcNow)
            .GroupBy(r => (r.Player, Setup:ScoreMods.SetupKey(r)))
            .Select(g => g.OrderByDescending(r=>r.PlayedAt).Take(5).ToArray()).ToArray();
        var group = candidates.OrderByDescending(g=>g.Length>=3).ThenByDescending(g=>g[0].PlayedAt).FirstOrDefault();
        if (group is null) return null;
        var target=group[0];
        double accuracy=median(group.Select(r=>r.Accuracy));
        int misses=(int)Math.Round(median(group.Select(r=>(double)r.MissCount)));
        var skill=model.GlobalProfile.MeasuredSkillAreas.Where(s=>s.Confidence>=CoachingConfidence.Medium && s.RunCount>=3 && s.MapCount>=2)
            .OrderByDescending(s=>s.ShareOfClassifiedMisses).FirstOrDefault();
        string focus="Make your result repeatable";
        string why=$"Your baseline from {group.Length} matching {(group.Length == 1 ? "play" : "plays")}: {accuracy:P2} accuracy and {misses} {(misses == 1 ? "miss" : "misses")}.";
        string cue="Keep the same mods and rate. Prioritise a complete play over an early restart; use the retry to change one thing.";
        if (skill is not null) {
            (focus,cue)=skill.Area switch {
                CoachingSkillArea.AimControl => ("Control the transition between notes", "Look ahead to the next note, move in one deliberate motion, then tap. Repeat the difficult transition at a comfortable rate before returning to your normal setup."),
                CoachingSkillArea.AimPrecision => ("Land securely before you tap", "Aim into the centre of each circle. Use a slower practice rate to find a clean landing; return to your normal rate for the final two plays."),
                CoachingSkillArea.TapTiming => ("Keep your tapping rhythm steady", "Listen for the subdivision through the difficult pattern. Keep the tapping motion small and even; practise slowly until the rhythm stays stable."),
                _ => ("Bring aim and tapping together", "On the difficult transition, arrive before tapping. Practise the motion slowly, then increase the rate only after two clean repetitions.")
            };
            why += $" {skill.Label} appeared in {skill.EvidenceCount} missed-note observations across {skill.RunCount} plays and {skill.MapCount} maps. Test this cue on today's map.";
        }
        if (group.Length<3) { focus="Set a useful baseline"; cue="Play this map three times with the same mods and rate. Finish each attempt. We will use these results to set a realistic next target."; }
        return new(focus,why,cue,target.ScoreId,target.Title,target.Difficulty,ScoreMods.SetupKey(target),ScoreMods.Display(target),target.Player,
            group.Length,accuracy,misses, Math.Min(1,accuracy+(misses==0?.002:0)),Math.Max(0,misses-1));
    }
    public static CoachingTrainingReview Review(CoachingTrainingPlan plan,IEnumerable<LocalReplay> history) {
        if (plan.StartedAt is null) return new(0,0,false,"Start the session to track your next plays.");
        var attempts=history.Where(r=>r.PlayedAt>plan.StartedAt && r.Player==plan.Player && ScoreMods.SetupKey(r)==plan.Setup && ScoreMods.IsManualPlay(r))
            .DistinctBy(r=>r.ScoreId).OrderBy(r=>r.PlayedAt).ToArray();
        int successes=attempts.Count(r=>r.Passed && r.Accuracy>=plan.TargetAccuracy && r.MissCount<=plan.TargetMisses);
        if(plan.BaselineCount<3) {
            int completed=attempts.Count(r=>r.Passed);
            return new(attempts.Length,completed,completed>=3,completed>=3 ? "Baseline complete. Build your next session from these three plays." : $"{completed}/3 completed baseline plays. Keep the same setup and finish each attempt.");
        }
        if(successes>=2) return new(attempts.Length,successes,true,"Target repeated twice. Move on to another map next session and check whether the improvement carries over.");
        if(attempts.Length>=4 && successes==0) return new(attempts.Length,0,false,"Four attempts without reaching the target. Stop retrying for now. Practise the troublesome section at a slower rate, or finish with an easier map; return to this target next session.");
        return new(attempts.Length,successes,false,$"{successes}/2 target plays. Aim for at least {plan.TargetAccuracy:P2} with at most {plan.TargetMisses} {(plan.TargetMisses == 1 ? "miss" : "misses")}, on the original setup.");
    }
    private static double median(IEnumerable<double> values) { var v=values.Order().ToArray();return (v[(v.Length-1)/2]+v[v.Length/2])/2; }
}

public sealed class CoachingTrainingStore(string path) {
    public CoachingTrainingPlan? Load() {
        try {
            if(!File.Exists(path) || new FileInfo(path).Length>32768)return null;
            var plan=JsonSerializer.Deserialize<CoachingTrainingPlan>(File.ReadAllText(path));
            return plan is { StartedAt:not null, BaselineCount: > 0, TargetAccuracy: >= 0 and <= 1, TargetMisses: >= 0 } ? plan : null;
        } catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public void Save(CoachingTrainingPlan? plan) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp=path+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(plan));
        File.Move(temp,path,true);
    }
}
