using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.ScoreHistory;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;

namespace AimMod.Desktop.Creator;

public sealed record CreatorAccountAccess(Func<OsuProfile?> Current, Func<CancellationToken, Task<OnlineAccountScoreHistoryResult>> FetchScores, Action OpenSettings);

public partial class NativeFootageWorkspace
{
    private readonly CreatorAccountAccess? accountAccess;
    private bool accountsPage;
    private bool loadingOwnScores;
    private bool useOwnScores;
    private int? ownScoreAccount;
    private ILocalLibrarySource? ownScores;
    private LocalReplay[] ownScoreRecords = [];
    private string ownScoreStatus = "";

    private void openAccounts()
    {
        debounce?.Cancel(); cancelQuery(); accountsPage = true; recordingsPage = false; render();
    }

    private FootageChannel? ownChannel() => FootageIndex.FindOwnChannel(library, accountAccess?.Current()?.UserId, twitchAccount?.Id);

    private void renderAccounts()
    {
        body.Add(text("Your accounts", 20, true));
        var account = accountAccess?.Current();
        body.Add(text(account is null ? "Connect your osu! client in Settings to use your account and scores." : $"osu! · {account.Username}", 16));
        if (accountAccess is not null) body.Add(new AimModButton(account is null ? "Connect osu!" : "Manage osu! account", accountAccess.OpenSettings));
        if (account is not null) body.Add(new AimModButton(loadingOwnScores ? "Loading your scores..." : "Refresh your online scores", refreshOwnScores));
        if (ownScoreStatus.Length > 0) body.Add(text(ownScoreStatus));
        body.Add(text("Local recordings", 16, true));
        body.Add(text("Match scores to videos on your computer. No Twitch account is needed."));
        body.Add(new AimModButton("Add local recording", () => renderRecordingEditor(null, true)));
        if (twitch is null) return;
        if (restoringTwitch) body.Add(text("Opening your Twitch connection..."));
        else if (twitchAccount is null)
        {
            body.Add(text("Connect your Twitch account to import your past broadcasts."));
            if (connectingTwitch)
            {
                body.Add(text(twitchCode is null ? "Getting your sign-in code..." : $"Enter {twitchCode.UserCode} on Twitch, then return here."));
                if (twitchCode is { } code) body.Add(new AimModButton("Open Twitch sign-in", () => openUrl(code.VerificationUri), true));
                body.Add(new AimModButton("Cancel", () => { twitchLinkLifetime?.Cancel(); connectingTwitch = false; twitchCode = null; render(); }));
            }
            else body.Add(new AimModButton("Connect my Twitch account", connectTwitch, true));
        }
        else
        {
            body.Add(text($"Twitch · {twitchAccount.Login}", 16));
            if (account is not null)
            {
                var link = ownChannel();
                body.Add(text(link is null ? "Link these accounts to match your osu! scores with your Twitch broadcasts."
                    : $"{account.Username} is linked to twitch.tv/{link.TwitchLogin}."));
                body.Add(new AimModButton(link is null ? "Link my accounts & import broadcasts" : "Refresh my broadcasts", linkOwnAccounts, true));
            }
            body.Add(new AimModButton("Disconnect Twitch", disconnectTwitch));
        }
        body.Add(text("Public scores cover the available best and recent score windows. Older attempts or expired broadcasts may be missing.", 12));
        body.Add(new AimModButton("Choose a score", () => { accountsPage = false; selected = null; render(); loadScores(0); }));
    }

    private void linkOwnAccounts()
    {
        if (saving || twitchAccount is null || accountAccess?.Current() is not { } account) return;
        var link = new FootageChannel(account.Username, twitchAccount.Login, account.UserId, twitchAccount.Id);
        archivesRequested = false;
        save(library with { Channels = library.Channels.Where(c => c.OsuUserId != account.UserId
            && !string.Equals(c.Player, account.Username, StringComparison.OrdinalIgnoreCase)).Append(link).ToArray() },
            successMessage: "Accounts linked. Loading your broadcasts...");
    }

    private void refreshOwnScores()
    {
        if (loadingOwnScores || accountAccess?.Current() is not { } account) return;
        loadingOwnScores = true; ownScoreStatus = "Loading your online scores..."; if (accountsPage) render();
        int expected = account.UserId;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await accountAccess.FetchScores(lifetime.Token).ConfigureAwait(false);
                if (!IsDisposed) Schedule(() =>
                {
                    if (lifetime.IsCancellationRequested) return;
                    loadingOwnScores = false;
                    if (accountAccess.Current()?.UserId != expected) { ownScores = null; ownScoreAccount = null; return; }
                    if (result.Profile?.UserId != expected) { ownScoreStatus = "Your online scores could not be loaded. Check your osu! connection and retry."; if (accountsPage) render(); return; }
                    ownScoreAccount = expected;
                    ownScoreRecords = result.Scores.Select(s => FootageScoreLookup.FromHistory(s, result.Profile.Username)).ToArray();
                    ownScores = new InMemoryLocalLibrarySource([], ownScoreRecords);
                    useOwnScores = true;
                    ownScoreStatus = $"{result.Scores.Count} online scores loaded for {result.Profile.Username}.";
                    if (accountsPage) render(); else if (selected is null && !recordingsPage) loadScores(0);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!IsDisposed) Schedule(() => { loadingOwnScores = false; ownScoreStatus = safeError(error); if (accountsPage) render(); }); }
        }, lifetime.Token);
    }
}
