using System.Text.Json;

namespace AimMod.Desktop.Coaching;

/// <summary>The date the statistics page compares from, kept per account so a baseline survives restarts.</summary>
public sealed class StatisticsComparisonStore(string path)
{
    private sealed record Saved(DateOnly? Since);

    public DateOnly? Load()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            return JsonSerializer.Deserialize<Saved>(File.ReadAllText(path))?.Since;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(DateOnly? since)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new Saved(since)));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Local midnight at the start of the date.</summary>
    public static DateTimeOffset StartOf(DateOnly date)
    {
        DateTime local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}
