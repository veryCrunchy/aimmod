using AimMod.Desktop.Hub;
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
        Action<string>? copyText)
    {
        this.destinationService = destinationService ?? throw new ArgumentNullException(nameof(destinationService));
        destination = new Bindable<string>(label(destinationService.Destination));
        RelativeSizeAxes = Axes.Both;
        InternalChildren = new Drawable[]
        {
            new AimModSectionHeader(
                "Settings",
                "Choose which installed osu! client receives beatmaps and generated practice maps.",
                "osu! client"),
            new SpriteText
            {
                Y = 104,
                Text = "OPEN AND INSTALL DESTINATION",
                Font = new FontUsage(size: 10, weight: "Bold"),
                Colour = AimModPalette.Cyan,
            },
            new OsuDropdown<string>
            {
                Y = 124,
                Width = 360,
                Items = new[] { "Auto", "osu!stable", "osu!lazer" },
                Current = destination,
            },
            detail = new SpriteText
            {
                Y = 178,
                Font = new FontUsage(size: 13),
                Colour = AimModPalette.Muted,
            },
            new AimModScrollContainer
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = 220 },
                Child = new NativeHubSettingsPanel(
                    deviceLinkClient,
                    credentialStore,
                    uploadQueue,
                    preferenceStore,
                    openUrl,
                    copyText)
                {
                    RelativeSizeAxes = Axes.X,
                },
            },
        };
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
            OsuClientDestination.Stable => "Beatmaps are opened with osu!stable. AimMod will not fall back to another client.",
            OsuClientDestination.Lazer => "Beatmaps are opened with osu!lazer. AimMod will not fall back to another client.",
            _ => "AimMod tries osu!lazer first, then osu!stable when lazer is unavailable.",
        };
    }
}
