using System.Text.Json;
using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Creator;

public sealed class CreatorSettingsStore(string path)
{
    public bool Load()
    {
        try { return File.Exists(path) && new FileInfo(path).Length < 4096 && JsonSerializer.Deserialize<Settings>(File.ReadAllText(path))?.Enabled == true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }
    public void Save(bool enabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new Settings(enabled)));
        File.Move(path + ".tmp", path, true);
    }
    private sealed record Settings(bool Enabled);
}

public partial class CreatorSettingsPanel : FillFlowContainer<Drawable>
{
    public CreatorSettingsPanel(CreatorSettingsStore store, bool enabled, Action<bool> changed, Action open)
    {
        RelativeSizeAxes = Axes.X; AutoSizeAxes = Axes.Y; Direction = FillDirection.Vertical; Spacing = new(8);
        var detail = new TextFlowContainer(t => { t.Font = new(size: 13); t.Colour = AimModPalette.Muted; })
            { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = "Find footage of your osu! scores in local recordings or connected Twitch broadcasts." };
        var toggle = new AimModButton("Creator tools: " + (enabled ? "On" : "Off"), () => { });
        toggle.SetSelected(enabled);
        var openButton = new AimModButton("Set up accounts & find footage", open, true) { Alpha = enabled ? 1 : 0 };
        bool saving = false;
        toggle.Action = () =>
        {
            if (saving) return;
            saving = true; bool next = !enabled;
            _ = Task.Run(() => store.Save(next)).ContinueWith(task =>
            {
                if (IsDisposed) return;
                Schedule(() =>
                {
                    saving = false;
                    if (task.IsFaulted) { _ = task.Exception; detail.Text = "Could not save this setting. Try again."; return; }
                    enabled = next; toggle.SetCaption("Creator tools: " + (enabled ? "On" : "Off")); toggle.SetSelected(enabled);
                    openButton.Alpha = enabled ? 1 : 0; changed(enabled);
                });
            }, TaskScheduler.Default);
        };
        Children = [toggle, detail, openButton];
    }
}
