using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;

namespace AimMod.Desktop.Updates;

internal partial class NativeReleaseNotesPanel : CompositeDrawable
{
    private readonly FillFlowContainer<Drawable> versions, content;
    private readonly AimModScrollContainer scroll;
    private readonly TextFlowContainer status;
    private readonly Dictionary<string, AimModButton> buttons = [];
    private IReadOnlyList<NativeReleaseNotes> entries = [];
    private NativeUpdateState state = NativeUpdateState.Initial(NativeUpdateChannel.Stable);
    private string? selection;

    public NativeReleaseNotesPanel()
    {
        RelativeSizeAxes = Axes.Both;
        var body = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical, Spacing = new(12), Padding = new MarginPadding { Right = 14, Bottom = 12 } };
        body.Add(label("What's new", 20, true));
        body.Add(status = label("", 12));
        body.Add(versions = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(6) });
        body.Add(content = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(9) });
        InternalChild = scroll = new AimModScrollContainer { RelativeSizeAxes = Axes.Both, Child = body };
    }

    public void SetState(NativeUpdateState next)
    {
        var releases = NativeReleaseNotes.ForState(next);
        bool changed = state.Version != next.Version || state.Channel != next.Channel;
        bool sameEntries = entries.SequenceEqual(releases);
        state = next;
        updateStatus();
        if (sameEntries && !changed) return;
        entries = releases;
        if (changed || entries.All(entry => entry.Version != selection)) selection = entries.FirstOrDefault()?.Version;
        versions.Clear(); buttons.Clear();
        foreach (var entry in entries)
        {
            var button = new AimModButton(entry.Version, () => show(entry.Version)) { Height = 32 };
            versions.Add(button); buttons[entry.Version] = button;
        }
        show(selection);
    }

    private void show(string? version)
    {
        selection = version; content.Clear();
        updateStatus();
        foreach (var pair in buttons) pair.Value.SetSelected(pair.Key == version);
        var release = entries.FirstOrDefault(entry => entry.Version == version);
        if (release is null || string.IsNullOrWhiteSpace(release.Markdown))
        {
            content.Add(label("Release notes are not available for this version.", 14));
            return;
        }
        // Render a bounded subset as native text. Never load remote images or HTML from an update feed.
        foreach (string raw in release.Markdown.Split('\n').Take(300))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            bool heading = line.StartsWith('#');
            line = heading ? line.TrimStart('#').Trim() : line;
            content.Add(label(line.Replace("**", ""), heading ? 17 : 14, heading));
        }
        scroll.ScrollToStart();
    }

    private void updateStatus()
    {
        string context = selection == state.Version ? state.Stage switch {
            NativeUpdateStage.Available => "Available update",
            NativeUpdateStage.Downloading => "Downloading update",
            NativeUpdateStage.ReadyToRestart => "Ready to install",
            NativeUpdateStage.Current => "Installed version",
            _ => "Release history"
        } : "Release history";
        status.Text = selection is null ? context : $"Version {selection} | {context}";
    }

    private static TextFlowContainer label(string value, float size, bool strong = false) => new(t => {
        t.Font = new FontUsage(size: size, weight: strong ? "SemiBold" : "Regular");
        t.Colour = strong ? AimModPalette.Text : AimModPalette.Muted;
    }) { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = value };
}
