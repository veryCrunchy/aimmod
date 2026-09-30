using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;

namespace AimMod.Desktop.Creator;

public partial class NativeFootageWorkspace
{
    private readonly ITwitchVodDiscovery? twitch;
    private TwitchAccount? twitchAccount;
    private CancellationTokenSource? twitchLinkLifetime;
    private TwitchDeviceCode? twitchCode;
    private bool connectingTwitch;
    private bool restoringTwitch = true;
    private readonly FootageChannel? automaticChannel;
    private bool archivesRequested;
    private CancellationTokenSource? archiveLifetime;

    private async Task restoreTwitch()
    {
        try
        {
            TwitchAccount? account = await Task.Run(() => twitch!.SavedAccountAsync(lifetime.Token), lifetime.Token).ConfigureAwait(false);
            if (!IsDisposed) Schedule(() =>
            {
                if (lifetime.IsCancellationRequested) return;
                twitchAccount = account; restoringTwitch = false; if (!recordingsPage) render(); maybeImportArchives();
                if (automaticChannel is not null && account is null && twitch!.IsConfigured) connectTwitch();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!IsDisposed) Schedule(() => { if (lifetime.IsCancellationRequested) return; restoringTwitch = false; status.Text = "Your Twitch connection could not be opened. Connect Twitch again."; if (!recordingsPage) render(); }); }
    }

    private void renderTwitchSearch(LocalReplay score)
    {
        if (twitch is null) return;
        var section = flow();
        section.Add(text("Find the broadcast on Twitch", 16, true));
        if (restoringTwitch) section.Add(text("Opening your Twitch connection..."));
        else if (twitchAccount is null)
        {
            section.Add(text("Connect Twitch to look up a channel's past broadcasts by the score date."));
            if (connectingTwitch)
            {
                section.Add(text(twitchCode is null ? "Getting your sign-in code..." : $"Enter {twitchCode.UserCode} on Twitch, then return here."));
                if (twitchCode is { } code) section.Add(new AimModButton("Open Twitch sign-in", () => openUrl(code.VerificationUri), true));
                section.Add(new AimModButton("Cancel connection", () => { twitchLinkLifetime?.Cancel(); connectingTwitch = false; twitchCode = null; render(); }));
            }
            else section.Add(new AimModButton("Connect Twitch", connectTwitch, true));
        }
        else
        {
            section.Add(text($"Connected as {twitchAccount.Login}"));
            var association = library.Channels.FirstOrDefault(c => string.Equals(c.Player, score.Player, StringComparison.OrdinalIgnoreCase));
            var channel = field(section, $"Twitch channel for {score.Player}", association?.TwitchLogin
                ?? (string.Equals(accountAccess?.Current()?.Username, score.Player, StringComparison.OrdinalIgnoreCase) ? twitchAccount.Login : ""), "Channel name or twitch.tv/channel");
            section.Add(row(new AimModButton("Find matching VODs", () => findTwitch(score, channel.Current.Value), true),
                new AimModButton("Disconnect Twitch", disconnectTwitch)));
            if (automaticChannel is not null || ownChannel() is not null)
                section.Add(new AimModButton("Refresh channel broadcasts", () => { archivesRequested = false; maybeImportArchives(); }));
            section.Add(text("Matches use broadcast dates and durations. Previews load only the selected moment.", 12));
        }
        body.Add(card(section));
    }

    private void connectTwitch()
    {
        if (twitch is null || connectingTwitch) return;
        twitchLinkLifetime?.Cancel(); twitchLinkLifetime?.Dispose();
        twitchLinkLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        CancellationToken token = twitchLinkLifetime.Token;
        connectingTwitch = true; twitchCode = null; render();
        _ = Task.Run(async () =>
        {
            try
            {
                var code = await twitch.BeginAsync(token).ConfigureAwait(false);
                if (!IsDisposed) Schedule(() =>
                {
                    if (token.IsCancellationRequested) return;
                    twitchCode = code; if (!recordingsPage) render(); openUrl(code.VerificationUri);
                });
                TwitchAccount account = await twitch.CompleteAsync(code, token).ConfigureAwait(false);
                if (!IsDisposed) Schedule(() =>
                {
                    if (token.IsCancellationRequested) return;
                    twitchAccount = account; connectingTwitch = false; twitchCode = null; status.Text = "Twitch connected."; if (!recordingsPage) render(); maybeImportArchives();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                if (!IsDisposed) Schedule(() =>
                {
                    if (token.IsCancellationRequested) return;
                    connectingTwitch = false; twitchCode = null; status.Text = safeError(e); if (!recordingsPage) render();
                });
            }
        }, token);
    }

    private void disconnectTwitch()
    {
        if (twitch is null) return;
        cancelQuery(); twitchLinkLifetime?.Cancel();
        archiveLifetime?.Cancel(); archivesRequested = false;
        _ = Task.Run(async () =>
        {
            try
            {
                await twitch.DisconnectAsync(lifetime.Token).ConfigureAwait(false);
                if (!IsDisposed) Schedule(() => { if (lifetime.IsCancellationRequested) return; twitchAccount = null; status.Text = "Twitch disconnected. Saved recording links and timestamps have been kept."; render(); });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { showError(safeError(e)); }
        }, lifetime.Token);
    }

    private void findTwitch(LocalReplay score, string channel)
    {
        if (twitch is null || saving) return;
        string login = TwitchVodDiscovery.NormaliseChannel(channel);
        if (login.Length == 0) { status.Text = "Enter the Twitch channel that recorded this play."; return; }
        debounce?.Cancel(); cancelQuery();
        queryLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        CancellationToken token = queryLifetime.Token; int revision = queryRevision;
        querying = true; status.Text = "Finding broadcasts around this score's date...";
        _ = Task.Run(async () =>
        {
            try
            {
                TwitchVodSearch result = await twitch.FindAsync(login, score.PlayedAt, token).ConfigureAwait(false);
                if (!IsDisposed) Schedule(() =>
                {
                    if (token.IsCancellationRequested || revision != queryRevision) return;
                    querying = false;
                    FootageRecording[] additions = result.Matches.Select(v => v.ForPlayer(score.Player))
                        .Where(r => !library.Recordings.Any(old => string.Equals(old.Player, score.Player, StringComparison.OrdinalIgnoreCase)
                            && (old.Id == r.Id || sameVideo(old.Location, r.Location)))).ToArray();
                    var next = library with
                    {
                        Recordings = library.Recordings.Concat(additions).ToArray(),
                        Channels = library.Channels.Where(c => c.OsuUserId is not null || !string.Equals(c.Player, score.Player, StringComparison.OrdinalIgnoreCase))
                            .Append(new FootageChannel(score.Player, login)).ToArray(),
                    };
                    string message = result.Matches.Length > 0 ? $"Found {result.Matches.Length} matching broadcast(s). Check the suggested timestamp below."
                        : "No broadcasts cover this score's time. The VOD may have expired; you can add a saved recording.";
                    if (!result.Complete) message += " Search limit reached; older archives may remain.";
                    if (result.UsedCachedMetadata) message += " Using archive details checked within the last 10 minutes.";
                    save(next, true, message);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (!token.IsCancellationRequested && revision == queryRevision) showError(safeError(e)); }
        }, token);
    }

    private static bool sameVideo(string left, string right) => FootageIndex.TryVideoUri(left, out var a)
        && FootageIndex.TryVideoUri(right, out var b) && a!.Host.Replace("www.", "") == b!.Host.Replace("www.", "")
        && a.AbsolutePath.TrimEnd('/') == b.AbsolutePath.TrimEnd('/');

    private void maybeImportArchives()
    {
        var channel = automaticChannel ?? ownChannel();
        if (!loaded || saving || archivesRequested || twitchAccount is null || twitch is null || channel is null) return;
        archivesRequested = true;
        archiveLifetime?.Cancel(); archiveLifetime?.Dispose(); archiveLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = archiveLifetime.Token;
        string connectedUser = twitchAccount.Id;
        status.Text = $"Loading {channel.TwitchLogin}'s available broadcasts...";
        _ = Task.Run(async () =>
        {
            try
            {
                TwitchVodSearch result = await twitch.ListArchivesAsync(channel.TwitchLogin, token).ConfigureAwait(false);
                if (!IsDisposed) Schedule(() =>
                {
                    if (token.IsCancellationRequested || twitchAccount?.Id != connectedUser
                        || channel.OsuUserId is { } owner && accountAccess?.Current()?.UserId != owner) return;
                    if (saving) { archivesRequested = false; return; }
                    var additions = result.Matches.Select(v => v.ForPlayer(channel.Player))
                        .Where(r => !library.Recordings.Any(old => string.Equals(old.Player, r.Player, StringComparison.OrdinalIgnoreCase)
                            && (old.Id == r.Id || sameVideo(old.Location, r.Location)))).ToArray();
                    save(library with { Recordings = library.Recordings.Concat(additions).ToArray() }, false,
                        $"{result.Matches.Length} available broadcasts checked. {additions.Length} added."
                        + (result.Complete ? "" : " Archive limit reached; older broadcasts may remain."), background: true);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!IsDisposed) Schedule(() => { archivesRequested = false; showError(safeError(error)); }); }
        }, token);
    }
}
