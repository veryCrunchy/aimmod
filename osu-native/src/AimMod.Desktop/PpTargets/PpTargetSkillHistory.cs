using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.ScoreHistory;

namespace AimMod.Desktop.PpTargets;

internal static class PpTargetSkillHistory
{
    internal static IReadOnlyList<ScoreHistoryEntry> PassHistory(IReadOnlyList<LocalReplay> local,
        IReadOnlyList<ScoreHistoryEntry> online, IReadOnlyList<LocalBeatmapSet> sets)
    {
        var maps = sets.SelectMany(s => s.Difficulties).GroupBy(d => d.BeatmapId).ToDictionary(g => g.Key, g => g.First());
        return ScoreHistoryMerger.Merge(local, online).Select(score =>
        {
            if (score.LocalBeatmapId is not { } id || !maps.TryGetValue(id, out var map)) return score;
            // LocalReplay has no pass/completion flag. Only retain explicit outcomes supplied by history.
            return score with { OnlineBeatmapId = score.OnlineBeatmapId > 0 ? score.OnlineBeatmapId : map.OnlineId,
                Bpm = score.Bpm is > 0 ? score.Bpm : map.Bpm,
                LengthSeconds = score.LengthSeconds is > 0 ? score.LengthSeconds : (int)(map.LengthMilliseconds / 1000) };
        }).ToArray();
    }

    internal static IReadOnlyList<LocalReplay> Merge(IReadOnlyList<LocalReplay> local,
        IReadOnlyList<ScoreHistoryEntry> online, IReadOnlyList<LocalBeatmapSet> sets)
    {
        var onlineByScore = online.Where(s => s.OnlineScoreId > 0).GroupBy(s => s.OnlineScoreId)
            .ToDictionary(g => g.Key, g => g.First());
        var maps = sets.SelectMany(s => s.Difficulties.Select(d => (Set: s, Map: d)))
            .Where(item => item.Map.OnlineId > 0).GroupBy(item => item.Map.OnlineId)
            .ToDictionary(g => g.Key, g => g.First());
        return ScoreHistoryMerger.MergeAsLocalReplays(local, online).Select(run =>
        {
            if (run.IsLocallyStored || !onlineByScore.TryGetValue(run.OnlineScoreId, out var score)
                || !maps.TryGetValue(score.OnlineBeatmapId, out var map)) return run;
            // An online ID links the difficulty, but cannot establish the played revision's checksum.
            return run with { BeatmapId = map.Map.BeatmapId, SetId = map.Set.SetId };
        }).ToArray();
    }
}
