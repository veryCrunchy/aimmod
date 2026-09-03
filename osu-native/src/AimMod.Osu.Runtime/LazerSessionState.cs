namespace AimMod.Osu.Runtime;

public enum LazerSessionStatus
{
    Unavailable,
    SignedOut,
    Remembered,
    SignedIn,
}

public sealed record LazerSessionState(LazerSessionStatus Status, string? Username, long Revision);
