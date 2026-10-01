using System.Text;

namespace AimMod.InGame;

// Replace a private file without exposing a torn write to the game or UI.
// The temporary name is unique, so concurrent writers never share a ".next".
static class AtomicFile
{
    static readonly UTF8Encoding Utf8 = new(false);
    internal static void WriteText(string path, string text) => WriteBytes(path, Utf8.GetBytes(text));
    internal static void WriteBytes(string path, ReadOnlySpan<byte> bytes, bool durable = false)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); if (durable) file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
    // Remove a published handoff file only if it still holds this process's value.
    internal static void DeleteIfContent(string path, string expected)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length <= 4096 && File.ReadAllText(path) == expected) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
