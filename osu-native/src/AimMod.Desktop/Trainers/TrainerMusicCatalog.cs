namespace AimMod.Desktop.Trainers;

public static class TrainerMusicCatalog
{
    public static readonly IReadOnlyDictionary<string, string> Songs = new Dictionary<string, string>
    {
        ["midnight-pulse"] = "Midnight Pulse", ["mint-current"] = "Mint Current", ["afterglow"] = "Afterglow",
        ["moonlit-orbit"] = "Moonlit Orbit", ["neon-cascade"] = "Neon Cascade", ["velvet-horizon"] = "Velvet Horizon",
        ["mint-breaker"] = "Mint Breaker · breakbeat", ["night-drive"] = "Night Drive · synthwave",
        ["sidechain-city"] = "Sidechain City · garage", ["tidal-signal"] = "Tidal Signal · percussion",
    };
    public static readonly int[] Tempos = [90, 120, 150, 180, 210];
    public static bool IsSong(string name) => Songs.ContainsKey(name);
    public static string RandomSong(string? previous = null)
    {
        var choices = Songs.Keys.Where(s => s != previous).ToArray();
        return choices[Random.Shared.Next(choices.Length)];
    }
    public static string Asset(string name, int bpm)
    {
        if (!IsSong(name) || !Tempos.Contains(bpm)) throw new ArgumentOutOfRangeException(nameof(bpm));
        return $"training-{name}-{bpm}.ogg";
    }
}
