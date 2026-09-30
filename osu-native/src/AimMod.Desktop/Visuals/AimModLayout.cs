using System.Net;
using System.Text.Json;

namespace AimMod.Desktop.Visuals;

public enum AimModSidebarMode
{
    Full,
    Rail,
}

/// <summary>Pure layout decisions so they can be tested without a window.</summary>
public static class AimModLayout
{
    public const float CompactWidth = 640;
    public const float RailWindowWidth = 900;
    public const float CompactNavigationHeight = 620;

    /// <summary>The sidebar collapses to an icon rail once page content would drop below a usable width.</summary>
    public static AimModSidebarMode SelectSidebarMode(float windowWidth) =>
        windowWidth > 0 && windowWidth < RailWindowWidth ? AimModSidebarMode.Rail : AimModSidebarMode.Full;

    public static float SidebarWidth(AimModSidebarMode mode) =>
        mode == AimModSidebarMode.Rail ? AimModVisualStyle.CompactSidebarWidth : AimModVisualStyle.SidebarWidth;

    public static bool IsCompact(float width) => width < CompactWidth;

    public static int ColumnsFor(float width, float minColumnWidth, int maxColumns)
    {
        if (width <= 0 || minColumnWidth <= 0)
            return 1;
        return Math.Clamp((int)(width / minColumnWidth), 1, Math.Max(1, maxColumns));
    }

    public static float ClampPanelWidth(float available, float preferred, float margin = 32) =>
        Math.Clamp(available - margin, 1, preferred);

    /// <summary>Tracks the last observed value so per-frame code only reassigns on change.</summary>
    public struct ChangeTracker<T> where T : IEquatable<T>
    {
        private T last;
        private bool hasValue;

        public bool Update(T value)
        {
            if (hasValue && last.Equals(value))
                return false;
            last = value;
            hasValue = true;
            return true;
        }

        public void Reset() => hasValue = false;
    }
}

/// <summary>Turns exceptions into a short, actionable message plus optional technical detail.</summary>
public static class AimModFriendlyError
{
    public static (string Message, string? Detail) Describe(Exception error, string action)
    {
        ArgumentNullException.ThrowIfNull(error);
        Exception root = error is AggregateException { InnerExceptions.Count: 1 } aggregate ? aggregate.InnerExceptions[0] : error;
        string message = root switch
        {
            OperationCanceledException => $"{action} was cancelled.",
            TimeoutException => $"{action} took too long. Check your connection or drive, then try again.",
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => $"{action} was rate limited. Wait a moment, then try again.",
            HttpRequestException => $"{action} failed because the server could not be reached.",
            UnauthorizedAccessException => $"{action} failed because AimMod does not have permission.",
            FileNotFoundException or DirectoryNotFoundException => $"{action} failed because a file is missing.",
            IOException => $"{action} failed while reading or writing files.",
            JsonException or FormatException or InvalidDataException => $"{action} failed because the data could not be read.",
            _ => $"{action} failed.",
        };
        string detail = root.Message.Trim();
        return (message, detail.Length == 0 || detail == message ? null : detail);
    }

    public static string Message(Exception error, string action) => Describe(error, action).Message;
}
