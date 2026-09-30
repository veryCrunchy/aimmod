using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class OsuRateLimitTextTests
{
    private static readonly DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void UnknownRetryAfterUsesGenericHint() =>
        Assert.That(OsuRateLimitText.RetryHint(null, now), Is.EqualTo("Try again in a minute."));

    [Test]
    public void ElapsedRetryAfterSaysNow() =>
        Assert.That(OsuRateLimitText.RetryHint(now.AddSeconds(-5), now), Is.EqualTo("Try again now."));

    [Test]
    public void ShortRetryAfterUsesSeconds() =>
        Assert.That(OsuRateLimitText.RetryHint(now.AddSeconds(12.2), now), Is.EqualTo("Try again in 13 seconds."));

    [Test]
    public void LongRetryAfterRoundsUpToMinutes() =>
        Assert.That(OsuRateLimitText.RetryHint(now.AddSeconds(125), now), Is.EqualTo("Try again in 3 minutes."));
}
