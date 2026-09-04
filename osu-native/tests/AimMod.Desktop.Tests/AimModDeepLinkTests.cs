using NUnit.Framework;
using osu.Framework.Platform;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class AimModDeepLinkTests
{
    [TestCase("aimmod-osu://beatmapsets/1", 1)]
    [TestCase("aimmod-osu://beatmapsets/2147483647", int.MaxValue)]
    public void StartupSelectsExactSet(string value, int expected)
    {
        var options = AimModLaunchOptions.Parse([value]);
        Assert.That(options.Error, Is.Null);
        Assert.That(options.Replay, Is.Null);
        Assert.That(options.DeepLink?.BeatmapSetId, Is.EqualTo(expected));
    }

    [TestCase("osuskins", "3sXe0RR", "osuskins-net")]
    [TestCase("osuck", "1234-skin_name", "skins-osuck-net")]
    public void StartupSelectsExactProviderAndSource(string provider, string id, string expected)
    {
        var options = AimModLaunchOptions.Parse([$"aimmod-osu://skins/{provider}/{id}"]);
        Assert.That(options.Error, Is.Null);
        Assert.That(options.DeepLink?.ProviderId, Is.EqualTo(expected));
        Assert.That(options.DeepLink?.SourceId, Is.EqualTo(id));
    }

    [TestCase("")]
    [TestCase("https://beatmapsets/1")]
    [TestCase("aimmod-osu:beatmapsets/1")]
    [TestCase("aimmod-osu://beatmapsets/0")]
    [TestCase("aimmod-osu://beatmapsets/01")]
    [TestCase("aimmod-osu://beatmapsets/-1")]
    [TestCase("aimmod-osu://beatmapsets/+1")]
    [TestCase("aimmod-osu://beatmapsets/2147483648")]
    [TestCase("aimmod-osu://beatmapsets/18446744073709551615")]
    [TestCase("aimmod-osu://beatmapsets/1.0")]
    [TestCase("aimmod-osu://beatmapsets/1/")]
    [TestCase("aimmod-osu://beatmapsets/1?download=true")]
    [TestCase("aimmod-osu://beatmapsets/1#2")]
    [TestCase("aimmod-osu://beatmapsets/%31")]
    [TestCase("aimmod-osu://beatmapsets/2/../1")]
    [TestCase("aimmod-osu://beatmapsets@evil/1")]
    [TestCase("aimmod-osu://beatmapsets:80/1")]
    [TestCase("aimmod-osu://skins/other/123")]
    [TestCase("aimmod-osu://skins/osuskins/123456")]
    [TestCase("aimmod-osu://skins/osuskins/12345678")]
    [TestCase("aimmod-osu://skins/osuskins/123-567")]
    [TestCase("aimmod-osu://skins/osuck/../123")]
    [TestCase("aimmod-osu://skins/osuck/%2f")]
    [TestCase("aimmod-osu://skins/osuck/a\\b")]
    [TestCase("aimmod-osu://skins/osuck/a\n")]
    [TestCase("aimmod-osu://skins/osuck/")]
    public void RejectsInvalidLinks(string value)
    {
        Assert.That(AimModDeepLink.TryParse(value, out _), Is.False);
        Assert.That(AimModLaunchOptions.Parse([value]).DeepLink, Is.Null);
    }

    [Test]
    public void RejectsMixedLaunchModesAndOversizedSlugs()
    {
        Assert.That(AimModLaunchOptions.Parse(["aimmod-osu://beatmapsets/1", "--replay", "a.osr"]).Error, Is.Not.Null);
        Assert.That(AimModDeepLink.TryParse("aimmod-osu://skins/osuck/" + new string('a', 81), out _), Is.False);
        Assert.That(AimModLaunchOptions.Parse([]), Is.EqualTo(AimModLaunchOptions.Home));
    }

    [Test]
    public void InboxRetainsLinksUntilStartupIsReadyAndRejectsOtherCommands()
    {
        var inbox = new AimModLinkInbox();
        Assert.That(inbox.Accept(["aimmod-osu://beatmapsets/12"]), Is.True);
        Assert.That(inbox.Accept(["--worker"]), Is.False);
        Assert.That(inbox.Accept(["aimmod-osu://skins/osuskins/3sXe0RR"]), Is.True);
        Assert.That(inbox.TryTake(out var first), Is.True);
        Assert.That(first?.BeatmapSetId, Is.EqualTo(12));
        Assert.That(inbox.TryTake(out var second), Is.True);
        Assert.That(second?.SourceId, Is.EqualTo("3sXe0RR"));
        Assert.That(inbox.TryTake(out _), Is.False);
    }

    [Test]
    public async Task FrameworkPipeAcknowledgesAndQueuesLinkBeforeUiStartup()
    {
        string name = "aimmod-link-test-" + Guid.NewGuid().ToString("N");
        using var primary = new NamedPipeIpcProvider(name);
        using var secondary = new NamedPipeIpcProvider(name);
        var inbox = new AimModLinkInbox();
        primary.MessageReceived += message => new IpcMessage
        {
            Type = typeof(string[]).AssemblyQualifiedName!,
            Value = new[] { inbox.Accept(message.Value as string[]) ? "accepted" : "rejected" },
        };
        Assert.That(primary.Bind(), Is.True);
        Assert.That(secondary.Bind(), Is.False);
        var response = await secondary.SendMessageWithResponseAsync(new IpcMessage
        {
            Type = typeof(string[]).AssemblyQualifiedName!,
            Value = new[] { "aimmod-osu://beatmapsets/123" },
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(response?.Value, Is.EqualTo(new[] { "accepted" }));
        Assert.That(inbox.TryTake(out var link), Is.True);
        Assert.That(link?.BeatmapSetId, Is.EqualTo(123));
    }

    [Test]
    public void RegistrationQuotesPathsAndPassesOneUri()
    {
        Assert.That(AimModProtocolRegistration.WindowsCommand(@"C:\My Apps\AimMod.exe"),
            Is.EqualTo("\"C:\\My Apps\\AimMod.exe\" \"%1\""));
        string desktop = AimModProtocolRegistration.LinuxDesktopEntry("/home/user/My Apps/100%/AimMod.AppImage");
        Assert.That(desktop, Does.Contain("Exec=\"/home/user/My Apps/100%%/AimMod.AppImage\" %u\n"));
        Assert.That(desktop, Does.Contain("MimeType=x-scheme-handler/aimmod-osu;"));
        Assert.That(AimModProtocolRegistration.LinuxDesktopEntry("/a\"b/AimMod"),
            Does.Contain("""Exec="/a\\"b/AimMod" %u"""));
        Assert.That(AimModProtocolRegistration.LinuxDesktopEntry("/a\\b/$x`y/AimMod"),
            Does.Contain("""Exec="/a\\\\b/\\$x\\`y/AimMod" %u"""));
    }

    [Test]
    public async Task OldOrUnresponsivePrimaryDoesNotConsumeStartupLink()
    {
        string[] args = ["aimmod-osu://beatmapsets/123"];
        using var channel = new IpcChannel<string[], string[]>(new UnresponsiveHost());
        Assert.That(await Program.TryForwardLinkAsync(channel, args, TimeSpan.FromMilliseconds(30)), Is.False);
        Assert.That(AimModLaunchOptions.Parse(args).DeepLink?.BeatmapSetId, Is.EqualTo(123));
    }

    private sealed class UnresponsiveHost : IIpcHost
    {
        public event Func<IpcMessage, IpcMessage?>? MessageReceived { add { } remove { } }
        public Task SendMessageAsync(IpcMessage message) => Task.CompletedTask;
        public Task<IpcMessage?> SendMessageWithResponseAsync(IpcMessage message) => new TaskCompletionSource<IpcMessage?>().Task;
    }
}
