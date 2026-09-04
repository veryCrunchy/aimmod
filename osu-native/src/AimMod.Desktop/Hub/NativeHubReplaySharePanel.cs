using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
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
    private readonly Bindable<OsuHubVisibility> visibility = new(OsuHubVisibility.Private);
    private readonly BindableBool uploadReplayFile = new(false);
    private readonly BindableBool uploadAnalysis = new(false);
    private readonly OsuCheckbox replayFileCheckbox;
    private readonly OsuCheckbox analysisCheckbox;
    private readonly OsuButton shareButton;
    private readonly OsuButton cancelRetryButton;
    private readonly OsuButton copyButton;
    private readonly OsuButton openButton;
    private readonly TruncatingSpriteText status;
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

        AutoSizeAxes = Axes.None;
        RelativeSizeAxes = Axes.X;
        Height = 270;
        InternalChildren = new Drawable[]
        {
            new CircularContainer
            {
                RelativeSizeAxes = Axes.Both,
                Masking = true,
                CornerRadius = AimModVisualStyle.CardRadius,
                Depth = 10,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                    new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = AimModPalette.Pink },
                },
            },
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Depth = 0,
                Padding = new MarginPadding { Left = 14, Top = 12, Right = 12, Bottom = 12 },
                Children = new Drawable[]
                {
                    status = text("Choose a replay to share.", 11, AimModPalette.Muted, "SemiBold", 0),
                    new OsuDropdown<OsuHubVisibility>
                    {
                        RelativeSizeAxes = Axes.X,
                        Y = 28,
                        Items = Enum.GetValues<OsuHubVisibility>(),
                        Current = visibility,
                    },
                    replayFileCheckbox = new OsuCheckbox
                    {
                        Y = 74,
                        LabelText = "Replay file",
                        Current = uploadReplayFile,
                    },
                    analysisCheckbox = new OsuCheckbox
                    {
                        Y = 100,
                        LabelText = "Judgement analysis",
                        Current = uploadAnalysis,
                    },
                    shareButton = button("Share replay", share, 118, AimModPalette.Pink, 0, 136),
                    cancelRetryButton = button("Cancel", cancelOrRetry, 82, AimModPalette.PanelHover, 128, 136),
                    copyButton = button("Copy link", copyLink, 92, AimModPalette.PanelHover, 0, 180),
                    openButton = button("Open", openLink, 72, AimModPalette.PanelHover, 102, 180),
                },
            },
        };
        cancelRetryButton.Alpha = copyButton.Alpha = openButton.Alpha = 0;
        cancelRetryButton.Enabled.Value = copyButton.Enabled.Value = openButton.Enabled.Value = false;
    }

    protected override void Update()
    {
        base.Update();
        status.MaxWidth = Math.Max(100, DrawWidth - 28);
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
        status.Text = !linked
            ? "Link an AimMod Hub account in Settings before sharing."
            : replay is null
                ? "Choose a replay to share."
                : "Nothing uploads until you press Share replay.";
        status.Colour = linked ? AimModPalette.Muted : AimModPalette.Pink;
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
            status.Text = "Wait for exact judgement analysis before including it.";
            status.Colour = AimModPalette.Pink;
            return;
        }

        preparing?.Cancel();
        preparing?.Dispose();
        preparing = new CancellationTokenSource();
        shareButton.Enabled.Value = false;
        status.Text = "Preparing a verified Hub upload...";
        status.Colour = AimModPalette.Cyan;
        _ = prepareAsync(new HubReplayShareSelection(replay, visibility.Value, uploadReplayFile.Value, uploadAnalysis.Value), preparing.Token);
    }

    private async Task prepareAsync(HubReplayShareSelection selection, CancellationToken cancellationToken)
    {
        try
        {
            if (preferenceStore is not null)
                await preferenceStore.SaveAsync(new HubSharingPreferences(selection.Visibility, selection.UploadReplayFile, selection.UploadAnalysis), cancellationToken).ConfigureAwait(false);
            HubUploadQueueItem item = await shareService!.QueueAsync(selection, cancellationToken).ConfigureAwait(false);
            if (!IsDisposed)
                Schedule(() =>
                {
                    queueItemId = item.Id;
                    applyQueueItem(item);
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!IsDisposed)
                Schedule(() =>
                {
                    status.Text = error.Message;
                    status.Colour = AimModPalette.Pink;
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
        status.Text = item.Status switch
        {
            HubUploadQueueStatus.Queued => "Queued for upload.",
            HubUploadQueueStatus.Uploading => "Uploading to AimMod Hub...",
            HubUploadQueueStatus.Completed when item.Request.Visibility == "private" => "Private Hub copy is ready.",
            HubUploadQueueStatus.Completed => "Share link is ready.",
            HubUploadQueueStatus.Failed => item.Error,
            _ => "Upload cancelled.",
        };
        status.Colour = item.Status switch
        {
            HubUploadQueueStatus.Completed => AimModPalette.Success,
            HubUploadQueueStatus.Failed => AimModPalette.Pink,
            HubUploadQueueStatus.Uploading => AimModPalette.Cyan,
            _ => AimModPalette.Muted,
        };

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

    private static OsuButton button(string label, Action action, float width, Colour4 colour, float x = 0, float y = 0) => new HubButton
    {
        Text = label,
        Action = action,
        Width = width,
        Height = AimModVisualStyle.CompactControlHeight,
        Position = new(x, y),
        BackgroundColour = colour,
    };

    private static TruncatingSpriteText text(string value, float size, Colour4 colour, string weight = "Regular", float y = 0) => new()
    {
        Text = value,
        Font = new FontUsage(size: size, weight: weight),
        Colour = colour,
        MaxWidth = 280,
        Y = y,
    };

    private partial class HubButton : OsuButton
    {
        public HubButton()
        {
            AutoSizeAxes = Axes.None;
        }
    }
}
