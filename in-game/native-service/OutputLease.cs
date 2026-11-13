namespace AimMod.InGame;

// A handle-based lease survives async thread switches and covers preview instances
// sharing an IPC directory, regardless of their instance names or session.
static class OutputLease
{
    internal static FileStream? TryAcquire(string output)
    {
        try { return new FileStream(Path.Combine(output, "service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }
}
