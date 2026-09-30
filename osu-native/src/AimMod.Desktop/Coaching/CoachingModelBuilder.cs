using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Coaching;

internal readonly record struct CoachingModelKey(object History, int AnalysisCount, CoachingTimeRange TimeRange, DateTimeOffset? Now)
{
    public bool Equals(CoachingModelKey other) =>
        ReferenceEquals(History, other.History)
        && AnalysisCount == other.AnalysisCount
        && TimeRange == other.TimeRange
        && Now == other.Now;

    public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(History), AnalysisCount, TimeRange, Now);
}

/// <summary>
/// Builds coaching workspace models and caches the account-wide part by (history identity, analysis count,
/// time range), so selecting a play only computes the selection-dependent part. Thread-safe; builds are
/// intended to run off the update thread.
/// </summary>
internal sealed class CoachingModelBuilder
{
    private const int run_model_limit = 12;

    private readonly object gate = new();
    private Scope? scope;

    public int GlobalBuildCount { get; private set; }

    public NativeCoachingWorkspaceModel Build(
        IReadOnlyList<LocalReplay> runs,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        Guid? selectedScoreId = null,
        CoachingTimeRange timeRange = CoachingTimeRange.Days30,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(analyses);

        Scope current = scopeFor(runs, analyses, timeRange, now);
        if (selectedScoreId is not { } scoreId)
            return current.Global;

        lock (gate)
        {
            if (current.Runs.TryGetValue(scoreId, out NativeCoachingWorkspaceModel? cached))
                return cached;
        }

        NativeCoachingWorkspaceModel model = buildRun(current, analyses, scoreId);
        lock (gate)
            return current.AddRun(scoreId, model);
    }

    /// <summary>Returns a model without building when the global scope and the selection are already cached.</summary>
    public bool TryGetCached(
        IReadOnlyList<LocalReplay> runs,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        Guid? selectedScoreId,
        CoachingTimeRange timeRange,
        [NotNullWhen(true)] out NativeCoachingWorkspaceModel? model,
        DateTimeOffset? now = null)
    {
        lock (gate)
        {
            model = null;
            if (scope is null || !scope.Key.Equals(new CoachingModelKey(runs, analyses.Count, timeRange, now)))
                return false;

            if (selectedScoreId is not { } scoreId || !scope.Contains(scoreId))
            {
                model = scope.Global;
                return true;
            }

            return scope.Runs.TryGetValue(scoreId, out model);
        }
    }

    public bool TryGetPracticePool(NativeCoachingWorkspaceModel model, [NotNullWhen(true)] out IReadOnlyList<PracticeMapCandidate>? pool)
    {
        lock (gate)
        {
            pool = scope is not null && ReferenceEquals(scope.Global.History, model.History) ? scope.PracticePool : null;
            return pool is not null;
        }
    }

    /// <summary>Builds the practice candidate pool for the scope of <paramref name="model"/> once.</summary>
    public IReadOnlyList<PracticeMapCandidate> GetPracticePool(
        NativeCoachingWorkspaceModel model,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        int limit)
    {
        Scope? current;
        lock (gate)
        {
            current = scope is not null && ReferenceEquals(scope.Global.History, model.History) ? scope : null;
            if (current?.PracticePool is { } pool)
                return pool;
        }

        IReadOnlyList<PracticeMapCandidate> built = PracticeMapCandidateBuilder.Build(model.History, analyses, limit);
        if (current is not null)
        {
            lock (gate)
                current.PracticePool ??= built;
        }

        return built;
    }

    public void Invalidate()
    {
        lock (gate)
            scope = null;
    }

    private Scope scopeFor(
        IReadOnlyList<LocalReplay> runs,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        CoachingTimeRange timeRange,
        DateTimeOffset? now)
    {
        var key = new CoachingModelKey(runs, analyses.Count, timeRange, now);
        lock (gate)
        {
            if (scope is not null && scope.Key.Equals(key))
                return scope;
        }

        Scope built = buildScope(key, runs, analyses, timeRange, now);
        lock (gate)
        {
            GlobalBuildCount++;
            if (scope is not null && scope.Key.Equals(key))
                return scope;
            scope = built;
            return built;
        }
    }

    private static Scope buildScope(
        CoachingModelKey key,
        IReadOnlyList<LocalReplay> runs,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        CoachingTimeRange timeRange,
        DateTimeOffset? now)
    {
        LocalReplay[] history = NativeCoachingWorkspaceModel.NormaliseHistory(runs, timeRange, now);
        GlobalCoachingProfile profile = GlobalCoachingProfileBuilder.Build(history, analyses);
        CoachingReport report = CoachingReportBuilder.BuildGlobal(history, analyses, profile);
        var global = new NativeCoachingWorkspaceModel(
            history,
            NativeCoachingWorkspaceModel.TrendRunsOf(history),
            Array.Empty<LocalReplay>(),
            null,
            null,
            NativeCoachingWorkspaceModel.SummariseGlobal(history, analyses),
            report)
        {
            GlobalProfile = profile,
        };
        return new Scope(key, global, history.OrderBy(run => run.PlayedAt).ToArray());
    }

    private static NativeCoachingWorkspaceModel buildRun(
        Scope scope,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        Guid scoreId)
    {
        NativeCoachingWorkspaceModel global = scope.Global;
        LocalReplay? selected = global.History.FirstOrDefault(run => run.ScoreId == scoreId);
        if (selected is null)
            return global;

        LocalReplay[] sessionRuns = NativeCoachingWorkspaceModel.FindSession(scope.Chronological, scoreId);
        return global with
        {
            SessionRuns = sessionRuns,
            SelectedRun = selected,
            Session = sessionRuns.Length == 0 ? null : NativeCoachingWorkspaceModel.SummariseSession(sessionRuns),
            Report = CoachingReportBuilder.BuildForSelection(global.Report, global.History, analyses, scoreId),
        };
    }

    private sealed class Scope
    {
        private readonly Queue<Guid> order = new();
        private readonly HashSet<Guid> scores;

        public Scope(CoachingModelKey key, NativeCoachingWorkspaceModel global, LocalReplay[] chronological)
        {
            Key = key;
            Global = global;
            Chronological = chronological;
            scores = global.History.Select(run => run.ScoreId).ToHashSet();
        }

        public CoachingModelKey Key { get; }

        public NativeCoachingWorkspaceModel Global { get; }

        public LocalReplay[] Chronological { get; }

        public IReadOnlyList<PracticeMapCandidate>? PracticePool { get; set; }

        public Dictionary<Guid, NativeCoachingWorkspaceModel> Runs { get; } = new();

        public bool Contains(Guid scoreId) => scores.Contains(scoreId);

        public NativeCoachingWorkspaceModel AddRun(Guid scoreId, NativeCoachingWorkspaceModel model)
        {
            if (Runs.TryGetValue(scoreId, out NativeCoachingWorkspaceModel? existing))
                return existing;

            Runs.Add(scoreId, model);
            order.Enqueue(scoreId);
            while (order.Count > run_model_limit)
                Runs.Remove(order.Dequeue());
            return model;
        }
    }
}
