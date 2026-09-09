using System.Reflection;
using System.Runtime.Versioning;
using AimMod.Desktop.Visuals;
using NUnit.Framework;
using osu.Framework.Graphics.Sprites;

namespace AimMod.Desktop.Tests;

public sealed partial class OffscreenVisualCaptureTests
{
    [TestCase(800, "first")]
    [TestCase(1280, "refresh")]
    [TestCase(800, "profile")]
    [TestCase(800, "menu")]
    [TestCase(1280, "menu")]
    [Explicit("Renders synthetic PP loading states on a private desktop.")]
    [SupportedOSPlatform("windows")]
    public async Task CapturePpLoading(int width, string mode)
    {
        string output = Path.Combine(TestContext.CurrentContext.WorkDirectory, "visual-captures", $"ppTargets-{mode}-loading-{width}.png");
        var cache = await createPpTargetCaptureCache(output);
        await WindowsPrivateDesktopCapture.CaptureAsync((host, success, fail) =>
            new CapturePpTargetsGame(host, cache, output, width, 800, success, fail) { LoadingCaptureMode = mode }, TimeSpan.FromSeconds(60));
    }

    private sealed partial class CapturePpTargetsGame
    {
        public string? LoadingCaptureMode { get; init; }

        private bool loadingChecked;

        protected override void Update()
        {
            base.Update();
            if (LoadingCaptureMode is null || workspace is null || !workspace.IsLoaded) return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(NativePpTargetsWorkspace);
            if (LoadingCaptureMode is not null)
            {

                bool first = LoadingCaptureMode is "first" or "profile";
                type.GetField("firstScan", flags)!.SetValue(workspace, first);
                if (LoadingCaptureMode == "profile")
                {
                    var overlay = (AimModLoadingOverlay)type.GetField("loadingOverlay", flags)!.GetValue(workspace)!;
                    overlay.LoadingHint = NativePpTargetsWorkspace.ScanHint(true);
                    overlay.ShowLoading("Preparing your first PP targets", "Updating PP for your recorded plays", 24, 150);
                }
            }
            // Keep the synthetic scan active while the offline fixture finishes its
            // unrelated empty-history refresh.
            type.GetMethod("showRefresh", flags)!.Invoke(workspace, ["Checking map patterns and PP", 1367, 5000]);
            if (loadingChecked || string.IsNullOrEmpty(((SpriteText)type.GetField("scanHint", flags)!.GetValue(workspace)!).Text.ToString())) return;
            loadingChecked = true;
            var hint = (SpriteText)type.GetField("scanHint", flags)!.GetValue(workspace)!;
            var status = (SpriteText)type.GetField("status", flags)!.GetValue(workspace)!;
            var header = (osu.Framework.Graphics.Drawable)type.GetField("filterHeader", flags)!.GetValue(workspace)!;
            try
            {
                Assert.That(hint.Text.ToString(), Does.Contain("elapsed"));
                Assert.That(hint.Y, Is.GreaterThan(status.Y + status.DrawHeight));
                Assert.That(hint.Y + hint.DrawHeight, Is.LessThanOrEqualTo(header.DrawHeight));
            }
            catch (Exception error) { failed(error); host.Exit(); }
        }
    }
}
