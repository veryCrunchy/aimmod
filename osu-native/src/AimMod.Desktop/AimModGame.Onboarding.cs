using AimMod.Desktop.Onboarding;
using AimMod.Desktop.Practice;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Graphics;
using osu.Framework.IO.Stores;

namespace AimMod.Desktop;

public partial class AimModGame
{

    private ISample? startupTheme;
    private ISampleChannel? startupChannel;
    private bool startupThemePlayed;
    private UserSetupScreen? userSetupScreen;
    private UserSetupStore userSetupStore => new(Storage.GetFullPath("user-setup.json",true));

    private OsuClientSettingsScreen createUserSettings(bool inSetup=false) => new(
        beatmapDestinationService!,hubDeviceLinkClient,hubCredentialStore,hubUploadQueue,hubSharingPreferenceStore,
        openHubUrl,copyHubText,new AutomaticPracticeStore(Storage.GetFullPath("practice-maps",true)),userSetupStore,
        inSetup?null:showUserSetup,()=>playTheme(true), inline:inSetup, trainingStatus:()=>hubTrainingSyncService?.Status ?? "Training sync is unavailable.",
        creatorSettings: inSetup ? null : new Creator.CreatorSettingsPanel(creatorSettingsStore, creatorToolsEnabled, setCreatorToolsEnabled, showCreatorTools));

    private void showFirstRunSetup()
    {
        if(configuredLocalLibrary is not null || launchOptions.DeepLink is not null || launchOptions.Replay is not null || launchOptions.Error is not null)return;
        if(!userSetupStore.Load().Completed)showUserSetup();
    }
    private void showUserSetup()
    {
        if(beatmapDestinationService is null)return;
        userSetupScreen ??= new UserSetupScreen(userSetupStore,()=>createUserSettings(true),showHome,
            ()=>openHubUrl(new Uri("https://aimmod.app/osu/help")));
        userSetupScreen.Restart();
        switchWorkspaceRoute(NativeRoute.Setup,userSetupScreen);
    }
    private void playStartupTheme()
    {
        if(startupThemePlayed)return;
        startupThemePlayed=true;
        if(userSetupStore.Load().StartupSound)playTheme(false);
    }
    private void playTheme(bool preview)
    {
        try
        {
            startupTheme ??= Audio.GetSampleStore(new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(AimModGame).Assembly),"Resources/Audio")).Get("startup.ogg");
            if(startupTheme is null)return;
            startupChannel?.Stop();
            startupTheme.Volume.Value=.4;
            startupChannel=startupTheme.Play();
        }
        catch(Exception error){logFailure("startup sound",error);}
    }
}
