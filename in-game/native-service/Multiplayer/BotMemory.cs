namespace AimMod.InGame.Multiplayer;

// What a side knows (BotBrain), shared between its bots like callouts: the latest sighting of each
// enemy (any teammate's eyes or ears), the gunfire it heard grouped by where it came from (and
// whether that was a decoy), the grenades it saw or heard land, and the decoys its bots called.
// Memory fades: a sighting counts for KnowMs, a sound for HeardMs, less sure as it ages.
sealed class TeamKnowledge
{
    public const long KnowMs = 6000, HeardMs = 4500;
    public readonly Dictionary<string, (double[] At, long T, bool Heard)> Enemies = new(StringComparer.Ordinal);
    public void Saw(string enemy, double[] at, long t, bool heard = false)
    {
        // A sound never overwrites a fresher sighting.
        if (heard && Enemies.TryGetValue(enemy, out var have) && !have.Heard && t - have.T < 1500) return;
        Enemies[enemy] = (at, t, heard);
    }
    public IEnumerable<(string Enemy, double[] At, long T, bool Heard)> Fresh(long now) => Fresh(now, null);
    // Fresh, without what `ignore` rejects (the sounds a bot took for a decoy).
    public IEnumerable<(string Enemy, double[] At, long T, bool Heard)> Fresh(long now, Func<string, bool>? ignore) =>
        Enemies.Where(kv => now - kv.Value.T <= (kv.Value.Heard ? HeardMs : KnowMs) && now >= kv.Value.T - 1000 && (ignore is null || !ignore(kv.Key)))
            .Select(kv => (kv.Key, kv.Value.At, kv.Value.T, kv.Value.Heard));
    // How sure the side still is (1 just now, 0 forgotten).
    public static double Confidence(long t, bool heard, long now) => Math.Clamp(1 - (now - t) / (double)(heard ? HeardMs : KnowMs), 0, 1);

    // Gunfire heard by the side, grouped by where it came from (BotEars).
    public readonly List<SoundSource> Sounds = [];
    // Grenades the side saw thrown or heard land: id -> where and when it came to rest.
    public readonly Dictionary<long, (double[] At, long RestT)> Grenades = new();
    public void Clear() { Enemies.Clear(); Sounds.Clear(); Grenades.Clear(); }
    // Long forgotten: out of memory altogether (a deathmatch never clears between rounds).
    public void Prune(long now)
    {
        foreach (var key in Enemies.Where(kv => now - kv.Value.T > 30_000).Select(kv => kv.Key).ToArray()) Enemies.Remove(key);
        foreach (var id in Grenades.Where(kv => now - kv.Value.RestT > 30_000).Select(kv => kv.Key).ToArray()) Grenades.Remove(id);
        Sounds.RemoveAll(s => now - s.LastT > 30_000);
    }
    public static string SoundKey(int id) => "sound#" + id;
}

// One place gunfire comes from, as heard: its shots (where and when), its bursts, the gun class,
// and what each bot that heard it makes of it.
sealed class SoundSource
{
    public int Id;
    public required string Weapon;
    public readonly List<(long T, double[] At)> Shots = [];
    public readonly List<int> Bursts = [];
    public long FirstT, LastT;
    public double[] First => Shots[0].At;
    public double[] Last => Shots[^1].At;
    public readonly HashSet<string> HeardBy = new(StringComparer.Ordinal);
    // A bot's verdict: decoy (with the reasons), or believed. A decoy called by a Normal or Hard bot is
    // told to the side; an easy bot doesn't listen.
    public readonly Dictionary<string, string> Verdicts = new(StringComparer.Ordinal);
    public string? CalledBy;
    public IReadOnlyList<string> Cues = [];
    public const long BurstGapMs = 350;

    public void Add(long t, double[] at)
    {
        if (Shots.Count > 0 && Shots.Any(s => s.T == t && Math.Abs(s.At[0] - at[0]) < 1 && Math.Abs(s.At[1] - at[1]) < 1)) return;
        if (Shots.Count == 0) FirstT = t;
        if (Shots.Count == 0 || t - LastT > BurstGapMs) Bursts.Add(1); else Bursts[^1]++;
        Shots.Add((t, at));
        if (Shots.Count > 128) Shots.RemoveAt(1);
        LastT = Math.Max(LastT, t);
    }
    // How far the shots wander from the first (a player moves; a grenade on the floor doesn't).
    public double Spread => Shots.Count == 0 ? 0 : Shots.Max(s => Math.Sqrt((s.At[0] - First[0]) * (s.At[0] - First[0]) + (s.At[1] - First[1]) * (s.At[1] - First[1])));
}
