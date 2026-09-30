namespace AimMod.Desktop.Visuals;

/// <summary>
/// Coalesces rapid query edits into one run. The owner drives it from Update with the current time.
/// </summary>
public sealed class QueryDebouncer
{
    public const double DefaultDelayMs = 280;

    private readonly Action<string> run;
    private readonly double delayMs;
    private string? pending;
    private string? lastRun;
    private double dueAt;

    public QueryDebouncer(Action<string> run, double delayMs = DefaultDelayMs)
    {
        ArgumentNullException.ThrowIfNull(run);
        this.run = run;
        this.delayMs = Math.Clamp(delayMs, 150, 400);
    }

    public bool IsPending => pending is not null;

    public void Submit(string text, double now)
    {
        pending = text;
        dueAt = now + delayMs;
    }

    /// <summary>Runs immediately and cancels any pending run, so an Enter after typing queries once.</summary>
    public void Commit(string text)
    {
        pending = null;
        execute(text, force: true);
    }

    public void Cancel() => pending = null;

    public void Update(double now)
    {
        if (pending is not { } text || now < dueAt)
            return;

        pending = null;
        execute(text, force: false);
    }

    private void execute(string text, bool force)
    {
        // A debounced run that lands on the text already queried is redundant.
        if (!force && string.Equals(lastRun, text, StringComparison.Ordinal))
            return;
        lastRun = text;
        run(text);
    }
}
