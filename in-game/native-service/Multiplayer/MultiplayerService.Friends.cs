using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// "Juniper is playing with AimMod" toasts when a friend starts AimMod, shown in game
// through the notice layer with Join, Watch or Invite. Never for friends already online
// when this machine starts, never during a run, at most one every 20 s and once per
// friend every 30 minutes.
sealed partial class MultiplayerService
{
    const long FriendToastMs = 6000, FriendToastGap = 20_000, FriendToastRepeat = 30 * 60_000;
    readonly Dictionary<string, string> friendStatus = new();
    readonly Dictionary<string, long> friendToasted = new();
    bool friendsPrimed;
    long lastFriendToast = long.MinValue / 2, friendsCheckedAt;

    static bool InAimMod(string? status) => status is "aimmod" or "aimmod-lobby";

    void WatchFriends(long now)
    {
        if (now - friendsCheckedAt < 1000) return;
        friendsCheckedAt = now;
        var list = Friends();
        if (list.Count == 0) return;
        var seen = new HashSet<string>();
        FriendEntry? arrived = null;
        foreach (var f in list)
        {
            seen.Add(f.Id);
            var before = friendStatus.GetValueOrDefault(f.Id);
            friendStatus[f.Id] = f.Status;
            if (!friendsPrimed || InAimMod(before) || !InAimMod(f.Status)) continue;
            if (now - friendToasted.GetValueOrDefault(f.Id, long.MinValue / 2) < FriendToastRepeat) continue;
            arrived ??= f;
        }
        foreach (var gone in friendStatus.Keys.Where(k => !seen.Contains(k)).ToArray()) friendStatus.Remove(gone);
        friendsPrimed = true;
        if (arrived is null || !prefs.FriendToasts || OwnRankedRun() || liveRun().Active || now - lastFriendToast < FriendToastGap) return;
        if (flash is { } f0 && now < f0.Until) return;
        FriendToast(arrived, now);
    }

    void FriendToast(FriendEntry arrived, long now)
    {
        friendToasted[arrived.Id] = now; lastFriendToast = now;
        var name = LobbyRules.CleanName(arrived.Name, "A friend");
        var actions = new List<NoticeAction>();
        if (arrived.Joinable && Current is null && hostPeer is null) actions.Add(new("Join", "friend-join", arrived.Id));
        else if (Current is { } lobby && lobby.HostId == SelfId && lobby.Members.Count < lobby.Settings.MaxPlayers) actions.Add(new("Invite", "friend-invite", arrived.Id));
        if (arrived.Spectatable && watch is null) actions.Add(new("Watch", "friend-watch", arrived.Id));
        actions.Add(new("Dismiss", "friend-dismiss", arrived.Id));
        var detail = arrived.Status == "aimmod-lobby" ? "In an AimMod lobby" : arrived.Detail is { Length: > 0 } d ? Detail(d) : "Playing KovaaK’s with AimMod";
        flash = (new GameNotice("fr-" + arrived.Id + "-" + now, "friend", name + " is on AimMod", detail, null, null, "click") { Actions = actions }, now + FriendToastMs);
    }

    // Status lines like "Playing <scenario>" are longer than a name; keep up to 80 characters.
    static string Detail(string text)
    {
        var clean = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > 80 ? clean[..79].TrimEnd() + "…" : clean;
    }

    LobbyResult FriendNotice(string action, string? id)
    {
        if (action == "friend-dismiss") { if (flash?.Notice.Kind == "friend") flash = null; return LobbyResult.Success; }
        if (id is null) return LobbyResult.Fail("invalid", "Choose a friend.");
        if (flash?.Notice.Kind == "friend") flash = null;
        var args = JsonSerializer.SerializeToElement(new { friend = id });
        var result = action switch { "friend-join" => Act("join-friend", args), "friend-invite" => Act("invite-friend", args), "friend-watch" => Act("watch", args), _ => LobbyResult.Fail("invalid", "Unknown action.") };
        if (result.Ok && action == "friend-join") RequestPanel();
        return result;
    }
}
