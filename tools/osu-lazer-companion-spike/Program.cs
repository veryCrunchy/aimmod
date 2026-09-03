// Compile-only architecture spike for hosting official osu!lazer gameplay.
// This intentionally uses an isolated game name and therefore isolated storage.

using osu.Framework;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Game;
using osu.Game.Screens;

string? beatmapArchive = getOption(args, "--beatmap");
string? replayArchive = getOption(args, "--replay");

if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine("Usage: osu-lazer-companion-spike [--beatmap map.osz] [--replay score.osr]");
    return;
}

var hostOptions = new HostOptions
{
    FriendlyGameName = "AimMod lazer companion spike",
    // Never contend with an installed osu!lazer instance for its IPC pipe.
    IPCPipeName = null,
};

using DesktopGameHost host = Host.GetSuitableDesktopHost("aimmod-lazer-companion-spike", hostOptions);
host.Run(new CompanionGame(beatmapArchive, replayArchive));

static string? getOption(string[] arguments, string name)
{
    int index = Array.IndexOf(arguments, name);
    return index >= 0 && index + 1 < arguments.Length ? Path.GetFullPath(arguments[index + 1]) : null;
}

internal partial class CompanionGame : OsuGame
{
    private readonly string? beatmapArchive;
    private readonly string? replayArchive;

    public CompanionGame(string? beatmapArchive, string? replayArchive)
    {
        this.beatmapArchive = beatmapArchive;
        this.replayArchive = replayArchive;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        // The stock client presents imported scores on its results screen. The
        // companion needs the stock native ReplayPlayer instead.
        ScoreManager.PresentImport = scores => PresentScore(scores.First().Value, ScorePresentType.Gameplay);

        _ = importRequestedFiles();
    }

    private async Task importRequestedFiles()
    {
        try
        {
            if (beatmapArchive != null)
                await Import(beatmapArchive).ConfigureAwait(false);

            if (replayArchive != null)
                await Import(replayArchive).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "AimMod companion import failed");
        }
    }
}
