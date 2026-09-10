using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.ScoreHistory;
using AimMod.Osu.Runtime;

namespace AimMod.Desktop.Creator;

public static class FootageScoreLookup
{
    public static LocalReplay FromHistory(ScoreHistoryEntry score, string player)
    {
        static Guid id(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
        return new(id("score:" + score.OnlineScoreId), id("set:" + score.OnlineBeatmapSetId), id("map:" + score.OnlineBeatmapId),
            score.Title, score.Artist, score.Difficulty, "osu", player, score.PlayedAt, score.StarRating,
            score.Accuracy, score.TotalScore, score.MaximumCombo, score.MissCount, score.PerformancePoints,
            score.Mods, false, ModsJson: score.ModsJson, OnlineScoreId: score.OnlineScoreId, IsLocallyStored: false,
            Origin: LocalLibraryOrigin.Online, OnlineBeatmapId: score.OnlineBeatmapId, Passed: score.Passed ?? true,
            // The Hub public feed always supplies the modern score ID, including stable plays.
            LegacyScore: score.Provenance == ScoreHistoryProvenance.OnlinePublic ? false : score.LegacyScore);
    }

    public static LocalReplay Parse(JsonElement root, OsuScoreAddress address)
    {
        string str(JsonElement e, string key) => e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        long number(JsonElement e, string key) => e.TryGetProperty(key, out var p) && p.TryGetInt64(out long n) ? n : 0;
        double real(JsonElement e, string key) => e.TryGetProperty(key, out var p) && p.TryGetDouble(out double n) ? n : 0;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("beatmap", out var map)
            || map.ValueKind != JsonValueKind.Object || !root.TryGetProperty("beatmapset", out var set)
            || set.ValueKind != JsonValueKind.Object || !root.TryGetProperty("user", out var user)
            || user.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("osu! returned incomplete score details.");
        long scoreId = number(root, "id");
        bool matchingId = scoreId == address.Id || address.LegacyRuleset is not null && number(root, "legacy_score_id") == address.Id;
        if (!matchingId || number(map, "id") <= 0 || string.IsNullOrWhiteSpace(str(set, "title"))
            || string.IsNullOrWhiteSpace(str(map, "version")) || string.IsNullOrWhiteSpace(str(user, "username")))
            throw new InvalidDataException("osu! returned incomplete score details.");
        DateTimeOffset played;
        if (!(root.TryGetProperty("ended_at", out var ended) && ended.ValueKind == JsonValueKind.String && ended.TryGetDateTimeOffset(out played))
            && !(root.TryGetProperty("created_at", out var created) && created.ValueKind == JsonValueKind.String && created.TryGetDateTimeOffset(out played)))
            throw new InvalidDataException("This score has no timestamp to match against footage.");
        string mode = str(root, "mode");
        if (mode.Length == 0) mode = number(root, "ruleset_id") switch { 1 => "taiko", 2 => "fruits", 3 => "mania", _ => "osu" };
        if (mode == "fruits") mode = "catch";
        string[] mods = root.TryGetProperty("mods", out var m) && m.ValueKind == JsonValueKind.Array
            ? m.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : str(v, "acronym")).ToArray() : [];
        int misses = 0;
        if (root.TryGetProperty("statistics", out var stats) && stats.ValueKind == JsonValueKind.Object)
            misses = checked((int)(stats.TryGetProperty("miss", out _) ? number(stats, "miss") : number(stats, "count_miss")));
        double accuracy = real(root, "accuracy");
        if (!double.IsFinite(accuracy) || accuracy is < 0 or > 1) throw new InvalidDataException("This score has invalid accuracy.");
        Guid guid = new(SHA256.HashData(Encoding.UTF8.GetBytes(address.ApiPath)).AsSpan(0, 16));
        return new(guid, Guid.Empty, Guid.Empty, str(set, "title"), str(set, "artist"), str(map, "version"), mode,
            str(user, "username"), played, real(map, "difficulty_rating"), accuracy,
            root.TryGetProperty("total_score", out _) ? number(root, "total_score") : number(root, "score"),
            checked((int)number(root, "max_combo")), misses, root.TryGetProperty("pp", out var pp) && pp.ValueKind == JsonValueKind.Number ? pp.GetDouble() : null,
            mods, false, OnlineScoreId: scoreId, IsLocallyStored: false, Origin: LocalLibraryOrigin.Online,
            OnlineBeatmapId: checked((int)number(map, "id")), Passed: !root.TryGetProperty("passed", out var passed) || passed.ValueKind != JsonValueKind.False,
            LegacyScore: address.LegacyRuleset is not null && !root.TryGetProperty("legacy_score_id", out _));
    }
}
