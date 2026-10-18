using System.Text.Json;
using AimMod.Desktop.Practice;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Coaching;

/// <summary>
/// Combo through a stretch of play, kept so stretches from different plays can be joined: the combo carried in
/// from the start, the combo carried out at the end, and the longest combo inside. Unbroken stretches only add.
/// </summary>
public readonly record struct ComboRun(int Prefix, int Suffix, int Best, bool Broken)
{
    public static ComboRun Empty => new(0, 0, 0, false);

    public int MaxCombo => Broken ? Math.Max(Best, Math.Max(Prefix, Suffix)) : Prefix;

    public ComboRun Add(int combo) => Broken
        ? this with { Suffix = Suffix + combo, Best = Math.Max(Best, Suffix + combo) }
        : new(Prefix + combo, Prefix + combo, Prefix + combo, false);

    public ComboRun Break() => Broken ? this with { Suffix = 0 } : new(Prefix, 0, Prefix, true);

    public static ComboRun Join(ComboRun left, ComboRun right)
    {
        if (!right.Broken) return left.Add(right.Prefix);
        if (!left.Broken) return new(left.Prefix + right.Prefix, right.Suffix, Math.Max(right.Best, left.Prefix + right.Prefix), true);
        return new(left.Prefix, right.Suffix, Math.Max(Math.Max(left.Best, right.Best), left.Suffix + right.Prefix), true);
    }
}

/// <summary>Judgement counts and combo for one section of one play, the way osu! counts them for pp.</summary>
public sealed record SectionTally(int Great, int Ok, int Meh, int Miss, int SliderTailHit, int SliderTailMiss,
    int LargeTickHit, int LargeTickMiss, int Breaks, ComboRun Combo, bool Complete)
{
    public int Objects => Great + Ok + Meh + Miss;
    public int SliderBreaks => SliderTailMiss + LargeTickMiss;

    /// <summary>Lower is better: combo breaks first, then 100s and 50s by the accuracy they cost.</summary>
    public (int Breaks, int Hits) Cost => (Breaks, 4 * Ok + 5 * Meh);

    public bool BetterThan(SectionTally other) => Cost.CompareTo(other.Cost) < 0;
}

/// <summary>Which mistakes a what-if play has taken back.</summary>
[Flags]
public enum SectionRepair { None = 0, Breaks = 1, Hits = 2, All = Breaks | Hits }

/// <summary>
/// Splits an analysed play into the map's practice sections and rebuilds exact pp statistics for what-if plays:
/// a section played without its combo breaks, without its 100s and 50s, or replaced by its best from another play.
/// </summary>
public static class SectionLedger
{
    public static IReadOnlyList<SectionTally> Tally(IReadOnlyList<ReplayObjectJudgement> judgements, IReadOnlyList<AdaptiveSection> sections,
        Func<int, SectionRepair>? repair = null)
    {
        var tallies = new Builder[sections.Count];
        for (int i = 0; i < tallies.Length; i++) tallies[i] = new Builder();
        foreach (var judgement in judgements)
        {
            if (judgement.ObjectIndex is not { } index || sectionOf(sections, index) is not { } s) continue;
            tallies[s].Add(judgement, repair?.Invoke(s) ?? SectionRepair.None);
        }
        return tallies.Select((t, i) => t.Build(sections[i].ObjectCount)).ToArray();
    }

    /// <summary>Exact statistics for a whole play made of these sections, in order.</summary>
    public static (PpScoreStatistics Statistics, int MaxCombo) Combine(IEnumerable<SectionTally> tallies)
    {
        int great = 0, ok = 0, meh = 0, miss = 0, tail = 0, largeHit = 0, largeMiss = 0;
        var combo = ComboRun.Empty;
        foreach (var t in tallies)
        {
            great += t.Great; ok += t.Ok; meh += t.Meh; miss += t.Miss;
            tail += t.SliderTailHit; largeHit += t.LargeTickHit; largeMiss += t.LargeTickMiss;
            combo = ComboRun.Join(combo, t.Combo);
        }
        return (new PpScoreStatistics(great, ok, meh, miss, tail, largeMiss, LargeTickHit: largeHit), combo.MaxCombo);
    }

    public static double Accuracy(PpScoreStatistics s)
    {
        int total = s.Great + s.Ok + s.Meh + s.Miss;
        return total == 0 ? 1 : (300.0 * s.Great + 100.0 * s.Ok + 50.0 * s.Meh) / (300.0 * total);
    }

    private static int? sectionOf(IReadOnlyList<AdaptiveSection> sections, int objectIndex)
    {
        int low = 0, high = sections.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (objectIndex < sections[middle].FirstObject) high = middle - 1;
            else if (objectIndex > sections[middle].LastObject) low = middle + 1;
            else return middle;
        }
        return null;
    }

    private sealed class Builder
    {
        private int great, ok, meh, miss, tailHit, tailMiss, largeHit, largeMiss, breaks;
        private ComboRun combo = ComboRun.Empty;

        public void Add(ReplayObjectJudgement j, SectionRepair repair)
        {
            string result = j.Result;
            bool breaksCombo = isComboMiss(j);
            if ((breaksCombo || isDroppedEnd(j)) && repair.HasFlag(SectionRepair.Breaks)) { result = j.MaximumResult; breaksCombo = false; }
            else if (result is "Ok" or "Meh" && j.MaximumResult == "Great" && repair.HasFlag(SectionRepair.Hits)) result = "Great";

            switch (j.MaximumResult, result)
            {
                case ("Great", "Great"): great++; break;
                case ("Great", "Ok"): ok++; break;
                case ("Great", "Meh"): meh++; break;
                case ("Great", "Miss"): miss++; break;
                case ("SliderTailHit", "SliderTailHit"): tailHit++; break;
                case ("SliderTailHit", _): tailMiss++; break;
                case ("LargeTickHit", "LargeTickHit"): largeHit++; break;
                case ("LargeTickHit", _): largeMiss++; break;
            }

            if (breaksCombo) { breaks++; combo = combo.Break(); }
            else if (result != j.Result) combo = combo.Add(1);
            else if (j.ComboAfter > j.ComboBefore) combo = combo.Add(j.ComboAfter - j.ComboBefore);
            else if (j.ComboAfter < j.ComboBefore) { breaks++; combo = combo.Break(); }
        }

        public SectionTally Build(int objects) => new(great, ok, meh, miss, tailHit, tailMiss, largeHit, largeMiss, breaks, combo,
            great + ok + meh + miss == objects);

        /// <summary>A miss that resets combo: a circle or slider head, or a slider tick or repeat.</summary>
        private static bool isComboMiss(ReplayObjectJudgement j) => j.MaximumResult switch
        {
            "Great" => j.Result == "Miss",
            "LargeTickHit" => j.Result != j.MaximumResult,
            _ => false,
        };

        /// <summary>A dropped slider end keeps the combo but does not add to it, and costs pp like a slider break.</summary>
        private static bool isDroppedEnd(ReplayObjectJudgement j) => j.MaximumResult == "SliderTailHit" && j.Result != j.MaximumResult;
    }
}

/// <summary>The best a player has played one section, at one setup, across all their analysed plays.</summary>
public sealed record SectionBest(SectionTally Tally, Guid ScoreId, DateTimeOffset PlayedAt, int Plays);

/// <param name="FirstObjects">The first object of every section, so a ledger is only reused for the same split.</param>
public sealed record SectionLedgerEntry(string Key, int[] FirstObjects, SectionBest?[] Bests, Guid[] Counted, DateTimeOffset UpdatedAt)
{
    public static SectionLedgerEntry Create(string key, IReadOnlyList<AdaptiveSection> sections, DateTimeOffset now) =>
        new(key, sections.Select(s => s.FirstObject).ToArray(), new SectionBest?[sections.Count], [], now);

    public bool Matches(IReadOnlyList<AdaptiveSection> sections) => FirstObjects.SequenceEqual(sections.Select(s => s.FirstObject));

    /// <summary>Adds one play's sections; a section only counts when the play reached its end, failed plays included.</summary>
    public SectionLedgerEntry Record(Guid scoreId, DateTimeOffset playedAt, IReadOnlyList<SectionTally> tallies, DateTimeOffset now)
    {
        if (Counted.Contains(scoreId) || tallies.Count != Bests.Length) return this;
        var bests = Bests.ToArray();
        for (int i = 0; i < bests.Length; i++)
        {
            if (!tallies[i].Complete) continue;
            var best = bests[i];
            bests[i] = best is null || tallies[i].BetterThan(best.Tally)
                ? new SectionBest(tallies[i], scoreId, playedAt, (best?.Plays ?? 0) + 1)
                : best with { Plays = best.Plays + 1 };
        }
        return this with { Bests = bests, Counted = [.. Counted, scoreId], UpdatedAt = now };
    }

    public bool HasEveryBest => Bests.All(b => b is not null);
}

public sealed class SectionLedgerStore(string path)
{
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    private readonly Lock gate = new();

    public SectionLedgerEntry? Load(string key)
    {
        lock (gate) return read().GetValueOrDefault(key);
    }

    public void Save(SectionLedgerEntry entry)
    {
        lock (gate)
        {
            var all = read();
            all[entry.Key] = entry;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, json));
            File.Move(temporary, path, true);
        }
    }

    private Dictionary<string, SectionLedgerEntry> read()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, SectionLedgerEntry>>(File.ReadAllText(path), json) ?? [] : [];
        }
        catch (JsonException) { return []; }
    }
}
