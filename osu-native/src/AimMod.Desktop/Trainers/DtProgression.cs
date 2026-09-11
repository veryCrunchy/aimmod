using System.Text.Json;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;

namespace AimMod.Desktop.Trainers;

public sealed record DtAttempt(Guid Id, DateTimeOffset At, int Speed, double Accuracy, int Misses, int Notes, int NextSpeed, string Reason);
public sealed record DtProgress(string MapKey, int Speed = 100, int CleanRuns = 0, bool Completed = false, DtAttempt[]? Attempts = null, double? AccuracyReference = null)
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

    // Keep a personal reference across speed changes. Old histories migrate from their
    // lowest recorded speed, so a failed high-speed run cannot lower the target.
    private static double? reference(DtProgress state)
    {
        if (state.AccuracyReference is { } value && double.IsFinite(value) && value is >= 80 and <= 100) return value;
        var history = state.History.Where(healthy).ToArray();
        if (history.Length == 0) return null;
        int lowestSpeed = history.Min(attempt => attempt.Speed);
        return median(history.Where(attempt => attempt.Speed == lowestSpeed).TakeLast(5).Select(attempt => attempt.Accuracy));
    }

    public static double? AccuracyTarget(DtProgress state) => reference(state) is { } value ? Math.Max(80, value - 2) : null;

    private static bool healthy(DtAttempt attempt) => attempt.Notes >= 20 && attempt.Speed is >= 100 and <= 150
        && double.IsFinite(attempt.Accuracy) && attempt.Accuracy is >= 80 and <= 100
        && attempt.Misses >= 0 && attempt.Misses <= attempt.Notes * .01;

    private static double median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
    }

    public static DtProgress Apply(DtProgress state, int playedSpeed, TrainerResult? result)
    {
        // Cancelled, assisted, duplicated and stale attempts never change the next speed.
        if (result is null || result.Assisted || playedSpeed != state.Speed || state.History.Any(a => a.Id == result.Id)) return state;
        if (state.Speed is < 100 or > 150 || result.Id == Guid.Empty || result.Notes < 20
            || result.Accuracy is not { } acc || !double.IsFinite(acc) || acc is < 0 or > 100
            || result.Misses < 0 || result.Misses > result.Notes) return state;

        double missRatio = (double)result.Misses / result.Notes;
        double? personalReference = reference(state);
        if (personalReference is null && acc >= 80 && missRatio <= .01) personalReference = acc;
        double target = Math.Max(80, (personalReference ?? 82) - 2);
        int step;
        string reason;
        bool supported = false;
        if (acc < 80 || missRatio > .06)
        {
            step = -5;
            reason = missRatio > .06 ? "Too many misses to build speed on this run." : "Get comfortable with this map's rhythm before adding speed.";
        }
        else if (missRatio > .03)
        {
            step = -3;
            reason = "The miss count rose. Slow down a little and regain control.";
        }
        else if (acc < target)
        {
            step = -Math.Clamp((int)Math.Ceiling((target - acc) / 2), 1, 5);
            reason = $"{acc:0.00}% accuracy is below your {target:0.00}% target.";
        }
        else if (missRatio > .01)
        {
            step = 0;
            reason = "Accuracy is on track. Repeat this speed and bring the misses down.";
        }
        else
        {
            supported = true;
            step = acc >= 96 ? 3 : acc >= 90 ? 2 : 1;
            if (acc - target < 1 || result.Misses > 0) step = 1;
            reason = $"{acc:0.00}% accuracy with {result.Misses} misses.";
        }

        int next = Math.Clamp(state.Speed + step, 100, 150);
        // Two supported attempts at full DT confirm the goal; lower rates advance after each run.
        int supportedRuns = supported && playedSpeed == 150 ? state.CleanRuns + 1 : 0;
        bool completed = state.Completed && next == 150 || supportedRuns >= 2;
        if (supportedRuns >= 2) supportedRuns = 0;
        if (supported && playedSpeed == 150)
            reason = completed ? "Full DT is within your accuracy target. Keep practising here or choose another map."
                : "On target at full DT. Repeat once more to confirm it.";
        else reason += next > playedSpeed ? $" Try {next}% next." : next < playedSpeed ? $" Step back to {next}%." : $" Stay at {next}%.";

        var attempt = new DtAttempt(result.Id, result.CompletedAt, playedSpeed, acc, result.Misses, result.Notes, next, reason);
        // Raise the reference only when several recent low-miss runs support it.
        // A single accuracy spike or a series of failed attempts cannot move the goalposts.
        var recent = state.History.Append(attempt).Where(a => a.Speed <= playedSpeed).TakeLast(5).ToArray();
        if (recent.Length >= 3 && recent.All(healthy))
            personalReference = Math.Max(personalReference ?? 80, median(recent.Select(a => a.Accuracy)));
        return state with { Speed = next, CleanRuns = supportedRuns, Completed = completed,
            AccuracyReference = personalReference, Attempts = state.History.Append(attempt).TakeLast(200).ToArray() };
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
        if (states.Length > 100 || states.Any(s => s is null || string.IsNullOrEmpty(s.MapKey) || s.MapKey.Length > 128 || s.Speed is < 100 or > 150 || s.CleanRuns is < 0 or > 1 || s.History.Length > 200
            || s.AccuracyReference is { } reference && (!double.IsFinite(reference) || reference is < 80 or > 100)))
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
