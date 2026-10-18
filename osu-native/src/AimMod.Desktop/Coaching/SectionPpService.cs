using System.Collections.Concurrent;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;

namespace AimMod.Desktop.Coaching;

/// <param name="LostToMisses">pp this play would gain with the section's misses, slider breaks and dropped slider ends taken back.</param>
/// <param name="LostToHits">pp this play would gain with the section's 100s and 50s played as 300s.</param>
/// <param name="GainFromBest">pp this play would gain with the section played as well as the player's best of it.</param>
public sealed record SectionPpLine(AdaptiveSection Section, SectionTally Tally, double? LostToMisses, double? LostToHits, double? Lost,
    SectionBest? Best, double? GainFromBest);

/// <param name="Calculator">The pp calculator the numbers come from, so they can be checked and compared.</param>
/// <param name="SumOfBestPp">pp of one play made of the player's best of every section, once every section has a best.</param>
/// <param name="FullComboOkGoal">The most 100s a full combo can have and still be worth more than this play.</param>
public sealed record SectionPpReport(Guid ScoreId, bool Passed, string Calculator, double? Pp, IReadOnlyList<SectionPpLine> Sections,
    double? SumOfBestPp, int CountedPlays, int? FullComboOkGoal)
{
    /// <summary>
    /// The sections that cost this play the most pp, worst first. A section counts when it cost at least half a pp
    /// and a tenth of the worst one, so a stray 100 does not fill the list.
    /// </summary>
    public IEnumerable<SectionPpLine> Costliest(int count)
    {
        double worst = Sections.Max(s => s.Lost ?? 0);
        return Sections.Where(s => s.Lost is { } lost && lost >= Math.Max(.5, worst * .1)).OrderByDescending(s => s.Lost).Take(count);
    }
}

public interface ISectionPpService
{
    Task<SectionPpReport?> CalculateAsync(LocalReplay run, IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        IReadOnlyList<LocalReplay> sameMap, CancellationToken cancellationToken);
}

/// <summary>
/// Works out where a play lost its pp with osu!'s own calculator. Every what-if is a whole play with exact
/// judgement counts rebuilt from the replay, so combo, slider ends and accuracy are counted as osu! counts them.
/// </summary>
public sealed class SectionPpService(SectionLedgerStore ledger, Func<LocalReplay, CancellationToken, Task<IBeatmapSourceLease>> openBeatmap,
    Func<SidecarRuntimeClient>? startRuntime = null) : ISectionPpService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<string, double> ppCache = new();

    public Action<Exception>? Failed { get; init; }

    public async Task<SectionPpReport?> CalculateAsync(LocalReplay run, IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        IReadOnlyList<LocalReplay> sameMap, CancellationToken cancellationToken)
    {
        if (run.RulesetShortName != "osu" || !analyses.TryGetValue(run.ScoreId, out var analysis) || analysis.Judgements.Count == 0) return null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await openBeatmap(run, cancellationToken).ConfigureAwait(false);
            var setup = AdaptivePracticeSetup.For(run);
            var ruleset = new OsuRuleset();
            Mod[] mods = setup.Mods.Select(ruleset.CreateModFromAcronym).OfType<Mod>().ToArray();
            var sections = await Task.Run(() => AdaptivePracticeSections.Split(
                new FlatWorkingBeatmap(lease.BeatmapPath).GetPlayableBeatmap(ruleset.RulesetInfo, mods, cancellationToken)), cancellationToken).ConfigureAwait(false);
            if (sections.Count == 0) return null;

            var tallies = SectionLedger.Tally(analysis.Judgements, sections);
            var entry = ledger.Load(setup.Key) is { } saved && saved.Matches(sections) ? saved : SectionLedgerEntry.Create(setup.Key, sections, DateTimeOffset.UtcNow);
            var before = entry;
            foreach (var play in sameMap.Append(run).DistinctBy(p => p.ScoreId).OrderBy(p => p.PlayedAt))
                if (AdaptivePracticeSetup.For(play).Key == setup.Key && analyses.TryGetValue(play.ScoreId, out var other))
                    entry = entry.Record(play.ScoreId, play.PlayedAt, play.ScoreId == run.ScoreId ? tallies : SectionLedger.Tally(other.Judgements, sections), DateTimeOffset.UtcNow);
            if (!ReferenceEquals(entry, before))
            {
                try { ledger.Save(entry); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }

            bool exact = run.Passed && tallies.All(t => t.Complete);
            if (!exact)
                return new SectionPpReport(run.ScoreId, run.Passed, PpCalculationProtocol.EngineVersion, null,
                    sections.Select((s, i) => new SectionPpLine(s, tallies[i], null, null, null, entry.Bests[i], null)).ToArray(), null, entry.Counted.Length, null);

            await using SidecarRuntimeClient runtime = startRuntime?.Invoke() ?? SidecarRuntimeClient.Start();
            var client = new PpWhatIfClient(new SidecarRuntimeRequestClient(runtime));
            string calculator = PpCalculationProtocol.EngineVersion;
            async Task<double> pp(IEnumerable<SectionTally> play)
            {
                var (statistics, combo) = SectionLedger.Combine(play);
                string key = string.Join('|', setup.Key, run.ModsJson, run.LegacyScore, statistics, combo);
                if (ppCache.TryGetValue(key, out double cached)) return cached;
                var result = await client.CalculateAsync(new PpWhatIfRequest(Path.GetDirectoryName(lease.BeatmapPath)!, lease.BeatmapPath,
                    calculatorMods(run.Mods), SectionLedger.Accuracy(statistics), statistics.Miss, combo, statistics, run.ModsJson,
                    run.LegacyScore || run.Origin == LocalLibraryOrigin.Stable), cancellationToken).ConfigureAwait(false);
                calculator = $"{result.EngineVersion}, difficulty v{result.DifficultyVersion}";
                return ppCache[key] = result.PerformancePoints;
            }

            double current = await pp(tallies).ConfigureAwait(false);
            var lines = new List<SectionPpLine>();
            for (int i = 0; i < sections.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var t = tallies[i];
                int section = i;
                double? misses = t.Breaks + t.Miss + t.SliderBreaks == 0 ? 0 : await pp(SectionLedger.Tally(analysis.Judgements, sections, s => s == section ? SectionRepair.Breaks : SectionRepair.None)).ConfigureAwait(false) - current;
                double? hits = t.Ok + t.Meh == 0 ? 0 : await pp(SectionLedger.Tally(analysis.Judgements, sections, s => s == section ? SectionRepair.Hits : SectionRepair.None)).ConfigureAwait(false) - current;
                double? lost = misses == 0 ? hits : hits == 0 ? misses
                    : await pp(SectionLedger.Tally(analysis.Judgements, sections, s => s == section ? SectionRepair.All : SectionRepair.None)).ConfigureAwait(false) - current;
                var best = entry.Bests[i];
                double? fromBest = best is null || !best.Tally.BetterThan(t) ? 0
                    : await pp(tallies.Select((x, j) => j == section ? best.Tally : x)).ConfigureAwait(false) - current;
                lines.Add(new SectionPpLine(sections[i], t, misses, hits, lost, best, fromBest));
            }

            double? sumOfBest = entry.HasEveryBest ? await pp(entry.Bests.Select(b => b!.Tally)).ConfigureAwait(false) : null;
            int? goal = await fullComboGoal(tallies, current, pp).ConfigureAwait(false);
            return new SectionPpReport(run.ScoreId, true, calculator, current, lines, sumOfBest, entry.Counted.Length, goal);
        }
        // The breakdown is extra detail on the coaching page; a map or calculator problem only hides it.
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Failed?.Invoke(error);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The largest number of 100s a full combo can have and still beat <paramref name="current"/>, or null when even an SS does not.</summary>
    private static async Task<int?> fullComboGoal(IReadOnlyList<SectionTally> tallies, double current, Func<IEnumerable<SectionTally>, Task<double>> pp)
    {
        var whole = SectionLedger.Combine(tallies.Select(t => t with
        {
            Great = t.Objects, Ok = 0, Meh = 0, Miss = 0, SliderTailHit = t.SliderTailHit + t.SliderTailMiss, SliderTailMiss = 0,
            LargeTickHit = t.LargeTickHit + t.LargeTickMiss, LargeTickMiss = 0, Breaks = 0,
        }));
        int objects = whole.Statistics.Great;
        SectionTally fullCombo(int oks) => new(objects - oks, oks, 0, 0, whole.Statistics.SliderTailHit, 0, whole.Statistics.LargeTickHit, 0, 0,
            ComboRun.Empty.Add(whole.MaxCombo), true);
        if (await pp([fullCombo(0)]).ConfigureAwait(false) <= current) return null;
        int low = 0, high = objects / 2;
        if (await pp([fullCombo(high)]).ConfigureAwait(false) > current) return high;
        while (high - low > 1)
        {
            int middle = (low + high) / 2;
            if (await pp([fullCombo(middle)]).ConfigureAwait(false) > current) low = middle;
            else high = middle;
        }
        return low;
    }

    private static IReadOnlyList<string> calculatorMods(IReadOnlyList<string> mods) =>
        (mods ?? []).Select(m => (m ?? "").Trim().ToUpperInvariant()).Where(m => m.Length > 0 && m != "NM").Distinct().Order(StringComparer.Ordinal).ToArray();
}
