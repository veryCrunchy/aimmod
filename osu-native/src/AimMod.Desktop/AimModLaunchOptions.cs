namespace AimMod.Desktop;

public sealed record ReplayOpenRequest(string BeatmapPath, string ReplayPath);

public sealed record AimModLaunchOptions(ReplayOpenRequest? Replay, string? Error)
{
    public AimModDeepLink? DeepLink { get; init; }
    public static AimModLaunchOptions Home { get; } = new(null, null);

    public static AimModLaunchOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return Home;

        if (args.Count == 1 && AimModDeepLink.TryParse(args[0], out var link))
            return Home with { DeepLink = link };
        if (args.Any(arg => arg.StartsWith("aimmod-osu:", StringComparison.OrdinalIgnoreCase)))
            return invalid("This AimMod link is invalid. Open a beatmap set or skin link from the website.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int i = 0; i < args.Count; i += 2)
        {
            string argument = args[i];

            if (i + 1 >= args.Count)
                return invalid($"{argument} needs a file path.");

            if (argument is not "--beatmap" and not "--replay")
                return invalid($"AimMod does not recognise the option '{argument}'.");

            if (!values.TryAdd(argument, args[i + 1]))
                return invalid($"{argument} was supplied more than once.");
        }

        if (!values.TryGetValue("--beatmap", out string? beatmap) || !values.TryGetValue("--replay", out string? replay))
            return invalid("Opening a replay needs both --beatmap and --replay.");

        try
        {
            beatmap = Path.GetFullPath(beatmap);
            replay = Path.GetFullPath(replay);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return invalid("The beatmap or replay path is not valid.");
        }

        if (!File.Exists(beatmap))
            return invalid("AimMod could not find the selected beatmap bundle.");

        if (!File.Exists(replay))
            return invalid("AimMod could not find the selected replay.");

        string beatmapExtension = Path.GetExtension(beatmap);
        if (!string.Equals(beatmapExtension, ".osz", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(beatmapExtension, ".osu", StringComparison.OrdinalIgnoreCase))
        {
            return invalid("The beatmap must be a staged .osz or an extracted .osu file with its sibling audio and assets.");
        }

        if (!string.Equals(Path.GetExtension(replay), ".osr", StringComparison.OrdinalIgnoreCase))
            return invalid("The replay must be an .osr file.");

        return new AimModLaunchOptions(new ReplayOpenRequest(beatmap, replay), null);
    }

    private static AimModLaunchOptions invalid(string message) => new(null, message);
}
