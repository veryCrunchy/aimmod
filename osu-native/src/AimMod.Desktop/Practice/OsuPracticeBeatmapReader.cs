using System.Globalization;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu;

namespace AimMod.Desktop.Practice;

public static class OsuPracticeBeatmapReader
{
    public static PracticeSourceBeatmap Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".osu", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Practice-map sources must be .osu beatmap files.");

        string[] lines = File.ReadAllLines(fullPath);
        if (lines.Length == 0 || !lines[0].StartsWith("osu file format v", StringComparison.Ordinal))
            throw new InvalidDataException("The practice-map source is not a supported osu! beatmap.");

        Dictionary<string, IReadOnlyList<string>> sections = splitSections(lines);
        IReadOnlyList<string> general = section(sections, "General");
        IReadOnlyList<string> metadata = section(sections, "Metadata");
        int mode = parseInt(value(general, "Mode", "0"), "Mode");
        if (mode != 0)
            throw new InvalidDataException("Practice maps can only be created from osu!standard beatmaps.");

        var mapMetadata = new PracticeMapMetadata(
            value(metadata, "Title"),
            value(metadata, "Artist"),
            value(metadata, "Creator"),
            value(metadata, "Version"),
            value(general, "AudioFilename"),
            mode);
        if (string.IsNullOrWhiteSpace(mapMetadata.AudioFilename))
            throw new InvalidDataException("The source beatmap does not declare an audio file.");

        PracticeTimingPoint[] timing = section(sections, "TimingPoints")
            .Where(contentLine)
            .Select(parseTimingPoint)
            .OrderBy(point => point.TimeMs)
            .ToArray();
        double[] decodedEndTimes = decodeEndTimes(fullPath);
        string[] objectLines = section(sections, "HitObjects").Where(contentLine).ToArray();
        if (decodedEndTimes.Length != objectLines.Length)
            throw new InvalidDataException("osu! decoded a different hitobject count than the source file contains.");
        PracticeHitObject[] objects = objectLines
            .Select((line, index) => parseHitObject(line, index, decodedEndTimes[index]))
            .OrderBy(hitObject => hitObject.StartTimeMs)
            .ToArray();
        if (objects.Length == 0)
            throw new InvalidDataException("The source beatmap contains no hitobjects.");

        return new PracticeSourceBeatmap(fullPath, mapMetadata, sections, timing, objects);
    }

    private static Dictionary<string, IReadOnlyList<string>> splitSections(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        string? currentName = null;
        List<string>? currentLines = null;
        foreach (string raw in lines.Skip(1))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentName = line[1..^1];
                currentLines = [];
                result[currentName] = currentLines;
            }
            else if (currentLines is not null)
            {
                currentLines.Add(line);
            }
        }
        return result;
    }

    private static IReadOnlyList<string> section(IReadOnlyDictionary<string, IReadOnlyList<string>> sections, string name) =>
        sections.TryGetValue(name, out IReadOnlyList<string>? lines) ? lines : Array.Empty<string>();

    private static string value(IEnumerable<string> lines, string key, string fallback = "")
    {
        string prefix = key + ':';
        string? match = lines.FirstOrDefault(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return match is null ? fallback : match[prefix.Length..].Trim();
    }

    private static PracticeTimingPoint parseTimingPoint(string line)
    {
        string[] fields = line.Split(',');
        if (fields.Length < 2)
            throw new InvalidDataException("A timing point in the source beatmap is malformed.");
        double time = parseDouble(fields[0], "timing point time");
        bool uninherited = fields.Length < 7 || fields[6].Trim() != "0";
        return new PracticeTimingPoint(time, uninherited, fields);
    }

    private static double[] decodeEndTimes(string path)
    {
        // Use osu!'s own decoder for slider/spinner end times. Slider duration depends on
        // inherited timing points and difficulty settings and cannot be inferred from one line.
        _ = typeof(OsuRuleset).Assembly;
        IBeatmap decoded = new FlatWorkingBeatmap(path).Beatmap;
        return decoded.HitObjects.Select(hitObject => hitObject.GetEndTime()).ToArray();
    }

    private static PracticeHitObject parseHitObject(string line, int index, double decodedEndTime)
    {
        string[] fields = line.Split(',');
        if (fields.Length < 5)
            throw new InvalidDataException("A hitobject in the source beatmap is malformed.");
        int type = parseInt(fields[3], "hitobject type");
        double start = parseDouble(fields[2], "hitobject time");
        double end = Math.Max(start, decodedEndTime);
        return new PracticeHitObject(index, parseInt(fields[0], "hitobject x"), parseInt(fields[1], "hitobject y"), start, end, type, fields);
    }

    private static bool contentLine(string line) => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("//", StringComparison.Ordinal);
    private static int parseInt(string value, string field) => int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
        ? parsed
        : throw new InvalidDataException($"The source beatmap has an invalid {field}.");
    private static double parseDouble(string value, string field) => double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed)
        ? parsed
        : throw new InvalidDataException($"The source beatmap has an invalid {field}.");
}
