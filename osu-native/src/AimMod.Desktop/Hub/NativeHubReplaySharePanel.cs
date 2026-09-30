using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.Hub;

public partial class NativeHubReplaySharePanel : CompositeDrawable
{
    private readonly OsuHubReplayShareService? shareService;
    private readonly IHubCredentialStore? credentialStore;
    private readonly IOsuHubUploadQueue? uploadQueue;
    private readonly IHubSharingPreferenceStore? preferenceStore;
    private readonly Action<Uri>? openUrl;
    private readonly Action<string>? copyText;
    private readonly Bindable<OsuHubVisibility> visibility = new(HubSharingPreferences.Default.Visibility);
    private readonly BindableBool uploadReplayFile = new(false);
    private readonly BindableBool uploadAnalysis = new(false);
    private readonly OsuCheckbox replayFileCheckbox;
    private readonly OsuCheckbox analysisCheckbox;
    private readonly OsuButton shareButton;
    private readonly OsuButton cancelRetryButton;
    private readonly OsuButton copyButton;
    private readonly OsuButton openButton;
    private readonly OsuTextFlowContainer status;
    private string statusText = string.Empty;
    private LocalReplay? replay;
    private bool analysisAvailable;
    private Guid? queueItemId;
    private string shareUrl = string.Empty;
    private CancellationTokenSource? preparing;

    public NativeHubReplaySharePanel(
        OsuHubReplayShareService? shareService,
        IHubCredentialStore? credentialStore,
        IOsuHubUploadQueue? uploadQueue,
        IHubSharingPreferenceStore? preferenceStore,
        Action<Uri>? openUrl,
        Action<string>? copyText)
    {
        this.shareService = shareService;
        this.credentialStore = credentialStore;
        this.uploadQueue = uploadQueue;
        this.preferenceStore = preferenceStore;
        this.openUrl = openUrl;
        this.copyText = copyText;

        RelativeSizeAxes = Axes.X;
        AutoSizeAxes = Axes.Y;
        // A vertical flow lets long status and error text wrap without overlapping the controls.
        InternalChildren = new Drawable[]
        {
            new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new(AimModVisualStyle.RowSpacing),
                Padding = new MarginPadding { Left = 14, Top = 12, Right = 12, Bottom = 12 },
                Children = new Drawable[]
                {
                    status = new OsuTextFlowContainer(t =>
                    {
                        t.Font = new FontUsage(size: AimModVisualStyle.MinReadableFontSize, weight: "SemiBold");
                    })
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Colour = AimModPalette.Muted,
                    },
                    new AimModDropdown<OsuHubVisibility>
                    {
                        RelativeSizeAxes = Axes.X,
                        Depth = -10,
                        Items = Enum.GetValues<OsuHubVisibility>(),
                        Current = visibility,
                    },
                    replayFileCheckbox = new OsuCheckbox
                    {
                        LabelText = "Replay file",
                        Current = uploadReplayFile,
                    },
                    analysisCheckbox = new OsuCheckbox
                    {
                        LabelText = "Judgement analysis",
                        Current = uploadAnalysis,
                    },
                    new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Full,
                        Spacing = new(AimModVisualStyle.RelatedSpacing),
                        Children = new Drawable[]
                        {
                            shareButton = button("Share replay", share, 118, AimModPalette.Pink),
                            cancelRetryButton = button("Cancel", cancelOrRetry, 82, AimModPalette.PanelHover),
                            copyButton = button("Copy link", copyLink, 92, AimModPalette.PanelHover),
                            openButton = button("Open", openLink, 72, AimModPalette.PanelHover),
                        },
                    },
                },
            },
        };
        setStatus("Choose a replay to share.", AimModPalette.Muted);
        cancelRetryButton.Alpha = copyButton.Alpha = openButton.Alpha = 0;
        cancelRetryButton.Enabled.Value = copyButton.Enabled.Value = openButton.Enabled.Value = false;
        refreshAvailability();
    }

    private void setStatus(string text, Colour4 colour)
    {
        status.Colour = colour;
        if (statusText == text)
            return;
        statusText = text;
        status.Text = text;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        if (uploadQueue is not null)
            uploadQueue.Changed += queueChanged;
    }

    protected override void Dispose(bool isDisposing)
    {
        if (isDisposing)
        {
            preparing?.Cancel();
            preparing?.Dispose();
            if (uploadQueue is not null)
                uploadQueue.Changed -= queueChanged;
        }
        base.Dispose(isDisposing);
    }

    public void SetReplay(LocalReplay selected, bool hasAnalysis)
    {
        preparing?.Cancel();
        preparing?.Dispose();
        preparing = null;
        replay = selected;
        analysisAvailable = hasAnalysis;
        queueItemId = null;
        shareUrl = string.Empty;
        HubSharingPreferences preferences = preferenceStore?.Load() ?? HubSharingPreferences.Default;
        visibility.Value = preferences.Visibility;
        uploadReplayFile.Disabled = false;
        uploadAnalysis.Disabled = false;
        uploadReplayFile.Value = preferences.UploadReplayFile && selected.HasReplayFile;
        uploadAnalysis.Value = preferences.UploadAnalysis && hasAnalysis;
        uploadReplayFile.Disabled = !selected.HasReplayFile;
        uploadAnalysis.Disabled = !hasAnalysis;
        replayFileCheckbox.Alpha = selected.HasReplayFile ? 1 : 0.45f;
        analysisCheckbox.Alpha = hasAnalysis ? 1 : 0.45f;
        resetActions();
        refreshAvailability();

        HubUploadQueueItem? existing = uploadQueue?.Snapshot()
            .FirstOrDefault(item => string.Equals(item.Request.Score.ClientScoreId, clientScoreId(selected), StringComparison.Ordinal));
        if (existing is not null)
        {
            queueItemId = existing.Id;
            applyQueueItem(existing);
        }
    }

    public void SetAnalysisAvailable(bool available)
    {
        analysisAvailable = available;
        if (!available)
        {
            uploadAnalysis.Disabled = false;
            uploadAnalysis.Value = false;
        }
        uploadAnalysis.Disabled = !available;
        analysisCheckbox.Alpha = available ? 1 : 0.45f;
        if (replay is not null && queueItemId is null)
            refreshAvailability();
    }

    private void refreshAvailability()
    {
        bool linked = credentialStore?.Load() is not null;
        shareButton.Enabled.Value = replay is not null && shareService is not null && linked;
        setStatus(!linked
            ? "Link an AimMod Hub account in Settings before sharing."
            : replay is null
                ? "Choose a replay to share."
                : "Nothing uploads until you press Share replay.", linked ? AimModPalette.Muted : AimModPalette.Pink);
    }

    private void share()
    {
        if (replay is null || shareService is null)
            return;
        if (credentialStore?.Load() is null)
        {
            refreshAvailability();
            return;
        }
        if (uploadAnalysis.Value && !analysisAvailable)
        {
            setStatus("Wait for exact judgement analysis before including it.", AimModPalette.Pink);
            return;
        }

        preparing?.Cancel();
        preparing?.Dispose();
        preparing = new CancellationTokenSource();
        queueItemId = null;
        shareUrl = string.Empty;
        resetActions();
        shareButton.Enabled.Value = false;
        cancelRetryButton.Text = "Cancel";
        cancelRetryButton.Alpha = 1;
        cancelRetryButton.Enabled.Value = true;
        setStatus("Preparing a verified Hub upload...", AimModPalette.Cyan);
        _ = prepareAsync(new HubReplayShareSelection(replay, visibility.Value, uploadReplayFile.Value, uploadAnalysis.Value), preparing.Token);
    }

    private async Task prepareAsync(HubReplayShareSelection selection, CancellationToken cancellationToken)
    {
        try
        {
            if (preferenceStore is not null)
                await preferenceStore.UpdateAsync(previous => previous with
                {
                    Visibility = selection.Visibility,
                    UploadReplayFile = selection.UploadReplayFile,
                    UploadAnalysis = selection.UploadAnalysis,
                }, cancellationToken).ConfigureAwait(false);
            HubUploadQueueItem item = await shareService!.QueueAsync(selection, cancellationToken).ConfigureAwait(false);
            if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                Schedule(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;
                    queueItemId = item.Id;
                    applyQueueItem(item);
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                Schedule(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;
                    resetActions();
                    // Share-service validation messages are written for players; anything else is not.
                    setStatus(error is InvalidOperationException ? error.Message : AimModFriendlyError.Message(error, "Preparing the upload"), AimModPalette.Pink);
                    shareButton.Enabled.Value = true;
                });
        }
    }

    private void queueChanged()
    {
        if (IsDisposed || queueItemId is null)
            return;
        Schedule(() =>
        {
            HubUploadQueueItem? item = uploadQueue?.Snapshot().FirstOrDefault(candidate => candidate.Id == queueItemId);
            if (item is not null)
                applyQueueItem(item);
        });
    }

    private void applyQueueItem(HubUploadQueueItem item)
    {
        queueItemId = item.Id;
        shareUrl = item.ShareUrl;
        setStatus(item.Status switch
        {
            HubUploadQueueStatus.Queued => "Queued for upload.",
            HubUploadQueueStatus.Uploading => "Uploading to AimMod Hub...",
            HubUploadQueueStatus.Completed when item.Request.Visibility == "private" => "Private Hub copy is ready.",
            HubUploadQueueStatus.Completed => "Share link is ready.",
            HubUploadQueueStatus.Failed => string.IsNullOrWhiteSpace(item.Error) ? "Upload failed. Retry when you are online." : item.Error,
            _ => "Upload cancelled.",
        }, item.Status switch
        {
            HubUploadQueueStatus.Completed => AimModPalette.Success,
            HubUploadQueueStatus.Failed => AimModPalette.Pink,
            HubUploadQueueStatus.Uploading => AimModPalette.Cyan,
            _ => AimModPalette.Muted,
        });

        bool active = item.Status is HubUploadQueueStatus.Queued or HubUploadQueueStatus.Uploading;
        bool retryable = item.Status is HubUploadQueueStatus.Failed or HubUploadQueueStatus.Cancelled;
        cancelRetryButton.Text = retryable ? "Retry" : "Cancel";
        cancelRetryButton.Alpha = active || retryable ? 1 : 0;
        cancelRetryButton.Enabled.Value = active || retryable;
        bool completed = item.Status == HubUploadQueueStatus.Completed && Uri.TryCreate(item.ShareUrl, UriKind.Absolute, out _);
        copyButton.Alpha = openButton.Alpha = completed ? 1 : 0;
        copyButton.Enabled.Value = openButton.Enabled.Value = completed;
        shareButton.Enabled.Value = item.Status is HubUploadQueueStatus.Completed or HubUploadQueueStatus.Failed or HubUploadQueueStatus.Cancelled;
    }

    private void cancelOrRetry()
    {
        if (queueItemId is null && preparing is not null)
        {
            preparing.Cancel();
            resetActions();
            refreshAvailability();
            return;
        }
        if (queueItemId is not { } id || uploadQueue is null)
            return;
        HubUploadQueueItem? item = uploadQueue.Snapshot().FirstOrDefault(candidate => candidate.Id == id);
        if (item is null)
            return;
        _ = item.Status is HubUploadQueueStatus.Failed or HubUploadQueueStatus.Cancelled
            ? uploadQueue.RetryAsync(id)
            : uploadQueue.CancelAsync(id);
    }

    private void copyLink()
    {
        if (!string.IsNullOrWhiteSpace(shareUrl))
            copyText?.Invoke(shareUrl);
    }

    private void openLink()
    {
        if (Uri.TryCreate(shareUrl, UriKind.Absolute, out Uri? uri))
            openUrl?.Invoke(uri);
    }

    private void resetActions()
    {
        cancelRetryButton.Alpha = copyButton.Alpha = openButton.Alpha = 0;
        cancelRetryButton.Enabled.Value = copyButton.Enabled.Value = openButton.Enabled.Value = false;
    }

    private static string clientScoreId(LocalReplay replay) => replay.Origin switch
    {
        LocalLibraryOrigin.Stable => $"stable:{replay.ScoreId:N}",
        LocalLibraryOrigin.Online when replay.OnlineScoreId > 0 => $"online:{replay.OnlineScoreId}",
        _ => $"lazer:{replay.ScoreId:N}",
    };

    private static OsuButton button(string label, Action action, float width, Colour4 colour) => new HubButton
    {
        Text = label,
        Action = action,
        Width = width,
        Height = AimModVisualStyle.CompactControlHeight,
        BackgroundColour = colour,
    };

    private partial class HubButton : OsuButton
    {
        public HubButton()
        {
            AutoSizeAxes = Axes.None;
        }
    }
}
