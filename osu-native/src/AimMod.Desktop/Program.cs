using osu.Framework;
using osu.Framework.Platform;
using osu.Game;
using osu.Game.Rulesets.Osu;
using osu.Game.Scoring.Legacy;
using osu.Game.Screens.Play;
using AimMod.Osu.Worker;
using Velopack;

namespace AimMod.Desktop;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--worker"])
            return WorkerProtocolHost.RunConsoleAsync().GetAwaiter().GetResult();

        if (args is ["--probe"])
            return runProbe();

        if (ShouldRunVelopackBootstrap(args))
        {
            var app = VelopackApp.Build();
            if (OperatingSystem.IsWindows())
                app.OnAfterInstallFastCallback(_ => AimModProtocolRegistration.Refresh())
                   .OnAfterUpdateFastCallback(_ => AimModProtocolRegistration.Refresh());
            app.Run();
        }
        return runDesktop(args);
    }

    internal static bool ShouldRunVelopackBootstrap(string[] args) => args is not ["--worker"] and not ["--probe"];

    private static int runDesktop(string[] args)
    {
        AimModLaunchOptions launchOptions = AimModLaunchOptions.Parse(args);

        var options = new HostOptions
        {
            FriendlyGameName = "AimMod",
            IPCPipeName = "aimmod-native-shell",
        };

        using DesktopGameHost host = Host.GetSuitableDesktopHost("aimmod", options);
        var inbox = new AimModLinkInbox();
        using var channel = new IpcChannel<string[], string[]>(host);
        channel.MessageReceived += arguments => inbox.Accept(arguments) ? ["accepted"] : ["rejected"];
        // Install the receiver before binding: another process may connect during startup.
        if (!host.IsPrimaryInstance && launchOptions.DeepLink is not null)
        {
            if (TryForwardLinkAsync(channel, args, TimeSpan.FromSeconds(15)).GetAwaiter().GetResult())
                return 0;
            // Older instances have no receiver. Keep the link available in a review window.
        }
        AimModProtocolRegistration.Refresh();
        host.Run(new AimModGame(launchOptions) { LinkInbox = inbox });
        return 0;
    }

    internal static async Task<bool> TryForwardLinkAsync(IpcChannel<string[], string[]> channel, string[] args, TimeSpan timeout)
    {
        try
        {
            var reply = await channel.SendMessageWithResponseAsync(args).WaitAsync(timeout).ConfigureAwait(false);
            return reply is ["accepted"];
        }
        catch (Exception error) when (error is IOException or TimeoutException)
        {
            Console.Error.WriteLine($"AimMod link handoff failed: {error.Message}");
            return false;
        }
    }

    private static int runProbe()
    {
        bool hasExpectedBase = typeof(AimModGame).IsSubclassOf(typeof(OsuGameBase));
        bool hasStandardRuleset = typeof(OsuRuleset).Assembly.GetName().Name == "osu.Game.Rulesets.Osu";
        bool hasOfficialReplayPlayer = typeof(NativeReplayPlayer).IsSubclassOf(typeof(ReplayPlayer));
        bool hasOfficialReplayDecoder = typeof(ImportedBeatmapReplayDecoder).IsSubclassOf(typeof(LegacyScoreDecoder));
        bool excludesFullClient = typeof(AimModGame).Assembly.GetReferencedAssemblies()
                                                    .All(reference => reference.Name is not "osu.Desktop");

        Console.WriteLine($"AimModGame base: {typeof(AimModGame).BaseType?.FullName}");
        Console.WriteLine($"Standard ruleset: {typeof(OsuRuleset).FullName}");
        Console.WriteLine($"Official replay player: {typeof(NativeReplayPlayer).BaseType?.FullName}");
        Console.WriteLine($"Full osu! desktop excluded: {excludesFullClient}");

        bool valid = hasExpectedBase && hasStandardRuleset && hasOfficialReplayPlayer && hasOfficialReplayDecoder && excludesFullClient;
        Console.WriteLine($"Native replay wiring valid: {valid}");

        return valid ? 0 : 1;
    }
}
