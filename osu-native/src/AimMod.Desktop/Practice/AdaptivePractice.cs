using System.Text.Json;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Types;

namespace AimMod.Desktop.Practice;

/// <summary>A top-level object reduced to what section splitting needs.</summary>
public sealed record AdaptiveObject(double StartMs, double EndMs, bool NewCombo);

/// <summary>A contiguous run of the original map's objects. Times are in map time, never playback time.</summary>
public sealed record AdaptiveSection(int Index, double StartMs, double EndMs, int FirstObject, int ObjectCount)
{
    public int LastObject => FirstObject + ObjectCount - 1;
}

/// <summary>
/// Speed is a fraction of the target playback rate; 1 is the map at its played speed. A section is mastered after
/// a streak of clean clears at full speed; the streak it needs grows with how often it has failed.
/// </summary>
public sealed record AdaptiveSectionProgress(double Speed = 1, int Fails = 0, int ConsecutiveFails = 0, int Clears = 0,
    bool Tried = false, bool ClearedAtFullSpeed = false, int CleanStreak = 0, double? LastFailMs = null,
    double? SpotStartMs = null, double? SpotEndMs = null, int SpotClears = 0,
    int Loops = 0, bool Parked = false, DateTimeOffset? SolidAt = null, double ReviewDays = 0, bool Review = false)
{
    public bool HasSpot => SpotStartMs is not null && SpotEndMs is not null;

    /// <summary>Still to drill this session: not solid yet, or solid but due for a review.</summary>
    public bool NeedsWork => !ClearedAtFullSpeed || Review;
}

/// <param name="Leniency">The player's usual share of 100s and 50s among their hits, from the play being practised.</param>
public sealed record AdaptivePracticeProgress(string MapKey, string Title, string Difficulty, double TargetRate,
    AdaptiveSectionProgress[] Sections, DateTimeOffset UpdatedAt, int Attempts = 0, int Fails = 0,
    DateTimeOffset? CompletedAt = null, int? ConnectTo = null, double Leniency = AdaptivePracticeCoach.DefaultLeniency,
    int[]? Focus = null)
{
    public int FullSpeedSections => Sections.Count(s => s.ClearedAtFullSpeed);
    public bool Completed => CompletedAt is not null;
    public double SlowestSpeed => Sections.Length == 0 ? 1 : Sections.Min(s => s.Speed);
}

/// <summary>A review is one full-speed run of a section that was solid in an earlier session.</summary>
public enum AdaptiveDrillKind { Section, Spot, Connect, FullRun, Review }

/// <summary>
/// How the focused part of an attempt went. <see cref="Notes"/> are hits judged 300/100/50 (circles, slider heads,
/// spinners); <see cref="Offbeat"/> of them were 100s or 50s. Combo breaks are misses and slider breaks.
/// </summary>
public sealed record AdaptiveResult(int ComboBreaks, int Notes, int Offbeat, double Accuracy, IReadOnlyList<double> MissTimes, int Objects);

/// <summary>Clean speeds up, shaky holds (two in a row below full speed also speed up), struggled slows down.</summary>
public enum AdaptiveGrade { Clean, Shaky, Struggled }

/// <summary>
/// One attempt: the objects from <see cref="StartMs"/> to <see cref="EndMs"/> in map time, played at one constant
/// speed. Sections are inclusive indices; a spot drill lies inside a single section.
/// </summary>
public sealed record AdaptiveDrill(AdaptiveDrillKind Kind, int FirstSection, int LastSection, double StartMs, double EndMs, double Speed)
{
    public int Focus => Kind == AdaptiveDrillKind.Connect ? LastSection : FirstSection;
}

public enum AdaptiveAttemptOutcome { Failed, ReachedEnd, Quit }

public static class AdaptivePracticeSections
{
    public const double TargetMs = 10_000, MinimumMs = 5_000, MaximumMs = 18_000;
    private const double hard_gap_ms = 3_000, phrase_gap_ms = 400;

    public static IReadOnlyList<AdaptiveSection> Split(IBeatmap beatmap) => Split(
        beatmap.HitObjects.Select(o => new AdaptiveObject(o.StartTime, o.GetEndTime(), o is IHasCombo { NewCombo: true })).ToArray(),
        beatmap.Breaks.Select(b => (b.StartTime, b.EndTime)).ToArray());

    /// <summary>
    /// Cuts at musical boundaries: always at breaks and long gaps, otherwise at the first new combo or
    /// phrase gap after the target length. A section never runs past the maximum length.
    /// </summary>
    public static IReadOnlyList<AdaptiveSection> Split(IReadOnlyList<AdaptiveObject> objects,
        IReadOnlyList<(double Start, double End)>? breaks = null)
    {
        breaks ??= [];
        var cuts = new List<int> { 0 };
        for (int i = 1; i < objects.Count; i++)
        {
            double length = objects[i].StartMs - objects[cuts[^1]].StartMs;
            double previousEnd = Math.Max(objects[i - 1].StartMs, objects[i - 1].EndMs);
            double gap = objects[i].StartMs - previousEnd;
            bool hard = gap >= hard_gap_ms || breaks.Any(b => b.Start >= previousEnd - 1 && b.End <= objects[i].StartMs + 1);
            bool phrase = length >= TargetMs && (objects[i].NewCombo || gap >= phrase_gap_ms);
            if (hard || phrase || length >= MaximumMs) cuts.Add(i);
        }
        var sections = new List<AdaptiveSection>();
        for (int c = 0; c < cuts.Count; c++)
        {
            int first = cuts[c], last = (c + 1 < cuts.Count ? cuts[c + 1] : objects.Count) - 1;
            if (last < first) continue;
            sections.Add(new(sections.Count, objects[first].StartMs, objects.Skip(first).Take(last - first + 1).Max(o => Math.Max(o.StartMs, o.EndMs)), first, last - first + 1));
        }
        // A short tail is too little to practise on its own; it joins the previous section.
        if (sections.Count > 1 && sections[^1].EndMs - sections[^1].StartMs < MinimumMs)
        {
            var tail = sections[^1];
            var previous = sections[^2];
            sections[^2] = previous with { EndMs = tail.EndMs, ObjectCount = previous.ObjectCount + tail.ObjectCount };
            sections.RemoveAt(sections.Count - 1);
        }
        return sections;
    }
}

/// <summary>
/// Schedules short drills instead of replaying the rest of the map. The first section that is not yet mastered is
/// drilled on its own at one constant speed: fails slow it down, comfortable clears speed it up, and at full speed
/// it needs a streak of clean clears that grows with its fails. A section cleared with a few misses close together,
/// or failed twice at the same place, gets a spot drill around that place first. A newly mastered section is
/// connected to the previous one, and once every section is mastered the map is played from the start.
/// </summary>
public static class AdaptivePracticeCoach
{
    public const double MinimumSpeed = .5, Step = .05, MaximumDrop = .15;
    /// <summary>Misses within this window are one spot; a spot drill is never shorter than the minimum.</summary>
    public const double SpotWindowMs = 4_000, SpotMinimumMs = 2_500, SpotLeadMs = 1_500, SpotTailMs = 1_000;
    public const int SpotClearsNeeded = 2;
    /// <summary>Tries on one section before it is parked and the others come first, so a mind block can settle.</summary>
    public const int LoopCap = 8;
    /// <summary>Days before a section that took work is reviewed; each clean review doubles it.</summary>
    public const double FirstReviewDays = 1;
    /// <summary>Share of 100s and 50s assumed when the practised play has no hit counts.</summary>
    public const double DefaultLeniency = .15;

    public static AdaptivePracticeProgress Create(string key, string title, string difficulty, double targetRate, int sections, DateTimeOffset now) =>
        new(key, title, difficulty, targetRate, Enumerable.Repeat(new AdaptiveSectionProgress(), sections).ToArray(), now);

    /// <summary>The player keeps their saved speeds unless the map now splits differently.</summary>
    /// <remarks>
    /// A new session clears parking, and sections that took work and have been solid for their review interval are
    /// due for one full-speed review before anything else.
    /// </remarks>
    public static AdaptivePracticeProgress Resume(AdaptivePracticeProgress? saved, string key, string title, string difficulty,
        double targetRate, int sections, DateTimeOffset now)
    {
        if (saved is null || saved.MapKey != key || saved.Sections.Length != sections || Math.Abs(saved.TargetRate - targetRate) >= .001)
            return Create(key, title, difficulty, targetRate, sections, now);
        var fresh = saved.Sections.Select(s => s with
        {
            Loops = 0, Parked = false,
            Review = s.Review || s.ClearedAtFullSpeed && s.SolidAt is { } solid && s.ReviewDays > 0 && (now - solid).TotalDays >= s.ReviewDays,
        }).ToArray();
        return saved with
        {
            Title = title, Difficulty = difficulty, ConnectTo = null,
            Sections = fresh.SequenceEqual(saved.Sections) ? saved.Sections : fresh,
        };
    }

    /// <summary>Clean full-speed clears in a row a section needs: one, plus one for every two fails, up to four.</summary>
    public static int RequiredStreak(AdaptiveSectionProgress section) => 1 + Math.Min(3, section.Fails / 2);

    /// <summary>The next attempt. <paramref name="start"/> optionally puts the first drill on a chosen section.</summary>
    public static AdaptiveDrill Next(AdaptivePracticeProgress progress, IReadOnlyList<AdaptiveSection> sections, int? start = null)
    {
        int last = sections.Count - 1;
        if (start is { } chosen && chosen >= 0 && chosen <= last && !progress.Sections[chosen].ClearedAtFullSpeed)
            return drill(progress, sections, chosen);
        if (progress.ConnectTo is { } to && to > 0 && to <= last)
            return new(AdaptiveDrillKind.Connect, to - 1, to, sections[to - 1].StartMs, sections[to].EndMs,
                Math.Min(progress.Sections[to - 1].Speed, progress.Sections[to].Speed));
        int focus = Order(progress).FirstOrDefault(-1);
        return focus < 0
            ? new(AdaptiveDrillKind.FullRun, 0, last, sections[0].StartMs, sections[last].EndMs, 1)
            : drill(progress, sections, focus);
    }

    /// <summary>
    /// Sections still to drill, in the order they come: reviews first, then the chosen focus sections (the ones that
    /// cost the play most), then the map in order. Parked sections wait until nothing else is left.
    /// </summary>
    public static IEnumerable<int> Order(AdaptivePracticeProgress progress)
    {
        var all = progress.Sections;
        var focus = (progress.Focus ?? []).Where(i => i >= 0 && i < all.Length);
        return Enumerable.Range(0, all.Length).Where(i => all[i].Review)
            .Concat(focus).Concat(Enumerable.Range(0, all.Length)).Distinct()
            .Where(i => all[i].NeedsWork).OrderBy(i => all[i].Parked);
    }

    private static AdaptiveDrill drill(AdaptivePracticeProgress progress, IReadOnlyList<AdaptiveSection> sections, int focus)
    {
        var state = progress.Sections[focus];
        if (state.ClearedAtFullSpeed && state.Review)
            return new(AdaptiveDrillKind.Review, focus, focus, sections[focus].StartMs, sections[focus].EndMs, 1);
        return state.HasSpot
            ? new(AdaptiveDrillKind.Spot, focus, focus, state.SpotStartMs!.Value, state.SpotEndMs!.Value, state.Speed)
            : new(AdaptiveDrillKind.Section, focus, focus, sections[focus].StartMs, sections[focus].EndMs, state.Speed);
    }

    /// <summary>Whether the player may skip this drill. The full run is the final check and cannot be skipped.</summary>
    public static bool CanSkip(AdaptiveDrill drill) => drill.Kind != AdaptiveDrillKind.FullRun;

    /// <summary>
    /// The player marks the drilled part as done: a section becomes solid at full speed (its spot is dropped),
    /// and a connect run is passed over. A section that is not really solid fails again in the full run.
    /// </summary>
    public static AdaptivePracticeProgress Skip(AdaptivePracticeProgress progress, AdaptiveDrill drill, DateTimeOffset now)
    {
        if (!CanSkip(drill)) return progress;
        if (drill.Kind == AdaptiveDrillKind.Connect) return progress with { ConnectTo = null, UpdatedAt = now };
        var all = progress.Sections.ToArray();
        all[drill.Focus] = all[drill.Focus] with
        {
            Speed = 1, Tried = true, ClearedAtFullSpeed = true, ConsecutiveFails = 0, CleanStreak = 0,
            SpotStartMs = null, SpotEndMs = null, SpotClears = 0, Loops = 0, Parked = false, Review = false,
            SolidAt = all[drill.Focus].SolidAt ?? now,
        };
        return progress with { Sections = all, ConnectTo = null, UpdatedAt = now };
    }

    /// <summary>A fail anywhere in the attempt slows that section; a repeat fail at the same place marks a spot.</summary>
    public static AdaptivePracticeProgress Fail(AdaptivePracticeProgress progress, IReadOnlyList<AdaptiveSection> sections,
        int section, double atMs, DateTimeOffset now)
    {
        var all = progress.Sections.ToArray();
        var current = all[section];
        int streak = current.ConsecutiveFails + 1;
        double drop = Math.Min(MaximumDrop, Step * streak);
        var next = current with
        {
            Speed = Math.Max(MinimumSpeed, round(current.Speed - drop)), Fails = current.Fails + 1, ConsecutiveFails = streak,
            Tried = true, ClearedAtFullSpeed = false, CleanStreak = 0, LastFailMs = atMs, SpotClears = 0,
            Review = false, SolidAt = null, ReviewDays = 0,
        };
        next = loop(next, progress, section);
        if (!current.HasSpot && streak >= 2 && current.LastFailMs is { } previous && Math.Abs(previous - atMs) <= 2_000)
            next = withSpot(next, sections[section], Math.Min(previous, atMs), Math.Max(previous, atMs));
        all[section] = next;
        return progress with { Sections = all, Attempts = progress.Attempts + 1, Fails = progress.Fails + 1, ConnectTo = null, UpdatedAt = now };
    }

    /// <summary>
    /// The attempt reached its end without osu! failing it. <paramref name="result"/> describes its focused section or spot.
    /// A section or spot drill graded <see cref="AdaptiveGrade.Struggled"/> counts as a fail.
    /// </summary>
    public static AdaptivePracticeProgress Complete(AdaptivePracticeProgress progress, IReadOnlyList<AdaptiveSection> sections,
        AdaptiveDrill drill, AdaptiveResult result, DateTimeOffset now)
    {
        var grade = Grade(result, progress.Leniency);
        if (drill.Kind is AdaptiveDrillKind.Section or AdaptiveDrillKind.Spot or AdaptiveDrillKind.Review && grade == AdaptiveGrade.Struggled)
            return Fail(progress, sections, drill.Focus, result.MissTimes.Count > 0 ? result.MissTimes[0] : drill.StartMs, now);
        var missTimes = result.MissTimes;
        progress = progress with { Attempts = progress.Attempts + 1, UpdatedAt = now };
        switch (drill.Kind)
        {
            case AdaptiveDrillKind.FullRun:
                return progress with { CompletedAt = progress.CompletedAt ?? now };
            case AdaptiveDrillKind.Connect:
                return progress with { ConnectTo = null };
        }
        var all = progress.Sections.ToArray();
        var current = all[drill.Focus];
        bool comfortable = grade == AdaptiveGrade.Clean;
        if (drill.Kind == AdaptiveDrillKind.Review)
        {
            // A clean review waits twice as long for the next; a shaky one comes back after the first interval.
            all[drill.Focus] = current with
            {
                Review = false, SolidAt = now, Clears = current.Clears + 1,
                ReviewDays = comfortable ? Math.Max(FirstReviewDays, current.ReviewDays * 2) : FirstReviewDays,
            };
            return progress with { Sections = all };
        }
        current = current with { ConsecutiveFails = 0, Clears = current.Clears + 1, Tried = true };
        if (drill.Kind == AdaptiveDrillKind.Spot)
        {
            // The spot is learned after a few clean repeats; then the whole section is drilled again.
            int clears = comfortable ? current.SpotClears + 1 : 0;
            all[drill.Focus] = loop(clears >= SpotClearsNeeded
                ? current with { SpotStartMs = null, SpotEndMs = null, SpotClears = 0 }
                : current with { SpotClears = clears }, progress, drill.Focus);
            return progress with { Sections = all };
        }
        int? connect = null;
        if (current.Speed < 1)
        {
            // Below full speed the streak counts runs without a struggle: a clean one, or two steady ones, speed it up.
            int steady = current.CleanStreak + 1;
            current = comfortable || steady >= 2
                ? current with { Speed = Math.Min(1, round(current.Speed + Step)), CleanStreak = 0 }
                : current with { CleanStreak = steady };
        }
        else if (comfortable && current.CleanStreak + 1 >= RequiredStreak(current))
        {
            // Only a section that took work is reviewed in later sessions.
            current = current with
            {
                CleanStreak = current.CleanStreak + 1, ClearedAtFullSpeed = true, SolidAt = now, Loops = 0, Parked = false,
                ReviewDays = current.Fails >= 2 ? FirstReviewDays : 0,
            };
            if (drill.Focus > 0) connect = drill.Focus;
        }
        else current = current with { CleanStreak = comfortable ? current.CleanStreak + 1 : 0 };
        if (result.ComboBreaks > 0 && missTimes.Count is > 0 and <= 3 && missTimes.Max() - missTimes.Min() <= SpotWindowMs)
            current = withSpot(current, sections[drill.Focus], missTimes.Min(), missTimes.Max());
        all[drill.Focus] = current.ClearedAtFullSpeed ? current : loop(current, progress, drill.Focus);
        return progress with { Sections = all, ConnectTo = connect };
    }

    /// <summary>
    /// Counts a try on a section that is not solid yet. After <see cref="LoopCap"/> tries it is parked while another
    /// section still needs work; a parked section that is drilled again starts a fresh count.
    /// </summary>
    private static AdaptiveSectionProgress loop(AdaptiveSectionProgress state, AdaptivePracticeProgress progress, int section)
    {
        int loops = state.Parked ? 1 : state.Loops + 1;
        bool elsewhere = progress.Sections.Where((s, i) => i != section && s.NeedsWork && !s.Parked).Any();
        return loops >= LoopCap && elsewhere ? state with { Loops = 0, Parked = true } : state with { Loops = loops, Parked = false };
    }

    private static AdaptiveSectionProgress withSpot(AdaptiveSectionProgress state, AdaptiveSection section, double first, double last)
    {
        double start = Math.Max(section.StartMs, first - SpotLeadMs), end = Math.Min(section.EndMs, last + SpotTailMs);
        if (end - start < SpotMinimumMs) end = Math.Min(section.EndMs, start + SpotMinimumMs);
        // A spot that would cover most of the section is just the section.
        if (end - start >= (section.EndMs - section.StartMs) * .8) return state;
        return state with { SpotStartMs = start, SpotEndMs = end, SpotClears = 0 };
    }

    /// <summary>The section a fail belongs to: the latest one of the drill already under way at that map time.</summary>
    public static int SectionAt(IReadOnlyList<AdaptiveSection> sections, AdaptiveDrill drill, double time)
    {
        int index = drill.FirstSection;
        while (index < drill.LastSection && time >= sections[index + 1].StartMs) index++;
        return index;
    }

    /// <summary>
    /// Grades by hit counts, not accuracy: on a few notes a couple of 100s swing accuracy wildly. A drill starts with
    /// full health and lasts seconds, so osu! rarely fails it; too many combo breaks for its length, or a run that is
    /// nearly all 50s, is a fail in practice. Clean allows the player's usual share of 100s and 50s.
    /// </summary>
    public static AdaptiveGrade Grade(AdaptiveResult result, double leniency)
    {
        if (result.ComboBreaks > Math.Max(2, result.Objects / 10) || result.Notes >= 6 && result.Accuracy < .6) return AdaptiveGrade.Struggled;
        return result.ComboBreaks == 0 && result.Offbeat <= Allowance(result.Notes, leniency) ? AdaptiveGrade.Clean : AdaptiveGrade.Shaky;
    }

    /// <summary>100s and 50s a clean run may have: the player's usual share plus a margin, never fewer than one.</summary>
    public static int Allowance(int notes, double leniency) =>
        Math.Max(1, (int)Math.Ceiling(notes * Math.Max(.1, leniency * 1.25) - 1e-9));

    /// <summary>The player's usual share of 100s and 50s, from hit counts or else estimated from accuracy.</summary>
    public static double LeniencyFrom(int great, int ok, int meh, double accuracy)
    {
        int hits = great + ok + meh;
        double share = hits >= 20 ? (ok + meh) / (double)hits
            : double.IsFinite(accuracy) && accuracy is > 0 and <= 1 ? 1.5 * (1 - accuracy) : DefaultLeniency;
        return Math.Clamp(share, .08, .4);
    }

    /// <summary>One line on why an attempt counted the way it did.</summary>
    public static string Describe(AdaptiveResult result, double leniency)
    {
        string misses = result.ComboBreaks switch { 0 => "No misses", 1 => "1 miss", var n => $"{n} misses" };
        return $"{misses}, {result.Offbeat} × 100/50 ({Allowance(result.Notes, leniency)} allowed for a clean run).";
    }

    public static string Percent(double speed) => $"{Math.Round(speed * 100):0}%";

    private static double round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

/// <summary>Saved adaptive practice per map and setup, so a session can stop and resume later.</summary>
public sealed class AdaptivePracticeStore(string path)
{
    private const int maximum_maps = 200;
    private const long maximum_bytes = 4 * 1024 * 1024;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string file = Path.GetFullPath(path);
    private object gate => gates.GetOrAdd(file, _ => new());

    public AdaptivePracticeProgress? Load(string key)
    {
        lock (gate) return read().FirstOrDefault(p => p.MapKey == key);
    }

    public IReadOnlyList<AdaptivePracticeProgress> List()
    {
        lock (gate) return read();
    }

    public void Save(AdaptivePracticeProgress progress)
    {
        lock (gate)
        {
            var all = read().Where(p => p.MapKey != progress.MapKey).Append(progress)
                .OrderByDescending(p => p.UpdatedAt).Take(maximum_maps).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(all));
            File.Move(file + ".tmp", file, true);
        }
    }

    private IReadOnlyList<AdaptivePracticeProgress> read()
    {
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > maximum_bytes) return [];
            return (JsonSerializer.Deserialize<AdaptivePracticeProgress[]>(File.ReadAllText(file)) ?? []).Where(valid).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private static bool valid(AdaptivePracticeProgress? p) => p is not null && !string.IsNullOrWhiteSpace(p.MapKey) && p.MapKey.Length <= 256
        && p.Sections is { Length: > 0 and <= 1000 } && double.IsFinite(p.TargetRate) && p.TargetRate is > .25 and < 4
        && p.Sections.All(s => s is not null && double.IsFinite(s.Speed) && s.Speed is >= AdaptivePracticeCoach.MinimumSpeed and <= 1)
        && (p.ConnectTo is null || p.ConnectTo >= 0 && p.ConnectTo < p.Sections.Length);
}
