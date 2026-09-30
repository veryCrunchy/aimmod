using System.Globalization;

namespace AimMod.Desktop.Visuals;

/// <summary>Short, player-facing wording for beatmap facts shared by the beatmap browsers.</summary>
public static class MapBrowserFormat
{
    public static string Duration(double milliseconds)
    {
        TimeSpan duration = TimeSpan.FromMilliseconds(double.IsFinite(milliseconds) ? Math.Max(0, milliseconds) : 0);
        return $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";
    }

    public static string DurationSeconds(int seconds) => Duration(seconds * 1000d);

    public static string Bpm(double bpm) => double.IsFinite(bpm) && bpm > 0 ? $"{bpm:0} BPM" : "BPM unknown";

    public static string StarRange(IReadOnlyCollection<double> stars)
    {
        if (stars.Count == 0)
            return string.Empty;
        double min = stars.Min(), max = stars.Max();
        return Math.Abs(max - min) < 0.005 ? $"{min:0.00}" : $"{min:0.00} – {max:0.00}";
    }

    public static string DifficultyCount(int count) => count == 1 ? "1 difficulty" : $"{count} difficulties";

    public static string Count(long value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000d).ToString(value >= 10_000_000 ? "0" : "0.#", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (value / 1_000d).ToString("0", CultureInfo.InvariantCulture) + "k",
        >= 1_000 => (value / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    public static string Ago(DateTimeOffset date, DateTimeOffset? now = null)
    {
        TimeSpan elapsed = (now ?? DateTimeOffset.Now) - date;
        if (elapsed < TimeSpan.FromMinutes(1)) return "just now";
        if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed < TimeSpan.FromDays(1)) return $"{(int)elapsed.TotalHours}h ago";
        if (elapsed < TimeSpan.FromDays(30)) return $"{(int)elapsed.TotalDays}d ago";
        if (elapsed < TimeSpan.FromDays(365)) return $"{(int)(elapsed.TotalDays / 30)}mo ago";
        return date.ToLocalTime().ToString("MMM yyyy", CultureInfo.InvariantCulture);
    }

    public static string Accuracy(double accuracy) => $"{accuracy * 100:0.00}%";

    /// <summary>Approach rate: how early circles appear.</summary>
    public static string ApproachRate(double value) => value switch
    {
        < 5 => "Slow approach",
        < 8 => "Moderate approach",
        < 9.3 => "Fast approach",
        < 10 => "Very fast approach",
        _ => "Extreme approach",
    };

    /// <summary>Overall difficulty: the timing window for hits.</summary>
    public static string OverallDifficulty(double value) => value switch
    {
        < 6 => "Lenient timing",
        < 8 => "Moderate timing",
        < 9.3 => "Tight timing",
        _ => "Very tight timing",
    };

    public static string CircleSize(double value) => value switch
    {
        < 3 => "Large circles",
        < 4.5 => "Medium circles",
        < 5.5 => "Small circles",
        _ => "Tiny circles",
    };

    public static string DrainRate(double value) => value switch
    {
        < 4 => "Light drain",
        < 6.5 => "Moderate drain",
        _ => "Heavy drain",
    };

    /// <summary>Pass rate as the share of submitted plays that finished the map.</summary>
    public static string? PassRate(long plays, long passes) =>
        plays <= 0 ? null : $"{Math.Clamp(passes / (double)plays, 0, 1) * 100:0}% pass";

    public static string Status(string status) => status.ToLowerInvariant() switch
    {
        "ranked" => "Ranked",
        "approved" => "Approved",
        "qualified" => "Qualified",
        "loved" => "Loved",
        "pending" => "Pending",
        "wip" => "WIP",
        "graveyard" => "Graveyard",
        _ => string.IsNullOrWhiteSpace(status) ? "Unknown" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(status),
    };
}
