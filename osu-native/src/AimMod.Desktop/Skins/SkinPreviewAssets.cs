using System.Collections.Concurrent;
using System.Globalization;
using osu.Framework.Graphics;

namespace AimMod.Desktop.Skins;

/// <summary>One element file and whether it is a high-resolution (@2x) variant.</summary>
public sealed record SkinElementFile(string Path, bool HighResolution);

/// <summary>
/// The parts of an installed skin needed to draw a gameplay-like preview: element files
/// by lookup name, combo colours and the configurable number-font prefixes from skin.ini.
/// Parsing is cached per skin identity and never runs on the update thread.
/// </summary>
public sealed class SkinPreviewAssets
{
    private const int maximum_ini_bytes = 512 * 1024;
    private static readonly ConcurrentDictionary<string, Task<SkinPreviewAssets>> cache = new(StringComparer.Ordinal);

    public static readonly SkinPreviewAssets Empty = new(new Dictionary<string, SkinElementFile>(), [], null, null, "default", "score", null, null);

    private readonly IReadOnlyDictionary<string, SkinElementFile> elements;

    private SkinPreviewAssets(
        IReadOnlyDictionary<string, SkinElementFile> elements,
        IReadOnlyList<Colour4> comboColours,
        Colour4? sliderBorder,
        Colour4? sliderTrack,
        string hitCirclePrefix,
        string scorePrefix,
        string? version,
        string? author)
    {
        this.elements = elements;
        ComboColours = comboColours;
        SliderBorder = sliderBorder;
        SliderTrack = sliderTrack;
        HitCirclePrefix = hitCirclePrefix;
        ScorePrefix = scorePrefix;
        Version = version;
        Author = author;
    }

    public IReadOnlyList<Colour4> ComboColours { get; }
    public Colour4? SliderBorder { get; }
    public Colour4? SliderTrack { get; }
    public string HitCirclePrefix { get; }
    public string ScorePrefix { get; }
    public string? Version { get; }
    public string? Author { get; }

    /// <summary>True when the skin ships its own hit circle, so previews show real artwork.</summary>
    public bool HasGameplayElements => Find("hitcircle") is not null;

    public IEnumerable<string> Paths => elements.Values.Select(element => element.Path);

    /// <summary>Combo colour for the n-th combo; osu!'s default colours when the skin sets none.</summary>
    public Colour4 Combo(int index)
    {
        IReadOnlyList<Colour4> colours = ComboColours.Count > 0 ? ComboColours : default_combo_colours;
        return colours[((index % colours.Count) + colours.Count) % colours.Count];
    }

    /// <summary>Finds an element by its skin lookup name ("hitcircle", "default-1", "hit300"), preferring @2x.</summary>
    public SkinElementFile? Find(string name) =>
        elements.GetValueOrDefault(name) ?? elements.GetValueOrDefault(name + "-0");

    public static Task<SkinPreviewAssets> LoadAsync(InstalledLazerSkin skin, CancellationToken cancellationToken = default)
    {
        if (skin.ElementFiles.Count == 0)
            return Task.FromResult(Empty);
        string key = $"{skin.SkinId:N}:{skin.Summary.ContentHash}:{skin.ElementFiles.Count}";
        Task<SkinPreviewAssets> task = cache.GetOrAdd(key, _ => Task.Run(() => read(skin.ElementFiles), CancellationToken.None));
        if (task.IsFaulted || cache.Count > 512)
            cache.TryRemove(key, out _);
        return task.WaitAsync(cancellationToken);
    }

    internal static SkinPreviewAssets Read(IReadOnlyDictionary<string, string> files) => read(files);

    private static SkinPreviewAssets read(IReadOnlyDictionary<string, string> files)
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ini = files.TryGetValue("skin.ini", out string? iniPath)
            ? readIni(iniPath)
            : new Dictionary<string, IReadOnlyDictionary<string, string>>();
        IReadOnlyDictionary<string, string> general = ini.GetValueOrDefault("General") ?? new Dictionary<string, string>();
        IReadOnlyDictionary<string, string> colours = ini.GetValueOrDefault("Colours") ?? new Dictionary<string, string>();
        IReadOnlyDictionary<string, string> fonts = ini.GetValueOrDefault("Fonts") ?? new Dictionary<string, string>();

        var combo = new List<Colour4>();
        for (int i = 1; i <= 8; i++)
        {
            if (colours.TryGetValue($"Combo{i}", out string? value) && parseColour(value) is { } colour)
                combo.Add(colour);
        }

        var elements = new Dictionary<string, SkinElementFile>(StringComparer.OrdinalIgnoreCase);
        foreach ((string logicalName, string path) in files)
        {
            if (!logicalName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                continue;
            string name = logicalName[..^4];
            bool highResolution = name.EndsWith("@2x", StringComparison.OrdinalIgnoreCase);
            if (highResolution)
                name = name[..^3];
            if (!elements.TryGetValue(name, out SkinElementFile? existing) || highResolution && !existing.HighResolution)
                elements[name] = new SkinElementFile(path, highResolution);
        }

        return new SkinPreviewAssets(
            elements,
            combo,
            colours.TryGetValue("SliderBorder", out string? border) ? parseColour(border) : null,
            colours.TryGetValue("SliderTrackOverride", out string? track) ? parseColour(track) : null,
            prefix(fonts.GetValueOrDefault("HitCirclePrefix"), "default"),
            prefix(fonts.GetValueOrDefault("ScorePrefix"), "score"),
            general.GetValueOrDefault("Version") is { Length: > 0 and <= 32 } version ? version : null,
            general.GetValueOrDefault("Author") is { Length: > 0 and <= 200 } author ? author : null);
    }

    private static string prefix(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().Replace('\\', '/');

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> readIni(string path)
    {
        var sections = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > maximum_ini_bytes)
                return sections;
            Dictionary<string, string>? current = null;
            foreach (string raw in File.ReadLines(path).Take(4_000))
            {
                string line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    string section = line[1..^1].Trim();
                    current = sections.TryGetValue(section, out var existing)
                        ? (Dictionary<string, string>)existing
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sections[section] = current;
                    continue;
                }
                if (current is null || line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                    continue;
                int colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                string value = line[(colon + 1)..];
                int comment = value.IndexOf("//", StringComparison.Ordinal);
                current[line[..colon].Trim()] = (comment >= 0 ? value[..comment] : value).Trim();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return sections;
    }

    private static Colour4? parseColour(string value)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
            return null;
        byte[] channels = new byte[3];
        for (int i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int channel))
                return null;
            channels[i] = (byte)Math.Clamp(channel, 0, 255);
        }
        return new Colour4(channels[0], channels[1], channels[2], 255);
    }

    private static readonly Colour4[] default_combo_colours =
    [
        new(255, 192, 0, 255),
        new(0, 202, 0, 255),
        new(18, 124, 255, 255),
        new(242, 24, 57, 255),
    ];
}
