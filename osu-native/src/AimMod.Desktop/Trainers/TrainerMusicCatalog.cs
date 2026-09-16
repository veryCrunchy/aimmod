namespace AimMod.Desktop.Trainers;

public static class TrainerMusicCatalog
{
    public static readonly IReadOnlyDictionary<string, string> Songs = new Dictionary<string, string>
    {
        ["night-drive"] = "Last Bus",
        ["midnight-pulse"] = "One More",
        ["mint-current"] = "Out of Breath",
        ["afterglow"] = "Second Wind",
        ["moonlit-orbit"] = "Empty Streets",
        ["neon-cascade"] = "Wrong Turn",
        ["velvet-horizon"] = "Stay a While",
        ["mint-breaker"] = "Loose Change",
        ["sidechain-city"] = "Upstairs",
        ["tidal-signal"] = "Skipping Stones",
        ["glass-harbor"] = "Breakwater",
        ["copper-sky"] = "Backseat",
        ["aurora-circuit"] = "All the Way",
        ["signal-bloom"] = "Good Company",
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
