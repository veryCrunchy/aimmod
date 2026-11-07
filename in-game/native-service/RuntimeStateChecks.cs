namespace AimMod.InGame;

static class RuntimeStateChecks
{
    internal static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "aimmod-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using (var first = OutputLease.TryAcquire(folder))
            {
                if (first is null) throw new Exception("First output lease refused");
                using var duplicate = OutputLease.TryAcquire(Path.Combine(folder, "."));
                if (duplicate is not null) throw new Exception("Duplicate output lease accepted");
                var separate = Path.Combine(folder, "preview");
                Directory.CreateDirectory(separate);
                using var preview = OutputLease.TryAcquire(separate);
                if (preview is null) throw new Exception("Separate preview refused");
            }
            using var reopened = OutputLease.TryAcquire(folder);
            if (reopened is null) throw new Exception("Disposed lease blocks restart");
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("4 runtime state checks passed.");
    }
}
