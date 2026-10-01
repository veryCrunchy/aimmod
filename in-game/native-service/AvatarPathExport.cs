using System.Globalization;
using System.Text;

namespace AimMod.InGame;

/// <summary>
/// Offline avatar spike (in-game/docs/game-modes.md, phase 0): turns a recorded
/// run's camera into avatar-test-path.tsv, which AimModSteam's avatar test
/// (avatar_test=1) plays on a real avatar bot in the scenario the run was
/// recorded in. No network and no game command are involved.
/// Usage: --export-avatar-path &lt;replay id&gt; [--output &lt;AimMod folder&gt;]
/// </summary>
static class AvatarPathExport
{
    public const string FileName = "avatar-test-path.tsv", Header = "AIMMOD_AVATAR_PATH_1";
    public const double RateHz = 30;
    public const int MaxRows = 40000; // the bridge's AvatarPath::MaxRows

    static string Esc(string s) => Uri.EscapeDataString(s);
    static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The path text: camera position, pitch and yaw at 30 Hz from the replay's own sampling.</summary>
    public static string Build(NativeReplay replay)
    {
        var text = new StringBuilder();
        text.Append(Header).Append('\n');
        text.Append("meta\t").Append(Esc(replay.Scenario)).Append('\t').Append(Esc(replay.MapName ?? "")).Append('\t').Append(N(replay.MapScale is > 0 ? replay.MapScale.Value : 1)).Append('\n');
        var rows = (int)Math.Min(MaxRows, Math.Floor(replay.Duration * RateHz) + 1);
        for (var i = 0; i < rows; i++)
        {
            var t = i / RateHz;
            var c = NativeReplayPlayback.Sample(replay, t).Camera;
            text.Append("p\t").Append((long)Math.Round(t * 1000)).Append('\t').Append(N(c[0])).Append('\t').Append(N(c[1])).Append('\t').Append(N(c[2]))
                .Append('\t').Append(N(Math.Clamp(c[3], -90, 90))).Append('\t').Append(N(c[4])).Append('\n');
        }
        return text.ToString();
    }

    public static int Run(string output, string id)
    {
        var replay = new ReplayCatalog(output).Read(id);
        if (replay is null || replay.Frames.Count < 2) { Console.Error.WriteLine("Replay not found or unreadable."); return 1; }
        var path = Path.Combine(output, FileName);
        AtomicFile.WriteText(path, Build(replay));
        Console.WriteLine($"Wrote {FileName}: {replay.Scenario}, {replay.Duration:0.0} s. Load that scenario in freeplay with AimModSteam's avatar_test=1 to watch a bot follow it.");
        return 0;
    }
}
