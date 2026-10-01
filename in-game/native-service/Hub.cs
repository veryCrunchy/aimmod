using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace AimMod.InGame;

sealed partial class Hub : IDisposable
{
    const string Origin = "https://aimmod.app";
    readonly HttpClient http;
    readonly string folder, vault;
    readonly TimeProvider clock;
    readonly Action<string> openBrowser;
    HubAccount? account;
    string deviceCode = "", userCode = "", approvalUrl = "";
    DateTimeOffset expires, nextPoll, nextSync;
    int interval = 3;
    readonly Queue<string> scenarios = new();
    HubPagination historyPage = new();
    bool? paginationSupported;
    readonly Dictionary<string, Run> downloaded = new();
    List<Row> benchmarks = [];
    string status = "Link your account to bring your Hub scores into your history.";
    bool partial;
    long expectedRuns;
    DateTimeOffset nextHistory;
    // Written by HTTP handlers (leaderboard/benchmark requests) and the refresh
    // loop; stored as UTC ticks so reads and writes are never torn. Only extended.
    long retryAfterTicks;
    DateTimeOffset retryAfter => new(Interlocked.Read(ref retryAfterTicks), TimeSpan.Zero);
    void DeferUntil(DateTimeOffset value)
    {
        long observed, next = value.UtcTicks;
        do { observed = Interlocked.Read(ref retryAfterTicks); if (observed >= next) return; }
        while (Interlocked.CompareExchange(ref retryAfterTicks, next, observed) != observed);
    }
    // Consecutive refresh failures: 2, 4, 8, 16 then 30 minutes between retries.
    int failures;
    DateTimeOffset lastCacheSave;
    internal static TimeSpan Backoff(int failures) => TimeSpan.FromMinutes(Math.Min(30, 2 << Math.Clamp(failures - 1, 0, 4)));
    readonly Dictionary<string, long> syncedCounts = new(StringComparer.Ordinal);
    readonly Dictionary<string, long> desiredCounts = new(StringComparer.Ordinal);
    readonly System.Collections.Concurrent.ConcurrentQueue<string> commands = new();
    public bool Enqueue(string command)
    {
        if (commands.Count >= 16 || command.Length > 1024) return false;
        if (command is not ("link" or "open-link" or "cancel-link" or "unlink" or "refresh-hub" or "history-next" or "history-previous" or "history-all") && !command.StartsWith("scenario\t", StringComparison.Ordinal) && !command.StartsWith("search-history\t", StringComparison.Ordinal)) return false;
        commands.Enqueue(command); return true;
    }
    public object AccountInfo => new { label = account?.Label, linked = account is not null, pending = deviceCode.Length > 0, code = userCode, status, partial, cachedRuns = downloaded.Count };
    public int Revision { get; private set; }
    public int HistoryPage { get; private set; }
    public int MaxHistoryPage { get; set; }
    public string SelectedScenario { get; private set; } = "";
    public string HistorySearch { get; private set; } = "";
    public bool MatchesHistory(Run run) => (SelectedScenario.Length == 0 || run.Scenario.Equals(SelectedScenario, StringComparison.OrdinalIgnoreCase))
        && (HistorySearch.Length == 0 || run.Scenario.Contains(HistorySearch, StringComparison.OrdinalIgnoreCase));
    public string ExternalId => account?.ExternalId ?? "";
    // Public Hub handle of the linked account, for the Discord profile button.
    public string? LinkedHandle => account?.Handle;
    public IReadOnlyCollection<Run> Runs => downloaded.Values;

    readonly Func<bool> historyEnabled;
    bool? previousHistoryEnabled;
    public Hub(string folder, HttpMessageHandler? handler = null, TimeProvider? clock = null, Action<string>? openBrowser = null, Func<bool>? historyEnabled = null)
    {
        this.historyEnabled = historyEnabled ?? (() => true);
        this.clock = clock ?? TimeProvider.System; this.folder = folder; vault = Path.Combine(folder, "account.bin");
        this.openBrowser = openBrowser ?? (url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
        http = handler is null ? new(new HttpClientHandler { AllowAutoRedirect = false }) : new(handler);
        http.Timeout = TimeSpan.FromSeconds(12);
        http.DefaultRequestHeaders.Add("User-Agent", "AimMod-InGame/0.1");
        try { account = AccountVault.Read(vault); }
        catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException)
        { account = null; status = "Saved account unavailable. Link your account again."; }
        // A damaged offline cache is only a cache: keep the account linked and re-download.
        try { if (account is not null) LoadCache(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { downloaded.Clear(); benchmarks = []; benchmarkItems = []; syncedCounts.Clear(); expectedRuns = 0; partial = false; status = "Saved Hub history could not be read. Downloading it again…"; }
        try
        {
            var pending = AccountVault.ReadRecord<PendingLink>(Path.Combine(folder, "pending-link.bin"));
            if (pending is not null && pending.Expires > this.clock.GetUtcNow() && SafeApprovalUrl(pending.ApprovalUrl)
                && pending.DeviceCode.Length is > 0 and <= 4096 && pending.UserCode.Length is > 0 and <= 128)
            {
                deviceCode = pending.DeviceCode; userCode = pending.UserCode; approvalUrl = pending.ApprovalUrl;
                expires = pending.Expires; nextPoll = pending.NextPoll; interval = Math.Clamp(pending.Interval, 3, 60);
                status = "Waiting for account approval in your browser.";
            }
            else File.Delete(Path.Combine(folder, "pending-link.bin"));
        }
        catch (Exception ex) when (ex is IOException or JsonException) { /* A damaged pending link can be replaced by Link account. */ }
    }
    public IEnumerable<Row> Rows()
    {
        yield return new("Account", account?.Label ?? "AimMod account", status);
        if (deviceCode.Length > 0) yield return new("Account", userCode, "Your code is filled in automatically. Approve the link in your browser, then return to KovaaK’s.");
        if (account is not null)
            yield return new("Account", "Hub history", $"{downloaded.Count:N0} downloaded runs available offline." + (partial ? " More history may be available on Hub." : ""));
        foreach (var row in benchmarks) yield return row;
    }
    public async Task Tick(CancellationToken token)
    {
        try
        {
            var downloadsEnabled = historyEnabled();
            if (previousHistoryEnabled != downloadsEnabled)
            {
                previousHistoryEnabled = downloadsEnabled;
                if (downloadsEnabled) nextSync = default;
                else if (account is not null && deviceCode.Length == 0) status = "Hub history downloads paused. Saved history is still available.";
                Revision++;
            }
            var commandPath = Path.Combine(folder, "command.tsv");
            var command = "";
            if (!commands.TryDequeue(out command) && File.Exists(commandPath))
            {
                command = new FileInfo(commandPath).Length <= 1024 ? File.ReadAllText(commandPath).Trim() : "";
                File.Delete(commandPath); // Consume once, including failed operations.
            }
            if (!string.IsNullOrEmpty(command))
            {
                switch (command)
                {
                    case "history-next": HistoryPage = Math.Min(HistoryPage + 1, MaxHistoryPage); Revision++; break;
                    case "history-previous": HistoryPage = Math.Max(0, HistoryPage - 1); Revision++; break;
                    case "history-all": SelectedScenario = ""; HistoryPage = 0; Revision++; break;
                    case "link": await Start(token); break;
                    case "open-link":
                        if (deviceCode.Length > 0 && clock.GetUtcNow() < expires && SafeApprovalUrl(approvalUrl))
                            OpenApproval();
                        break;
                    case "cancel-link": ClearPending(); status = account is null ? "Account linking cancelled." : "Account linked."; Revision++; break;
                    case "unlink":
                        // Forget the credential in memory first, so a locked file can never
                        // leave the account usable for the rest of this session.
                        account = null;
                        File.Delete(vault); File.Delete(Path.Combine(folder, "hub-cache.json"));
                        ClearPending(); scenarios.Clear(); historyPage = new(); downloaded.Clear(); benchmarks.Clear(); benchmarkItems = []; syncedCounts.Clear(); desiredCounts.Clear(); expectedRuns = 0; partial = false;
                        status = "Account unlinked from this device."; Revision++; break;
                    case "refresh-hub": if (account is not null && downloadsEnabled) { scenarios.Clear(); historyPage = new(); nextSync = default; } break;
                    default:
                        if (command.StartsWith("search-history\t", StringComparison.Ordinal))
                        { HistorySearch = NativeRuns.Decode(command[15..]).Trim(); HistoryPage = 0; Revision++; }
                        if (command.StartsWith("scenario\t", StringComparison.Ordinal))
                        { SelectedScenario = NativeRuns.Decode(command[9..]); HistoryPage = 0; Revision++; }
                        break;
                }
            }
            var now = clock.GetUtcNow();
            if (now < retryAfter) return;
            if (deviceCode.Length > 0)
            {
                if (now >= expires) { ClearPending(); status = "Link code expired. Select Link account for a new code."; Revision++; }
                else if (now >= nextPoll) { nextPoll = now.AddSeconds(interval); await Poll(token); }
                return;
            }
            if (account is null || !downloadsEnabled || !historyEnabled()) return;
            if (scenarios.Count > 0 && now < nextHistory) return;
            if (scenarios.TryPeek(out var slug))
            {
                                JsonDocument response;
                if(paginationSupported==false)response=await Rpc("GetPlayerScenarioHistory",new{handle=account.Handle,scenarioSlug=slug},token);
                else try{response=await Rpc("GetPlayerScenarioHistory",new{handle=account.Handle,scenarioSlug=slug,cursor=historyPage.Cursor,limit=500},token);}
                catch(HttpRequestException ex)when(ex.StatusCode==System.Net.HttpStatusCode.BadRequest&&historyPage.Cursor.Length==0){paginationSupported=false;response=await Rpc("GetPlayerScenarioHistory",new{handle=account.Handle,scenarioSlug=slug},token);}
                using var ownedResponse=response;
                var rows = HubHistory.ReadArray(response.RootElement, "runs", account.Handle).ToArray();
                var more=historyPage.Advance(response.RootElement);paginationSupported=historyPage.Supported;
                if(!more){scenarios.Dequeue();historyPage=new();} nextHistory = now.AddSeconds(2);
                if (!more && desiredCounts.TryGetValue(slug, out var count)) syncedCounts[slug] = count;
                foreach (var run in rows) downloaded[run.Id] = run;
                partial = expectedRuns > downloaded.Count;
                status = scenarios.Count > 0 ? $"Downloading history · {scenarios.Count} scenarios remaining" : "Hub history refreshed.";
                // Serializing up to 40,000 runs after every 2 s page is wasteful; persist
                // at most every 30 s while downloading and always when the queue drains.
                // Unsaved pages are simply requested again after a restart.
                if (scenarios.Count == 0 || now - lastCacheSave >= TimeSpan.FromSeconds(30)) SaveCache();
                Revision++; return;
            }
            if (now >= nextSync)
            {
                nextSync = now.AddMinutes(10);
                using var profile = await Rpc("GetProfile", new { handle = account.Handle }, token);
                var root = profile.RootElement;
                if (!HubHistory.Text(root, "userHandle").Equals(account.Handle, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Unexpected profile identity.");
                var externalId = HubHistory.Text(root, "userExternalId");
                if (externalId.Length == 0) throw new IOException("Missing profile identity.");
                if (account.ExternalId.Length > 0 && !account.ExternalId.Equals(externalId, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Profile identity changed.");
                account = account with { ExternalId = externalId };
                AccountVault.Save(vault, account);
                var discovered = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var key in new[] { "recentRuns", "personalBests" })
                    foreach (var run in HubHistory.ReadArray(root, key, account.Handle))
                    {
                        var runSlug = HubHistory.ScenarioSlug(run.Scenario);
                        discovered[runSlug] = discovered.GetValueOrDefault(runSlug) || !downloaded.ContainsKey(run.Id);
                        downloaded[run.Id] = run;
                    }
                expectedRuns = (long)Math.Clamp(HubHistory.Number(root, "runCount") ?? 0, 0, int.MaxValue);
                desiredCounts.Clear();
                if (root.TryGetProperty("topScenarios", out var top) && top.ValueKind == JsonValueKind.Array)
                {
                    foreach (var scenario in top.EnumerateArray())
                    {
                        var slugValue = HubHistory.Text(scenario, "scenarioSlug");
                        var count = (long)Math.Clamp(HubHistory.Number(scenario, "runCount") ?? 0, 0, int.MaxValue);
                        if (slugValue.Length == 0 || !desiredCounts.TryAdd(slugValue, count)) continue;
                        if (!syncedCounts.TryGetValue(slugValue, out var oldCount) || oldCount != count && (oldCount < 500 || paginationSupported != false) || oldCount >= 500 && paginationSupported is null) scenarios.Enqueue(slugValue);
                    }
                }
                foreach (var entry in discovered)
                    if (entry.Key.Length > 0 && desiredCounts.TryAdd(entry.Key, -1)
                        && (!syncedCounts.ContainsKey(entry.Key) || entry.Value)) scenarios.Enqueue(entry.Key);
                partial = expectedRuns > downloaded.Count || (HubHistory.Number(root, "scenarioCount") ?? 0) > desiredCounts.Count;
                benchmarkItems = BenchmarkData.Summaries(root);
                benchmarks = [];
                if (root.TryGetProperty("benchmarks", out var ranks) && ranks.ValueKind == JsonValueKind.Array)
                    foreach (var rank in ranks.EnumerateArray())
                    {
                        var name = HubHistory.Text(rank, "benchmarkName");
                        var label = rank.TryGetProperty("overallRank", out var overall) ? HubHistory.Text(overall, "rankName") : "";
                        if (name.Length > 0) benchmarks.Add(new("Benchmarks", name, label.Length > 0 ? label : "Unranked"));
                    }
                status = scenarios.Count > 0 ? "Downloading Hub history…" : "Hub history refreshed.";
                SaveCache(); Revision++;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or System.ComponentModel.Win32Exception || ex is OperationCanceledException && !token.IsCancellationRequested)
        {
            // Never include response bodies, device codes, or tokens in logs/UI.
            status = "Hub unavailable. Your saved history is still available. Select Refresh to retry.";
            failures = Math.Min(failures + 1, 16);
            var retry = clock.GetUtcNow().Add(Backoff(failures));
            nextSync = retryAfter > retry ? retryAfter : retry;
            nextPoll = retryAfter > clock.GetUtcNow().AddSeconds(15) ? retryAfter : clock.GetUtcNow().AddSeconds(15);
            nextHistory = nextSync; Revision++;
            Console.Error.WriteLine("Hub operation failed (" + ex.GetType().Name + ").");
        }
    }
    internal static bool SafeApprovalUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "aimmod.app" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/link-device";
    async Task<JsonDocument> Post(string path, object payload, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Origin + path) { Content = JsonContent.Create(payload) };
        request.Headers.Add("Connect-Protocol-Version", "1");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if ((int)response.StatusCode is 429 or 503)
        {
            var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - clock.GetUtcNow()) ?? TimeSpan.FromMinutes(2);
            DeferUntil(clock.GetUtcNow().AddSeconds(Math.Clamp(delay.TotalSeconds, 2, 3600)));
        }
        response.EnsureSuccessStatusCode();
        failures = 0; // Hub reachable again: the next failure starts a fresh backoff.
        if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new IOException("Oversized Hub response.");
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream(); var buffer = new byte[16384]; int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        { if (memory.Length + read > 4 * 1024 * 1024) throw new IOException("Oversized Hub response."); memory.Write(buffer, 0, read); }
        return JsonDocument.Parse(memory.ToArray());
    }
    Task<JsonDocument> Rpc(string method, object payload, CancellationToken token) => Post("/aimmod.hub.v1.HubService/" + method, payload, token);
    async Task Start(CancellationToken token)
    {
        if (deviceCode.Length > 0 && clock.GetUtcNow() < expires) { OpenApproval(); return; }
        using var doc = await Post("/auth/device/start", new { label = "AimMod in KovaaK's" }, token);
        var root = doc.RootElement;
        var url = HubHistory.Text(root, "verificationUriComplete");
        var code = HubHistory.Text(root, "deviceCode"); var shown = HubHistory.Text(root, "userCode");
        if (!SafeApprovalUrl(url) || code.Length is 0 or > 4096 || shown.Length is 0 or > 128 || url.Length > 8192
            || (HubHistory.Number(root, "expiresIn") ?? 0) <= 0) throw new IOException("Invalid link response.");
        approvalUrl = url; deviceCode = code; userCode = shown;
        interval = (int)Math.Clamp(HubHistory.Number(root, "interval") ?? 3, 3, 60);
        expires = clock.GetUtcNow().AddSeconds(Math.Clamp(HubHistory.Number(root, "expiresIn") ?? 0, 0, 1800));
        nextPoll = clock.GetUtcNow().AddSeconds(interval);
        SavePending();
        status = "Approve your account link in the browser."; Revision++;
        OpenApproval();
    }
    void OpenApproval()
    {
        if (SafeApprovalUrl(approvalUrl)) openBrowser(approvalUrl);
    }
    async Task Poll(CancellationToken token)
    {
        using var doc = await Post("/auth/device/poll", new { deviceCode }, token);
        var root = doc.RootElement; var state = HubHistory.Text(root, "status");
        if (state == "approved")
        {
            var secret = HubHistory.Text(root, "uploadToken");
            if (!root.TryGetProperty("user", out var user) || secret.Length == 0) throw new IOException("Invalid account response.");
            var handle = HubHistory.Text(user, "profileHandle");
            if (handle.Length == 0) throw new IOException("Missing account handle.");
            var label = HubHistory.Text(user, "displayName");
            var nextAccount = new HubAccount(handle, label.Length > 0 ? label : handle, HubHistory.Text(user, "userExternalId"), secret);
            AccountVault.Save(vault, nextAccount);
            if (!string.Equals(account?.Handle, handle, StringComparison.OrdinalIgnoreCase)) { downloaded.Clear(); benchmarks.Clear(); benchmarkItems = []; syncedCounts.Clear(); expectedRuns = 0; File.Delete(Path.Combine(folder, "hub-cache.json")); }
            account = nextAccount; ClearPending(); scenarios.Clear(); historyPage = new(); nextSync = default;
            status = "Account linked. Your Hub history will appear shortly."; Revision++;
        }
        else if (state is "expired" or "denied") { ClearPending(); status = "Account link was not approved. Select Link account to try again."; Revision++; }
        else if (state == "slow_down") { interval = Math.Min(interval + 5, 60); nextPoll = clock.GetUtcNow().AddSeconds(interval); SavePending(); }
    }
    void SavePending() => AccountVault.Save(Path.Combine(folder, "pending-link.bin"), new PendingLink(deviceCode, userCode, approvalUrl, expires, nextPoll, interval));
    void ClearPending() { File.Delete(Path.Combine(folder, "pending-link.bin")); deviceCode = userCode = approvalUrl = ""; }
    record Cache(string Handle, Run[] Runs, List<Row> Benchmarks, bool Partial, Dictionary<string, long>? SyncedCounts = null, long ExpectedRuns = 0, BenchmarkSummary[]? BenchmarkItems = null);
    void SaveCache()
    {
        if (account is null) return;
        // Keep the offline preview cache bounded; omitted runs remain explicitly partial.
        if (downloaded.Count > 40000)
        {
            foreach (var id in downloaded.Values.OrderByDescending(r => HubHistory.Date(r.Timestamp)).Skip(40000).Select(r => r.Id).ToArray()) downloaded.Remove(id);
            partial = true;
        }
        var path = Path.Combine(folder, "hub-cache.json");
        AtomicFile.WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(new Cache(account.Handle, downloaded.Values.ToArray(), benchmarks, partial, syncedCounts, expectedRuns, benchmarkItems)));
        lastCacheSave = clock.GetUtcNow();
    }
    void LoadCache()
    {
        var path = Path.Combine(folder, "hub-cache.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024 * 1024) return;
        var cache = JsonSerializer.Deserialize<Cache>(File.ReadAllText(path));
        if (cache is null || !string.Equals(cache.Handle, account?.Handle, StringComparison.OrdinalIgnoreCase) || cache.Runs is null || cache.Benchmarks is null) return;
        foreach (var run in cache.Runs)
            if (run is not null && !string.IsNullOrEmpty(run.Id) && run.Id.Length <= 512 && !string.IsNullOrEmpty(run.Scenario) && run.Timestamp is not null && double.IsFinite(run.Score) && double.IsFinite(run.Duration) && run.Duration > 0 && downloaded.Count < 40000) downloaded[run.Id] = run;
        benchmarkItems = (cache.BenchmarkItems ?? []).Where(x => x is not null && x.Id > 0 && !string.IsNullOrWhiteSpace(x.Name)).Take(512).ToArray();
        benchmarks = cache.Benchmarks.Where(r => r is not null && r.Page == "Benchmarks" && r.Heading is { Length: > 0 and <= 512 } && r.Body is { Length: <= 512 }).Take(512).ToList(); partial = cache.Partial;
        expectedRuns = cache.ExpectedRuns;
        if (cache.SyncedCounts is not null) foreach (var entry in cache.SyncedCounts.Take(4096)) if (entry.Key is { Length: > 0 and <= 512 }) syncedCounts[entry.Key] = entry.Value;
        status = "Saved Hub history is available. Checking for new scores…";
    }
    public void Dispose() => http.Dispose();
}

