using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Osu;
using osu.Game.Scoring.Legacy;

namespace AimMod.Desktop;

public sealed class PreservedBeatmapImportTask : ImportTask
{
    public PreservedBeatmapImportTask(string path)
        : base(path)
    {
    }

    public override void DeleteFile()
    {
        // The caller owns the selected file. Importing it must never consume it.
    }
}

public sealed class ImportedBeatmapReplayDecoder : LegacyScoreDecoder
{
    private readonly BeatmapManager beatmapManager;
    private readonly IReadOnlyList<BeatmapInfo> candidates;

    public WorkingBeatmap? SelectedBeatmap { get; private set; }

    public ImportedBeatmapReplayDecoder(BeatmapManager beatmapManager, IReadOnlyList<BeatmapInfo> candidates)
    {
        this.beatmapManager = beatmapManager;
        this.candidates = candidates;
    }

    protected override Ruleset GetRuleset(int rulesetId)
    {
        if (rulesetId != 0)
            throw new NotSupportedException($"AimMod currently supports osu!standard replays, but this replay uses ruleset {rulesetId}.");

        return new OsuRuleset();
    }

    protected override WorkingBeatmap GetBeatmap(string md5Hash)
    {
        BeatmapInfo? match = candidates.FirstOrDefault(beatmap =>
            string.Equals(md5Hash, beatmap.MD5Hash, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            throw new InvalidOperationException("The replay belongs to a difficulty that is not present in the selected beatmap bundle.");

        return SelectedBeatmap = beatmapManager.GetWorkingBeatmap(match);
    }
}
