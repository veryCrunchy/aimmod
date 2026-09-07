using System.Text.Json;
using AimMod.Desktop.Trainers;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using osu.Game.Configuration;
using AimMod.Desktop.LocalLibrary;

namespace AimMod.Desktop;

public partial class AimModGame
{
    private string? trainerStableRoot;
    private string? trainerLazerRoot;
    private bool trainerSettingsLoading;
    private TrainerOsuSettings? trainerOsuSettings;
    private TrainerGameplayPreferences? trainerGameplayPreferences;
    private bool trainerEvidenceLoading;
    private async Task refreshTrainerSkillEvidenceAsync()
    {
        if(trainerEvidenceLoading || trainersWorkspace is null) return;
        trainersWorkspace.SkillEvidence=[];
        trainersWorkspace.SkillEvidenceAccountId=null;
        var account=currentOsuProfile;
        if(account is null) { trainersWorkspace.RefreshHistory(); return; }
        trainerEvidenceLoading=true;
        var analyses=replayAnalyses.ToDictionary(p=>p.Key,p=>p.Value);
        try
        {
            var evidence=new List<TrainerSkillEvidence>();
            for(int offset=0;offset<1000;offset+=200)
            {
                var page=await localLibrary.SearchReplaysAsync(new LocalLibraryQuery(Sort:LocalLibrarySort.RecentlyPlayed,Offset:offset,Limit:200),appLifetime.Token).ConfigureAwait(false);
                var samples=await Task.Run(()=>page.Items.Select(run=>analyses.TryGetValue(run.ScoreId,out var analysis)
                    ? TrainerSkillProfile.FromReplay(run,analysis,account.Username,DateTimeOffset.UtcNow) : null).OfType<TrainerSkillEvidence>().ToArray(),appLifetime.Token).ConfigureAwait(false);
                evidence.AddRange(samples);
                if(!page.HasMore || evidence.Count>=40) break;
            }
            Schedule(()=> { if(currentOsuProfile?.UserId==account.UserId && trainersWorkspace is {} workspace) { workspace.SkillEvidence=evidence; workspace.SkillEvidenceAccountId=account.UserId; workspace.RefreshHistory(); } });
        }
        catch(OperationCanceledException) when(appLifetime.IsCancellationRequested) { }
        catch(Exception error) { logFailure("trainer skill evidence",error); }
        finally { trainerEvidenceLoading=false; }
    }

    private async Task refreshTrainerSettingsAsync()
    {
        if (trainerSettingsLoading) return;
        trainerSettingsLoading = true;
        try
        {
            bool stable = beatmapDestinationService?.Destination == OsuClientDestination.Stable
                || (beatmapDestinationService?.Destination != OsuClientDestination.Lazer && trainerLazerRoot is null);
            TrainerOsuSettings? inherited = null;
            if (stable && trainerStableRoot is { } stableRoot)
            {
                trainerGameplayPreferences = null;
                string exact = Path.Combine(stableRoot, $"osu!.{Environment.UserName}.cfg");
                // Do not guess between another Windows user's configurations.
                string[] configs = File.Exists(exact) ? [exact] : Directory.GetFiles(stableRoot, "osu!.*.cfg");
                if (configs.Length == 1 && new FileInfo(configs[0]).Length <= 1024 * 1024)
                    inherited = TrainerOsuSettingsReader.Stable(await File.ReadAllTextAsync(configs[0], appLifetime.Token));
            }
            else if (trainerLazerRoot is { } lazerRoot)
            {
                await using var runtime = SidecarRuntimeClient.Start();
                var client = new SidecarRuntimeRequestClient(runtime);
                var request = RuntimeProtocol.CreateRequest(RuntimeCommands.ReadExternalTrainerSettings, new ExternalTrainerSettingsRequest(lazerRoot));
                var response = await client.SendAsync(request, appLifetime.Token);
                if (response.Id == request.Id && response.ProtocolVersion == RuntimeProtocol.CurrentVersion
                    && response.Success && response.Error is null && response.Payload is { } payload
                    && payload.Deserialize<ExternalTrainerSettingsResult>(RuntimeProtocol.JsonOptions) is { } result)
                {
                    string gameIni = Path.Combine(lazerRoot, "game.ini");
                    bool mouseButtons = File.Exists(gameIni) && new FileInfo(gameIni).Length <= 1024 * 1024
                        ? TrainerOsuSettingsReader.LazerMouseButtons(await File.ReadAllTextAsync(gameIni, appLifetime.Token))
                        : !LocalConfig.Get<bool>(OsuSetting.MouseDisableButtons);
                    if (File.Exists(gameIni)) trainerGameplayPreferences = new TrainerGameplayPreferences(await File.ReadAllTextAsync(gameIni, appLifetime.Token));
                    inherited = TrainerOsuSettingsReader.Lazer(result, lazerPreferencesMonitor?.Current.AudioOffset ?? 0, mouseButtons);
                    string inputFile = Path.Combine(lazerRoot, "input.json");
                    if (File.Exists(inputFile) && new FileInfo(inputFile).Length <= 1024 * 1024)
                        inherited = inherited with { Input = TrainerInputSettings.Lazer(await File.ReadAllTextAsync(inputFile, appLifetime.Token)) };
                }
            }
            if (inherited is { } settings && !IsDisposed)
                Schedule(() => { trainerOsuSettings = settings; trainersWorkspace?.ApplyOsuSettings(settings); });
        }
        catch (OperationCanceledException) when (appLifetime.IsCancellationRequested) { }
        catch (Exception error) { logFailure("trainer preferences", error); }
        finally { trainerSettingsLoading = false; }
    }
}
