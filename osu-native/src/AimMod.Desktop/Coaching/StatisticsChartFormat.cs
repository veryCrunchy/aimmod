using System.Globalization;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Coaching;

/// <summary>Labels, units and axis ticks shared by the statistics charts, cards and table.</summary>
public static class StatisticsChartFormat
{
    public const string Missing = "—";

    public static string Name(StatisticsMetric metric) => metric switch
    {
        StatisticsMetric.Accuracy => "Accuracy",
        StatisticsMetric.Performance => "PP",
        StatisticsMetric.Misses => "Misses",
        _ => "Stars",
    };

    /// <summary>What the headline number means, in two or three words.</summary>
    public static string HeadlineName(StatisticsMetric metric) => metric switch
    {
        StatisticsMetric.Accuracy => "Average accuracy",
        StatisticsMetric.Performance => "Median PP",
        StatisticsMetric.Misses => "Misses per play",
        _ => "Average stars",
    };

    public static string Value(StatisticsMetric metric, double? value) => value is not { } number || !double.IsFinite(number)
        ? Missing
        : metric switch
        {
            StatisticsMetric.Accuracy => number.ToString("0.00", CultureInfo.InvariantCulture) + "%",
            StatisticsMetric.Performance => number.ToString("0", CultureInfo.InvariantCulture) + "pp",
            StatisticsMetric.Misses => number.ToString(number < 10 && number % 1 != 0 ? "0.0" : "0", CultureInfo.InvariantCulture),
            _ => number.ToString("0.00", CultureInfo.InvariantCulture) + "*",
        };

    /// <summary>Axis labels drop needless decimals so the gutter stays narrow.</summary>
    public static string Axis(StatisticsMetric metric, double value) => metric switch
    {
        StatisticsMetric.Accuracy => value.ToString("0.#", CultureInfo.InvariantCulture) + "%",
        StatisticsMetric.Performance => value.ToString("0", CultureInfo.InvariantCulture),
        StatisticsMetric.Misses => value.ToString("0", CultureInfo.InvariantCulture),
        _ => value.ToString("0.#", CultureInfo.InvariantCulture) + "*",
    };

    public static string Change(StatisticsMetric metric, double change) => metric switch
    {
        StatisticsMetric.Accuracy => Math.Abs(change).ToString("0.00", CultureInfo.InvariantCulture) + " pts",
        StatisticsMetric.Performance => Math.Abs(change).ToString("0", CultureInfo.InvariantCulture) + "pp",
        StatisticsMetric.Misses => Math.Abs(change).ToString("0.0", CultureInfo.InvariantCulture),
        _ => Math.Abs(change).ToString("0.00", CultureInfo.InvariantCulture) + "*",
    };

    /// <summary>Changes smaller than this are shown as "no change".</summary>
    public static double ChangeThreshold(StatisticsMetric metric) => metric switch
    {
        StatisticsMetric.Accuracy => 0.05,
        StatisticsMetric.Performance => 0.5,
        StatisticsMetric.Misses => 0.05,
        _ => 0.01,
    };

    /// <summary>Returns the colour and direction icon for a change, given which direction is better.</summary>
    public static (Colour4 Colour, IconUsage Icon) ChangeStyle(StatisticsMetric metric, double change, bool? higherIsBetter)
    {
        if (Math.Abs(change) < ChangeThreshold(metric))
            return (AimModPalette.Muted, FontAwesome.Solid.Minus);
        IconUsage icon = change > 0 ? FontAwesome.Solid.CaretUp : FontAwesome.Solid.CaretDown;
        if (higherIsBetter is not { } higher)
            return (AimModPalette.Cyan, icon);
        return (change > 0 == higher ? AimModPalette.Success : AimModPalette.Danger, icon);
    }

    /// <summary>A histogram bin as a readable range, such as "90–92%" or "3 misses".</summary>
    public static string BinRange(StatisticsMetric metric, StatisticsHistogramBin bin) => metric switch
    {
        StatisticsMetric.Misses => bin.Label == "1" ? "1 miss" : bin.Label + " misses",
        StatisticsMetric.Accuracy => $"{bin.Start:0.#}–{bin.End:0.#}%",
        StatisticsMetric.Performance => $"{bin.Start:0}–{bin.End:0}pp",
        _ => $"{bin.Start:0.##}–{bin.End:0.##}*",
    };

    public static string Date(DateTimeOffset value) => value.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public static string DateTime(DateTimeOffset value) => value.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"Today", "3d ago" and so on for recent plays, otherwise a short date.</summary>
    public static string Relative(DateTimeOffset value, DateTimeOffset now)
    {
        TimeSpan age = now - value;
        if (age < TimeSpan.FromHours(1)) return "Just now";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
        if (age < TimeSpan.FromDays(7)) return $"{(int)age.TotalDays}d ago";
        return value.ToLocalTime().ToString(value.Year == now.Year ? "d MMM" : "d MMM yy", CultureInfo.InvariantCulture);
    }

    /// <summary>Evenly spaced round numbers covering [minimum, maximum].</summary>
    public static double[] NiceTicks(double minimum, double maximum, int target = 4)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
            return [];
        if (maximum - minimum < 1e-9)
        {
            minimum -= 1;
            maximum += 1;
        }

        double rough = (maximum - minimum) / Math.Max(1, target);
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        double step = new[] { 1, 2, 2.5, 5, 10 }.Select(factor => factor * magnitude).First(candidate => candidate >= rough);
        double first = Math.Floor(minimum / step) * step;
        double last = Math.Ceiling(maximum / step) * step;
        int count = (int)Math.Round((last - first) / step) + 1;
        return Enumerable.Range(0, Math.Clamp(count, 2, 12)).Select(index => Math.Round(first + index * step, 6)).ToArray();
    }

    /// <summary>Date ticks at whole days, weeks or months depending on the span.</summary>
    public static (DateTimeOffset Time, string Label)[] TimeTicks(DateTimeOffset start, DateTimeOffset end, int maximum)
    {
        if (end <= start || maximum < 2)
            return [];
        TimeSpan span = end - start;
        DateTimeOffset localStart = start.ToLocalTime();
        var ticks = new List<(DateTimeOffset, string)>();
        if (span <= TimeSpan.FromDays(2))
        {
            int hours = new[] { 1, 2, 3, 6, 12, 24 }.First(step => span.TotalHours / step <= maximum);
            var cursor = new DateTimeOffset(localStart.Year, localStart.Month, localStart.Day, localStart.Hour, 0, 0, localStart.Offset).AddHours(1);
            for (; cursor <= end; cursor = cursor.AddHours(1))
                if (cursor.Hour % hours == 0) ticks.Add((cursor, cursor.ToString("HH:mm", CultureInfo.InvariantCulture)));
        }
        else if (span <= TimeSpan.FromDays(75))
        {
            int days = new[] { 1, 2, 7, 14, 28 }.First(step => span.TotalDays / step <= maximum);
            var cursor = new DateTimeOffset(localStart.Date, localStart.Offset).AddDays(1);
            if (days >= 7)
                while (cursor.DayOfWeek != DayOfWeek.Monday) cursor = cursor.AddDays(1);
            for (; cursor <= end; cursor = cursor.AddDays(days))
                ticks.Add((cursor, cursor.ToString("d MMM", CultureInfo.InvariantCulture)));
        }
        else
        {
            int months = new[] { 1, 2, 3, 6, 12 }.First(step => span.TotalDays / 30.4 / step <= maximum);
            var cursor = new DateTimeOffset(localStart.Year, localStart.Month, 1, 0, 0, 0, localStart.Offset).AddMonths(1);
            for (; cursor <= end; cursor = cursor.AddMonths(1))
                if ((cursor.Month - 1) % months == 0)
                    ticks.Add((cursor, cursor.ToString(cursor.Month == 1 || span.TotalDays > 400 ? "MMM yyyy" : "MMM", CultureInfo.InvariantCulture)));
        }

        return ticks.ToArray();
    }

    public static OsuSpriteText Text(string value, float size, Colour4 colour, string weight = "Regular") => new()
    {
        Text = value,
        Font = new FontUsage(size: Math.Max(Visuals.AimModVisualStyle.MinReadableFontSize, size), weight: weight),
        Colour = colour,
    };

    public static TruncatingSpriteText Truncating(string value, float size, Colour4 colour, string weight = "Regular") => new()
    {
        Text = value,
        Font = new FontUsage(size: Math.Max(Visuals.AimModVisualStyle.MinReadableFontSize, size), weight: weight),
        Colour = colour,
    };
}
