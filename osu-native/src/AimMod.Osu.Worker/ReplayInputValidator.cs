using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Osu.Worker;

internal sealed record ValidatedReplayInput(string StagingDirectory, string BeatmapPath, string ReplayPath);

internal sealed class ReplayInputValidator
{
    public ValidatedReplayInput Validate(ReplayAnalysisRequest request)
    {
        try
        {
            string stagingDirectory = validateStagingDirectory(request.StagingDirectory);
            string beatmapPath = validateFile(stagingDirectory, request.BeatmapPath, ".osu", ReplayAnalysisProtocol.MaximumBeatmapBytes, "beatmap");
            string replayPath = validateFile(stagingDirectory, request.ReplayPath, ".osr", ReplayAnalysisProtocol.MaximumReplayBytes, "replay");
            return new ValidatedReplayInput(stagingDirectory, beatmapPath, replayPath);
        }
        catch (RuntimeCommandException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new RuntimeCommandException("staged_path_invalid", "The staged input paths could not be validated.");
        }
    }

    private static string validateStagingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new RuntimeCommandException("staging_directory_invalid", "The staging directory must be an absolute path.");

        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(fullPath))
            throw new RuntimeCommandException("staging_directory_invalid", "The staging directory does not exist.");

        rejectReparsePoint(fullPath, "The staging directory cannot be a symbolic link.");
        return fullPath;
    }

    private static string validateFile(string stagingDirectory, string path, string expectedExtension, long maximumBytes, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new RuntimeCommandException("staged_path_invalid", $"The staged {label} path must be absolute.");

        string fullPath = Path.GetFullPath(path);
        string relativePath = Path.GetRelativePath(stagingDirectory, fullPath);
        if (relativePath == "." || Path.IsPathRooted(relativePath) || relativePath == ".." || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new RuntimeCommandException("staged_path_invalid", $"The {label} file must be inside the staging directory.");

        if (!string.Equals(Path.GetExtension(fullPath), expectedExtension, StringComparison.OrdinalIgnoreCase))
            throw new RuntimeCommandException("staged_path_invalid", $"The staged {label} file must use the {expectedExtension} extension.");

        if (!File.Exists(fullPath))
            throw new RuntimeCommandException("input_not_found", $"The staged {label} file does not exist.");

        rejectPathReparsePoints(stagingDirectory, fullPath, label);

        var info = new FileInfo(fullPath);
        if (info.Length == 0)
            throw new RuntimeCommandException("input_empty", $"The staged {label} file is empty.");
        if (info.Length > maximumBytes)
            throw new RuntimeCommandException("input_too_large", $"The staged {label} file exceeds the {maximumBytes / (1024 * 1024)} MiB limit.");

        return fullPath;
    }

    private static void rejectPathReparsePoints(string stagingDirectory, string filePath, string label)
    {
        string relativePath = Path.GetRelativePath(stagingDirectory, filePath);
        string currentPath = stagingDirectory;
        foreach (string segment in relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            rejectReparsePoint(currentPath, $"The staged {label} path cannot contain symbolic links.");
        }
    }

    private static void rejectReparsePoint(string path, string message)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new RuntimeCommandException("staged_path_invalid", message);
    }
}
