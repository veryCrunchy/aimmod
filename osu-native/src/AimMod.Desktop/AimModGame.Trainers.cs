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
    private string? trainerStableConfiguration;
    private string? trainerLazerRoot;
    private Task? trainerSettingsRefresh;
    private string? trainerSettingsFailure;
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

    private Task refreshTrainerSettingsAsync()
        => trainerSettingsRefresh is { IsCompleted: false } pending ? pending
            : trainerSettingsRefresh = refreshTrainerSettingsCoreAsync();

    private async Task refreshTrainerSettingsCoreAsync()
    {
        try
        {
            trainerSettingsFailure = null;
            bool stable = beatmapDestinationService?.Destination == OsuClientDestination.Stable
                || (beatmapDestinationService?.Destination != OsuClientDestination.Lazer && trainerLazerRoot is null);
            TrainerOsuSettings? inherited = null;
            TrainerGameplayPreferences? gameplayPreferences = null;
            if (stable && trainerStableRoot is { } stableRoot)
            {
                string exact = Path.Combine(stableRoot, $"osu!.{Environment.UserName}.cfg");
                // Use the same selected user config as library discovery, including
                // portable installs moved from an older Windows account.
                string[] configs = File.Exists(exact) ? [exact]
                    : trainerStableConfiguration is { } selected && File.Exists(selected) ? [selected]
                    : Directory.GetFiles(stableRoot, "osu!.*.cfg");
                if (configs.Length == 1 && new FileInfo(configs[0]).Length <= 1024 * 1024)
                {
                    string contents = await File.ReadAllTextAsync(configs[0], appLifetime.Token);
                    inherited = TrainerOsuSettingsReader.Stable(contents);
                    gameplayPreferences = TrainerGameplayPreferences.Stable(contents);
                }
                else throw new IOException("The current osu!stable user configuration could not be identified.");
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
                    if (!File.Exists(gameIni) || new FileInfo(gameIni).Length > 1024 * 1024)
                        throw new IOException("The lazer gameplay configuration is unavailable.");
                    string gameContents = await File.ReadAllTextAsync(gameIni, appLifetime.Token);
                    bool mouseButtons = TrainerOsuSettingsReader.LazerMouseButtons(gameContents);
                    gameplayPreferences = new TrainerGameplayPreferences(gameContents);
                    inherited = TrainerOsuSettingsReader.Lazer(result, lazerPreferencesMonitor?.Current.AudioOffset ?? 0, mouseButtons);
                    string inputFile = Path.Combine(lazerRoot, "input.json");
                    if (File.Exists(inputFile) && new FileInfo(inputFile).Length <= 1024 * 1024)
                        inherited = inherited with { Input = TrainerInputSettings.Lazer(await File.ReadAllTextAsync(inputFile, appLifetime.Token)) };
                }
                else throw new IOException("The osu! settings worker did not return valid controls.");
            }
            trainerOsuSettings = inherited;
            trainerGameplayPreferences = gameplayPreferences;
            if (inherited is { } settings && !IsDisposed)
                Schedule(() => { if (ReferenceEquals(trainerOsuSettings, settings)) trainersWorkspace?.ApplyOsuSettings(settings); });
        }
        catch (OperationCanceledException) when (appLifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            trainerOsuSettings = null;
            trainerGameplayPreferences = null;
            trainerSettingsFailure = "Your osu! controls could not be read. Try again or check the osu! installation in Settings.";
            try
            {
                if (new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!).AvailableFreeSpace < 16 * 1024 * 1024)
                    trainerSettingsFailure = "There is not enough free disk space to read your osu! controls. Free some space and try again.";
            }
            catch (Exception spaceError) when (spaceError is IOException or UnauthorizedAccessException or ArgumentException) { }
            logFailure("trainer preferences", error);
            osu.Framework.Logging.Logger.Error(error, "AimMod could not read osu! trainer settings.");
        }
    }
}
