using System.Text.Json;
using AimMod.Desktop.LocalLibrary;

namespace AimMod.Desktop.Practice;

public sealed record AutomaticPracticeSettings(bool Enabled = false, bool Cleanup = true, int MaximumActiveMaps = 5, int InactiveDays = 30, int RetentionDays = 7);

public sealed class AutomaticPracticeStore(string root)
{
    private string PathName => Path.Combine(root,"automatic-practice.json");
    public AutomaticPracticeSettings Load()
    {
        try { return File.Exists(PathName) ? JsonSerializer.Deserialize<AutomaticPracticeSettings>(File.ReadAllText(PathName)) ?? new() : new(); }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(AutomaticPracticeSettings settings)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(PathName+".tmp",JsonSerializer.Serialize(settings)); File.Move(PathName+".tmp",PathName,true);
    }
}

public static class AutomaticPracticePolicy
{
    public static bool IsMastered(SavedPracticeMap set, IEnumerable<LocalReplay> history)
    {
        if(set.Tracking is not { } tracking)return false;
        var recent=history.Where(r=>PracticeProgressTracker.SamePlayer(r,tracking.Player) && PracticeProgressTracker.SameSource(r,tracking)
            && r.PlayedAt>set.CreatedAt && ScoreMods.IsManualPlay(r)).OrderByDescending(r=>r.PlayedAt).Take(3).ToArray();
        return recent.Length==3 && recent.All(r=>r.Passed && r.Accuracy>=.98 && r.Accuracy<=1 && r.MissCount==0);
    }
    public static bool NeedsRevision(SavedPracticeMap set, LocalReplay latest, IEnumerable<LocalReplay> history, DateTimeOffset now)
    {
        if(!set.Automatic || set.Favourite || set.RetiredAt is not null || set.Tracking is not { } tracking
            || latest.ScoreId==set.RevisionScoreId || now-set.CreatedAt<TimeSpan.FromHours(6))return false;
        return history.Count(r=>r.PlayedAt>set.CreatedAt && r.Passed && ScoreMods.IsManualPlay(r)
            && PracticeProgressTracker.SamePlayer(r,tracking.Player) && PracticeProgressTracker.SameSource(r,tracking))>=3;
    }
    public static DateTimeOffset LastActivity(SavedPracticeMap set, PracticeProgress progress, IEnumerable<LocalReplay> history)
    {
        var original=set.Tracking is { } t ? history.Where(r=>PracticeProgressTracker.SamePlayer(r,t.Player) && PracticeProgressTracker.SameSource(r,t)).Select(r=>r.PlayedAt) : [];
        return original.Concat(progress.Attempts.Select(a=>a.PlayedAt)).Append(set.CreatedAt).Max();
    }
}
