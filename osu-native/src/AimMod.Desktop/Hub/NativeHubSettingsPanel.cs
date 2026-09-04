using AimMod.Desktop.Visuals;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.Hub;

public partial class NativeHubSettingsPanel : CompositeDrawable
{
    private readonly HubDeviceLinkClient? deviceLinkClient;
    private readonly IHubCredentialStore? credentialStore;
    private readonly IOsuHubUploadQueue? uploadQueue;
    private readonly IHubSharingPreferenceStore? preferenceStore;
    private readonly Action<Uri>? openUrl;
    private readonly Action<string>? copyText;
    private readonly SpriteText accountStatus;
    private readonly SpriteText deviceCode;
    private readonly OsuButton linkButton;
    private readonly OsuButton unlinkButton;
    private readonly OsuButton copyCodeButton;
    private readonly Bindable<OsuHubVisibility> visibility;
    private readonly BindableBool uploadReplayFile;
    private readonly BindableBool uploadAnalysis;
    private readonly SpriteText queueSummary;
    private readonly FillFlowContainer<Drawable> queueRows;
    private CancellationTokenSource? linking;
    private HubDeviceLinkSession? currentSession;

    public NativeHubSettingsPanel(
        HubDeviceLinkClient? deviceLinkClient,
        IHubCredentialStore? credentialStore,
        IOsuHubUploadQueue? uploadQueue,
        IHubSharingPreferenceStore? preferenceStore,
        Action<Uri>? openUrl,
        Action<string>? copyText)
    {
        this.deviceLinkClient = deviceLinkClient;
        this.credentialStore = credentialStore;
        this.uploadQueue = uploadQueue;
        this.preferenceStore = preferenceStore;
        this.openUrl = openUrl;
        this.copyText = copyText;

        HubSharingPreferences preferences = preferenceStore?.Load() ?? HubSharingPreferences.Default;
        visibility = new Bindable<OsuHubVisibility>(preferences.Visibility);
        uploadReplayFile = new BindableBool(preferences.UploadReplayFile);
        uploadAnalysis = new BindableBool(preferences.UploadAnalysis);

        RelativeSizeAxes = Axes.X;
        AutoSizeAxes = Axes.Y;
        InternalChild = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(AimModVisualStyle.SectionSpacing),
            Children = new Drawable[]
            {
                panel("AimMod Hub", "Connect your account to share scores and replays.", new Drawable[]
                {
                    accountStatus = text("AimMod Hub is not linked.", 15, AimModPalette.Text, "SemiBold"),
                    new FillFlowContainer
                    {
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Horizontal,
                        Spacing = new(AimModVisualStyle.RelatedSpacing),
                        Children = new Drawable[]
                        {
                            linkButton = button("Link account", beginLink, 142, AimModPalette.Pink),
                            unlinkButton = button("Unlink", unlink, 96, AimModPalette.PanelHover),
                        },
                    },
                    deviceCode = text("", 20, AimModPalette.Cyan, "Bold"),
                    copyCodeButton = button("Copy code", copyCode, 116, AimModPalette.PanelHover),
                }),
                panel("Sharing", "Default options for new replay shares.", new Drawable[]
                {
                    text("VISIBILITY", 10, AimModPalette.Cyan, "Bold"),
                    new OsuDropdown<OsuHubVisibility>
                    {
                        Width = 360,
                        Items = Enum.GetValues<OsuHubVisibility>(),
                        Current = visibility,
                    },
                    new OsuCheckbox { LabelText = "Include replay file", Current = uploadReplayFile, RelativeSizeAxes = Axes.X },
                    new OsuCheckbox { LabelText = "Include judgement analysis", Current = uploadAnalysis, RelativeSizeAxes = Axes.X },
                    text("Uploads start only when you share a replay.", 12, AimModPalette.Muted),
                }),
                panel("Uploads", "Recent shares and pending uploads.", new Drawable[]
                {
                    queueSummary = text("No queued uploads.", 12, AimModPalette.Muted, "SemiBold"),
                    queueRows = new FillFlowContainer<Drawable>
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Spacing = new(AimModVisualStyle.RelatedSpacing),
                    },
                }),
            },
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        visibility.BindValueChanged(_ => savePreferences());
        uploadReplayFile.BindValueChanged(_ => savePreferences());
        uploadAnalysis.BindValueChanged(_ => savePreferences());
        if (uploadQueue is not null)
            uploadQueue.Changed += queueChanged;
        refreshAccount();
        refreshQueue();
    }

    protected override void Dispose(bool isDisposing)
    {
        if (isDisposing)
        {
            linking?.Cancel();
            linking?.Dispose();
            if (uploadQueue is not null)
                uploadQueue.Changed -= queueChanged;
        }
        base.Dispose(isDisposing);
    }

    private void beginLink()
    {
        if (deviceLinkClient is null || credentialStore is null)
        {
            setAccountState("AimMod Hub linking is unavailable in this session.", AimModPalette.Pink);
            return;
        }

        linking?.Cancel();
        linking?.Dispose();
        linking = new CancellationTokenSource();
        setAccountState("Requesting a secure device code...", AimModPalette.Cyan);
        linkButton.Enabled.Value = false;
        _ = linkAsync(linking.Token);
    }

    private async Task linkAsync(CancellationToken cancellationToken)
    {
        try
        {
            HubDeviceLinkSession session = await deviceLinkClient!.BeginAsync(Environment.MachineName, cancellationToken).ConfigureAwait(false);
            currentSession = session;
            if (!IsDisposed)
            {
                Schedule(() =>
                {
                    deviceCode.Text = $"CODE  {session.UserCode}";
                    deviceCode.Alpha = 1;
                    copyCodeButton.Alpha = 1;
                    setAccountState("Approve this device in the AimMod Hub page opened in your browser.", AimModPalette.Cyan);
                    openUrl?.Invoke(session.VerificationUriComplete);
                });
            }

            HubDeviceLinkPollResult result = await deviceLinkClient.WaitForApprovalAsync(session, cancellationToken).ConfigureAwait(false);
            if (!IsDisposed)
                Schedule(() => applyLinkResult(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!IsDisposed)
                Schedule(() =>
                {
                    setAccountState(error is HttpRequestException
                        ? "AimMod Hub could not be reached. Check your connection and try again."
                        : error.Message, AimModPalette.Pink);
                    linkButton.Enabled.Value = true;
                });
        }
    }

    private void applyLinkResult(HubDeviceLinkPollResult result)
    {
        linkButton.Enabled.Value = true;
        currentSession = null;
        deviceCode.Alpha = 0;
        copyCodeButton.Alpha = 0;
        if (result.Status == HubDeviceLinkStatus.Approved)
            refreshAccount();
        else
            setAccountState("The device code expired. Start linking again when ready.", AimModPalette.Pink);
    }

    private void unlink()
    {
        linking?.Cancel();
        credentialStore?.Clear();
        currentSession = null;
        refreshAccount();
    }

    private void copyCode()
    {
        if (currentSession is not null)
            copyText?.Invoke(currentSession.UserCode);
    }

    private void refreshAccount()
    {
        HubCredential? credential = credentialStore?.Load();
        bool linked = credential is not null;
        setAccountState(linked
            ? $"Linked as {credential!.AccountLabel}  //  {credential.LinkedAt.LocalDateTime:g}"
            : "AimMod Hub is not linked.", linked ? AimModPalette.Success : AimModPalette.Text);
        unlinkButton.Alpha = linked ? 1 : 0;
        unlinkButton.Enabled.Value = linked;
        linkButton.Text = linked ? "Relink account" : "Link account";
        deviceCode.Alpha = 0;
        copyCodeButton.Alpha = 0;
    }

    private void setAccountState(string value, Colour4 colour)
    {
        accountStatus.Text = value;
        accountStatus.Colour = colour;
    }

    private void savePreferences()
    {
        if (preferenceStore is not null)
            _ = preferenceStore.SaveAsync(new HubSharingPreferences(visibility.Value, uploadReplayFile.Value, uploadAnalysis.Value));
    }

    private void queueChanged()
    {
        if (!IsDisposed)
            Schedule(refreshQueue);
    }

    private void refreshQueue()
    {
        IReadOnlyList<HubUploadQueueItem> items = uploadQueue?.Snapshot() ?? [];
        int active = items.Count(item => item.Status is HubUploadQueueStatus.Queued or HubUploadQueueStatus.Uploading);
        int failed = items.Count(item => item.Status == HubUploadQueueStatus.Failed);
        queueSummary.Text = items.Count == 0
            ? "No queued uploads."
            : $"{active:N0} active  //  {failed:N0} need attention  //  {items.Count:N0} retained";
        queueRows.Clear();
        foreach (HubUploadQueueItem item in items.Take(6))
            queueRows.Add(queueRow(item));
    }

    private Drawable queueRow(HubUploadQueueItem item)
    {
        bool retryable = item.Status is HubUploadQueueStatus.Failed or HubUploadQueueStatus.Cancelled;
        bool cancellable = item.Status is HubUploadQueueStatus.Queued or HubUploadQueueStatus.Uploading;
        OsuButton action = button(retryable ? "Retry" : "Cancel", () =>
        {
            if (uploadQueue is null)
                return;
            _ = retryable ? uploadQueue.RetryAsync(item.Id) : uploadQueue.CancelAsync(item.Id);
        }, 78, retryable ? AimModPalette.Pink : AimModPalette.PanelHover);
        action.Anchor = Anchor.CentreRight;
        action.Origin = Anchor.CentreRight;
        action.X = -10;
        action.Alpha = retryable || cancellable ? 1 : 0;
        action.Enabled.Value = retryable || cancellable;

        return new Container
        {
            RelativeSizeAxes = Axes.X,
            Height = 58,
            Masking = true,
            CornerRadius = AimModVisualStyle.ControlRadius,
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Width = 0.78f,
                    Padding = new MarginPadding { Left = 12, Top = 8 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(3),
                    Children = new Drawable[]
                    {
                        text(item.Title, 11, AimModPalette.Text, "SemiBold"),
                        text(queueDetail(item), 9, statusColour(item.Status)),
                    },
                },
                action,
            },
        };
    }

    private static string queueDetail(HubUploadQueueItem item)
    {
        string status = item.Status switch
        {
            HubUploadQueueStatus.Queued => "Waiting",
            HubUploadQueueStatus.Uploading => "Uploading",
            HubUploadQueueStatus.Completed => item.Request.Visibility == "private" ? "Private copy ready" : "Share link ready",
            HubUploadQueueStatus.Failed => item.Error,
            _ => "Cancelled",
        };
        return item.AttemptCount > 0 ? $"{status}  //  attempt {item.AttemptCount:N0}" : status;
    }

    private static Colour4 statusColour(HubUploadQueueStatus status) => status switch
    {
        HubUploadQueueStatus.Completed => AimModPalette.Success,
        HubUploadQueueStatus.Failed => AimModPalette.Pink,
        HubUploadQueueStatus.Uploading => AimModPalette.Cyan,
        _ => AimModPalette.Muted,
    };

    internal HubSharingPreferences PreferencesForTesting => new(visibility.Value, uploadReplayFile.Value, uploadAnalysis.Value);

    private static Container panel(string title, string subtitle, IReadOnlyList<Drawable> children) => new()
    {
        RelativeSizeAxes = Axes.X,
        AutoSizeAxes = Axes.Y,
        Children = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.X,
                Height = 1,
                Colour = AimModPalette.PanelHover,
            },
            new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Depth = 0,
                Padding = new MarginPadding { Top = 24, Bottom = 8 },
                Direction = FillDirection.Vertical,
                Spacing = new(12),
                Children = new Drawable[]
                {
                    text(title, 20, AimModPalette.Text, "Bold"),
                    text(subtitle, 12, AimModPalette.Muted),
                }.Concat(children).ToArray(),
            },
        },
    };

    private static OsuButton button(string label, Action action, float width, Colour4 colour) => new HubButton
    {
        Text = label,
        Action = action,
        Width = width,
        Height = AimModVisualStyle.CompactControlHeight,
        BackgroundColour = colour,
    };

    private static SpriteText text(string value, float size, Colour4 colour, string weight = "Regular") => new()
    {
        Text = value,
        Font = new FontUsage(size: size, weight: weight),
        Colour = colour,
    };

    private partial class HubButton : OsuButton
    {
        public HubButton()
        {
            AutoSizeAxes = Axes.None;
        }
    }
}
