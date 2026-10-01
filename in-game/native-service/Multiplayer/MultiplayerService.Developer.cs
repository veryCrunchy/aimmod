using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// Developer menu hooks: the simulation switched on at runtime, simulated lobbies
// of any size, and every in-game notice on demand. Reached only through the
// local UI (capability URL + X-AimMod-UI); never through lobby data or peers.
// Nothing here starts a run, so ranked play is never touched.
sealed partial class MultiplayerService
{
    int simulationSeed;
    bool simulationForced;
    long devCountdownUntil; string? devCountdownTitle;
    long devWatcherUntil;

    public bool SimulationOn { get { lock (gate) return Simulation is not null; } }
    public ContentLibrary Library => library;
    public string? OutputFolder => outputFolder;
    // Spectating someone for real: the bridge owns spectate-pose.tsv then.
    public bool WatchingSomeone { get { lock (gate) return watch is not null || spectating is not null; } }

    public LobbyResult DevAvatar(bool on, string? mode)
    {
        lock (gate)
        {
            if (Simulation is null) return LobbyResult.Fail("dev-off", "Turn on developer mode first.");
            if (mode is not ("circle" or "path")) return LobbyResult.Fail("invalid", "Pick circle or path.");
            return transport.DevAvatar(on, mode) ? LobbyResult.Success
                : LobbyResult.Fail("bridge", "The Steam bridge can’t switch the test avatar yet. Set avatar_test=1 in AimModSteam’s config.txt and restart KovaaK’s.");
        }
    }

    // The Workshop search the Map Library uses, on demand; items arrive with the next view.
    public LobbyResult DevWorkshop(string? text)
    {
        lock (gate)
        {
            if (Simulation is null) return LobbyResult.Fail("dev-off", "Turn on developer mode first.");
            if (!transport.Available) return LobbyResult.Success; // the simulated catalog answers
            return transport.QueryWorkshop(string.IsNullOrWhiteSpace(text) ? MapPorts.TitlePrefix : text.Trim()) ? LobbyResult.Success : LobbyResult.Fail("bridge", "The Steam bridge can’t list Workshop items (feature ugc-query).");
        }
    }
    public object DevWorkshopItems() { lock (gate) return Catalog().Take(50).Select(i => new { i.Item, i.Title, i.Bytes, i.Installed, i.NeedsUpdate, port = MapPorts.Parse(i.Title) is not null }).ToArray(); }

    // Developer mode on: the simulation runs alongside the real transport (simulated
    // friends only show while Steam is off). Off: a simulated lobby is left.
    public void SetSimulation(bool on)
    {
        lock (gate)
        {
            if (on && Simulation is null) Simulation = new MultiplayerSimulation(clock, library, completedRuns, simulationSeed);
            else if (!on && Simulation is not null && !simulationForced)
            {
                if (Current is { } lobby && lobby.Members.Any(m => m.Simulated)) Leave("left");
                watchers.RemoveAll(w => w.Peer.StartsWith("dev-", StringComparison.Ordinal)); watchAsks.RemoveAll(a => a.Peer.StartsWith("dev-", StringComparison.Ordinal));
                Simulation = null;
            }
        }
    }

    // A lobby with `members` simulated players: hosted here, or hosted by a simulated player
    // (to test being a member, host migration and the host's start).
    public LobbyResult DevLobby(int members, string? mode, bool simulatedHost)
    {
        lock (gate)
        {
            if (Simulation is null) return LobbyResult.Fail("dev-off", "Turn on developer mode first.");
            if (liveRun().Active) return LobbyResult.Fail("run-active", "Finish or leave your run first. Developer tools never touch a run in progress.");
            members = Math.Clamp(members, 1, 7);
            if (Current is not null || hostPeer is not null || joinPendingSince is not null) Leave("left");
            if (simulatedHost)
            {
                var joined = JoinBy(LobbyCore.NewCode());
                if (!joined.Ok || core is null) return joined;
            }
            else
            {
                var created = Act("create", JsonSerializer.SerializeToElement(new { mode = mode is not null && LobbyModes.All.Contains(mode) ? mode : LobbyModes.Race }));
                if (!created.Ok || core is null) return created;
            }
            // Room for everyone: the simulated host (if any), the simulated players and you.
            var size = Math.Min(LobbySettings.MaxPlayerLimit, members + 1);
            if (core.Settings.MaxPlayers < size) core.Apply(core.HostId, "settings", JsonSerializer.SerializeToElement(new { settings = new { maxPlayers = size } }), library);
            var have = core.Members.Count(m => m.Simulated);
            for (var i = have; i < members; i++) Simulation.Add(core);
            return LobbyResult.Success;
        }
    }

    // What the developer page shows about the bridge, the game side and the simulation.
    public object DevStatus()
    {
        lock (gate)
        {
            var lobby = Current;
            return new
            {
                transport = transport.Kind, online = transport.Available, bridge = transport.BridgeVersion,
                capabilities = game.Capabilities.OrderBy(c => c, StringComparer.Ordinal).ToArray(),
                simulation = Simulation is not null, simulationForced,
                looks = AvatarProfiles.All.Select(a => new { a.Id, a.Label }), look = prefs.Avatar,
                lobby = lobby is null ? null : new { lobby.Code, members = lobby.Members.Count, simulated = lobby.Members.Count(m => m.Simulated), isHost = lobby.HostId == SelfId, phase = lobby.Match?.Phase ?? "lobby" },
            };
        }
    }

    public static readonly string[] DevNotices = ["invite", "request", "ready", "countdown", "round", "friend", "watching", "ask", "update", "repair"];

    // Show one in-game notice now, through the same notice layer the real ones use.
    public LobbyResult DevNotice(string kind)
    {
        lock (gate)
        {
            if (Simulation is null) return LobbyResult.Fail("dev-off", "Turn on developer mode first.");
            var now = clock();
            var key = HotkeyName;
            switch (kind)
            {
                case "invite" or "request":
                    Simulation.Control(null, kind, null); TakeSimulatedInvites(); return LobbyResult.Success;
                case "ready":
                    flash = (new GameNotice("dev-rc-" + now, "ready", "Nova is starting", "Press " + key + " to ready up.", key, null, "popup"), now + 8000); return LobbyResult.Success;
                case "countdown" or "round":
                    devCountdownUntil = now + 5000; devCountdownTitle = kind == "round" ? "Round 2 starting" : "Match starting"; return LobbyResult.Success;
                case "friend":
                    FriendToast(new FriendEntry("sim-f4", "Vesper", "aimmod", "Playing Air Angelic 4", null, false, Spectatable: true), now); return LobbyResult.Success;
                case "watching":
                    watchers.RemoveAll(w => w.Peer == "dev-watcher"); watchers.Add(("dev-watcher", "Nova", now)); devWatcherUntil = now + 20_000; return LobbyResult.Success;
                case "ask":
                    watchAsks.RemoveAll(a => a.Peer == "dev-asker"); watchAsks.Add(("dev-asker", "Kestrel", now)); return LobbyResult.Success;
                case "update":
                    flash = (new GameNotice("dev-up-" + now, "info", "AimMod update ready", "Restart KovaaK’s to finish updating.", null, null, "click"), now + 6000); return LobbyResult.Success;
                case "repair":
                    flash = (new GameNotice("dev-fix-" + now, "ready", "AimMod needs a repair", "Open AimMod > Settings and choose Repair.", null, null, "popup"), now + 6000); return LobbyResult.Success;
                default:
                    return LobbyResult.Fail("invalid", "Unknown notice.");
            }
        }
    }

    // The developer countdown notice, counting down like a real one; the watcher goes after 20 s.
    GameNotice? DevNoticeNow(long now)
    {
        if (devWatcherUntil != 0 && now > devWatcherUntil) { devWatcherUntil = 0; watchers.RemoveAll(w => w.Peer == "dev-watcher"); }
        if (devCountdownUntil == 0) return null;
        if (now >= devCountdownUntil) { devCountdownUntil = 0; return null; }
        var seconds = (int)Math.Ceiling((devCountdownUntil - now) / 1000.0);
        return new GameNotice("dev-cd-" + devCountdownUntil, "countdown", devCountdownTitle + " in " + seconds, "Developer test · Get ready.", null, seconds, "countdown");
    }
}
