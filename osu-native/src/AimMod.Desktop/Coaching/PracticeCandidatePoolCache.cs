using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Coaching;

internal sealed class PracticeCandidatePoolCache
{
    private readonly int limit;
    private readonly Func<IEnumerable<LocalReplay>, IReadOnlyDictionary<Guid, ReplayAnalysisResult>, int, IReadOnlyList<PracticeMapCandidate>> build;
    private IReadOnlyList<PracticeMapCandidate>? cached;

    public PracticeCandidatePoolCache(
        int limit,
        Func<IEnumerable<LocalReplay>, IReadOnlyDictionary<Guid, ReplayAnalysisResult>, int, IReadOnlyList<PracticeMapCandidate>>? build = null)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));

        this.limit = limit;
        this.build = build ?? PracticeMapCandidateBuilder.Build;
    }

    public IReadOnlyList<PracticeMapCandidate> Get(
        IReadOnlyList<LocalReplay> replays,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses) => cached ??= build(replays, analyses, limit);

    public void Invalidate() => cached = null;
}
