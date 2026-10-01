using AimMod.InGame;

namespace AimMod.Setup;

enum SetupState { NoGame, Checking, Offline, InstallerOutdated, NotInstalled, UpdateAvailable, UpToDate, NewerThanChannel, DeveloperInstall, NeedsNewerGame, NeedsRepair }
enum SetupAction { None, Install, Update, Repair, SwitchChannel, Retry }

// What is installed in the game folder (from ue4ss\aimmod-install.json and the health check).
sealed record InstalledInfo(string? Version, string? Channel, bool Managed, bool NeedsRepair, IReadOnlyList<string> Problems)
{
    public SemanticVersion? SemVer => Version is not null && SemanticVersion.TryParse(Version, out var v) ? v : null;
}

sealed record SetupInputs(
    SemanticVersion InstallerVersion,
    string Channel,
    string? Win64,
    bool GameRunning,
    InstalledInfo? Installed,
    FeedStatus Feed,
    long? SteamBuildId = null,
    bool RepairCached = false,
    bool DataFolderExists = false,
    bool Busy = false);

sealed record SetupView(
    SetupState State,
    string Eyebrow,
    string Title,
    string Detail,
    SetupAction Primary,
    string? PrimaryLabel,
    string? TargetVersion,
    bool CanInstall,
    bool CanRepair,
    bool CanUninstall,
    bool CanOpenFolder,
    bool CanChangeChannel,
    bool Blocked,
    string? BlockedMessage)
{
    public bool CanPrimary => Primary != SetupAction.None && !Blocked && (Primary is SetupAction.Retry || CanInstall || (Primary == SetupAction.Repair && CanRepair));
}

// The installer's decisions, free of UI and IO, so they can be checked with
// synthetic feeds (in-game/installer/checks).
static class SetupDecision
{
    public const string GameRunningMessage = "Close KovaaK's to continue";

    public static SetupView Decide(SetupInputs input)
    {
        var view = DecideState(input);
        // Nothing that changes files while the game runs: the view stays, the actions wait.
        if (input.GameRunning && input.Win64 is not null)
            view = view with { Blocked = true, BlockedMessage = GameRunningMessage };
        if (input.Busy)
            view = view with { Blocked = true, BlockedMessage = null, CanChangeChannel = false };
        return view;
    }

    // Upgrade, same version or a downgrade (a channel switch from a newer beta to stable).
    public static int Compare(SemanticVersion latest, SemanticVersion installed) => latest.CompareTo(installed);

    public static bool NeedsNewerGame(UpdateFeed feed, long? steamBuildId) =>
        feed.MinimumSteamBuildId > 0 && steamBuildId is long build && build < feed.MinimumSteamBuildId;

    static SetupView DecideState(SetupInputs input)
    {
        var installed = input.Installed;
        var feed = input.Feed;
        var canUninstall = installed is not null;
        var canRepairFromCache = installed is { Managed: true } && input.RepairCached;
        SetupView View(SetupState state, string eyebrow, string title, string detail, SetupAction primary = SetupAction.None, string? label = null, string? target = null, bool install = false, bool? repair = null) =>
            new(state, eyebrow, title, detail, primary, label, target, install, repair ?? canRepairFromCache, canUninstall, input.DataFolderExists, true, false, null);

        if (input.Win64 is null)
            return View(SetupState.NoGame, "KovaaK's not found", "Where is KovaaK's installed?",
                "Setup looks for KovaaK's in your Steam libraries. Pick its folder (FPSAimTrainer) to continue.", repair: false) with { CanUninstall = false };

        if (feed.State == FeedState.InstallerOutdated)
            return View(SetupState.InstallerOutdated, "Setup update needed", "A new installer is required",
                (installed is not null ? "Your installed AimMod keeps working. " : "") + "To install the latest AimMod, download the new AimMod Setup and run it.");

        if (installed is { Managed: false })
        {
            if (feed.State == FeedState.Ready)
                return View(SetupState.DeveloperInstall, "Developer install", "AimMod was installed with the developer scripts",
                    $"Install AimMod {feed.Feed!.Version} from the {Name(input.Channel)} channel to get automatic updates. Files AimMod replaced stay backed up.",
                    SetupAction.Install, $"Install AimMod {feed.Feed.Version}", feed.Feed.Version, install: true, repair: false);
            return Pending(input, View, repair: false);
        }

        var current = installed?.SemVer;
        if (feed.State != FeedState.Ready || feed.Feed is null)
        {
            if (installed?.NeedsRepair == true && canRepairFromCache)
                return View(SetupState.NeedsRepair, "Repair needed", "AimMod needs a repair",
                    Problems(installed) + " Setup can repair it from the copy saved at install time.", SetupAction.Repair, "Repair AimMod", installed.Version);
            return Pending(input, View);
        }

        var latest = feed.Feed.SemVer;
        var newerGame = NeedsNewerGame(feed.Feed, input.SteamBuildId);
        var gameNote = "This AimMod release needs a newer KovaaK's. Update the game in Steam, then try again.";
        var repairFromFeed = current is { } c && Compare(latest, c) == 0;

        if (installed is null || current is null)
        {
            if (newerGame) return View(SetupState.NeedsNewerGame, "Update KovaaK's", $"AimMod {latest} needs a newer KovaaK's", gameNote, repair: false);
            return View(SetupState.NotInstalled, "Ready to install", $"Install AimMod {latest}",
                $"Adds UE4SS and the AimMod mods to KovaaK's. Files it replaces are backed up and restored when you uninstall. Channel: {Name(input.Channel)}.",
                SetupAction.Install, $"Install AimMod {latest}", latest.ToString(), install: true, repair: false);
        }

        var cmp = Compare(latest, current.Value);
        if (cmp > 0)
        {
            if (newerGame) return View(SetupState.NeedsNewerGame, "Update KovaaK's", $"AimMod {latest} needs a newer KovaaK's", gameNote);
            var detail = installed.NeedsRepair
                ? $"AimMod {current} also needs a repair; updating fixes it. {Problems(installed)}"
                : $"You have AimMod {current}. The update installs in a few seconds and keeps your history, replays and settings.";
            return View(SetupState.UpdateAvailable, "Update available", $"AimMod {latest} is available", detail,
                SetupAction.Update, $"Update to {latest}", latest.ToString(), install: true);
        }
        if (installed.NeedsRepair)
            return View(SetupState.NeedsRepair, "Repair needed", "AimMod needs a repair",
                Problems(installed) + (cmp == 0 ? " Repairing re-installs the same version." : ""),
                SetupAction.Repair, "Repair AimMod", current.ToString(), repair: canRepairFromCache || repairFromFeed);
        if (cmp == 0)
            return View(SetupState.UpToDate, "Up to date", $"AimMod {current} is installed",
                $"This is the latest {Name(input.Channel)} release. AimMod checks for updates on its own when KovaaK's starts.",
                repair: canRepairFromCache || repairFromFeed);
        // The installed version is newer than the channel offers: a switch from Beta to Stable.
        if (newerGame) return View(SetupState.NeedsNewerGame, "Update KovaaK's", $"AimMod {latest} needs a newer KovaaK's", gameNote);
        var switching = installed.Channel is not null && installed.Channel != input.Channel;
        return View(SetupState.NewerThanChannel, switching ? "Switch channel" : "Newer version installed",
            switching ? $"Switch to {Name(input.Channel)}" : $"AimMod {current} is newer than the {Name(input.Channel)} release",
            $"You have AimMod {current}. The latest {Name(input.Channel)} release is {latest}, which is older. AimMod only updates forward, so it stays on {current} until {Name(input.Channel)} catches up, unless you install {latest} now.",
            SetupAction.SwitchChannel, $"Install {latest}", latest.ToString(), install: true);
    }

    static SetupView Pending(SetupInputs input, Func<SetupState, string, string, string, SetupAction, string?, string?, bool, bool?, SetupView> view, bool? repair = null)
    {
        var installed = input.Installed;
        var title = installed?.Version is { } v ? $"AimMod {v} is installed" : installed is not null ? "AimMod is installed" : "Install AimMod";
        if (input.Feed.State is FeedState.Unreachable or FeedState.NotPublished)
            return view(SetupState.Offline, input.Feed.State == FeedState.NotPublished ? "Not published yet" : "Unavailable", title,
                input.Feed.State == FeedState.NotPublished
                    ? (input.Feed.Message ?? $"No {Name(input.Channel)} release has been published yet.") + $" Switch to {(input.Channel == "beta" ? "Stable" : "Beta")} or try again later."
                    : input.Feed.Message ?? "Could not reach GitHub to get the latest AimMod. Check your connection and try again.",
                SetupAction.Retry, "Try again", null, false, repair);
        return view(SetupState.Checking, "Checking", title, $"Looking for the latest {Name(input.Channel)} release…", SetupAction.None, null, null, false, repair);
    }

    static string Problems(InstalledInfo installed) =>
        installed.Problems.Count == 0 ? "Some AimMod files are missing or changed." : installed.Problems[0] + (installed.Problems.Count > 1 ? $" (and {installed.Problems.Count - 1} more)" : "");

    public static string Name(string channel) => channel == "beta" ? "Beta" : "Stable";
}
