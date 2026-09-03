using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Platform;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AimMod.Desktop.Tests;

[TestFixture]
[NonParallelizable]
public sealed partial class OffscreenVisualCaptureTests
{
    [TestCase("home", 1600, 900)]
    [TestCase("home", 1100, 760)]
    [TestCase("skins", 1100, 760)]
    [Explicit("Creates a real graphics device and writes a visual-review artifact.")]
    [SupportedOSPlatform("windows")]
    public async Task CaptureWorkspaceOnPrivateDesktop(string route, int width, int height)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("Private-desktop captures are only supported on Windows.");

        string outputPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "visual-captures",
            $"aimmod-{route}-{width}x{height}.png");

        var source = new InMemoryLocalLibrarySource([], []);

        await WindowsPrivateDesktopCapture.CaptureAsync(
            (host, succeeded, failed) => new CaptureAimModGame(host, source, outputPath, route, width, height, succeeded, failed),
            TimeSpan.FromSeconds(30));

        using Image<Rgba32> image = Image.Load<Rgba32>(outputPath);

        Assert.Multiple(() =>
        {
            Assert.That(image.Width, Is.EqualTo(width));
            Assert.That(image.Height, Is.EqualTo(height));
            Assert.That(countSampledColours(image), Is.GreaterThan(8), "The capture should contain rendered UI, not a blank frame.");
        });

        TestContext.AddTestAttachment(outputPath, $"AimMod {route} captured on a private Windows desktop");
    }

    private static int countSampledColours(Image<Rgba32> image)
    {
        var colours = new HashSet<Rgba32>();
        int stepX = Math.Max(1, image.Width / 32);
        int stepY = Math.Max(1, image.Height / 18);

        for (int y = 0; y < image.Height; y += stepY)
        {
            for (int x = 0; x < image.Width; x += stepX)
                colours.Add(image[x, y]);
        }

        return colours.Count;
    }

    private sealed partial class CaptureAimModGame : AimModGame
    {
        private readonly GameHost host;
        private readonly string outputPath;
        private readonly string route;
        private readonly int width;
        private readonly int height;
        private readonly Action succeeded;
        private readonly Action<Exception> failed;

        [Resolved]
        private FrameworkConfigManager frameworkConfig { get; set; } = null!;

        public CaptureAimModGame(
            GameHost host,
            ILocalLibrarySource source,
            string outputPath,
            string route,
            int width,
            int height,
            Action succeeded,
            Action<Exception> failed)
            : base(AimModLaunchOptions.Home, source)
        {
            this.host = host;
            this.outputPath = outputPath;
            this.route = route;
            this.width = width;
            this.height = height;
            this.succeeded = succeeded;
            this.failed = failed;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            if (!string.Equals(route, "home", StringComparison.Ordinal))
            {
                MethodInfo routeMethod = typeof(AimModGame).GetMethod($"show{char.ToUpperInvariant(route[0])}{route[1..]}", BindingFlags.Instance | BindingFlags.NonPublic)
                                         ?? throw new InvalidOperationException($"Unknown capture route '{route}'.");
                routeMethod.Invoke(this, null);
            }

            frameworkConfig.SetValue(FrameworkSetting.WindowMode, WindowMode.Windowed);
            frameworkConfig.SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));
            Scheduler.AddDelayed(capture, 1500);
        }

        private void capture()
        {
            host.TakeScreenshotAsync().ContinueWith(task =>
            {
                try
                {
                    using Image<Rgba32> image = task.GetAwaiter().GetResult();
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    image.SaveAsPng(outputPath);
                    succeeded();
                }
                catch (Exception error)
                {
                    failed(error);
                }
                finally
                {
                    host.Exit();
                }
            }, TaskScheduler.Default);
        }
    }
}

[SupportedOSPlatform("windows")]
internal static class WindowsPrivateDesktopCapture
{
    private const uint generic_all = 0x10000000;

    public static async Task CaptureAsync(
        Func<GameHost, Action, Action<Exception>, Game> createGame,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(createGame);

        string desktopName = $"AimModCapture-{Guid.NewGuid():N}";
        nint desktop = CreateDesktop(desktopName, nint.Zero, nint.Zero, 0, generic_all, nint.Zero);

        if (desktop == nint.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the private capture desktop.");

        Exception? captureError = null;
        Exception? workerError = null;
        var captureFinished = new ManualResetEventSlim();
        GameHost? runningHost = null;

        var thread = new Thread(() =>
        {
            try
            {
                if (!SetThreadDesktop(desktop))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach the renderer thread to the private desktop.");

                var options = new HostOptions
                {
                    FriendlyGameName = "AimMod visual capture",
                    IPCPipeName = null,
                };

                using DesktopGameHost host = Host.GetSuitableDesktopHost($"aimmod-capture-{Guid.NewGuid():N}", options);
                runningHost = host;

                Game game = createGame(
                    host,
                    () => captureFinished.Set(),
                    error =>
                    {
                        captureError = error;
                        captureFinished.Set();
                    });

                host.Run(game);
            }
            catch (Exception error)
            {
                workerError = error;
                captureFinished.Set();
            }
            finally
            {
                runningHost = null;
            }
        })
        {
            IsBackground = true,
            Name = "AimMod private-desktop capture",
        };

        thread.Start();

        try
        {
            bool completed = await Task.Run(() => captureFinished.Wait(timeout)).ConfigureAwait(false);

            if (!completed)
            {
                runningHost?.Exit();
                throw new TimeoutException($"The off-screen capture did not complete within {timeout}.");
            }

            if (!thread.Join(TimeSpan.FromSeconds(10)))
            {
                runningHost?.Exit();
                throw new TimeoutException("The off-screen graphics host did not stop after the capture completed.");
            }

            if (workerError is not null)
                throw new InvalidOperationException("The off-screen graphics host failed.", workerError);

            if (captureError is not null)
                throw new InvalidOperationException("The renderer could not capture the frame.", captureError);
        }
        finally
        {
            captureFinished.Dispose();

            if (!thread.IsAlive)
            {
                // Graphics backends can briefly retain helper-thread references to the
                // desktop after the host exits. Cleanup is best-effort; Windows releases
                // the isolated desktop when this short-lived test process terminates.
                for (int attempt = 0; attempt < 5 && !CloseDesktop(desktop); attempt++)
                    Thread.Sleep(100);
            }
        }
    }

    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateDesktop(
        string desktop,
        nint device,
        nint deviceMode,
        uint flags,
        uint desiredAccess,
        nint securityAttributes);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(nint desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint desktop);
}
