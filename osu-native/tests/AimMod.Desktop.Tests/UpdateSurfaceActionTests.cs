using AimMod.Desktop.Updates;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class UpdateSurfaceActionTests
{
    [Test]
    public void DownloadingOffersCancelWithProgress()
    {
        var state = new NativeUpdateState(NativeUpdateStage.Downloading, NativeUpdateChannel.Stable, "Downloading", "42% complete", "1.2.3", 42);
        var action = NativeUpdateSurface.ActionFor(state);
        Assert.Multiple(() =>
        {
            Assert.That(action.Label, Is.EqualTo("Cancel 42%"));
            Assert.That(action.Enabled, Is.True);
            Assert.That(NativeUpdateSurface.ProgressFraction(state), Is.EqualTo(0.42f).Within(0.001f));
        });
    }

    [Test]
    public void UnavailableExplainsWhyUpdatesAreOff()
    {
        var state = new NativeUpdateState(NativeUpdateStage.Unavailable, NativeUpdateChannel.Stable, "Updates unavailable", "Install AimMod.");
        var action = NativeUpdateSurface.ActionFor(state);
        Assert.Multiple(() =>
        {
            Assert.That(action.Enabled, Is.False);
            Assert.That(action.Tooltip, Does.Contain("installer"));
            Assert.That(NativeUpdateSurface.ProgressFraction(state), Is.Zero);
        });
    }

    [TestCase(-5, 0f)]
    [TestCase(150, 1f)]
    public void ProgressIsClamped(int progress, float expected) =>
        Assert.That(NativeUpdateSurface.ProgressFraction(
            new NativeUpdateState(NativeUpdateStage.Downloading, NativeUpdateChannel.Stable, "", "", null, progress)), Is.EqualTo(expected));
}
