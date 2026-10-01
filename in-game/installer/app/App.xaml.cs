using System.Globalization;
using System.Windows;
using System.Windows.Threading;

namespace AimMod.Setup;

// Command line (all optional):
//   --uninstall                     open on the uninstall question (Apps & features)
//   --game-dir <folder>             KovaaK's folder instead of the Steam lookup
//   --channel stable|beta           channel to show first
//   --action install|update|repair|uninstall [--remove-data]
//                                   run that action at start (after an elevation restart)
//   --preview <screen>              show a screen with sample data, changing nothing
//                                   (install, update, outdated, uninstall, running, progress)
//   --capture <png>                 save the window as a PNG once it has loaded, then exit
//                                   (design review; never runs an action)
sealed record SetupOptions(bool Uninstall, string? GameDir, string? Channel, string? Action, bool RemoveData, string? Preview, string? Capture)
{
    public static SetupOptions Parse(string[] args)
    {
        string? Value(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var channel = Value("--channel") is "stable" or "beta" ? Value("--channel") : null;
        var action = Value("--action") is "install" or "update" or "repair" or "uninstall" ? Value("--action") : null;
        return new(args.Contains("--uninstall"), Value("--game-dir"), channel, action, args.Contains("--remove-data"), Value("--preview"), Value("--capture"));
    }
}

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        DispatcherUnhandledException += OnUnhandled;
        var window = new MainWindow(SetupOptions.Parse(e.Args));
        MainWindow = window;
        window.Show();
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        MessageBox.Show("AimMod Setup ran into a problem and has to close:\n\n" + e.Exception.Message + "\n\nNothing was left half-installed.",
            "AimMod Setup", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }
}
