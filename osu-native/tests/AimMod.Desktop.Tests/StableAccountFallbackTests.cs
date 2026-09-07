using System.Reflection;
using AimMod.Osu.Runtime;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class StableAccountFallbackTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void FailedLazerLookupShowsStablePublicProfileRegardlessOfArrivalOrder(bool publicArrivesFirst)
    {
        using var header = createHeader();
        call(header, "SetStableAccount", "Previous Name");
        call(header, "SetSessionState", new LazerSessionState(LazerSessionStatus.SignedIn, null, 1));
        var profile = new OsuProfile(42, "Current Name", null, null,
            new OsuProfileStatistics(123, null, 456, null, 50, 0, 0, 0, 0, 0));
        if (publicArrivesFirst) call(header, "SetPublicProfile", profile);
        call(header, "SetAccountUnavailable");
        if (!publicArrivesFirst) call(header, "SetPublicProfile", profile);
        Assert.That(label(header), Does.Contain("Current Name").And.Contain("123").And.Not.Contain("unavailable"));
    }

    [Test]
    public void VerifiedLazerProfileIsNotOverwrittenByLateStableLookup()
    {
        using var header = createHeader();
        call(header, "SetSessionState", new LazerSessionState(LazerSessionStatus.SignedIn, null, 1));
        call(header, "SetProfile", new OsuProfile(42, "Signed In Player", null, null, null));
        call(header, "SetPublicProfile", new OsuProfile(43, "Local Player", null, null, null));
        Assert.That(label(header), Is.EqualTo("Signed In Player"));
    }

    [Test]
    public void StableNameIsVisibleBeforeOnlineLookupAndAfterLazerFailure()
    {
        using var header = createHeader();
        call(header, "SetStableAccount", "Local Player");
        Assert.That(label(header), Does.Contain("Local Player"));
        call(header, "SetSessionState", new LazerSessionState(LazerSessionStatus.SignedIn, null, 1));
        call(header, "SetAccountUnavailable");
        Assert.That(label(header), Does.Contain("Local Player").And.Not.Contain("unavailable"));
    }

    private static Drawable createHeader()
    {
        var type = typeof(AimModGame).GetNestedType("HeaderBar", BindingFlags.NonPublic)!;
        var constructor = type.GetConstructors().Single();
        var bindableType = constructor.GetParameters()[0].ParameterType;
        object route = Enum.ToObject(bindableType.GetGenericArguments()[0], 0);
        object bindable = Activator.CreateInstance(bindableType, [route])!;
        Action noOp = () => { };
        return (Drawable)constructor.Invoke([bindable, noOp, noOp, noOp, noOp, noOp, noOp, noOp, noOp, noOp]);
    }

    private static void call(Drawable header, string method, params object[] args) =>
        header.GetType().GetMethod(method)!.Invoke(header, args);

    private static string label(Drawable header) => ((SpriteText)header.GetType()
        .GetField("sessionState", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(header)!).Text.ToString();
}
