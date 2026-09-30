using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace AimMod.Desktop.Skins;

/// <summary>
/// Turns stored skin names into readable labels. Folder names often carry sort prefixes
/// ("- ", "# ", "!!! "), padding runs, placeholder '?' characters left by lost emoji and a
/// lazer import suffix that repeats the folder name ("Name [- Name (2)]"). Only the label
/// changes: identifiers, search and apply always use the stored name.
/// </summary>
/// <summary>A skin's readable title with an optional creator or team tag shown separately.</summary>
public readonly record struct SkinLabel(string? Tag, string Title);

public static class SkinDisplayName
{
    private static readonly ConcurrentDictionary<string, string> cache = new(StringComparer.Ordinal);

    public static string Format(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Unnamed skin";
        if (cache.TryGetValue(raw, out string? cached))
            return cached;
        string formatted = format(raw);
        if (cache.Count < 4_096)
            cache.TryAdd(raw, formatted);
        return formatted;
    }

    private static readonly Dictionary<char, char> decorative_pairs = new()
    {
        ['《'] = '》', ['【'] = '】', ['「'] = '」', ['『'] = '』', ['〔'] = '〕', ['［'] = '］', ['〈'] = '〉', ['[' ] = ']', ['('] = ')',
    };

    /// <summary>
    /// Splits a readable name into an optional leading tag ("《CK》 Bacon boi" → "CK") and a title
    /// whose decorative CJK brackets become separators ("Bacon boi 1.0 『blue』" → "Bacon boi 1.0 · blue").
    /// </summary>
    public static SkinLabel Split(string? raw)
    {
        string name = Format(raw);
        string? tag = null;
        if (name.Length > 3 && decorative_pairs.TryGetValue(name[0], out char close))
        {
            int end = name.IndexOf(close, 1);
            string rest = end > 0 ? name[(end + 1)..].Trim() : string.Empty;
            string inner = end > 0 ? name[1..end].Trim() : string.Empty;
            if (end > 1 && inner.Length is > 0 and <= 12 && rest.Length >= 2 && !inner.Any(character => decorative_pairs.ContainsKey(character)))
            {
                tag = inner;
                name = rest;
            }
        }

        var builder = new StringBuilder(name.Length + 4);
        foreach (char character in name)
        {
            if (character > 127 && decorative_pairs.ContainsKey(character))
                builder.Append(" · ");
            else if (character > 127 && decorative_pairs.ContainsValue(character))
                builder.Append(' ');
            else
                builder.Append(character);
        }
        string title = collapse(builder.ToString()).Replace("· ·", "·", StringComparison.Ordinal).Trim(' ', '·');
        return new SkinLabel(tag, title.Length == 0 ? name : title);
    }

    /// <summary>True when the label hides meaningful differences, so the stored name should be offered too.</summary>
    public static bool Differs(string? raw) => !string.Equals(collapse(raw ?? string.Empty), Format(raw), StringComparison.Ordinal);

    private static string format(string raw)
    {
        string name = collapse(raw);
        name = removeDuplicateSuffix(name);
        name = removePlaceholders(name);
        name = trimDecoration(collapse(name));
        name = removeWrappingOrnament(name);
        name = removeTrailingCopyNumber(name);
        if (name.Length == 0)
            name = collapse(raw);
        return name.Length == 0 ? "Unnamed skin" : name;
    }

    /// <summary>Drops "[...]" when it only repeats the name (lazer appends the archive/folder name on import).</summary>
    private static string removeDuplicateSuffix(string name)
    {
        if (!name.EndsWith(']'))
            return name;
        int open = name.LastIndexOf(" [", StringComparison.Ordinal);
        if (open <= 0)
            return name;
        string main = name[..open];
        string suffix = name[(open + 2)..^1];
        string mainKey = key(main);
        string suffixKey = key(removeTrailingCopyNumber(suffix));
        if (mainKey.Length == 0 || suffixKey.Length == 0)
            return name;
        bool repeats = mainKey == suffixKey || mainKey.Contains(suffixKey, StringComparison.Ordinal) || suffixKey.Contains(mainKey, StringComparison.Ordinal);
        return repeats ? main : name;
    }

    private static string removeTrailingCopyNumber(string name)
    {
        string trimmed = name.TrimEnd();
        if (trimmed.Length < 4 || trimmed[^1] != ')')
            return trimmed;
        int open = trimmed.LastIndexOf(" (", StringComparison.Ordinal);
        if (open <= 0)
            return trimmed;
        string inside = trimmed[(open + 2)..^1];
        return inside.Length is > 0 and <= 3 && inside.All(char.IsAsciiDigit) ? trimmed[..open].TrimEnd() : trimmed;
    }

    /// <summary>'?' is kept only as real punctuation after a word ("Why?"); elsewhere it replaced a lost character.</summary>
    private static string removePlaceholders(string name)
    {
        var builder = new StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            char character = name[i];
            if (character is '?' or '�')
            {
                bool afterWord = i > 0 && char.IsLetterOrDigit(name[i - 1]);
                bool beforeBreak = i == name.Length - 1 || char.IsWhiteSpace(name[i + 1]);
                if (character == '?' && afterWord && beforeBreak)
                    builder.Append(character);
                continue;
            }
            builder.Append(character);
        }
        return builder.ToString();
    }

    /// <summary>Strips sort prefixes and trailing ornaments while keeping paired brackets such as 《CK》.</summary>
    private static string trimDecoration(string name)
    {
        int start = 0;
        int end = name.Length;
        while (start < end && isDecoration(name[start]))
            start++;
        while (end > start && isDecoration(name[end - 1]))
            end--;
        return name[start..end].Trim();
    }

    /// <summary>"⌈ Kindle ⌋" → "Kindle": a symbol pair around the whole name is ornament, and UI fonts often lack those glyphs.</summary>
    private static string removeWrappingOrnament(string name)
    {
        if (name.Length < 3 || name[0] < 128 || name[^1] < 128)
            return name;
        if (char.GetUnicodeCategory(name[0]) is not (UnicodeCategory.OpenPunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.OtherPunctuation)
            || char.GetUnicodeCategory(name[^1]) is not (UnicodeCategory.ClosePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation))
            return name;
        string inner = name[1..^1];
        // Keep tags such as "《CK》 Bacon 『blue』" where the edge brackets belong to inner pairs.
        if (inner.Any(character => char.GetUnicodeCategory(character) is UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
                or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation))
            return name;
        string trimmed = trimDecoration(inner);
        return trimmed.Length == 0 ? name : trimmed;
    }

    private static bool isDecoration(char character)
    {
        if (char.IsWhiteSpace(character))
            return true;
        if (character is '-' or '_' or '#' or '!' or '.' or '~' or '*' or '+' or '=' or '|' or ':' or ',' or '•' or '·' or '>' or '<' or '^')
            return true;
        UnicodeCategory category = char.GetUnicodeCategory(character);
        return category is UnicodeCategory.MathSymbol or UnicodeCategory.OtherSymbol or UnicodeCategory.DashPunctuation;
    }

    private static string collapse(string value)
    {
        var builder = new StringBuilder(value.Length);
        bool space = false;
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                space = builder.Length > 0;
                continue;
            }
            if (space)
                builder.Append(' ');
            space = false;
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static string key(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }
}
