using System.Text.RegularExpressions;

namespace AimMod.Setup;

enum NoteKind { Heading, Bullet, Text }
sealed record NoteLine(NoteKind Kind, string Text);

// A short, plain-text change log from the feed's release notes (the GitHub
// release body, Markdown written by release-please).
static class ReleaseNotes
{
    static readonly Regex Link = new(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant);
    static readonly Regex Reference = new(@"\s*\((?:#\d+|[0-9a-f]{7,40})\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    static readonly Regex VersionHeading = new(@"^v?\d+\.\d+\.\d+", RegexOptions.CultureInvariant);
    static readonly Regex Html = new(@"<[^>]+>", RegexOptions.CultureInvariant);

    public static IReadOnlyList<NoteLine> Summarize(string? markdown, int maxLines = 14)
    {
        var lines = new List<NoteLine>();
        if (string.IsNullOrWhiteSpace(markdown)) return lines;
        var truncated = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("<!--") || line.All(c => c is '-' or '=' or '*' or '_')) continue;
            var kind = NoteKind.Text;
            if (line.StartsWith('#')) { kind = NoteKind.Heading; line = line.TrimStart('#').Trim(); }
            else if (line.StartsWith("* ") || line.StartsWith("- ") || line.StartsWith("+ ")) { kind = NoteKind.Bullet; line = line[2..].Trim(); }
            line = Clean(line);
            if (line.Length == 0) continue;
            // "0.4.0 (2026-10-01)": the version is shown elsewhere.
            if (kind == NoteKind.Heading && VersionHeading.IsMatch(line)) continue;
            if (lines.Count == maxLines) { truncated = true; break; }
            lines.Add(new(kind, line.Length > 160 ? line[..157].TrimEnd() + "…" : line));
        }
        // A trailing heading without entries says nothing.
        while (lines.Count > 0 && lines[^1].Kind == NoteKind.Heading) lines.RemoveAt(lines.Count - 1);
        if (truncated) lines.Add(new(NoteKind.Text, "…and more in the release notes on GitHub."));
        return lines;
    }

    static string Clean(string line)
    {
        line = Link.Replace(line, "$1");
        line = Reference.Replace(line, "");
        line = Html.Replace(line, "");
        line = line.Replace("**", "").Replace("__", "").Replace("`", "");
        // release-please entries start with their scope ("kovaaks: ..."); the reader does not need it.
        line = Regex.Replace(line, @"^[a-z0-9-]+:\s+", "", RegexOptions.CultureInvariant).Trim();
        return line.Length > 0 ? char.ToUpperInvariant(line[0]) + line[1..] : line;
    }
}
