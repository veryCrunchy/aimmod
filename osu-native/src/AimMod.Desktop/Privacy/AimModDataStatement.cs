namespace AimMod.Desktop.Privacy;

public sealed record DataStatementSection(string Title, IReadOnlyList<string> Points);

/// <summary>
/// What AimMod reads, writes and shares. Shown in Settings and mirrored in docs/what-aimmod-reads.md;
/// every point must stay true of the code.
/// </summary>
public static class AimModDataStatement
{
    public static IReadOnlyList<DataStatementSection> Sections { get; } =
    [
        new("What AimMod reads", [
            "Your osu! files: beatmaps, replays, scores and skins, from a read-only copy of osu!lazer's database or osu!stable's files.",
            "Your osu! settings files, so practice and trainers use your own keys, offset and sensitivity.",
            "Nothing else on your computer. AimMod never reads osu!'s memory, never watches or records the game while you play, and never sends or simulates input. The only thing it checks about osu! itself is whether osu!stable is running, so it can wait until osu! is closed before writing collections.",
        ]),
        new("What AimMod writes into osu!", [
            "Beatmaps you choose to install, including generated practice sets, handed to osu! to import in the usual way.",
            "One collection, \"AimMod coaching\", with a backup kept before every change. osu!stable's collections are only written after osu! is closed.",
            "AimMod's own skin packages, if you install any. Other skins are never changed or removed.",
        ]),
        new("Practice", [
            "Adaptive practice and trainers run in AimMod's own player, not in osu!. Those plays are never submitted to osu! and are not saved as osu! scores.",
            "Generated practice sets are beatmaps installed into osu! when you choose to. They are unranked, so playing them never gives pp or leaderboard places.",
        ]),
        new("What AimMod shares", [
            "Nothing, unless you link an AimMod Hub account.",
            "Scores are shared only when you share them, or when you turn on automatic sharing. They say whether the play passed, and replay files and judgement analysis are only included if you choose to include them. Which key pressed each hit is never included.",
            "Completed trainer sessions sync to your Hub account while training sync is on (on by default once linked, and can be turned off in Account & sharing). Your keys, offset, file paths and devices never leave the app.",
            "Adaptive practice and trainer plays are never shared as scores.",
        ]),
    ];
}
