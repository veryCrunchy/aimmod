using AimMod.Desktop.Visuals;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;

namespace AimMod.Desktop.Creator;

public partial class TimestampThumbnailPreview : FillFlowContainer<Drawable>
{
    [Resolved] private IRenderer renderer { get; set; } = null!;
    private readonly TimestampThumbnailService service;
    private readonly string location;
    private readonly Sprite preview;
    private readonly TextFlowContainer label;
    private CancellationTokenSource? request;
    private readonly double initial;

    public TimestampThumbnailPreview(TimestampThumbnailService service, string location, double seconds)
    {
        this.service = service; this.location = location; initial = seconds;
        RelativeSizeAxes = Axes.X; AutoSizeAxes = Axes.Y; Direction = FillDirection.Vertical; Spacing = new(6);
        Children = [label = new TextFlowContainer(t => { t.Font = new FontUsage(size: 12); t.Colour = AimModPalette.Muted; })
            { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y },
            new Container { RelativeSizeAxes = Axes.X, Height = 220,
                Child = preview = new Sprite { RelativeSizeAxes = Axes.Both, FillMode = FillMode.Fit, Anchor = Anchor.Centre, Origin = Anchor.Centre } }];
    }

    protected override void LoadComplete() { base.LoadComplete(); ShowTime(initial); }

    public void ShowTime(double seconds)
    {
        request?.Cancel(); request?.Dispose(); request = new(); var token = request.Token;
        preview.Hide(); label.Text = $"Loading preview at {FootageIndex.Timecode(seconds)}...";
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await service.GetAsync(location, seconds, token).ConfigureAwait(false);
                if (!IsDisposed) Schedule(() =>
                {
                    if (token.IsCancellationRequested) return;
                    using var stream = File.OpenRead(result.Path);
                    preview.Texture?.Dispose(); preview.Texture = Texture.FromStream(renderer, stream); preview.Show();
                    label.Text = $"Video frame at {FootageIndex.Timecode(result.Seconds)}";
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!IsDisposed) Schedule(() => { if (!token.IsCancellationRequested) label.Text = error is InvalidOperationException ? error.Message : "Preview unavailable. Open the recording to check this moment."; });
            }
        }, token);
    }

    protected override void Dispose(bool isDisposing)
    {
        request?.Cancel(); request?.Dispose(); preview.Texture?.Dispose(); base.Dispose(isDisposing);
    }
}
