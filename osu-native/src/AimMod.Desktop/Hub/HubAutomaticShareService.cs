using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AimMod.Desktop.LocalLibrary;

namespace AimMod.Desktop.Hub;

public sealed record HubAutomaticShareAccount(string Scope, long OsuUserId, string Username)
{
    public string StorageScope => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{Scope.Trim().ToUpperInvariant()}\n{OsuUserId}")));
}

/// <summary>Observes new plays only; never interprets discovering old history as a new play.</summary>
public sealed class HubAutomaticShareService
{
    private static readonly JsonSerializerOptions json_options = new(JsonSerializerDefaults.Web);
    private readonly string statePath;
    private readonly IHubSharingPreferenceStore preferences;
    private readonly OsuHubReplayShareService shares;
    private readonly Func<HubAutomaticShareAccount?> currentAccount;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim observationGate = new(1, 1);
    private Dictionary<string, AccountState> accounts;
    private readonly HashSet<string> initializedScopes = [];

    public HubAutomaticShareService(string statePath, IHubSharingPreferenceStore preferences,
        OsuHubReplayShareService shares, Func<HubAutomaticShareAccount?> currentAccount, TimeProvider? timeProvider = null)
    {
        if (!Path.IsPathFullyQualified(statePath))
            throw new ArgumentException("Automatic sharing state requires an absolute path.", nameof(statePath));
        this.statePath = statePath;
        this.preferences = preferences;
        this.shares = shares;
        this.currentAccount = currentAccount;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        accounts = load();
        shares.SetAutomaticUploadPermission(uploadAllowed);
    }

    public async Task ObserveAsync(IReadOnlyList<LocalReplay> plays, CancellationToken cancellationToken = default)
    {
        await observationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-wake eligible persisted queue entries after a link or settings change.
            shares.SetAutomaticUploadPermission(uploadAllowed);
            HubSharingPreferences selected = preferences.Load();
            HubAutomaticShareAccount? account = currentAccount();
            if (!enabled(selected) || account is null || account.OsuUserId <= 0 || string.IsNullOrWhiteSpace(account.Username))
                return;
            string scope = scopeKey(account);
            DateTimeOffset now = timeProvider.GetUtcNow();
            if (!accounts.TryGetValue(scope, out AccountState? state)
                || state.Generation != selected.AutomaticSharingGeneration || !initializedScopes.Contains(scope))
            {
                // The wall-clock watermark also excludes history arriving in later pages.
                state = new AccountState(selected.AutomaticSharingGeneration, now, state?.Observed ?? []);
                accounts[scope] = state;
                await persistAsync(cancellationToken).ConfigureAwait(false);
                initializedScopes.Add(scope);
                return;
            }

            foreach (LocalReplay play in plays.OrderBy(play => play.PlayedAt))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!stillAllowed(account, selected))
                    break;
                if (play.PlayedAt <= state.StartedAt || play.PlayedAt > now
                    || !string.Equals(play.Player, account.Username, StringComparison.OrdinalIgnoreCase)
                    || play.RulesetShortName != "osu")
                    continue;
                string[] identities = playKeys(play);
                if (identities.Any(state.Observed.Contains))
                {
                    // Online IDs can arrive after the local score was already shared.
                    if (identities.Any(identity => !state.Observed.Contains(identity)))
                    {
                        state.Observed.UnionWith(identities);
                        await persistAsync(cancellationToken).ConfigureAwait(false);
                    }
                    continue;
                }
                // An uncalculated PP value is not a zero and may become available on a later refresh.
                if (play.PerformancePoints is not { } pp || !double.IsFinite(pp)
                    || !double.IsFinite(play.Accuracy) || play.Accuracy is < 0 or > 1)
                    continue;
                if (pp >= selected.MinimumPp && play.Accuracy * 100 + 1e-9 >= selected.MinimumAccuracy)
                {
                    await shares.QueueAutomaticAsync(play, selected, account.OsuUserId, scope,
                        scope + ":" + identities[0], () => stillAllowed(account, selected), cancellationToken).ConfigureAwait(false);
                    if (!stillAllowed(account, selected))
                        break;
                }
                // Remember below-threshold plays too: lowering a threshold is not a backfill operation.
                state.Observed.UnionWith(identities);
                await persistAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally { observationGate.Release(); }
    }

    private bool uploadAllowed(HubUploadQueueItem item)
    {
        HubSharingPreferences selected = preferences.Load();
        HubAutomaticShareAccount? account = currentAccount();
        return enabled(selected) && account is not null
            && selected.AutomaticSharingGeneration == item.AutomaticGeneration
            && scopeKey(account) == item.AutomaticAccountScope
            && account.OsuUserId == item.Request.Profile.OsuUserId;
    }

    private bool stillAllowed(HubAutomaticShareAccount account, HubSharingPreferences selected) =>
        preferences.Load() == selected && currentAccount() == account;

    private static bool enabled(HubSharingPreferences value) =>
        value.AutomaticSharingEnabled && value.AutomaticSharingGeneration != Guid.Empty;

    private static string scopeKey(HubAutomaticShareAccount account) => account.StorageScope;

    private static string[] playKeys(LocalReplay play)
    {
        // Submission can replace a local ID and score format. Map, end time and mods
        // remain stable; second precision matches the online API's timestamp precision.
        string fingerprint = JsonSerializer.Serialize(new
        {
            Title = play.Title.Trim().ToUpperInvariant(),
            Artist = play.Artist.Trim().ToUpperInvariant(),
            Difficulty = play.Difficulty.Trim().ToUpperInvariant(),
            play.RulesetShortName,
            EndedAt = play.PlayedAt.ToUnixTimeSeconds(),
            Mods = play.Mods.Select(mod => mod.ToUpperInvariant()).Order(StringComparer.Ordinal).ToArray(),
        });
        var identities = new List<string>
        {
            "play:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint))),
            $"local:{play.Origin}:{play.ScoreId:N}",
        };
        if (play.OnlineScoreId > 0)
            identities.Add($"online:{play.OnlineScoreId}");
        return identities.ToArray();
    }

    private Dictionary<string, AccountState> load()
    {
        try
        {
            if (!File.Exists(statePath))
                return [];
            using FileStream stream = File.OpenRead(statePath);
            StateDocument? document = JsonSerializer.Deserialize<StateDocument>(stream, json_options);
            if (document?.Version != 1 || document.Accounts is null
                || document.Accounts.Values.Any(value => value is null || value.Observed is null || value.StartedAt == default))
                return [];
            return document.Accounts;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing or damaged state establishes a fresh baseline, never replays history.
            return [];
        }
    }

    private async Task persistAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        string temporary = $"{statePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, new StateDocument(1, accounts), json_options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, statePath, true);
        }
        catch
        {
            accounts = load();
            throw;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record AccountState(Guid Generation, DateTimeOffset StartedAt, HashSet<string> Observed);
    private sealed record StateDocument(int Version, Dictionary<string, AccountState> Accounts);
}
