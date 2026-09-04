using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using Realms;

namespace AimMod.Desktop.LocalLibrary;

/// <summary>
/// Maintains a detached, bounded snapshot of the active lazer user's local osu!standard scores.
/// Add this component to the game before passing it to <see cref="OsuManagerLocalLibrarySource"/>.
/// </summary>
public sealed partial class RealmLocalReplayMetadataSource : Component, ILocalReplayMetadataSource
{
    private const int maximum_snapshot_rows = 100_000;

    private readonly object snapshotLock = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private ILocalReplaySnapshotProvider? snapshotProvider;
    private IBindable<APIUser>? localUser;
    private IDisposable? scoreSubscription;
    private Task<InMemoryLocalLibrarySource>? snapshotTask;
    private int? currentUserId;
    private bool disposed;

    public RealmLocalReplayMetadataSource()
    {
    }

    internal RealmLocalReplayMetadataSource(ILocalReplaySnapshotProvider snapshotProvider, int? currentUserId)
    {
        this.snapshotProvider = snapshotProvider;
        this.currentUserId = currentUserId;
    }

    [BackgroundDependencyLoader]
    private void load(RealmAccess realm, IAPIProvider api)
    {
        snapshotProvider ??= new RealmReplaySnapshotProvider(realm);

        localUser = api.LocalUser.GetBoundCopy();
        localUser.BindValueChanged(userChanged,
            runOnceImmediately: true);

        // Watch the score table once. Search and paging never perform per-row
        // Realm queries, and every callback only invalidates the detached cache.
        scoreSubscription = realm.RegisterForNotifications(
            r => r.All<ScoreInfo>().Where(score => !score.DeletePending),
            (_, _) => Invalidate());
    }

    public async ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(
        LocalLibraryQuery query,
        CancellationToken cancellationToken = default)
    {
        InMemoryLocalLibrarySource snapshot = await getSnapshot(cancellationToken).ConfigureAwait(false);
        return await snapshot.SearchReplaysAsync(query, cancellationToken).ConfigureAwait(false);
    }

    public void Invalidate()
    {
        lock (snapshotLock)
            snapshotTask = null;
    }

    protected override void Dispose(bool isDisposing)
    {
        if (isDisposing)
        {
            lock (snapshotLock)
            {
                if (!disposed)
                {
                    disposed = true;
                    snapshotTask = null;
                    lifetimeCancellation.Cancel();
                }
            }

            localUser?.UnbindAll();
            scoreSubscription?.Dispose();
            lifetimeCancellation.Dispose();
        }

        base.Dispose(isDisposing);
    }

    private void userChanged(ValueChangedEvent<APIUser> user)
    {
        lock (snapshotLock)
        {
            currentUserId = user.NewValue.Id > 1 ? user.NewValue.Id : null;
            snapshotTask = null;
        }
    }

    private async Task<InMemoryLocalLibrarySource> getSnapshot(CancellationToken cancellationToken)
    {
        Task<InMemoryLocalLibrarySource> task;

        lock (snapshotLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ILocalReplaySnapshotProvider provider = snapshotProvider
                                                    ?? throw new InvalidOperationException("The local replay source has not been loaded into the osu dependency container.");
            int? userId = currentUserId;
            task = snapshotTask ??= Task.Run(
                () => buildSnapshot(provider, userId, lifetimeCancellation.Token),
                lifetimeCancellation.Token);
        }

        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (snapshotLock)
            {
                if (ReferenceEquals(snapshotTask, task) && task.IsCompleted)
                    snapshotTask = null;
            }

            throw;
        }
    }

    private static InMemoryLocalLibrarySource buildSnapshot(
        ILocalReplaySnapshotProvider provider,
        int? userId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LocalReplay> replays = provider.ReadSnapshot(userId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new InMemoryLocalLibrarySource(Array.Empty<LocalBeatmapSet>(), replays);
    }

    internal interface ILocalReplaySnapshotProvider
    {
        IReadOnlyList<LocalReplay> ReadSnapshot(int? userId, CancellationToken cancellationToken);
    }

    internal static List<ScoreInfo> ReadDetachedScores(Realm realm, int? userId) =>
        realm.GetAllLocalScoresForUser(userId)
             .Filter($"{nameof(ScoreInfo.Ruleset)}.{nameof(RulesetInfo.ShortName)} == $0", "osu")
             .OrderByDescending(score => score.Date)
             // Realm cannot translate Take. Enumerate lazily before applying the limit.
             .AsEnumerable()
             .Take(maximum_snapshot_rows)
             .Detach();

    private sealed class RealmReplaySnapshotProvider(RealmAccess realm) : ILocalReplaySnapshotProvider
    {
        public IReadOnlyList<LocalReplay> ReadSnapshot(int? userId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // One bounded Realm query supplies the complete metadata batch. The
            // query is detached before Realm.Run returns, so no managed Realm
            // object reaches the background index or native route.
            List<ScoreInfo> scores = realm.Run(r => ReadDetachedScores(r, userId));

            var result = new List<LocalReplay>(scores.Count);
            foreach (ScoreInfo score in scores)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BeatmapInfo? beatmap = score.BeatmapInfo;
                if (beatmap is null)
                    continue;

                result.Add(toLocalReplay(score, beatmap));
            }

            return result;
        }

        private static LocalReplay toLocalReplay(ScoreInfo score, BeatmapInfo beatmap)
        {
            string[] mods = score.APIMods.Select(mod => mod.Acronym)
                                 .Where(acronym => !string.IsNullOrWhiteSpace(acronym))
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .ToArray();
            bool hasReplayFile = score.Files.Any(file => file.Filename.EndsWith(".osr", StringComparison.OrdinalIgnoreCase));

            return new LocalReplay(
                score.ID,
                beatmap.BeatmapSet?.ID ?? Guid.Empty,
                beatmap.ID,
                beatmap.Metadata.Title,
                beatmap.Metadata.Artist,
                beatmap.DifficultyName,
                score.Ruleset.ShortName,
                score.RealmUser.Username,
                score.Date,
                beatmap.StarRating,
                score.Accuracy,
                score.TotalScore,
                score.MaxCombo,
                score.Statistics.GetValueOrDefault(HitResult.Miss),
                score.PP,
                mods,
                hasReplayFile);
        }
    }
}
