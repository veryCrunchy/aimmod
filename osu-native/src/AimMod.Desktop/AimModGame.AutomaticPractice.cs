using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AimMod.Osu.Runtime;
using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop;

public partial class AimModGame
{
    private bool automaticPracticeBusy;
    private readonly Dictionary<Guid,DateTimeOffset> automaticPracticeRetries = [];
    private DateTimeOffset nextAutomaticPractice;

    private void tickAutomaticPractice()
    {
        if(automaticPracticeBusy || DateTimeOffset.UtcNow<nextAutomaticPractice || activeReplayScoreId is not null || trainerPlayer is not null || preparingTrainer)return;
        var store=new AutomaticPracticeStore(Storage.GetFullPath("practice-maps",true));
        var settings=store.Load();
        if(settings.Enabled && currentOsuProfile is not null)startReplayLibraryAnalysis(automatic: true);
        int account=currentOsuProfile?.UserId ?? 0; string player=currentOsuProfile?.Username ?? "";
        var analyses=replayAnalyses.ToDictionary(pair=>pair.Key,pair=>pair.Value);
        automaticPracticeBusy=true;
        nextAutomaticPractice=DateTimeOffset.UtcNow.AddSeconds(30);
        _=runAutomaticPractice(settings,account,player,analyses).ContinueWith(task=>Schedule(()=> {
            automaticPracticeBusy=false;
            if(task.Exception is not null)logFailure("automatic practice",task.Exception.GetBaseException());
        }));
    }

    private async Task runAutomaticPractice(AutomaticPracticeSettings settings,int account,string player,IReadOnlyDictionary<Guid,ReplayAnalysisResult> evidence)
    {
        var token=appLifetime.Token;
        string root=Storage.GetFullPath("practice-maps",true);
        var library=new PracticeMapLibrary(root);
        if(!settings.Enabled || account==0)
        {
            await deliverPracticeMaps(await library.ListAsync(token).ConfigureAwait(false), account, token).ConfigureAwait(false);
            return;
        }
        localLibrary.Invalidate();
        var history=(await StatisticsHistoryLoader.LoadAsync(localLibrary,token).ConfigureAwait(false)).Runs;
        var sets=await library.RunAsync(()=>library.RefreshProgress(history,account),token).ConfigureAwait(false);
        var now=DateTimeOffset.UtcNow;
        bool stillEnabled()=>!token.IsCancellationRequested && currentOsuProfile?.UserId==account && new AutomaticPracticeStore(root).Load().Enabled;
        if(!stillEnabled())return;
        foreach(var set in sets.Where(s=>s.Map.Automatic && !s.Map.Favourite))
        {
            if(!stillEnabled())return;
            if(settings.Cleanup && set.Map.RetiredAt is null && (AutomaticPracticePolicy.IsMastered(set.Map,history)
                || now-AutomaticPracticePolicy.LastActivity(set.Map,set.Progress,history)>TimeSpan.FromDays(settings.InactiveDays)))
                await library.RunAsync(()=> {library.RetireAutomatic(set.Map,now);return true;},token).ConfigureAwait(false);
            if(settings.Cleanup && set.Map.RetiredAt is { } retired && now-retired>TimeSpan.FromDays(settings.RetentionDays))
                await library.RunAsync(()=> {library.PruneRetiredPayload(set.Map);return true;},token).ConfigureAwait(false);
        }
        var maps=await library.ListAsync(token).ConfigureAwait(false);
        await deliverPracticeMaps(maps, account, token).ConfigureAwait(false);
        var candidates=history.Where(r=>PracticeProgressTracker.SamePlayer(r,player) && r.Passed && r.Accuracy>=.7 && r.Accuracy<=1
            && r.PlayedAt>now.AddDays(-7) && ScoreMods.IsManualPlay(r) && evidence.TryGetValue(r.ScoreId,out var analysed) && analysed.Judgements.Count>0
            && !maps.Any(m=>m.Tracking?.Difficulties.Any(d=>string.Equals(d.Sha256,r.BeatmapHash,StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.Md5,r.BeatmapHash,StringComparison.OrdinalIgnoreCase))==true))
            .OrderByDescending(r=>r.PlayedAt).ToArray();
        foreach(var run in candidates)
        {
            if(automaticPracticeRetries.TryGetValue(run.ScoreId,out var retry) && now<retry)continue;
            if(!stillEnabled())return;
            var existing=maps.Where(m=>m.Tracking is { } t && t.AccountId==account && PracticeProgressTracker.SamePlayer(run,t.Player)
                && PracticeProgressTracker.SameSource(run,t)).OrderByDescending(m=>m.CreatedAt).FirstOrDefault();
            if(existing is not null)
            {
                if(AutomaticPracticePolicy.IsMastered(existing,history) || run.PlayedAt<=existing.CreatedAt)continue;
                if(existing.RetiredAt is null && !AutomaticPracticePolicy.NeedsRevision(existing,run,history,now))continue;
                if(!existing.Automatic || existing.Favourite)continue;
            }
            if(existing?.RetiredAt is not null && run.Accuracy>=.98 && run.MissCount==0)continue;
            if(existing?.RetiredAt is not null || existing is null)
                if(maps.Count(m=>m.Automatic && m.RetiredAt is null && m.Tracking?.AccountId==account)>=settings.MaximumActiveMaps)continue;
            if(run.Accuracy>=.98 && run.MissCount==0)continue;
            var request=new PracticeMapGenerationRequest(new(run,[run.ScoreId],1,run.MissCount,0),PracticeDrillType.Mixed,
                new(PracticeDrillType.Mixed,MaximumSections:6,PlaybackRate:1),CreateSet:true,SourceHistory:history,Automatic:true);
            var result=await createPracticeMap(request,token).ConfigureAwait(false);
            if(!result.Success)automaticPracticeRetries[run.ScoreId]=now.AddHours(1);
            if(result.Success && existing is not null && stillEnabled())
                await library.RunAsync(()=> {library.RetireAutomatic(existing,now);return true;},token).ConfigureAwait(false);
            if (result.Success && stillEnabled()) await deliverPracticeMaps(await library.ListAsync(token).ConfigureAwait(false), account, token).ConfigureAwait(false);
            // At most one expensive audio render per cycle; failed sources retry on the next cycle.
            return;
        }
    }
    private string automaticPracticeStatus = "Automatic practice is waiting for recent replay results.";
    private async Task deliverPracticeMaps(IReadOnlyList<SavedPracticeMap> maps, int account, CancellationToken token)
    {
        var owned = maps.Where(m => m.Tracking?.AccountId == account).ToArray();
        var settings = new AutomaticPracticeStore(Storage.GetFullPath("practice-maps", true)).Load();
        var active = owned.Where(m => AutomaticPracticeDelivery.ShouldDeliver(m, account, settings.Enabled)).ToArray();
        if (owned.Length == 0) return;
        bool stable = beatmapDestinationService?.Destination == OsuClientDestination.Stable
            || (beatmapDestinationService?.Destination != OsuClientDestination.Lazer && trainerLazerRoot is null);
        string? clientRoot = stable ? trainerStableRoot : trainerLazerRoot;
        if (clientRoot is null || lazerBeatmapInstallService is null || beatmapDestinationService is null) return;
        string clientKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(clientRoot).ToUpperInvariant())))[..16];
        string stateRoot = Storage.GetFullPath($"practice-maps/delivery/{clientKey}", true);
        string[] hashes = active.SelectMany(m => m.Tracking!.Difficulties).Select(d => d.Md5).ToArray();
        string[] retired = owned.Where(m => m.RetiredAt is not null).SelectMany(m => m.Tracking!.Difficulties).Select(d => d.Md5).ToArray();
        string collectionStatus = "";
        bool collectionVerified = false;
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (stable)
            {
                await Task.Run(() => PracticeCollectionSync.Stable(clientRoot, stateRoot, hashes, retired, () =>
                {
                    foreach (var process in Process.GetProcessesByName("osu!"))
                    {
                        using (process) try { if (Path.GetDirectoryName(process.MainModule?.FileName) == clientRoot) return true; }
                        catch { return true; }
                    }
                    return false;
                }), token).ConfigureAwait(false);
                for (int offset = 0; ; offset += 200)
                {
                    var page = await localLibrary.SearchBeatmapSetsAsync(new LocalLibraryQuery("AimMod", Offset: offset, Limit: 200), token).ConfigureAwait(false);
                    foreach (var difficulty in page.Items.SelectMany(s => s.Difficulties).Where(d => d.Origin == LocalLibraryOrigin.Stable)) installed.Add(difficulty.BeatmapHash);
                    if (!page.HasMore) break;
                }
            }
            else
            {
                var collection = await Task.Run(() => PracticeCollectionSync.Lazer(clientRoot, stateRoot, hashes, retired), token).ConfigureAwait(false);
                installed.UnionWith(collection.InstalledHashes);
            }
            collectionVerified = true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { collectionStatus = stable ? " Close osu!stable once to sync the coaching collection." : " Collection sync will retry; your sets remain available in AimMod."; logFailure("coaching collection", error); }
        var delivery = await new AutomaticPracticeDelivery(Path.Combine(stateRoot, $"delivery-{account}.json")).DeliverAsync(active, collectionVerified ? installed : null, async (map, ct) =>
        {
            if ((currentOsuProfile?.UserId ?? 0) != account || !AutomaticPracticeDelivery.ShouldDeliver(map, account,
                    new AutomaticPracticeStore(Storage.GetFullPath("practice-maps", true)).Load().Enabled))
                throw new OperationCanceledException();
            var archive = await lazerBeatmapInstallService.PreserveAsync(new PracticeMapLibrary(Storage.GetFullPath("practice-maps", true)).ArchivePath(map.Id), 0, ct).ConfigureAwait(false);
            return await installPracticeMap(archive, ct).ConfigureAwait(false);
        }, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
        string message = $"AimMod coaching · {delivery.Confirmed} sets installed";
        if (delivery.Pending > 0) message += $" · {delivery.Pending} awaiting import";
        if (delivery.Failed > 0) message += $" · {delivery.Failed} imports will retry";
        message += collectionStatus;
        Schedule(() => { automaticPracticeStatus = message; coachingWorkspace?.SetAutomaticPracticeStatus(message); });
    }

}
