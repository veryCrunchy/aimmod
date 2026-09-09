using System.Reflection;
using AimMod.Desktop.ScoreHistory;
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
        call(header, "SetStableAccount", "Previous Name", true);
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
        call(header, "SetStableAccount", "Local Player", true);
        Assert.That(label(header), Does.Contain("Local Player"));
        call(header, "SetSessionState", new LazerSessionState(LazerSessionStatus.SignedIn, null, 1));
        call(header, "SetAccountUnavailable");
        Assert.That(label(header), Does.Contain("Local Player").And.Not.Contain("unavailable"));
    }

    [TestCase(LazerSessionStatus.Remembered, true)]
    [TestCase(LazerSessionStatus.Remembered, false)]
    [TestCase(LazerSessionStatus.SignedOut, true)]
    [TestCase(LazerSessionStatus.SignedOut, false)]
    [TestCase(LazerSessionStatus.SignedIn, true)]
    [TestCase(LazerSessionStatus.SignedIn, false)]
    public void StableLibraryRemainsConnectedRegardlessOfLazerStateAndDiscoveryOrder(LazerSessionStatus status, bool stableFirst)
    {
        using var header = createHeader();
        if (stableFirst) call(header, "SetStableAccount", "Local Player", true);
        call(header, "SetSessionState", new LazerSessionState(status, null, 1));
        if (!stableFirst) call(header, "SetStableAccount", "Local Player", true);
        Assert.That(label(header), Is.EqualTo("Local Player (osu!stable, local)"));
    }

    [Test]
    public void StableLibraryWithoutSavedUsernameIsStillConnectedLocally()
    {
        using var header = createHeader();
        call(header, "SetStableAccount", "", true);
        call(header, "SetSessionState", new LazerSessionState(LazerSessionStatus.Remembered, null, 1));
        Assert.That(label(header), Is.EqualTo("osu!stable connected (local)"));
        call(header, "SetAccountUnavailable");
        Assert.That(label(header), Is.EqualTo("osu!stable connected (local)"));
    }

    [Test]
    public void LazerOnlyExpiryIsExplicitAboutWhichClientExpired()
    {
        using var header = createHeader();
        call(header, "SetStableAccount", "", false);
        call(header, "SetSessionState", new LazerSessionState(LazerSessionStatus.Remembered, null, 1));
        Assert.That(label(header), Is.EqualTo("osu!lazer session expired"));
    }

    [TestCase(OsuClientDestination.Auto, true, true, true)]
    [TestCase(OsuClientDestination.Auto, true, false, false)]
    [TestCase(OsuClientDestination.Stable, true, true, false)]
    [TestCase(OsuClientDestination.Stable, true, false, false)]
    [TestCase(OsuClientDestination.Stable, false, true, true)]
    [TestCase(OsuClientDestination.Lazer, true, true, true)]
    public void AccountSelectionRespectsStablePreferenceAndRequiresVerifiedLazer(OsuClientDestination destination,
        bool stableInstalled, bool lazerVerified, bool expectedLazer)
        => Assert.That(AimModGame.UseLazerAccount(destination, stableInstalled,
            lazerVerified ? new OsuProfile(42, "Lazer Player", null, null, null) : null), Is.EqualTo(expectedLazer));

    [Test]
    public void SwitchingBackToStableRestoresItsPublicProfile()
    {
        using var header = createHeader();
        call(header, "SetStableAccount", "Stable Player", true);
        call(header, "SetPublicProfile", new OsuProfile(41, "Stable Player", null, null, null));
        call(header, "SetProfile", new OsuProfile(42, "Lazer Player", null, null, null));
        call(header, "SetProfilePreference", new object[] { null! });
        Assert.That(label(header), Is.EqualTo("Stable Player (public profile)"));
    }

    [TestCase(LazerSessionStatus.Remembered, true)]
    [TestCase(LazerSessionStatus.SignedOut, true)]
    [TestCase(LazerSessionStatus.Unavailable, true)]
    [TestCase(LazerSessionStatus.Remembered, false)]
    public void StableImportPopulatesTheActiveAccountAndSurvivesLazerStateChanges(LazerSessionStatus status, bool online)
    {
        using var game = new AimModGame(AimModLaunchOptions.Home);
        using var header = createHeader();
        using var http = new HttpClient();
        var history = new HubPublicAccountScoreHistoryService(http, new Uri("https://example.invalid/"), "Stable Player");
        var stable = new OsuProfile(41, "Stable Player", null, null, null);
        field("header").SetValue(game, header);
        field("stablePublicScoreHistoryService").SetValue(game, history);
        call(header, "SetStableAccount", stable.Username, true);
        invoke("applyStableProfile", stable, online);
        Assert.That(field("currentOsuProfile").GetValue(game), Is.SameAs(stable));
        invoke("applyLazerSessionState", new LazerSessionState(status, null, 1));
        Assert.Multiple(() =>
        {
            Assert.That(field("currentOsuProfile").GetValue(game), Is.SameAs(stable));
            Assert.That(field("accountScoreHistoryService").GetValue(game), Is.SameAs(history));
            Assert.That(label(header), Does.Contain("Stable Player").And.Not.Contain("expired"));
        });

        static FieldInfo field(string name) => typeof(AimModGame).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
        void invoke(string name, params object[] arguments) => typeof(AimModGame)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(game, arguments);
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
