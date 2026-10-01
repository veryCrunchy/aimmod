using System.Text.Json;
using System.Text.RegularExpressions;

namespace AimMod.InGame;

/// <summary>
/// Adds a replay received from another player (lobby content transfer) to the
/// library. Format 2 only; the file is decoded completely before it is
/// written, and it never replaces an existing replay with different bytes.
/// </summary>
static class ReplayImport
{
    static readonly Regex IdPattern = new(@"^[A-Za-z0-9_-]{1,100}\z", RegexOptions.CultureInvariant);

    public static (string? Id, string? Scenario, string? Error) Import(string output, byte[] bytes)
    {
        if (bytes.Length is < 24 or > 8 * 1024 * 1024 || !ReplayFormat2.IsFormat2(bytes)) return (null, null, "unsupported-format");
        string id;
        try
        {
            using var stream = new MemoryStream(bytes);
            using var header = ReplayFormat2.ReadHeader(stream);
            id = header?.RootElement.GetProperty("id").GetString() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or EndOfStreamException) { return (null, null, "invalid-replay"); }
        if (!IdPattern.IsMatch(id)) return (null, null, "invalid-replay");
        NativeReplay replay;
        try { replay = ReplayFormat2.Decode(bytes, id); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or IndexOutOfRangeException or ArgumentException)
        { return (null, null, "invalid-replay"); }
        if (replay.Frames.Count < 2 || replay.Reason != "completed") return (null, null, "invalid-replay");
        var folder = Path.Combine(output, "replays");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, id + ".amreplay");
        if (File.Exists(path)) return File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes) ? (id, replay.Scenario, null) : (null, null, "replay-exists");
        var partial = path + ".import";
        File.WriteAllBytes(partial, bytes);
        File.Move(partial, path, false);
        return (id, replay.Scenario, null);
    }
}
