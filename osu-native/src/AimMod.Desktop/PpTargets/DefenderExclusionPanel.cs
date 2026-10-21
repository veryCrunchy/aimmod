using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.PpTargets;

/// <summary>
/// Explains why Microsoft Defender slows PP target scans and offers to exclude AimMod's two cache folders. As a card in
/// PP targets it can be dismissed and hides once done; in Settings it also offers to remove the exclusions again.
/// </summary>
public partial class DefenderExclusionPanel : Container
{
    private readonly DefenderScanExclusion exclusion;
    private readonly bool inSettings;
    private readonly FillFlowContainer body;
    private string? message;
    private bool confirming;
    private bool working;

    public DefenderExclusionPanel(DefenderScanExclusion exclusion, bool inSettings)
    {
        this.exclusion = exclusion;
        this.inSettings = inSettings;
        RelativeSizeAxes = Axes.X;
        AutoSizeAxes = Axes.Y;
        Masking = !inSettings;
        CornerRadius = AimModVisualStyle.CardRadius;
        body = new FillFlowContainer { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(8),
            Padding = inSettings ? new MarginPadding() : new MarginPadding(14) };
        Children = inSettings ? [body] : [new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised }, body];
        render();
    }

    /// <summary>True when the PP targets card has something to offer: on Windows, with Defender running, not yet done or dismissed.</summary>
    public static bool ShouldOffer(DefenderScanExclusion exclusion) =>
        DefenderScanExclusion.Supported && DefenderScanExclusion.DefenderRunning() && exclusion.Load() is { Added: false, Dismissed: false };

    /// <summary>The card has finished: the player added the exclusions or chose not now.</summary>
    public event Action? Closed;

    private void render()
    {
        body.Clear();
        var state = exclusion.Load();
        if (inSettings)
            body.Add(new OsuSpriteText { Text = "MICROSOFT DEFENDER", Font = new FontUsage(size: 14, weight: "Bold"), Colour = AimModPalette.Cyan });
        else
            body.Add(new OsuSpriteText { Text = "Make PP target scans faster", Font = AimModVisualStyle.TitleFont, Colour = AimModPalette.Text });

        if (working)
        {
            body.Add(text("Waiting for Windows. Approve the permission prompt, and the change runs in a PowerShell window you can see."));
            return;
        }
        if (inSettings && state.Added && !confirming)
        {
            body.Add(text($"AimMod's PP target cache folders were added to Defender's exclusions on {state.ChangedAt?.LocalDateTime:d MMM yyyy}. Scans do not wait for Defender to check each beatmap file."));
            body.Add(folders());
            body.Add(buttons(("Remove exclusions", () => change(false), false)));
            addMessage();
            return;
        }
        if (confirming)
        {
            body.Add(text("Windows will ask for permission. AimMod then runs one Microsoft Defender command, in a PowerShell window you can see, that excludes exactly these folders:"));
            body.Add(folders());
            body.Add(buttons(("Add exclusions", () => change(true), true), ("Cancel", () => { confirming = false; render(); }, false)));
            return;
        }
        body.Add(text("Microsoft Defender checks every beatmap file a PP target scan writes, and those checks can take longer than the PP calculation itself. "
            + "Excluding AimMod's two PP target cache folders lets scans skip them. The folders only hold osu! beatmap files and AimMod's calculation results; "
            + "nothing else is excluded, and you can remove the exclusions again in Settings."));
        body.Add(inSettings
            ? buttons(("Add exclusions", () => { confirming = true; message = null; render(); }, false))
            : buttons(("Speed up scans", () => { confirming = true; message = null; render(); }, true), ("Not now", dismiss, false)));
        addMessage();
    }

    private void addMessage()
    {
        if (message is not null) body.Add(text(message, AimModPalette.Text));
    }

    private void dismiss()
    {
        try { exclusion.Save(exclusion.Load() with { Dismissed = true }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        Closed?.Invoke();
    }

    private async void change(bool add)
    {
        if (working) return;
        working = true; confirming = false; render();
        DefenderExclusionResult result;
        try { result = add ? await exclusion.AddAsync().ConfigureAwait(false) : await exclusion.RemoveAsync().ConfigureAwait(false); }
        // async void: anything escaping would end the app.
        catch (Exception error) { result = new(DefenderExclusionOutcome.Failed, error.Message); }
        Schedule(() =>
        {
            working = false;
            message = result.Message;
            render();
            if (!inSettings && add && result.Outcome == DefenderExclusionOutcome.Done)
                Scheduler.AddDelayed(() => Closed?.Invoke(), 4000);
        });
    }

    private Drawable folders()
    {
        var list = new FillFlowContainer { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(2),
            Padding = new MarginPadding { Left = 12 } };
        foreach (string folder in exclusion.Folders)
            list.Add(new OsuTextFlowContainer(t => { t.Font = new FontUsage(size: 12, weight: "SemiBold"); t.Colour = AimModPalette.Text; })
                { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = folder });
        return list;
    }

    private static Drawable buttons(params (string Caption, Action Action, bool Primary)[] actions)
    {
        var row = new FillFlowContainer { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(8) };
        foreach (var (caption, action, primary) in actions)
            row.Add(new AimModButton(caption, action, primary) { Height = AimModVisualStyle.CompactControlHeight });
        return row;
    }

    private static Drawable text(string value, Colour4? colour = null) => new OsuTextFlowContainer(t =>
    {
        t.Font = new FontUsage(size: 13);
        t.Colour = colour ?? AimModPalette.Muted;
    }) { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = value };
}
