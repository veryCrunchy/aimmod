using System.Text.Json;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;

namespace AimMod.Desktop.Trainers;

public sealed record DtAttempt(Guid Id, DateTimeOffset At, int Speed, double Accuracy, int Misses, int Notes, int NextSpeed, string Reason);
public sealed record DtProgress(string MapKey, int Speed = 100, int CleanRuns = 0, bool Completed = false, DtAttempt[]? Attempts = null)
{
    [System.Text.Json.Serialization.JsonIgnore] public DtAttempt[] History => Attempts ?? [];
}

public static class DtProgression
{
    public static Mod[] Mods(int percent)
    {
        if (percent is < 100 or > 150) throw new ArgumentOutOfRangeException(nameof(percent));
        if (percent == 100) return [new OsuModNoFail()];
        var dt = new OsuModDoubleTime();
        dt.SpeedChange.Value = percent / 100d;
        return [new OsuModNoFail(), dt];
    }

    public static DtProgress Apply(DtProgress state, int playedSpeed, TrainerResult? result)
    {
        // Cancelled, assisted, duplicated and stale attempts never change the next speed.
        if (result is null || result.Assisted || playedSpeed != state.Speed || state.History.Any(a => a.Id == result.Id)) return state;
        if (state.Speed is < 100 or > 150 || result.Id == Guid.Empty || result.Notes < 20
            || result.Accuracy is not { } acc || !double.IsFinite(acc) || acc is < 0 or > 100 || result.Misses < 0) return state;
        int next = state.Speed, clean = 0;
        bool completed = state.Completed;
        string reason;
        double misses = (double)result.Misses / result.Notes;
        if (acc >= 98 && result.Misses == 0)
        {
            clean = state.CleanRuns + 1;
            if (clean >= 2)
            {
                if (next == 150) { completed = true; reason = "Two clean runs at full DT. Keep practising here or choose another map."; }
                else
                {
                    var previous = state.History.LastOrDefault();
                    int step = acc >= 99.5 && previous is { Accuracy: >= 99.5, Misses: 0 } && previous.Speed == playedSpeed ? 3 : 2;
                    next = Math.Min(150, next + step);
                    reason = $"Two clean runs. Try {next}% next.";
                }
                clean = 0;
            }
            else reason = "Clean run. Repeat this speed once more with at least 98% accuracy and no misses.";
        }
        else if (acc < 95 || misses > .01)
        {
            int step = acc < 90 || misses > .05 ? 5 : 3;
            next = Math.Max(100, next - step);
            reason = next == playedSpeed ? "Stay at normal speed and get comfortable with this map first."
                : $"Step back to {next}% and rebuild a clean run.";
        }
        else reason = "Stay at this speed. Aim for 98% accuracy with no misses before moving up.";
        var attempt = new DtAttempt(result.Id, result.CompletedAt, playedSpeed, acc, result.Misses, result.Notes, next, reason);
        return state with { Speed = next, CleanRuns = clean, Completed = completed && next == 150, Attempts = state.History.Append(attempt).TakeLast(200).ToArray() };
    }
}

public sealed class DtProgressStore(string path)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private async Task<DtProgress[]> read(CancellationToken token)
    {
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("DT practice history is too large to open.");
        var states = JsonSerializer.Deserialize<DtProgress[]>(await File.ReadAllTextAsync(path, token)) ?? throw new InvalidDataException("DT history could not be read.");
        if (states.Length > 100 || states.Any(s => s is null || string.IsNullOrEmpty(s.MapKey) || s.MapKey.Length > 128 || s.Speed is < 100 or > 150 || s.CleanRuns is < 0 or > 1 || s.History.Length > 200))
            throw new InvalidDataException("DT practice history could not be read.");
        return states;
    }
    public async Task<DtProgress> LoadAsync(string key, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try { return (await read(token)).FirstOrDefault(s => s.MapKey == key) ?? new(key); }
        finally { gate.Release(); }
    }
    public async Task SaveAsync(DtProgress state, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            var states = (await read(token)).Where(s => s.MapKey != state.MapKey).Append(state).TakeLast(100).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(states);
            if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("DT practice history is full.");
            await File.WriteAllBytesAsync(path + ".tmp", bytes, token);
            File.Move(path + ".tmp", path, true);
        }
        finally { gate.Release(); }
    }
}
