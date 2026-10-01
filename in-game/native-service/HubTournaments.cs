using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AimMod.InGame.Tournaments;

namespace AimMod.InGame;

/// <summary>A Hub refusal with its player-facing message (Connect error JSON).</summary>
sealed class TournamentHubException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}

// Tournament calls act as the linked account: they carry the device's upload
// token. Everything else in Hub stays a public read.
sealed partial class Hub
{
    public bool Linked => account is not null;

    internal async Task<JsonDocument> TournamentRpc(string method, object payload, CancellationToken token)
    {
        var current = account ?? throw new TournamentHubException("Link your AimMod Hub account to play in tournaments.", "unauthenticated");
        using var request = new HttpRequestMessage(HttpMethod.Post, Origin + "/aimmod.tournament.v1.TournamentService/" + method) { Content = JsonContent.Create(payload) };
        request.Headers.Add("Connect-Protocol-Version", "1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.Token);
        return await Send(request, token);
    }

    internal async Task<JsonDocument> UploadTournamentReplay(string tournament, string match, int game, byte[] file, CancellationToken token)
    {
        var current = account ?? throw new TournamentHubException("Link your AimMod Hub account to play in tournaments.", "unauthenticated");
        var url = Origin + "/api/tournaments/v1/replays?tournament=" + Uri.EscapeDataString(tournament) + "&match=" + Uri.EscapeDataString(match) + "&game=" + game;
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(file) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.Token);
        request.Headers.Add("X-Content-SHA256", Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant());
        return await Send(request, token);
    }

    async Task<JsonDocument> Send(HttpRequestMessage request, CancellationToken token)
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if ((int)response.StatusCode is 429 or 503)
        {
            var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1);
            DeferUntil(clock.GetUtcNow().AddSeconds(Math.Clamp(delay.TotalSeconds, 2, 3600)));
        }
        if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new IOException("Oversized Hub response.");
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream(); var buffer = new byte[16384]; int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        { if (memory.Length + read > 4 * 1024 * 1024) throw new IOException("Oversized Hub response."); memory.Write(buffer, 0, read); }
        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(memory.ToArray()); } catch (JsonException) { }
        if (!response.IsSuccessStatusCode)
        {
            // Connect errors are {"code","message"}; the upload endpoint answers {"error"}.
            var message = doc is null ? "" : HubHistory.Text(doc.RootElement, "message") is { Length: > 0 } m ? m : HubHistory.Text(doc.RootElement, "error");
            var code = doc is null ? "" : HubHistory.Text(doc.RootElement, "code");
            doc?.Dispose();
            if (message.Length > 300) message = message[..300];
            throw new TournamentHubException(message.Length > 0 ? message : "AimMod Hub couldn’t do that right now (" + (int)response.StatusCode + ").", code);
        }
        return doc ?? throw new IOException("Invalid Hub response.");
    }
}

/// <summary>The real tournament source: AimMod Hub's TournamentService.</summary>
sealed class HubTournamentSource(Hub hub) : ITournamentHub
{
    public bool Available => hub.Linked;
    public bool Simulated => false;

    async Task<T> Call<T>(string method, object payload, Func<JsonElement, T> read, CancellationToken token)
    {
        using var doc = await hub.TournamentRpc(method, payload, token);
        return read(doc.RootElement);
    }
    static TMatch MatchOf(JsonElement root) => TournamentJson.Match(TournamentJson.Obj(root, "match"));

    public Task<TournamentFeed> Feed(CancellationToken token) => Call("ListMyMatches", new { }, TournamentJson.Feed, token);
    public Task<IReadOnlyList<TournamentSummary>> List(CancellationToken token) => Call("ListTournaments", new { mine = true, limit = 20 }, TournamentJson.List, token);
    public Task<TournamentDetail> Detail(string tournament, CancellationToken token) => Call("GetTournament", new { tournament }, TournamentJson.Detail, token);
    public Task CheckIn(string tournamentId, CancellationToken token) => Call("CheckIn", new { tournamentId }, _ => 0, token);
    public Task<TMatch> MarkReady(string tournamentId, string matchId, bool ready, bool canHost, CancellationToken token) =>
        Call("MarkReady", new { tournamentId, matchId, ready, canHost }, MatchOf, token);
    public Task<TMatch> Veto(string tournamentId, string matchId, string scenario, CancellationToken token) =>
        Call("SubmitVeto", new { tournamentId, matchId, scenario }, MatchOf, token);
    public Task<TMatch> ReportGame(string tournamentId, string matchId, int game, GameResultRecord result, IReadOnlyList<string> replays, bool hostValidated, string playedScenario, CancellationToken token) =>
        Call("ReportGame", new
        {
            tournamentId, matchId, gameIndex = game, replayIds = replays, hostValidated, playedScenario, seed = result.Seed,
            result = new
            {
                format = 1, match = result.Match, mode = result.Mode, settingsKey = result.SettingsKey, scenarioHash = result.ScenarioHash,
                startedAt = result.StartedAt.ToString(System.Globalization.CultureInfo.InvariantCulture), endedAt = result.EndedAt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                host = result.Host, winner = result.Winner, seed = result.Seed,
                players = result.Players.Select(p => new { key = p.Key, entrantId = p.Entrant, place = p.Place, score = p.Score, accuracy = p.Accuracy, replay = p.Replay ?? "", disputed = p.Disputed }),
            },
        }, MatchOf, token);
    public Task<TMatch> Confirm(string tournamentId, string matchId, CancellationToken token) => Call("ConfirmResult", new { tournamentId, matchId }, MatchOf, token);
    public Task Dispute(string tournamentId, string matchId, string reason, CancellationToken token) => Call("OpenDispute", new { tournamentId, matchId, reason }, _ => 0, token);
    public Task ReportLive(string tournamentId, LiveMatchState live, CancellationToken token) => Call("ReportLiveState", new
    {
        tournamentId,
        live = new
        {
            matchId = live.MatchId, gameIndex = live.GameIndex, scenario = live.Scenario, phase = live.Phase, spectators = live.Spectators, lobbyToken = live.LobbyToken ?? "",
            players = live.Players.Select(p => new { entrantId = p.Entrant, score = p.Score, accuracy = p.Accuracy, secondsLeft = p.SecondsLeft, pingMs = p.Ping, connection = p.Connection, status = p.Status, ready = p.Ready }),
        },
    }, _ => 0, token);
    public async Task<string?> UploadReplay(string tournamentId, string matchId, int game, byte[] file, CancellationToken token)
    {
        using var doc = await hub.UploadTournamentReplay(tournamentId, matchId, game, file, token);
        var id = HubHistory.Text(doc.RootElement, "id");
        return id.Length > 0 ? id : null;
    }
}
