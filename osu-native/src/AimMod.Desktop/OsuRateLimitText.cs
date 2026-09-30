namespace AimMod.Desktop;

public static class OsuRateLimitText
{
    public static string RetryHint(DateTimeOffset? retryAfter, DateTimeOffset? now = null)
    {
        if (retryAfter is not DateTimeOffset until)
            return "Try again in a minute.";

        double seconds = Math.Ceiling((until - (now ?? DateTimeOffset.UtcNow)).TotalSeconds);
        return seconds switch
        {
            <= 1 => "Try again now.",
            < 60 => $"Try again in {seconds:0} seconds.",
            _ => $"Try again in {Math.Ceiling(seconds / 60):0} minutes.",
        };
    }
}
