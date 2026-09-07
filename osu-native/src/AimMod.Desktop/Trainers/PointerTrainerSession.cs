namespace AimMod.Desktop.Trainers;

public sealed record TrainerTarget(double X, double Y, int Number);

public sealed class PointerTrainerSession
{
    public TrainerSettings Settings { get; }
    public List<TrainerTarget> Targets { get; } = [];
    public List<double> Responses { get; } = [];
    public int Extras { get; private set; }
    public int NextNumber { get; private set; } = 1;
    public double CueTime { get; private set; }
    public double EndMs => Settings.Seconds * 1000;
    private readonly Random random;
    private double lastHit;

    public PointerTrainerSession(TrainerSettings settings, int? seed = null)
    {
        settings.Validate(); Settings = settings; random = seed is { } s ? new Random(s) : new Random();
        if (settings.Kind == TrainerKind.Reaction) CueTime = delay();
        else populate();
    }
    private double delay() => Settings.ReactionDelay switch
    {
        TrainerReactionDelay.Short => 700 + random.NextDouble()*900,
        TrainerReactionDelay.Long => 2500 + random.NextDouble()*2500,
        TrainerReactionDelay.Wide => 700 + random.NextDouble()*4300,
        _ => 1300 + random.NextDouble() * 1900,
    };
    private void populate()
    {
        Targets.Clear(); NextNumber = 1;
        int count = Settings.Kind == TrainerKind.Reading ? 4 : 1;
        for (int i = 0; i < count; i++)
        {
            // Separate quadrants keep numbered targets readable at narrow widths.
            double x = count == 1 ? .12 + random.NextDouble() * .76 : .15 + (i % 2) * .5 + random.NextDouble() * .15;
            double y = count == 1 ? .18 + random.NextDouble() * .64 : .22 + (i / 2) * .43 + random.NextDouble() * .1;
            Targets.Add(new(x, y, i + 1));
        }
        if (count > 1)
        {
            var positions = Targets.OrderBy(_ => random.Next()).ToArray();
            Targets.Clear(); Targets.AddRange(positions.Select((t, i) => t with { Number = i + 1 }));
        }
    }
    public bool Tap(double timeMs, int? number = null)
    {
        if (!double.IsFinite(timeMs) || timeMs < 0 || timeMs >= EndMs) return false;
        if (Settings.Kind == TrainerKind.Reaction)
        {
            if (timeMs < CueTime) { Extras++; CueTime = timeMs + delay(); return false; }
            Responses.Add(timeMs - CueTime); CueTime = timeMs + delay(); return true;
        }
        if (number != NextNumber) { Extras++; return false; }
        Responses.Add(timeMs - lastHit); lastHit = timeMs;
        Targets.RemoveAll(t => t.Number == number);
        NextNumber++;
        if (Targets.Count == 0) populate();
        return true;
    }
    public TrainerResult Result(DateTimeOffset now)
    {
        double[] sorted = Responses.Order().ToArray();
        double? median = sorted.Length == 0 ? null : sorted.Length % 2 == 0
            ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2];
        return new(Guid.NewGuid(), now, Settings, Responses.Count, Responses.Count, Responses.Count,
            Extras, 0, null, null, null, median);
    }
}
