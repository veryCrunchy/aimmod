using System.Text;

namespace AimMod.Desktop.Practice;

public interface IPracticeAudioSlicer
{
    Task SliceAsync(PracticeAudioSliceRequest request, string destinationPath, CancellationToken cancellationToken = default);
}

public sealed record PracticeMapExportResult(string DirectoryPath, string BeatmapPath, string AudioPath);

public sealed class PracticeMapExporter
{
    public async Task<PracticeMapExportResult> ExportAsync(
        PracticeSourceBeatmap source,
        PracticeMapPlan plan,
        string destinationDirectory,
        IPracticeAudioSlicer audioSlicer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(audioSlicer);
        if (!string.Equals(Path.GetFullPath(source.SourcePath), Path.GetFullPath(plan.SourceBeatmapPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The plan does not belong to the supplied source beatmap.");

        string root = Path.GetFullPath(destinationDirectory);
        string sourceDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(source.SourcePath)!));
        string sourcePrefix = sourceDirectory + Path.DirectorySeparatorChar;
        if (string.Equals(root, sourceDirectory, StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Practice maps must be exported outside the original beatmap directory.");
        Directory.CreateDirectory(root);
        string audioPath = Path.Combine(root, plan.AudioSlice.OutputFilename);
        string beatmapPath = Path.Combine(root, sanitiseFilename(plan.OutputVersion) + ".osu");
        if (File.Exists(audioPath) || File.Exists(beatmapPath))
            throw new IOException("Practice-map export refuses to overwrite existing files.");

        try
        {
            await audioSlicer.SliceAsync(plan.AudioSlice, audioPath, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(audioPath) || new FileInfo(audioPath).Length == 0)
                throw new InvalidDataException("The audio slicer did not produce a usable synchronized audio asset.");
            await File.WriteAllTextAsync(beatmapPath, Serialize(source, plan), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            return new PracticeMapExportResult(root, beatmapPath, audioPath);
        }
        catch
        {
            tryDelete(beatmapPath);
            tryDelete(audioPath);
            throw;
        }
    }

    public static string Serialize(PracticeSourceBeatmap source, PracticeMapPlan plan)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        var builder = new StringBuilder("osu file format v14\n\n");
        appendSection(builder, "General", replace(source.Sections.GetValueOrDefault("General", Array.Empty<string>()), new Dictionary<string, string>
        {
            ["AudioFilename"] = plan.AudioSlice.OutputFilename,
            ["AudioLeadIn"] = PracticeMapPlanner.format(plan.AudioLeadInMs),
            ["PreviewTime"] = PracticeMapPlanner.format(plan.HitObjects[0].StartTimeMs),
            ["Mode"] = "0",
        }));
        appendSection(builder, "Editor", source.Sections.GetValueOrDefault("Editor", Array.Empty<string>()));
        appendSection(builder, "Metadata", replace(source.Sections.GetValueOrDefault("Metadata", Array.Empty<string>()), new Dictionary<string, string>
        {
            ["Version"] = plan.OutputVersion,
            ["Source"] = plan.Attribution,
            ["BeatmapID"] = "0",
            ["BeatmapSetID"] = "-1",
        }));
        appendSection(builder, "Difficulty", source.Sections.GetValueOrDefault("Difficulty", Array.Empty<string>()));
        appendSection(builder, "Events", practiceEvents(plan));
        appendSection(builder, "TimingPoints", plan.TimingPoints.Select(point => string.Join(',', point.Fields)));
        appendSection(builder, "Colours", source.Sections.GetValueOrDefault("Colours", Array.Empty<string>()));
        appendSection(builder, "HitObjects", plan.HitObjects.Select(hitObject => string.Join(',', hitObject.Fields)));
        return builder.ToString();
    }

    private static IReadOnlyList<string> replace(IReadOnlyList<string> source, IReadOnlyDictionary<string, string> replacements)
    {
        var result = source.ToList();
        foreach ((string key, string value) in replacements)
        {
            int index = result.FindIndex(line => line.StartsWith(key + ':', StringComparison.OrdinalIgnoreCase));
            string replacement = $"{key}:{value}";
            if (index >= 0)
                result[index] = replacement;
            else
                result.Add(replacement);
        }
        return result;
    }

    private static void appendSection(StringBuilder builder, string name, IEnumerable<string> lines)
    {
        builder.Append('[').Append(name).Append("]\n");
        foreach (string line in lines)
            builder.Append(line).Append('\n');
        builder.Append('\n');
    }

    private static IEnumerable<string> practiceEvents(PracticeMapPlan plan)
    {
        yield return "// Looped source audio; background/video events are intentionally omitted.";
        int objectsPerRound = plan.SourceSection.HitObjects.Count;
        for (int repetition = 0; repetition < plan.RepeatCount - 1; repetition++)
        {
            PracticeHitObject finalObject = plan.HitObjects[(repetition + 1) * objectsPerRound - 1];
            PracticeHitObject nextObject = plan.HitObjects[(repetition + 1) * objectsPerRound];
            double breakStart = finalObject.EndTimeMs + 250;
            double breakEnd = nextObject.StartTimeMs - 750;
            if (breakEnd - breakStart >= 1_000)
                yield return $"2,{PracticeMapPlanner.format(breakStart)},{PracticeMapPlanner.format(breakEnd)}";
        }
    }

    private static string sanitiseFilename(string value)
    {
        string result = string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();
        return string.IsNullOrWhiteSpace(result) ? "AimMod practice drill" : result;
    }

    private static void tryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
