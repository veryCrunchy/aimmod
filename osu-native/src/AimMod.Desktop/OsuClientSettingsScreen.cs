using AimMod.Desktop.Hub;
using AimMod.Desktop.Practice;
using AimMod.Desktop.Onboarding;
using AimMod.Desktop.Visuals;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop;

public partial class OsuClientSettingsScreen : CompositeDrawable
{
    private readonly IOsuBeatmapDestinationService destinationService;
    private readonly Bindable<string> destination;
    private readonly SpriteText detail;
    private readonly FillFlowContainer content;
    private readonly FillFlowContainer settingsNavigation = new() {RelativeSizeAxes=Axes.X,AutoSizeAxes=Axes.Y,Direction=FillDirection.Full,Spacing=new(8)};
    private readonly List<Drawable> settingsPages = [];
    private int selectedSettingsPage;

    public OsuClientSettingsScreen(IOsuBeatmapDestinationService destinationService)
        : this(destinationService, null, null, null, null, null, null)
    {
    }

    public OsuClientSettingsScreen(
        IOsuBeatmapDestinationService destinationService,
        HubDeviceLinkClient? deviceLinkClient,
        IHubCredentialStore? credentialStore,
        IOsuHubUploadQueue? uploadQueue,
        IHubSharingPreferenceStore? preferenceStore,
        Action<Uri>? openUrl,
        Action<string>? copyText, AutomaticPracticeStore? automaticPractice = null, UserSetupStore? setupStore = null, Action? reopenSetup = null, Action? previewSound = null, bool inline = false, Func<string>? trainingStatus = null)
    {
        this.destinationService = destinationService ?? throw new ArgumentNullException(nameof(destinationService));
        destination = new Bindable<string>(label(destinationService.Destination));
        RelativeSizeAxes = Axes.Both;
        var settingsScroll = new AimModScrollContainer
        {
            RelativeSizeAxes = Axes.Both,
            Child = content = new FillFlowContainer
            {
                Width = 680,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new(20),
                Padding = new MarginPadding { Right = 24, Bottom = 32 },
                Children = new Drawable[]
        {
            new AimModSectionHeader(
                "Settings",
                "Your osu! client, account and sharing preferences.",
                "Preferences"),
            new SpriteText
            {
                Text = "OPEN AND INSTALL DESTINATION",
                Font = new FontUsage(size: 10, weight: "Bold"),
                Colour = AimModPalette.Cyan,
            },
            new AimModDropdown<string>
            {
                Width = 360,
                Items = new[] { "Auto", "osu!stable", "osu!lazer" },
                Current = destination,
            },
            detail = new SpriteText
            {
                Font = new FontUsage(size: 13),
                Colour = AimModPalette.Muted,
            },
            new NativeHubSettingsPanel(
                    deviceLinkClient,
                    credentialStore,
                    uploadQueue,
                    preferenceStore,
                    openUrl,
                    copyText)
                {
                    RelativeSizeAxes = Axes.X,
                    TrainingStatus = trainingStatus,
                },
        },
            },
        };
        InternalChild = settingsScroll;
        if(inline)
        {
            settingsScroll.Clear(false);
            InternalChild=content;
            RelativeSizeAxes=Axes.X;
            AutoSizeAxes=Axes.Y;
        }
        int practiceStart=content.Count;
        if(automaticPractice is not null)addAutomaticPracticeSettings(automaticPractice);
        int startupStart=content.Count;
        if(setupStore is not null)
        {
            content.Add(new SpriteText {Text="STARTUP & INTRODUCTION",Font=new FontUsage(size:14,weight:"Bold"),Colour=AimModPalette.Cyan});
            var sound=new Bindable<string>(setupStore.Load().StartupSound?"Startup sound on":"Startup sound off");
            content.Add(new AimModDropdown<string>{Width=360,Items=new[]{"Startup sound on","Startup sound off"},Current=sound});
            var soundStatus=new SpriteText {Text="AimMod electronic pulse · follows app volume",Font=new FontUsage(size:12),Colour=AimModPalette.Muted};content.Add(soundStatus);
            sound.BindValueChanged(v=>{try{setupStore.Save(setupStore.Load() with {StartupSound=v.NewValue=="Startup sound on"});}catch(IOException){soundStatus.Text="Could not save startup sound. Try again.";}});
            if(previewSound is not null)content.Add(new UserSetupScreen.SetupButton("Preview startup sound",previewSound));
            if(reopenSetup is not null)content.Add(new UserSetupScreen.SetupButton("Open setup & app tour",reopenSetup));
        }
        var items=content.Children.ToArray();
        content.Clear(false);
        content.Spacing=new(14);
        content.Add(items[0]);
        content.Add(settingsNavigation);
        addSettingsPage(items.Skip(1).Take(3));
        addSettingsPage(items.Skip(4).Take(practiceStart-4));
        addSettingsPage(items.Skip(practiceStart).Take(startupStart-practiceStart));
        addSettingsPage(items.Skip(startupStart));
        showSettingsPage(0);
    }

    private void addSettingsPage(IEnumerable<Drawable> children)
    {
        var page=new FillFlowContainer {RelativeSizeAxes=Axes.X,AutoSizeAxes=Axes.Y,Direction=FillDirection.Vertical,Spacing=new(12),Padding=new MarginPadding(24)};
        var controls=children.ToArray();
        for(int i=0;i<controls.Length;i++)
            if(controls[i] is OsuDropdown<string>)controls[i].Depth=-100+i;
        page.AddRange(controls);
        var surface=new SettingsSurface(page);
        settingsPages.Add(surface);
        content.Add(surface);
    }
    private void showSettingsPage(int selected)
    {
        selectedSettingsPage=selected;
        settingsNavigation.Clear();
        foreach(var (name,index) in new[]{"General","Account & sharing","Practice","Startup & tour"}.Select((name,index)=>(name,index)))
            {
            var tab = new AimModButton(name,()=>showSettingsPage(index));
            tab.SetSelected(index==selected); settingsNavigation.Add(tab);
        }
        for(int i=0;i<settingsPages.Count;i++)settingsPages[i].Alpha=i==selected?1:0;
    }
    private sealed partial class SettingsSurface : Container
    {
        public SettingsSurface(Drawable body)
        {
            RelativeSizeAxes=Axes.X;AutoSizeAxes=Axes.Y;
            Children=[new Container {RelativeSizeAxes=Axes.Both,Masking=true,CornerRadius=8,BorderThickness=1,BorderColour=Colour4.White.Opacity(.06f),
                Child=new osu.Framework.Graphics.Shapes.Box{RelativeSizeAxes=Axes.Both,Colour=AimModPalette.Panel}},body];
        }
    }

    private void addAutomaticPracticeSettings(AutomaticPracticeStore store)
    {
        var settings=store.Load();
        content.Add(new SpriteText {Text="AUTOMATIC PRACTICE",Font=new FontUsage(size:14,weight:"Bold"),Colour=AimModPalette.Cyan});
        content.Add(new SpriteText {Text="Create sets from recent replay results and update them as you improve.",Font=new FontUsage(size:13),Colour=AimModPalette.Muted});
        var enabled=new Bindable<string>(settings.Enabled?"On":"Off");
        content.Add(new AimModDropdown<string>{Width=360,Items=new[]{"Off","On"},Current=enabled});
        var status=new SpriteText {Font=new FontUsage(size:12),Colour=AimModPalette.Muted};
        content.Add(status);
        enabled.BindValueChanged(v=> {
            try { settings=settings with {Enabled=v.NewValue=="On"};store.Save(settings);status.Text=settings.Enabled?"AimMod practice · up to 5 active maps · updates while AimMod is open":"Automatic practice is off"; }
            catch(IOException){status.Text="Could not save automatic practice. Try again.";}
        });
        status.Text="AimMod practice · up to 5 active maps · updates while AimMod is open";
        content.Add(new SpriteText {Text="CLEANUP",Font=new FontUsage(size:12,weight:"Bold"),Colour=AimModPalette.Cyan});
        var cleanup=new Bindable<string>(settings.Cleanup?"Automatic":"Keep all sets");
        content.Add(new AimModDropdown<string>{Width=360,Items=new[]{"Automatic","Keep all sets"},Current=cleanup});
        content.Add(new SpriteText {Text="Archive mastered or 30-day inactive sets. Remove old generated files after 7 days.",Font=new FontUsage(size:12),Colour=AimModPalette.Muted});
        content.Add(new SpriteText {Text="Progress history and favourites are kept. Manual sets are never cleaned up.",Font=new FontUsage(size:12),Colour=AimModPalette.Muted});
        cleanup.BindValueChanged(v=> {try{settings=settings with {Cleanup=v.NewValue=="Automatic"};store.Save(settings);}catch(IOException){status.Text="Could not save cleanup. Try again.";}});
    }

    protected override void Update()
    {
        base.Update();
        content.Width = Math.Max(0, DrawWidth);
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        destination.BindValueChanged(value =>
        {
            OsuClientDestination selected = parse(value.NewValue);
            destinationService.Destination = selected;
            updateDetail(selected);
        }, true);
        destinationService.DestinationChanged += destinationChanged;
    }

    protected override void Dispose(bool isDisposing)
    {
        if (isDisposing)
            destinationService.DestinationChanged -= destinationChanged;
        base.Dispose(isDisposing);
    }

    private void destinationChanged(OsuClientDestination value) => Schedule(() => destination.Value = label(value));

    private static string label(OsuClientDestination value) => value switch
    {
        OsuClientDestination.Stable => "osu!stable",
        OsuClientDestination.Lazer => "osu!lazer",
        _ => "Auto",
    };

    private static OsuClientDestination parse(string value) => value switch
    {
        "osu!stable" => OsuClientDestination.Stable,
        "osu!lazer" => OsuClientDestination.Lazer,
        _ => OsuClientDestination.Auto,
    };

    private void updateDetail(OsuClientDestination value)
    {
        detail.Text = value switch
        {
            OsuClientDestination.Stable => "Open and install beatmaps in osu!stable.",
            OsuClientDestination.Lazer => "Open and install beatmaps in osu!lazer.",
            _ => "Use osu!lazer when available, otherwise osu!stable.",
        };
    }
}
