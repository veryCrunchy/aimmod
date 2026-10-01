using System.Text;
using System.Text.RegularExpressions;

namespace AimMod.InGame.Multiplayer;

// aimmod-session.txt: tells AimModCosmetics (CosmeticsScope.lua) that the local
// player is in an AimMod lobby, match or spectate session, and for which
// generated match scenario. Cosmetics apply only while this agrees with the
// game's own state. Format (UTF-8, no BOM, at most 1 KiB):
//   v=1
//   mode=lobby|match|spectate
//   scenario=<MatchScenario.Name, empty for lobby>
//   expires=<unix seconds>
static partial class SessionMarker
{
    public const string FileName = "aimmod-session.txt";
    public const int MaxBytes = 1024, LifetimeSeconds = 90, RefreshSeconds = 30, MaxAgeSeconds = 120;
    public static readonly string[] Modes = ["lobby", "match", "spectate"];
    [GeneratedRegex(@"^AimMod Match - .{1,80} - [0-9a-f]{8}$")] private static partial Regex ScenarioPattern();

    public static bool ValidScenario(string? name) => name is not null && !name.Any(char.IsControl) && ScenarioPattern().IsMatch(name);

    // The marker text, or null when it must not be written (bad mode or scenario).
    public static string? Format(string mode, string? scenario, long expires)
    {
        if (!Modes.Contains(mode)) return null;
        var name = mode == "lobby" ? "" : scenario;
        if (mode != "lobby" && !ValidScenario(name)) return null;
        var text = "v=1\nmode=" + mode + "\nscenario=" + name + "\nexpires=" + expires + "\n";
        return Encoding.UTF8.GetByteCount(text) <= MaxBytes ? text : null;
    }

    public sealed record Parsed(string Mode, string Scenario, long Expires);

    // The reader's rules (CosmeticsScope.parseMarker and decide): version 1, a known mode,
    // a whole-number expiry with now < expires <= now + 120, and for match and spectate
    // a generated match scenario name. Used by the checks to keep writer and reader in step.
    public static Parsed? Read(string? text, long now)
    {
        if (text is null || text.Length == 0 || Encoding.UTF8.GetByteCount(text) > MaxBytes) return null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', '\r'))
        {
            var eq = line.IndexOf('=');
            if (eq <= 0 || !line[..eq].All(char.IsAsciiLetter)) continue;
            fields[line[..eq]] = line[(eq + 1)..];
        }
        if (fields.GetValueOrDefault("v") != "1") return null;
        var mode = fields.GetValueOrDefault("mode") ?? "";
        if (!Modes.Contains(mode)) return null;
        if (!long.TryParse(fields.GetValueOrDefault("expires"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var expires)) return null;
        if (expires <= now || expires > now + MaxAgeSeconds) return null;
        var scenario = fields.GetValueOrDefault("scenario") ?? "";
        if (mode != "lobby" && !ValidScenario(scenario)) return null;
        return new Parsed(mode, scenario, expires);
    }
}
