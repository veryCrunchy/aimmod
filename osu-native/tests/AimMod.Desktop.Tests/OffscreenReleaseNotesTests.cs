using System.Reflection;
using System.Runtime.Versioning;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Updates;
using AimMod.Desktop.Visuals;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Platform;
using SixLabors.ImageSharp;

namespace AimMod.Desktop.Tests;

public sealed partial class OffscreenVisualCaptureTests
{
    [TestCase(800, 760, false)]
    [TestCase(1600, 900, false)]
    [TestCase(800, 760, true)]
    [TestCase(1600, 900, true)]
    [Explicit("Checks the Home update changelog on a private desktop.")]
    [SupportedOSPlatform("windows")]
    public async Task ReleaseNotesAreReadableBeforeUpdating(int width, int height, bool missingNotes)
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "changelogs", $"notes-{width}-{missingNotes}.png");
        await WindowsPrivateDesktopCapture.CaptureAsync((host, ok, fail) => new ReleaseNotesGame(host, width, height, missingNotes, path, ok, fail), TimeSpan.FromSeconds(30));
        TestContext.AddTestAttachment(path);
    }

    private sealed partial class ReleaseNotesGame(GameHost host, int width, int height, bool missingNotes, string path, Action ok, Action<Exception> fail)
        : AimModGame(AimModLaunchOptions.Home, new InMemoryLocalLibrarySource([], []))
    {
        private const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        protected override void LoadComplete()
        {
            base.LoadComplete();
            Dependencies.Get<FrameworkConfigManager>().SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));
            Scheduler.AddDelayed(() =>
            {
                try
                {
                    var service = new NotesFixtureService(missingNotes);
                    Action nothing = () => { };
                    var home = (Drawable)Activator.CreateInstance(typeof(AimModGame).GetNestedType("HomeScreen", BindingFlags.NonPublic)!,
                        [service, nothing, nothing, nothing, nothing, nothing, nothing, nothing, nothing])!;
                    home.RelativeSizeAxes = Axes.Both;
                    var content = (Container)typeof(AimModGame).GetField("content", flags)!.GetValue(this)!;
                    content.Padding = AimModVisualStyle.PagePadding;
                    content.Child = home;
                    Scheduler.AddDelayed(async () =>
                    {
                        try
                        {
                            var surface = descendants(home).OfType<NativeUpdateSurface>().Single();
                            var toggle = (AimModButton)typeof(NativeUpdateSurface).GetField("notesButton", flags)!.GetValue(surface)!;
                            toggle.Action!.Invoke();
                            Assert.That(service.Downloads, Is.Zero, "Reading notes must not start an update.");
                            var panel = (NativeReleaseNotesPanel)typeof(NativeUpdateSurface).GetField("notesPanel", flags)!.GetValue(surface)!;
                            var buttons = (Dictionary<string, AimModButton>)typeof(NativeReleaseNotesPanel).GetField("buttons", flags)!.GetValue(panel)!;
                            buttons["0.2.11"].Action!.Invoke();
                            var status = (TextFlowContainer)typeof(NativeReleaseNotesPanel).GetField("status", flags)!.GetValue(panel)!;
                            Assert.That(string.Join(" ", descendants(status).OfType<SpriteText>().Select(text => text.Text.ToString().Trim())), Does.Contain("Version 0.2.11"));
                            buttons["9.1.0"].Action!.Invoke();
                            var selected = (string?)typeof(NativeReleaseNotesPanel).GetField("selection", flags)!.GetValue(panel);
                            Assert.That(selected, Is.EqualTo("9.1.0"));
                            descendants(home).OfType<AimModScrollContainer>().First().ScrollTo(540, false);
                            await Task.Delay(400);
                            using var image = await host.TakeScreenshotAsync();
                            Directory.CreateDirectory(Path.GetDirectoryName(path)!); image.SaveAsPng(path);
                            ok(); host.Exit();
                        }
                        catch (Exception error) { fail(error); host.Exit(); }
                    }, 600);
                }
                catch (Exception error) { fail(error); host.Exit(); }
            }, 1200);
        }

        private static IEnumerable<Drawable> descendants(Drawable item)
        {
            yield return item;
            if (item is not CompositeDrawable) yield break;
            var children = (IEnumerable<Drawable>)typeof(CompositeDrawable).GetProperty("InternalChildren", flags)!.GetValue(item)!;
            foreach (var child in children)
                foreach (var descendant in descendants(child)) yield return descendant;
        }
    }

    private sealed class NotesFixtureService(bool missingNotes) : INativeUpdateService
    {
        public int Downloads { get; private set; }
        public NativeUpdateState State { get; private set; } = new(NativeUpdateStage.Available, NativeUpdateChannel.Stable,
            "AimMod 9.1.0 is ready", "Download the update while you keep using AimMod.", "9.1.0",
            ReleaseNotes: missingNotes ? null : "# AimMod 9.1.0\n\n## Double Time practice\n- Build from normal speed to full DT with small adjustments after each attempt.\n- Keep your progress separately for each map.\n\n## Easier trainer navigation\n- Skill trainers and Mod trainers now have separate tabs.\n- Compact difficulty choices make installed maps easier to browse.\n\n## Clearer practice-map titles\n- See the source difficulty in automatic and manual practice-map titles.");
        public event Action<NativeUpdateState>? StateChanged;
        public Task CheckAsync() => Task.CompletedTask;
        public Task SelectChannelAsync(NativeUpdateChannel channel) { State = State with { Channel = channel }; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task DownloadAsync() { Downloads++; return Task.CompletedTask; }
        public void ApplyAndRestart() { }
        public void Dispose() { }
    }
}
