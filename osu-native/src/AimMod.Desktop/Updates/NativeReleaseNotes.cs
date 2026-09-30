using System.Text.RegularExpressions;

namespace AimMod.Desktop.Updates;

public sealed record NativeReleaseNotes(string Version, string Markdown)
{
    public const int MaximumLength = 24000;
    private static readonly Lazy<IReadOnlyList<NativeReleaseNotes>> bundled = new(loadBundled);
    private static readonly Regex versionPattern = new(@"^\d{1,9}\.\d{1,9}\.\d{1,9}(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant);

    public static string Normalise(string? markdown) => new((markdown ?? "").Take(MaximumLength)
        .Where(c => c == '\n' || c == '\t' || !char.IsControl(c)).ToArray());

    public static IReadOnlyList<NativeReleaseNotes> ForState(NativeUpdateState state)
    {
        var entries = bundled.Value.Where(entry => state.Channel == NativeUpdateChannel.Preview || !entry.Version.Contains('-'))
            .ToDictionary(entry => entry.Version, StringComparer.Ordinal);
        if (state.Version is { Length: <= 128 } version && versionPattern.IsMatch(version))
        {
            string notes = Normalise(state.ReleaseNotes);
            if (notes.Length > 0 || !entries.ContainsKey(version)) entries[version] = new(version, notes);
        }
        return entries.Values.OrderByDescending(entry => entry.Version == state.Version)
            .ThenByDescending(entry => System.Version.Parse(entry.Version.Split('-', '+')[0]))
            .ThenBy(entry => entry.Version.Contains('-'))
            .Take(40).ToArray();
    }

    private static IReadOnlyList<NativeReleaseNotes> loadBundled()
    {
        var assembly = typeof(NativeReleaseNotes).Assembly;
        const string prefix = "AimMod.Changelogs.";
        var entries = new List<NativeReleaseNotes>();
        foreach (string name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix) && name.EndsWith(".md")))
        {
            string version = name[prefix.Length..^3];
            if (!versionPattern.IsMatch(version)) continue;
            using Stream stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            char[] buffer = new char[MaximumLength];
            int count = reader.ReadBlock(buffer, 0, buffer.Length);
            entries.Add(new(version, Normalise(new string(buffer, 0, count))));
        }
        return entries;
    }
}
