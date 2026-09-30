using AimMod.Desktop.LocalLibrary;
namespace AimMod.Desktop.Practice;
public sealed record PracticeDifficultyIdentity(string Name, PracticeDrillType Skill, string Sha256, string Md5, double SourceStartMs, double SourceEndMs, bool FromReplayEvidence,
    PracticeBreakdownVariant BreakdownVariant = PracticeBreakdownVariant.Original, string BreakdownGroupId = "", string RequiredMods = "");
public sealed record PracticeAttempt(Guid ScoreId, long OnlineScoreId, DateTimeOffset PlayedAt, double Accuracy, int Misses, bool Passed, string Setup, string Difficulty, bool Original,
    bool Assisted = false, PracticeBreakdownVariant BreakdownVariant = PracticeBreakdownVariant.Original, string BreakdownGroupId = "", double SourceStartMs = 0, double SourceEndMs = 0);
public sealed record PracticeTracking(string Player, int AccountId, int OnlineBeatmapId, string SourceHash, Guid SourceBeatmapId, string SourceMods, bool Stable, IReadOnlyList<PracticeAttempt> Baseline, IReadOnlyList<PracticeDifficultyIdentity> Difficulties);
public sealed record PracticeProgress(IReadOnlyList<PracticeAttempt> Attempts) { public static PracticeProgress Empty => new([]); }
public sealed record PracticeSetProgress(SavedPracticeMap Map, PracticeProgress Progress);
public static class PracticeProgressTracker
{
    public static bool Stable(LocalReplay r) => r.LegacyScore || r.Origin == LocalLibraryOrigin.Stable;
    public static bool SamePlayer(LocalReplay r, string player) => player.Length > 0 && (string.Equals(r.Player, player, StringComparison.OrdinalIgnoreCase) || (!r.IsLocallyStored && r.Player.Length == 0));
    public static bool SameSource(LocalReplay r, PracticeTracking t) => r.RulesetShortName == "osu" && Stable(r) == t.Stable && ScoreMods.Configuration(r) == t.SourceMods &&
        // A known differing file revision must not be merged just because its online map ID stayed the same.
        (t.SourceHash.Length > 0 && r.BeatmapHash.Length > 0
            ? string.Equals(r.BeatmapHash, t.SourceHash, StringComparison.OrdinalIgnoreCase)
            : (t.OnlineBeatmapId > 0 && r.OnlineBeatmapId == t.OnlineBeatmapId)
                || (r.IsLocallyStored && t.SourceBeatmapId != Guid.Empty && r.BeatmapId == t.SourceBeatmapId));
    private static bool validResult(LocalReplay r) => r.RulesetShortName == "osu" && double.IsFinite(r.Accuracy) && r.Accuracy is >= 0 and <= 1 && r.MissCount >= 0 && r.PlayedAt <= DateTimeOffset.UtcNow;
    private static bool valid(LocalReplay r) => ScoreMods.IsManualPlay(r) && validResult(r);
    public static PracticeAttempt Snapshot(LocalReplay r, string name, bool original) => new(r.ScoreId, r.OnlineScoreId, r.PlayedAt, r.Accuracy, r.MissCount, r.Passed,
        (Stable(r) ? "stable" : "lazer") + " / " + ScoreMods.Display(r) + " / " + ScoreMods.Configuration(r), name, original, !ScoreMods.IsManualPlay(r));
    public static IReadOnlyList<PracticeAttempt> Baseline(IEnumerable<LocalReplay> history, PracticeTracking t, DateTimeOffset before) => history.Where(r => valid(r) && r.Passed && SamePlayer(r,t.Player) && SameSource(r,t) && r.PlayedAt < before)
        .DistinctBy(r => r.OnlineScoreId > 0 ? "online:" + r.OnlineScoreId : "local:" + r.ScoreId).OrderByDescending(r => r.PlayedAt).Take(5).Select(r => Snapshot(r,"Original map",true)).ToArray();
    public static PracticeProgress Reconcile(SavedPracticeMap map, PracticeProgress previous, IEnumerable<LocalReplay> history, int accountId)
    {
        if (map.Tracking is not { } t || (t.AccountId > 0 && t.AccountId != accountId)) return previous;
        var attempts = previous.Attempts.DistinctBy(a => a.ScoreId).ToDictionary(a => a.ScoreId);
        foreach (var r in history.Where(r => validResult(r) && SamePlayer(r,t.Player) && r.PlayedAt >= map.CreatedAt))
        {
            var difficulty = t.Difficulties.FirstOrDefault(d => r.BeatmapHash.Length > 0 && (r.BeatmapHash.Equals(d.Sha256,StringComparison.OrdinalIgnoreCase) || r.BeatmapHash.Equals(d.Md5,StringComparison.OrdinalIgnoreCase)));
            if (difficulty is not null && permittedPractice(r, difficulty))
                attempts[r.ScoreId] = Snapshot(r,difficulty.Name,false) with
                {
                    BreakdownVariant = difficulty.BreakdownVariant, BreakdownGroupId = difficulty.BreakdownGroupId,
                    SourceStartMs = difficulty.SourceStartMs, SourceEndMs = difficulty.SourceEndMs
                };
            else if (valid(r) && SameSource(r,t)) attempts[r.ScoreId] = Snapshot(r,"Original map",true);
        }
        return new(attempts.Values.GroupBy(a => a.OnlineScoreId > 0 ? "online:" + a.OnlineScoreId : "local:" + a.ScoreId).Select(g => g.First()).OrderBy(a => a.PlayedAt).ToArray());
    }
    private static bool permittedPractice(LocalReplay r, PracticeDifficultyIdentity difficulty)
    {
        if (ScoreMods.IsManualPlay(r)) return difficulty.RequiredMods.Length == 0;
        if (difficulty.BreakdownVariant != PracticeBreakdownVariant.AimFocus || difficulty.RequiredMods != "RX") return false;
        var mods = ScoreMods.Acronyms(r);
        return mods.Any(m => m is "RX" or "RELAX") && !mods.Any(m => m is "AT" or "AUTOPLAY" or "CN" or "CINEMA" or "AP" or "AUTOPILOT");
    }
    public static double Median(IEnumerable<double> values) { var v = values.Order().ToArray(); return v.Length == 0 ? 0 : (v[(v.Length-1)/2]+v[v.Length/2])/2; }
    public static string DescribePractice(IEnumerable<PracticeAttempt> attempts)
    {
        var all = attempts.OrderBy(a => a.PlayedAt).ToArray();
        if (all.Select(a => (a.Setup, a.Difficulty, a.Assisted, a.BreakdownGroupId, a.SourceStartMs, a.SourceEndMs)).Distinct().Count() > 1)
            return $"{all.Length} recorded attempts across different practice conditions. Select one difficulty and setup to compare results.";
        var done = all.Where(a => a.Passed).ToArray();
        if (all.Length == 0) return "Not practised yet";
        string text = $"{all.Length} recorded attempts / {done.Length} completed" + (all[0].Assisted ? " / assisted aim practice" : "");
        if (done.Length == 0) return text + ". Finish a play to compare accuracy.";
        text += $" / latest {done[^1].Accuracy:P2}";
        if (done.Length < 6) return text + ". Complete six plays with this setup to compare your first and recent results.";
        var first = done.Take(3).ToArray(); var recent = done.TakeLast(3).ToArray();
        double spread(PracticeAttempt[] scores) { double mean = scores.Average(a => a.Accuracy); return Math.Sqrt(scores.Average(a => Math.Pow(a.Accuracy-mean,2)))*100; }
        return text + $". Median accuracy {Median(first.Select(a=>a.Accuracy)):P2} to {Median(recent.Select(a=>a.Accuracy)):P2}; misses {Median(first.Select(a=>(double)a.Misses)):0.#} to {Median(recent.Select(a=>(double)a.Misses)):0.#}. Accuracy spread {spread(first):0.00} to {spread(recent):0.00} points (lower is steadier).";
    }
    public static string DescribeTransfer(PracticeSetProgress set)
    {
        // Assisted aim work still starts the practice period; its score is never part of the original-map comparison.
        var practice = set.Progress.Attempts.Where(a=>!a.Original).ToArray();
        if (practice.Length == 0)
        {
            int count=(set.Map.Tracking?.Baseline.Count ?? 0)+set.Progress.Attempts.Count(a=>a.Original && a.Passed);
            return count < 3 ? $"Complete {3-count} more original-map plays to set your baseline, then practise the new difficulties." : "Play a practice difficulty first, then return to the original map.";
        }
        var after = set.Progress.Attempts.Where(a=>a.Original && a.PlayedAt > practice.Min(p=>p.PlayedAt)).OrderBy(a=>a.PlayedAt).ToArray();
        var done = after.Where(a=>a.Passed).TakeLast(5).ToArray(); var baseline = (set.Map.Tracking?.Baseline ?? []).Concat(set.Progress.Attempts.Where(a=>a.Original && a.Passed && a.PlayedAt < practice.Min(p=>p.PlayedAt)))
            .DistinctBy(a=>a.ScoreId).OrderBy(a=>a.PlayedAt).TakeLast(5).ToArray();
        if (baseline.Length < 3) return $"{after.Length} original-map attempts after practice; {done.Length} completed. Only {baseline.Length} baseline plays were saved, so a reliable before-and-after comparison is not ready.";
        if (done.Length < 3) return $"{after.Length} original-map attempts after practice; {done.Length} completed. Compare after three completed retests and three baseline plays with the original setup.";
        double ba=Median(baseline.Select(a=>a.Accuracy)), aa=Median(done.Select(a=>a.Accuracy)), bm=Median(baseline.Select(a=>(double)a.Misses)), am=Median(done.Select(a=>(double)a.Misses));
        string change = aa > ba && am <= bm ? "Higher accuracy on the original map" : am < bm && aa >= ba ? "Fewer misses on the original map" : "No clear improvement on the original map yet";
        return $"{change}. Median accuracy {ba:P2} to {aa:P2}; misses {bm:0.#} to {am:0.#}. {baseline.Length} baseline plays / {done.Length} recent completed retests / {after.Count(a=>!a.Passed)} unfinished retests.";
    }
}
