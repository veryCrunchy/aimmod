using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AimMod.InGame;

namespace AimMod.Setup;

public partial class MainWindow : Window
{
    enum Tone { Info, Success, Error }
    enum Dialog { Uninstall, Elevate }

    readonly SetupOptions options;
    readonly SetupEngine engine;
    readonly AppRegistration registration = new();
    readonly SemanticVersion installerVersion;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly bool preview;
    string channel = "stable";
    string? win64;
    FeedStatus feed = FeedStatus.Loading;
    InstalledInfo? installed;
    bool running, busy, initializing = true;
    SetupView? view;
    // An action that waits for KovaaK's to close, then runs by itself.
    string? pending;
    bool pendingRemoveData;
    int feedRequest;
    Dialog dialog;
    // While an action runs: what the screen says instead of the decision's title.
    (string Eyebrow, string Title)? working;
    string? elevateAction;
    // Uninstalled from the Apps & features copy: that copy is removed after exit.
    bool deleteCopyOnExit;

    internal MainWindow(SetupOptions options)
    {
        this.options = options;
        preview = options.Preview is not null;
        InitializeComponent();
        installerVersion = CurrentVersion();
        SetupVersionText.Text = $"Setup {installerVersion}";
        engine = new SetupEngine();
        if (preview) { Preview.Load(this, options.Preview!); }
        else Loaded += async (_, _) => await Start();
        initializing = false;
    }

    static SemanticVersion CurrentVersion()
    {
        var text = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];
        return SemanticVersion.TryParse(text, out var version) ? version : new SemanticVersion(0, 0, 0, "");
    }

    // ---- start ----
    async Task Start()
    {
        win64 = FindGame();
        RefreshInstalled();
        channel = options.Channel ?? engine.SavedChannel();
        SetChannelToggle(channel);
        running = win64 is not null && engine.GameRunning(win64);
        timer.Tick += (_, _) => Tick();
        timer.Start();
        if (options.Uninstall && (installed is not null || registration.IsRegistered)) ShowDialog(Dialog.Uninstall);
        await LoadFeed();
        // Until the first Stable release exists, a fresh install starts on Beta.
        if (feed.State == FeedState.NotPublished && channel == "stable" && options.Channel is null && !engine.HasChannelPreference)
        {
            channel = "beta";
            SetChannelToggle(channel);
            await LoadFeed();
            if (feed.State == FeedState.Ready) Status("There is no Stable release yet, so Setup shows the Beta channel.");
        }
        if (options.Capture is { } capture)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Preview.Capture(Root, capture);
            Close();
            return;
        }
        if (options.Action is { } action)
        {
            if (win64 is not null && !SetupEngine.CanWrite(win64)) Status("Setup still cannot change the KovaaK's folder, even as administrator. Nothing was changed.", Tone.Error);
            else await Run(action, options.RemoveData, elevated: true);
        }
    }

    string? FindGame()
    {
        foreach (var candidate in new[] { options.GameDir, registration.GameFolder })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try { var resolved = InstallState.ResolveGameDir(candidate); if (InstallLayout.IsWin64(resolved)) return resolved; }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
        }
        return InstallLayout.FindWin64FromSteam();
    }

    void RefreshInstalled()
    {
        if (preview) return;
        try { installed = win64 is null ? null : engine.ReadInstalled(win64); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { installed = null; }
    }

    async Task LoadFeed()
    {
        var request = ++feedRequest;
        feed = FeedStatus.Loading;
        Render();
        var result = await engine.CheckFeed(channel, installerVersion, CancellationToken.None);
        if (request != feedRequest) return;
        feed = result;
        Render();
    }

    void Tick()
    {
        if (win64 is null) return;
        var now = engine.GameRunning(win64);
        if (now != running)
        {
            running = now;
            if (!running) RefreshInstalled();
            Render();
        }
        if (!running && pending is { } action && !busy)
        {
            pending = null;
            _ = Run(action, pendingRemoveData);
        }
    }

    // ---- view ----
    SetupInputs Inputs() => new(installerVersion, channel, win64, running, installed, feed,
        win64 is null || preview ? null : InstallLayout.SteamBuildId(win64), preview || engine.RepairCached, preview || engine.DataFolderExists, busy);

    void Render()
    {
        if (initializing && !preview) return;
        var v = view = SetupDecision.Decide(Inputs());
        EyebrowText.Text = (busy && working is { } w ? w.Eyebrow : v.Eyebrow).ToUpperInvariant();
        TitleText.Text = busy && working is { } t ? t.Title : v.Title;
        DetailText.Text = v.Detail;

        InstalledText.Text = installed is null ? "Not installed"
            : installed.Version is { } iv ? iv + (installed.Channel is { } ic ? $"  ·  {SetupDecision.Name(ic)}" : "")
            : "Developer install";
        InstalledText.Foreground = (Brush)FindResource(installed is null ? "Faint" : installed.NeedsRepair ? "Amber" : "Text");
        LatestText.Text = feed.State switch
        {
            FeedState.Ready => feed.Feed!.Version,
            FeedState.Loading => "Checking…",
            FeedState.InstallerOutdated => "Needs a newer Setup",
            FeedState.NotPublished => "None yet",
            _ => "Not available",
        };
        LatestText.Foreground = (Brush)FindResource(feed.State == FeedState.Ready ? "Text" : "Faint");
        NewPill.Visibility = v.State == SetupState.UpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
        ChannelHint.Text = channel == "beta" ? "Early builds, updated often" : "Tested releases";
        StableChannel.IsEnabled = BetaChannel.IsEnabled = v.CanChangeChannel;
        GameText.Text = win64 is null ? "Not found" : GameRoot(win64);
        GameText.ToolTip = win64;
        GameText.Foreground = (Brush)FindResource(win64 is null ? "Amber" : "Chalk");
        ChangeFolderButton.IsEnabled = !busy;

        var outdated = v.State == SetupState.InstallerOutdated;
        OutdatedCard.Visibility = outdated ? Visibility.Visible : Visibility.Collapsed;
        InfoCard.Visibility = outdated ? Visibility.Collapsed : Visibility.Visible;
        OutdatedVersions.Text = feed.RequiredInstaller is { } required ? $"Needs AimMod Setup {required}  ·  this is Setup {installerVersion}" : $"This is AimMod Setup {installerVersion}";
        if (outdated)
        {
            PrimaryButton.Content = "Download the new installer";
            PrimaryButton.Visibility = Visibility.Visible;
            PrimaryButton.IsEnabled = !busy;
        }
        else
        {
            PrimaryButton.Content = v.PrimaryLabel ?? "";
            PrimaryButton.Visibility = v.Primary == SetupAction.None ? Visibility.Collapsed : Visibility.Visible;
            PrimaryButton.IsEnabled = v.CanPrimary;
        }
        // Repair is the primary action when it is the one needed.
        RepairButton.Visibility = installed is not null && v.Primary != SetupAction.Repair ? Visibility.Visible : Visibility.Collapsed;
        RepairButton.IsEnabled = v.CanRepair && !v.Blocked;
        UninstallButton.Visibility = installed is not null || (!preview && registration.IsRegistered) ? Visibility.Visible : Visibility.Collapsed;
        UninstallButton.IsEnabled = !v.Blocked && (v.CanUninstall || (!preview && registration.IsRegistered));
        OpenFolderButton.IsEnabled = v.CanOpenFolder;

        RunningBanner.Visibility = v.BlockedMessage is not null ? Visibility.Visible : Visibility.Collapsed;
        RunningTitle.Text = v.BlockedMessage ?? "";
        RunningDetail.Text = pending is not null ? "  Setup continues by itself once the game has closed." : "  Setup never changes files while the game runs.";

        RenderNotes();
    }

    // …\steamapps\common\FPSAimTrainer, the folder people recognise.
    static string GameRoot(string win64)
    {
        var folder = win64;
        for (int i = 0; i < 3 && Path.GetDirectoryName(folder) is { } parent; i++) folder = parent;
        return Path.GetFileName(Path.GetDirectoryName(folder)) == "FPSAimTrainer" ? Path.GetDirectoryName(folder)! : folder;
    }

    void RenderNotes()
    {
        NotesPanel.Children.Clear();
        if (feed.State != FeedState.Ready)
        {
            NotesTitle.Text = feed.State == FeedState.Loading ? "Loading…" : "Release notes";
            NotesPanel.Children.Add(NoteText(feed.State switch
            {
                FeedState.Loading => "Getting the latest release from GitHub.",
                FeedState.InstallerOutdated => "The new installer shows what changed in this release.",
                _ => "Release notes appear here once Setup can reach GitHub.",
            }, "Faint"));
            return;
        }
        NotesTitle.Text = $"AimMod {feed.Feed!.Version}";
        var lines = ReleaseNotes.Summarize(feed.Feed.Notes);
        if (lines.Count == 0) { NotesPanel.Children.Add(NoteText("No release notes for this version.", "Faint")); return; }
        foreach (var line in lines)
        {
            switch (line.Kind)
            {
                case NoteKind.Heading:
                    NotesPanel.Children.Add(new TextBlock { Text = line.Text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Sage"), Margin = new Thickness(0, NotesPanel.Children.Count == 0 ? 0 : 12, 0, 6) });
                    break;
                case NoteKind.Bullet:
                    var row = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
                    var dot = new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), Background = (Brush)FindResource("Mint"), Margin = new Thickness(1, 7, 10, 0), VerticalAlignment = VerticalAlignment.Top };
                    DockPanel.SetDock(dot, Dock.Left);
                    row.Children.Add(dot);
                    row.Children.Add(new TextBlock { Text = line.Text, FontSize = 13, LineHeight = 19, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Text") });
                    NotesPanel.Children.Add(row);
                    break;
                default:
                    NotesPanel.Children.Add(NoteText(line.Text, "Muted"));
                    break;
            }
        }
    }
    TextBlock NoteText(string text, string brush) =>
        new() { Text = text, FontSize = 13, LineHeight = 19, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource(brush), Margin = new Thickness(0, 0, 0, 7) };

    void Status(string text, Tone tone = Tone.Info)
    {
        StatusText.Text = text;
        StatusText.ToolTip = text;
        StatusText.Foreground = (Brush)FindResource(tone switch { Tone.Success => "Mint", Tone.Error => "Danger", _ => "Muted" });
    }
    void ShowProgress(SetupProgress progress)
    {
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = progress.Fraction is null;
        if (progress.Fraction is { } f) Progress.Value = Math.Clamp(f, 0, 1);
        Status(progress.Text);
    }

    // ---- actions ----
    async Task Run(string kind, bool removeData = false, bool elevated = false)
    {
        if (win64 is null || busy || preview) return;
        if (engine.GameRunning(win64))
        {
            pending = kind; pendingRemoveData = removeData; running = true;
            Status("Waiting for KovaaK's to close…");
            Render();
            return;
        }
        if (!elevated && !SetupEngine.CanWrite(win64)) { elevateAction = kind; pendingRemoveData = removeData; ShowDialog(Dialog.Elevate); return; }
        busy = true;
        var target = feed.Feed?.Version;
        working = kind switch
        {
            "uninstall" => ("Uninstalling", "Removing AimMod…"),
            "repair" => ("Repairing", "Repairing AimMod…"),
            "update" => ("Updating", $"Updating to {target}…"),
            _ => ("Installing", $"Installing AimMod {target}…"),
        };
        Render();
        var progress = new Progress<SetupProgress>(ShowProgress);
        ShowProgress(new("Starting…", null));
        try
        {
            var outcome = kind switch
            {
                "uninstall" => await Task.Run(() => engine.Uninstall(win64, removeData)),
                "repair" => await engine.Repair(win64, feed.Feed, progress, CancellationToken.None),
                _ => await engine.InstallFromFeed(win64, feed.Feed ?? throw new InstallException("The latest release is not known yet. Try again."),
                    installed is { Managed: true } && kind == "update" ? "update" : "install", progress, CancellationToken.None),
            };
            if (kind == "uninstall") deleteCopyOnExit |= registration.Unregister(Environment.ProcessPath);
            else
            {
                try { registration.Register(win64, outcome.Version ?? "", outcome.Bytes, Environment.ProcessPath, installerVersion); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { engine.Log("could not add AimMod to Apps & features: " + ex.Message); }
            }
            Status(outcome.Message + (kind == "uninstall" ? "" : " Start KovaaK's from Steam."), Tone.Success);
        }
        catch (GameRunningException)
        {
            pending = kind; pendingRemoveData = removeData;
            Status("Waiting for KovaaK's to close…");
        }
        catch (Exception ex) when (ex is InstallException or ReleaseFormatException or IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException or TaskCanceledException or System.Security.SecurityException)
        {
            engine.Log($"{kind} failed: {ex.Message}");
            Status(Friendly(ex), Tone.Error);
        }
        finally
        {
            busy = false;
            Progress.Visibility = Visibility.Hidden;
            Progress.IsIndeterminate = false;
            RefreshInstalled();
            Render();
        }
    }

    static string Friendly(Exception ex) => ex switch
    {
        InstallException => ex.Message,
        ReleaseFormatException when ex.Message.StartsWith("Unsupported") => "This release needs a newer AimMod Setup. Download the new installer. Nothing was changed.",
        ReleaseFormatException => $"The download did not pass verification ({ex.Message}). Nothing was changed.",
        HttpRequestException or TaskCanceledException => "Could not download AimMod. Check your connection and try again. Nothing was changed.",
        UnauthorizedAccessException => "Windows denied access to a file AimMod needs to change. Close programs that use KovaaK's files and try again.",
        _ => ex.Message,
    };

    void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (view is null) return;
        if (view.State == SetupState.InstallerOutdated) { OpenUrl(feed.InstallerUrl ?? FeedCheck.DefaultInstallerUrl(channel)); return; }
        switch (view.Primary)
        {
            case SetupAction.Install or SetupAction.SwitchChannel: _ = Run("install"); break;
            case SetupAction.Update: _ = Run("update"); break;
            case SetupAction.Repair: _ = Run("repair"); break;
            case SetupAction.Retry: _ = LoadFeed(); break;
        }
    }
    void Repair_Click(object sender, RoutedEventArgs e) => _ = Run("repair");
    void Uninstall_Click(object sender, RoutedEventArgs e) => ShowDialog(Dialog.Uninstall);
    void Retry_Click(object sender, RoutedEventArgs e)
    {
        Tick();
        if (running) Status("KovaaK's is still running. Close it from the game menu or Steam.");
    }
    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(engine.Output)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { engine.Output } })?.Dispose(); }
        catch (Win32Exception) { }
    }
    void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the KovaaK's folder (FPSAimTrainer)" };
        if (win64 is not null) picker.InitialDirectory = GameRoot(win64);
        if (picker.ShowDialog(this) != true) return;
        var resolved = InstallState.ResolveGameDir(picker.FolderName);
        if (!InstallLayout.IsWin64(resolved)) { Status("That folder is not KovaaK's: FPSAimTrainer-Win64-Shipping.exe was not found in it.", Tone.Error); return; }
        win64 = resolved;
        pending = null;
        running = engine.GameRunning(win64);
        RefreshInstalled();
        Status("Using KovaaK's in " + GameRoot(win64));
        Render();
    }
    void Channel_Checked(object sender, RoutedEventArgs e)
    {
        if (initializing || preview) return;
        var selected = BetaChannel.IsChecked == true ? "beta" : "stable";
        if (selected == channel) return;
        channel = selected;
        _ = LoadFeed();
    }
    void SetChannelToggle(string value)
    {
        initializing = true;
        (value == "beta" ? BetaChannel : StableChannel).IsChecked = true;
        initializing = false;
    }

    static void OpenUrl(string url)
    {
        if (!UpdateFeed.IsHttps(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); } catch (Win32Exception) { }
    }

    // ---- dialogs ----
    void ShowDialog(Dialog kind)
    {
        dialog = kind;
        var uninstall = kind == Dialog.Uninstall;
        DialogEyebrow.Text = uninstall ? "UNINSTALL" : "PERMISSION NEEDED";
        DialogEyebrow.Foreground = (Brush)FindResource(uninstall ? "Danger" : "Amber");
        DialogTitle.Text = uninstall ? "Uninstall AimMod?" : "Allow changes to the KovaaK's folder?";
        DialogText.Text = uninstall
            ? "Removes UE4SS and the AimMod mods from KovaaK's and puts back the files AimMod replaced. KovaaK's itself is not changed."
            : $"Windows does not let your account change {(win64 is null ? "the game folder" : GameRoot(win64))}. Setup can restart with administrator rights for this one change; Windows asks you first.";
        RemoveDataBox.Visibility = uninstall ? Visibility.Visible : Visibility.Collapsed;
        RemoveDataCheck.IsChecked = false;
        DialogConfirm.Content = uninstall ? "Uninstall" : "Continue as administrator";
        DialogConfirm.Style = (Style)FindResource(uninstall ? "DangerButton" : "PrimaryButton");
        Overlay.Visibility = Visibility.Visible;
        DialogCancel.Focus();
    }
    void DialogCancel_Click(object sender, RoutedEventArgs e) => Overlay.Visibility = Visibility.Collapsed;
    void DialogConfirm_Click(object sender, RoutedEventArgs e)
    {
        Overlay.Visibility = Visibility.Collapsed;
        if (dialog == Dialog.Uninstall) { _ = Run("uninstall", RemoveDataCheck.IsChecked == true); return; }
        if (win64 is null || elevateAction is null || Environment.ProcessPath is not { } exe) return;
        var arguments = $"--game-dir \"{win64.TrimEnd('\\')}\" --channel {channel} --action {elevateAction}" + (pendingRemoveData ? " --remove-data" : "");
        try
        {
            Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true, Verb = "runas" })?.Dispose();
            Close();
        }
        catch (Win32Exception) { Status("Setup needs administrator rights to change this folder. Nothing was changed.", Tone.Error); }
    }

    // ---- title bar ----
    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing mid-install would leave the transaction to the next run; wait for it instead.
        if (busy && !preview) { e.Cancel = true; Status("Setup is finishing; it closes in a moment."); return; }
        timer.Stop();
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        if (deleteCopyOnExit) registration.DeleteCopyAfterExit();
        base.OnClosed(e);
    }

    // ---- sample screens for design review (--preview <screen> --capture <png>) ----
    static class Preview
    {
        const string Notes = "## [0.4.0](https://example.invalid/compare) (2026-10-01)\n\n### Features\n\n"
            + "* **kovaaks:** one window to install, update, repair and remove AimMod ([#41](https://example.invalid/41)) ([0a1b2c3](https://example.invalid/c))\n"
            + "* **kovaaks:** the buy menu remembers your last loadout\n* **kovaaks:** the replay browser opens three times faster\n\n### Bug Fixes\n\n"
            + "* **kovaaks:** no water under floors on ported maps\n* **kovaaks:** simulated players stand on the floor\n";
        const string Game = @"C:\Program Files (x86)\Steam\steamapps\common\FPSAimTrainer\FPSAimTrainer\Binaries\Win64";

        static UpdateFeed Feed(string version, string channel = "stable") =>
            new(UpdateFeed.SchemaName, channel, version, "2026-10-01T12:00:00Z", Notes, 0, new string('a', 64),
                new($"https://example.invalid/AimMod-InGame-{version}.zip", new string('b', 64), 48_000_000));

        public static void Load(MainWindow w, string screen)
        {
            w.win64 = Game;
            w.channel = "stable";
            var current = new InstalledInfo("0.3.2", "stable", true, false, []);
            switch (screen)
            {
                case "install": w.installed = null; w.feed = new(FeedState.Ready, Feed("0.4.0")); break;
                case "update": w.installed = current; w.feed = new(FeedState.Ready, Feed("0.4.0")); break;
                case "outdated":
                    w.installed = current;
                    w.feed = new(FeedState.InstallerOutdated, InstallerUrl: FeedCheck.DefaultInstallerUrl("stable"), RequiredInstaller: "1.1.0",
                        Message: $"This AimMod release needs AimMod Setup 1.1.0 or newer. This is Setup {w.installerVersion}.");
                    break;
                case "running": w.installed = current; w.feed = new(FeedState.Ready, Feed("0.4.0")); w.running = true; w.pending = "update"; break;
                case "progress":
                    w.installed = current; w.feed = new(FeedState.Ready, Feed("0.4.0")); w.busy = true; w.working = ("Updating", "Updating to 0.4.0…");
                    w.ShowProgress(new("Downloading AimMod 0.4.0… 26.9 of 45.8 MB", 0.5));
                    break;
                case "uninstall": w.installed = new InstalledInfo("0.4.0", "stable", true, false, []); w.feed = new(FeedState.Ready, Feed("0.4.0")); break;
                default: throw new ArgumentException("Unknown preview screen: " + screen);
            }
            w.Render();
            if (screen == "uninstall") w.ShowDialog(Dialog.Uninstall);
            if (w.options.Capture is { } path)
            {
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Left = -32000; w.Top = -32000; w.ShowInTaskbar = false;
                w.ContentRendered += (_, _) => w.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    Capture(w.Root, path);
                    w.Close();
                });
            }
        }

        public static void Capture(FrameworkElement element, string path)
        {
            const double scale = 2;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            element.UpdateLayout();
            bitmap.Render(element);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var file = File.Create(path);
            encoder.Save(file);
        }
    }
}
