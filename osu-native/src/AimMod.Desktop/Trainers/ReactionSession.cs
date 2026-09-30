namespace AimMod.Desktop.Trainers;

public enum ReactionMode { Simple, Choice, GoNoGo, ChoiceGoNoGo }
public enum ReactionOutcome { Hit, Early, WrongKey, Missed, Withheld, FalseAlarm }
public sealed record ReactionTrial(double CueMs, double? ResponseMs, ReactionOutcome Outcome, int Key);
public sealed record ReactionSummary(int Correct, int Early, int WrongKey, int Missed, int Withheld, int FalseAlarms,
    double? MedianMs, double? Slow90Ms, double? FirstHalfMs, double? LastHalfMs, ReactionTrial[] Trials);

public sealed class ReactionSession
{
    public static string Name(ReactionMode mode) => mode switch
    {
        ReactionMode.Choice => "Choose the key", ReactionMode.GoNoGo => "GO / STOP",
        ReactionMode.ChoiceGoNoGo => "Key choice + GO / STOP", _ => "Simple reaction",
    };
    public TrainerSettings Settings { get; }
    private readonly Random random;
    public List<ReactionTrial> Trials { get; } = [];
    public double CueTime { get; private set; }
    public int RequiredKey { get; private set; }
    public bool NoGo { get; private set; }
    public double EndMs => CueTime < Settings.Seconds * 1000 ? Math.Max(Settings.Seconds * 1000, CueTime + Settings.ReactionWindowMs) : Settings.Seconds * 1000;
    public string LastFeedback { get; private set; } = "";
    public double FeedbackUntil { get; private set; }

    public ReactionSession(TrainerSettings settings, int? seed = null)
    {
        settings.Validate(); Settings = settings;
        random = new Random(seed ?? settings.PatternSeed);
        next(0);
    }
    private void next(double time)
    {
        double delay = Settings.ReactionDelay switch
        {
            TrainerReactionDelay.Short => 700 + random.NextDouble() * 900,
            TrainerReactionDelay.Long => 2500 + random.NextDouble() * 2500,
            TrainerReactionDelay.Wide => 700 + random.NextDouble() * 4300,
            _ => 1300 + random.NextDouble() * 1900,
        };
        CueTime = time + delay;
        RequiredKey = random.Next(2);
        NoGo = Settings.ReactionMode is ReactionMode.GoNoGo or ReactionMode.ChoiceGoNoGo && random.Next(3) == 0;
    }
    public void Advance(double time)
    {
        if (!double.IsFinite(time) || time < 0 || CueTime >= Settings.Seconds * 1000) return;
        if (time >= CueTime + Settings.ReactionWindowMs)
        {
            Trials.Add(new(CueTime, null, NoGo ? ReactionOutcome.Withheld : ReactionOutcome.Missed, RequiredKey));
            feedback(NoGo ? "Good hold" : "Cue missed", time);
            next(time);
        }
    }
    public bool Tap(double time, int key)
    {
        if (!double.IsFinite(time) || time < 0 || key is < 0 or > 1 || time >= EndMs) return false;
        double previousCue = CueTime;
        Advance(time);
        if (CueTime != previousCue) return false;
        if (time < CueTime)
        {
            Trials.Add(new(CueTime, null, ReactionOutcome.Early, key));
            feedback("Too early. Wait for the cue.", time); next(time); return false;
        }
        bool wrong = Settings.ReactionMode is ReactionMode.Choice or ReactionMode.ChoiceGoNoGo && key != RequiredKey;
        var outcome = NoGo ? ReactionOutcome.FalseAlarm : wrong ? ReactionOutcome.WrongKey : ReactionOutcome.Hit;
        double response = time - CueTime;
        Trials.Add(new(CueTime, response, outcome, key));
        feedback(NoGo ? "Hold on STOP cues" : wrong ? "Wrong key" : $"{response:0} ms", time);
        next(time);
        return outcome == ReactionOutcome.Hit;
    }
    private void feedback(string text, double time) { LastFeedback = text; FeedbackUntil = time + 550; }
    public ReactionSummary Summary()
    {
        var hits = Trials.Where(t => t.Outcome == ReactionOutcome.Hit).ToArray();
        var values = hits.Select(t => t.ResponseMs!.Value).Order().ToArray();
        int count(ReactionOutcome outcome) => Trials.Count(t => t.Outcome == outcome);
        return new(count(ReactionOutcome.Hit), count(ReactionOutcome.Early), count(ReactionOutcome.WrongKey), count(ReactionOutcome.Missed),
            count(ReactionOutcome.Withheld), count(ReactionOutcome.FalseAlarm), Median(values),
            values.Length == 0 ? null : values[(int)Math.Ceiling(values.Length * .9) - 1],
            Median(hits.Where(t => t.CueMs < Settings.Seconds * 500).Select(t => t.ResponseMs!.Value)),
            Median(hits.Where(t => t.CueMs >= Settings.Seconds * 500).Select(t => t.ResponseMs!.Value)), Trials.ToArray());
    }
    public static double? Median(IEnumerable<double> samples)
    {
        var values = samples.Order().ToArray();
        return values.Length == 0 ? null : (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2;
    }
    public TrainerResult Result(DateTimeOffset now)
    {
        var s = Summary();
        return new(Guid.NewGuid(), now, Settings, s.Correct + s.Missed + s.WrongKey + s.FalseAlarms + s.Withheld,
            s.Correct + s.Withheld, 0, s.Early + s.WrongKey + s.FalseAlarms, 0, null, null, null,
            s.MedianMs, Engine: TrainerResult.EngineFor(Settings), PlayedSeconds: Settings.Seconds, Reaction: s);
    }
}
