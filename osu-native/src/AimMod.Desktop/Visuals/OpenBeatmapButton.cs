using AimMod.Desktop.LocalLibrary;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Localisation;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.Visuals;

public partial class OpenBeatmapButton : OsuButton, IHasTooltip
{
    private readonly CancellationTokenSource lifetime = new();
    private bool opening;
    private readonly Func<bool>? canOpen;
    public LocalisableString TooltipText { get; private set; } = "Open this difficulty in your preferred osu! client";

    public OpenBeatmapButton(Func<LocalReplay?> selected, Func<LocalReplay, CancellationToken, Task>? open)
        : this(open is null ? null : token => selected() is { } replay ? open(replay, token)
            : Task.FromException(new InvalidOperationException("Select a map first.")))
    {
        canOpen = () => open is not null && selected() is not null;
        Enabled.Value = canOpen();
    }

    protected override void Update()
    {
        base.Update();
        if (canOpen is not null) Enabled.Value = !opening && canOpen();
    }

    public OpenBeatmapButton(Func<CancellationToken, Task>? open)
    {
        AutoSizeAxes = Axes.None;
        Size = new(132, 32);
        Text = "Open in osu!";
        BackgroundColour = AimModPalette.PanelRaised;
        Content.BorderThickness = 1; Content.BorderColour = AimModPalette.Border;
        Content.CornerRadius = AimModVisualStyle.ControlRadius;
        SpriteText.Font = new osu.Framework.Graphics.Sprites.FontUsage(size:13,weight:"SemiBold");
        Enabled.Value = open is not null;
        Action = () =>
        {
            if (opening || open is null) return;
            opening = true;
            Text = "Opening...";
            _ = perform(open);
        };
    }

    private async Task perform(Func<CancellationToken, Task> open)
    {
        string? error = null;
        try { await open(lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception ex) { error = ex is InvalidOperationException ? ex.Message : "Could not open osu!. Check your client settings and try again."; }
        if (!IsDisposed) Schedule(() =>
        {
            opening = false;
            Text = error is null ? "Open in osu!" : "Retry open";
            TooltipText = error ?? "Open this difficulty in your preferred osu! client";
        });
    }

    protected override void Dispose(bool isDisposing)
    {
        lifetime.Cancel();
        lifetime.Dispose();
        base.Dispose(isDisposing);
    }
}
