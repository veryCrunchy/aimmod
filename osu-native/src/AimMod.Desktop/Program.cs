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
            VelopackApp.Build().Run();
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
        host.Run(new AimModGame(launchOptions));
        return 0;
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
