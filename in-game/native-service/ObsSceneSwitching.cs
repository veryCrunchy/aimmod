using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Builder;

namespace AimMod.InGame;

sealed record ObsSwitchState(bool Match, bool Run, bool Replay);
sealed record ObsSwitchConfig(bool Enabled = false, int Port = 4455, string? Password = null, string MatchStart = "", string MatchEnd = "",
    string RunStart = "", string RunEnd = "", string Replay = "", string ReplayEnd = "");

// Separate opt-in connection; browser-source capabilities never authorize OBS control.
sealed class ObsSceneSwitching : IAsyncDisposable
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly string path;
    readonly Func<ObsSwitchState> read;
    readonly CancellationTokenSource shutdown = new();
    readonly object gate = new();
    ObsSwitchConfig config = new();
    CancellationTokenSource? attempt;
    Task? loop;
    string status = "Disabled";
    public ObsSceneSwitching(string directory, Func<ObsSwitchState> read)
    {
        path = Path.Combine(directory, "obs-scene-switching.json"); this.read = read;
        try { if (File.Exists(path) && new FileInfo(path).Length < 16384) config = Clean(JsonSerializer.Deserialize<ObsSwitchConfig>(File.ReadAllText(path), Json) ?? new()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }
    internal static ObsSwitchConfig Clean(ObsSwitchConfig c)
    {
        if (c.Port is < 1024 or > 65535 || c.Password?.Length > 2048) throw new ArgumentException("Invalid OBS settings.");
        string Name(string? s) => s is null ? "" : s.Length <= 128 && !s.Any(char.IsControl) ? s.Trim() : throw new ArgumentException("Invalid scene name.");
        return c with { MatchStart = Name(c.MatchStart), MatchEnd = Name(c.MatchEnd), RunStart = Name(c.RunStart), RunEnd = Name(c.RunEnd), Replay = Name(c.Replay), ReplayEnd = Name(c.ReplayEnd) };
    }
    public object View() { lock (gate) return new { config.Enabled, config.Port, hasPassword = !string.IsNullOrEmpty(config.Password), config.MatchStart, config.MatchEnd,
        config.RunStart, config.RunEnd, config.Replay, config.ReplayEnd, status }; }
    internal void Save(ObsSwitchConfig next)
    {
        lock (gate)
        {
            next = Clean(next with { Password = next.Password ?? config.Password });
            AtomicFile.WriteText(path, JsonSerializer.Serialize(next, Json));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            config = next; attempt?.Cancel(); status = next.Enabled ? "Waiting for a state change" : "Disabled";
        }
    }
    public void Map(IEndpointRouteBuilder routes, string prefix)
    {
        routes.MapGet(prefix + "/overlay-obs-settings", () => Results.Json(View()));
        routes.MapPost(prefix + "/overlay-obs-settings", async (HttpRequest request, CancellationToken token) => {
            if (request.Headers["X-AimMod-UI"] != "1") return Results.StatusCode(403);
            if (!request.HasJsonContentType()) return Results.StatusCode(415);
            if (request.ContentLength is null or <= 0 or > 16384) return Results.StatusCode(413);
            try { Save(await JsonSerializer.DeserializeAsync<ObsSwitchConfig>(request.Body, Json, token) ?? throw new JsonException()); return Results.Json(View()); }
            catch (Exception ex) when (ex is JsonException or ArgumentException) { return Results.BadRequest(new { error = "Invalid OBS settings." }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.Json(new { error = "Could not save OBS settings." }, statusCode: 500); }
        });
    }
    internal static string? Transition(ObsSwitchState? before, ObsSwitchState now, ObsSwitchConfig c)
    {
        if (!c.Enabled) return null;
        string? scene = now.Replay ? before?.Replay != true ? c.Replay : null : before?.Replay == true ? c.ReplayEnd :
            now.Match && before?.Match != true ? c.MatchStart : before?.Match == true && !now.Match ? c.MatchEnd :
            now.Run && before?.Run != true ? c.RunStart : before?.Run == true && !now.Run ? c.RunEnd : null;
        return string.IsNullOrEmpty(scene) ? null : scene;
    }
    public void Start() => loop ??= Task.Run(async () => {
        ObsSwitchState? previous = null; ObsSwitchConfig? last = null; string? pending = null;
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                ObsSwitchConfig c; lock (gate) c = config;
                var state = c.Enabled ? read() : new ObsSwitchState(false, false, false);
                if (!ReferenceEquals(last, c)) { previous = null; pending = null; last = c; }
                if (previous != state) pending = Transition(previous, state, c);
                previous = state;
                if (!c.Enabled) pending = null;
                if (pending is not null)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    lock (gate) { if (!ReferenceEquals(c, config)) continue; attempt = timeout; }
                    try { await Switch(c, pending, timeout.Token); pending = null; lock (gate) if (ReferenceEquals(c, config)) status = "Connected; scene switched"; }
                    catch (Exception ex) when (ex is WebSocketException or IOException or JsonException or InvalidOperationException or OperationCanceledException or KeyNotFoundException or FormatException) { lock (gate) if (ReferenceEquals(c, config)) status = config.Enabled ? "OBS unavailable or request refused; retrying" : "Disabled"; }
                    finally { lock (gate) attempt = null; }
                }
                await Task.Delay(pending is null ? 250 : 3000, shutdown.Token);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
        }
    });
    internal static string Authentication(string password, string salt, string challenge)
    {
        string Hash(string s) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
        return Hash(Hash(password + salt) + challenge);
    }
    internal static async Task Switch(ObsSwitchConfig c, string scene, CancellationToken token)
    {
        using var socket = new ClientWebSocket(); socket.Options.Proxy = null;
        await socket.ConnectAsync(new Uri("ws://127.0.0.1:" + c.Port), token);
        async Task Send(object packet) => await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(packet, Json), WebSocketMessageType.Text, true, token);
        async Task<JsonElement> Receive()
        {
            using var buffer = new MemoryStream(); var chunk = new byte[4096]; WebSocketReceiveResult part;
            do { part = await socket.ReceiveAsync(new ArraySegment<byte>(chunk), token); if (part.MessageType != WebSocketMessageType.Text || buffer.Length + part.Count > 65536) throw new InvalidOperationException("Invalid OBS frame."); buffer.Write(chunk, 0, part.Count); } while (!part.EndOfMessage);
            using var parsed = JsonDocument.Parse(buffer.ToArray()); return parsed.RootElement.Clone();
        }
        var hello = await Receive(); if (hello.GetProperty("op").GetInt32() != 0) throw new InvalidOperationException("Expected OBS greeting.");
        var identify = new Dictionary<string, object?> { ["rpcVersion"] = 1, ["eventSubscriptions"] = 0 };
        if (hello.GetProperty("d").TryGetProperty("authentication", out var auth)) identify["authentication"] = Authentication(c.Password ?? "", auth.GetProperty("salt").GetString()!, auth.GetProperty("challenge").GetString()!);
        await Send(new { op = 1, d = identify });
        if ((await Receive()).GetProperty("op").GetInt32() != 2) throw new InvalidOperationException("OBS identification refused.");
        var id = Guid.NewGuid().ToString("N");
        await Send(new { op = 6, d = new { requestType = "SetCurrentProgramScene", requestId = id, requestData = new { sceneName = scene } } });
        var reply = await Receive();
        if (reply.GetProperty("op").GetInt32() != 7 || reply.GetProperty("d").GetProperty("requestId").GetString() != id || !reply.GetProperty("d").GetProperty("requestStatus").GetProperty("result").GetBoolean()) throw new InvalidOperationException("OBS scene request refused.");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", token);
    }
    public async ValueTask DisposeAsync() { shutdown.Cancel(); lock (gate) attempt?.Cancel(); if (loop is not null) await loop; shutdown.Dispose(); }
}
