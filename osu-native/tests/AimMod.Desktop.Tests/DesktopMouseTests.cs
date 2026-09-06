using NUnit.Framework;
using osu.Framework.Input.Handlers.Mouse;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class DesktopMouseTests
{
    [TestCase(0.25)]
    [TestCase(1)]
    [TestCase(8)]
    public void DesktopCoordinatesBypassScalingWithoutChangingSensitivity(double sensitivity)
    {
        using var mouse = new MouseHandler();
        mouse.Sensitivity.Value = sensitivity;
        mouse.UseRelativeMode.Value = true;

        AimModGame.UseDesktopMouseCoordinates([mouse]);

        Assert.Multiple(() =>
        {
            Assert.That(mouse.UseRelativeMode.Value, Is.False);
            Assert.That(mouse.Sensitivity.Value, Is.EqualTo(sensitivity));
        });
        // Subsequent sensitivity updates must not switch back to relative input.
        mouse.Sensitivity.Value = 2;
        Assert.That(mouse.UseRelativeMode.Value, Is.False);
    }
}
