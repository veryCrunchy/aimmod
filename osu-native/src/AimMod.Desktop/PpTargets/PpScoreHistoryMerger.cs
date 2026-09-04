using System.Security.Cryptography;
using System.Text;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.PpTargets;

public static class PpScoreHistoryMerger
{
    public static IReadOnlyList<LocalReplay> Merge(
        IReadOnlyList<LocalReplay> localRuns,
        IReadOnlyList<OsuBestScore> onlineScores,
        IReadOnlyList<LocalBeatmapSet> localSets)
    {
        ArgumentNullException.ThrowIfNull(localRuns);
        ArgumentNullException.ThrowIfNull(onlineScores);
        ArgumentNullException.ThrowIfNull(localSets);

        var merged = localRuns.GroupBy(run => run.ScoreId).Select(group => group.First()).ToList();
        Dictionary<long, int> localByOnlineId = merged.Select((run, index) => (run, index))
            .Where(item => item.run.OnlineScoreId > 0)
            .GroupBy(item => item.run.OnlineScoreId)
            .ToDictionary(group => group.Key, group => group.First().index);
        Dictionary<int, LocalBeatmapSet> setsByOnlineId = localSets.Where(set => set.OnlineId > 0)
            .GroupBy(set => set.OnlineId)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (OsuBestScore score in onlineScores.Where(validOnlineScore).DistinctBy(score => score.ScoreId))
        {
            if (localByOnlineId.TryGetValue(score.ScoreId, out int index))
            {
                LocalReplay local = merged[index];
                merged[index] = local with
                {
                    PerformancePoints = score.PerformancePoints is { } pp && double.IsFinite(pp) && pp >= 0
                        ? pp
                        : local.PerformancePoints,
                    OnlineScoreId = score.ScoreId,
                };
                continue;
            }

            LocalBeatmapSet? localSet = setsByOnlineId.GetValueOrDefault(score.BeatmapSet.BeatmapSetId);
            LocalBeatmapDifficulty? localDifficulty = localSet?.Difficulties.FirstOrDefault(difficulty => difficulty.OnlineId == score.Beatmap.BeatmapId);
            Guid setId = localSet?.SetId ?? stableGuid($"osu-set:{score.BeatmapSet.BeatmapSetId}");
            Guid beatmapId = localDifficulty?.BeatmapId ?? stableGuid($"osu-beatmap:{score.Beatmap.BeatmapId}");
            var statistics = new PpScoreStatistics(
                score.Statistics.Greats,
                score.Statistics.Oks,
                score.Statistics.Mehs,
                score.Statistics.Misses,
                0,
                0);
            merged.Add(new LocalReplay(
                stableGuid($"osu-score:{score.ScoreId}"),
                setId,
                beatmapId,
                score.BeatmapSet.Title,
                score.BeatmapSet.Artist,
                score.Beatmap.DifficultyName,
                "osu",
                score.Username,
                score.EndedAt ?? score.CreatedAt ?? DateTimeOffset.UnixEpoch,
                score.Beatmap.StarRating,
                score.Accuracy,
                score.TotalScore,
                score.MaximumCombo,
                score.Statistics.Misses,
                score.PerformancePoints,
                score.Mods,
                false,
                score.Beatmap.Checksum ?? string.Empty,
                string.Empty,
                statistics,
                score.ModsJson,
                score.ScoreId,
                IsLocallyStored: false));
        }

        return merged.OrderByDescending(run => run.PlayedAt).ThenBy(run => run.ScoreId).ToArray();
    }

    private static bool validOnlineScore(OsuBestScore score) => score is not null
        && score.ScoreId > 0
        && score.Beatmap.BeatmapId > 0
        && score.BeatmapSet.BeatmapSetId > 0
        && double.IsFinite(score.Accuracy) && score.Accuracy is >= 0 and <= 1
        && (score.PerformancePoints is null || double.IsFinite(score.PerformancePoints.Value) && score.PerformancePoints.Value >= 0);

    private static Guid stableGuid(string value)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return new Guid(hash[..16]);
    }
}
