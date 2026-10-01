using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AimMod.InGame;

// Discord local RPC framing: little-endian int32 opcode, int32 payload length,
// then UTF-8 JSON. Opcodes: 0 handshake, 1 frame, 2 close, 3 ping, 4 pong.
static class DiscordFrames
{
    public const int Handshake = 0, Frame = 1, Close = 2, Ping = 3, Pong = 4;
    public const int MaxPayload = 64 * 1024;
    public static byte[] Encode(int opcode, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        if (body.Length > MaxPayload) throw new ArgumentException("Discord payload too large.");
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);
        return frame;
    }
    // Returns null at end of stream. A frame that is too large or not JSON
    // ends the connection: the reader never buffers unbounded input.
    public static async Task<(int Opcode, string Json)?> Read(Stream stream, CancellationToken token)
    {
        var header = new byte[8];
        if (!await Fill(stream, header, token)) return null;
        var opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (opcode is < 0 or > 4 || length is < 0 or > MaxPayload) throw new InvalidDataException("Invalid Discord frame.");
        var body = new byte[length];
        if (!await Fill(stream, body, token)) return null;
        return (opcode, new UTF8Encoding(false, true).GetString(body));
    }
    static async Task<bool> Fill(Stream stream, byte[] buffer, CancellationToken token)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), token);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}

// Opens the pipe Discord listens on. The named-pipe implementation is the
// production path; tests substitute a private pipe prefix.
interface IDiscordPipe { Task<Stream?> Open(CancellationToken token); }

sealed class DiscordNamedPipe(string prefix = "discord-ipc-", int count = 10) : IDiscordPipe
{
    // Listing the pipe namespace avoids waiting out a connect timeout for each
    // of the ten names while Discord is not running.
    HashSet<string>? Existing()
    {
        try { return Directory.EnumerateFiles(@"\\.\pipe\").Select(p => Path.GetFileName(p) ?? "").Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    public async Task<Stream?> Open(CancellationToken token)
    {
        var existing = Existing();
        for (var i = 0; i < count; i++)
        {
            if (existing is not null && !existing.Contains(prefix + i)) continue;
            var pipe = new NamedPipeClientStream(".", prefix + i, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(200, token); return pipe; }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) { await pipe.DisposeAsync(); }
            catch { await pipe.DisposeAsync(); throw; }
        }
        return null;
    }
}

enum DiscordSendResult { Ok, Rejected, Failed }

// One connection to the Discord client: handshake, SET_ACTIVITY with a response
// per nonce, ping/pong, and an explicit close. Every failure disconnects; the
// caller owns reconnect timing.
sealed class DiscordIpcClient : IAsyncDisposable
{
    readonly string clientId;
    readonly IDiscordPipe pipe;
    readonly TimeSpan timeout;
    readonly SemaphoreSlim writeGate = new(1, 1);
    readonly Dictionary<string, TaskCompletionSource<bool>> pending = new(StringComparer.Ordinal);
    readonly object gate = new();
    Stream? stream;
    CancellationTokenSource? readerStop;
    Task? reader;
    TaskCompletionSource<bool>? ready;
    long nonce;
    volatile string? lastError;
    public bool Connected { get { lock (gate) return stream is not null; } }
    // Why the last connect or SET_ACTIVITY failed ("code message" from Discord,
    // or a transport reason). Never contains the Discord user.
    public string? LastError => lastError;
    // Every received frame, for diagnostics (--discord-test). Called on the reader.
    public Action<int, string>? Received { get; set; }
    // DISPATCH events other than READY (ACTIVITY_JOIN, ACTIVITY_JOIN_REQUEST, ...)
    // with a copy of their data. Called on the reader.
    public Action<string, JsonElement>? Dispatch { get; set; }
    static string ErrorText(JsonElement data)
    {
        var code = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetRawText() : "?";
        var message = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
        if (message.Length > 300) message = message[..300];
        return (code + " " + message).Trim();
    }
    public DiscordIpcClient(string clientId, IDiscordPipe? pipe = null, TimeSpan? timeout = null)
    { this.clientId = clientId; this.pipe = pipe ?? new DiscordNamedPipe(); this.timeout = timeout ?? TimeSpan.FromSeconds(5); }

    public async Task<bool> Connect(CancellationToken token)
    {
        await Disconnect(sendClose: false);
        var opened = await pipe.Open(token);
        if (opened is null) { lastError = "no discord-ipc pipe found"; return false; }
        lastError = null;
        var handshakeReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new CancellationTokenSource();
        lock (gate) { stream = opened; ready = handshakeReady; readerStop = stop; }
        reader = Task.Run(() => ReadLoop(opened, stop.Token));
        var handshake = new JsonObject { ["v"] = 1, ["client_id"] = clientId }.ToJsonString();
        if (!await Write(opened, DiscordFrames.Handshake, handshake, token)) { lastError = "handshake write failed"; await Disconnect(false); return false; }
        var finished = await Task.WhenAny(handshakeReady.Task, Task.Delay(timeout, token));
        if (finished != handshakeReady.Task || !handshakeReady.Task.Result) { lastError ??= finished != handshakeReady.Task ? "no READY within timeout" : "connection closed during handshake"; await Disconnect(false); return false; }
        return true;
    }

    // activity null clears this application's presence.
    public Task<DiscordSendResult> SetActivity(int pid, JsonObject? activity, CancellationToken token) =>
        Command("SET_ACTIVITY", new JsonObject { ["pid"] = pid, ["activity"] = activity?.DeepClone() }, null, token);
    public Task<DiscordSendResult> Subscribe(string evt, CancellationToken token) => Command("SUBSCRIBE", null, evt, token);
    // Any RPC command; the reply is matched by nonce.
    public async Task<DiscordSendResult> Command(string cmd, JsonObject? args, string? evt, CancellationToken token)
    {
        Stream? current; lock (gate) current = stream;
        if (current is null) { lastError = "not connected"; return DiscordSendResult.Failed; }
        lastError = null;
        var id = "aimmod-" + Interlocked.Increment(ref nonce);
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) pending[id] = response;
        try
        {
            var message = new JsonObject { ["cmd"] = cmd, ["args"] = args ?? new JsonObject(), ["nonce"] = id };
            if (evt is not null) message["evt"] = evt;
            var payload = message.ToJsonString();
            if (!await Write(current, DiscordFrames.Frame, payload, token)) { lastError = "write failed"; await Disconnect(false); return DiscordSendResult.Failed; }
            var finished = await Task.WhenAny(response.Task, Task.Delay(timeout, token));
            if (finished != response.Task) { lastError = "no reply within timeout"; await Disconnect(false); return DiscordSendResult.Failed; }
            return response.Task.Result ? DiscordSendResult.Ok : Connected ? DiscordSendResult.Rejected : DiscordSendResult.Failed;
        }
        finally { lock (gate) pending.Remove(id); }
    }

    async Task<bool> Write(Stream target, int opcode, string json, CancellationToken token)
    {
        var frame = DiscordFrames.Encode(opcode, json);
        await writeGate.WaitAsync(token);
        try { await target.WriteAsync(frame, token); await target.FlushAsync(token); return true; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { return false; }
        finally { writeGate.Release(); }
    }

    async Task ReadLoop(Stream source, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var frame = await DiscordFrames.Read(source, token);
                if (frame is null) break;
                var (opcode, json) = frame.Value;
                Received?.Invoke(opcode, json);
                if (opcode == DiscordFrames.Ping) { await Write(source, DiscordFrames.Pong, json, token); continue; }
                if (opcode == DiscordFrames.Close)
                {
                    try { using var closing = JsonDocument.Parse(json); lastError = "closed by Discord: " + ErrorText(closing.RootElement); }
                    catch (JsonException) { lastError = "closed by Discord"; }
                    break;
                }
                if (opcode != DiscordFrames.Frame) continue;
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var cmd = root.TryGetProperty("cmd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                var evt = root.TryGetProperty("evt", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                if (cmd == "DISPATCH" && evt == "READY") { lock (gate) ready?.TrySetResult(true); continue; }
                if (cmd == "DISPATCH" && evt is not null)
                {
                    var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
                    try { Dispatch?.Invoke(evt, data); } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException) { }
                    continue;
                }
                if (root.TryGetProperty("nonce", out var n) && n.ValueKind == JsonValueKind.String)
                {
                    TaskCompletionSource<bool>? waiter;
                    lock (gate) pending.TryGetValue(n.GetString()!, out waiter);
                    if (evt == "ERROR") lastError = root.TryGetProperty("data", out var data) ? ErrorText(data) : "error";
                    waiter?.TrySetResult(evt != "ERROR");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException or JsonException or DecoderFallbackException) { lastError ??= "connection lost (" + ex.GetType().Name + ")"; }
        catch (OperationCanceledException) { }
        // A closed or broken pipe ends the session; wake every waiter.
        lock (gate)
        {
            if (ReferenceEquals(stream, source)) { stream = null; }
            ready?.TrySetResult(false);
            foreach (var waiter in pending.Values) waiter.TrySetResult(false);
        }
        try { await source.DisposeAsync(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public async Task Disconnect(bool sendClose = true)
    {
        Stream? current; CancellationTokenSource? stop; Task? running;
        lock (gate) { current = stream; stream = null; stop = readerStop; readerStop = null; running = reader; reader = null; }
        if (current is not null && sendClose)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await Write(current, DiscordFrames.Close, "{}", limit.Token); } catch (OperationCanceledException) { }
        }
        stop?.Cancel();
        if (current is not null) { try { await current.DisposeAsync(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } }
        if (running is not null) { try { await running.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { } }
        stop?.Dispose();
    }

    public async ValueTask DisposeAsync() { await Disconnect(); writeGate.Dispose(); }
}

// Discord accepts at most five activity updates per 20 seconds per connection.
sealed class DiscordRateLimit(int limit = 5, TimeSpan? window = null)
{
    readonly TimeSpan span = window ?? TimeSpan.FromSeconds(20);
    readonly Queue<DateTime> sent = new();
    void Trim(DateTime now) { while (sent.Count > 0 && now - sent.Peek() >= span) sent.Dequeue(); }
    public bool Available(DateTime now) { Trim(now); return sent.Count < limit; }
    public bool TryTake(DateTime now) { if (!Available(now)) return false; sent.Enqueue(now); return true; }
    public void Reset() => sent.Clear();
}

// Reconnect delay after consecutive failures: 2, 4, 8, 16 then 30 seconds,
// close to KovaaK's own 15 s reconnect interval.
static class DiscordBackoff
{
    public static TimeSpan Delay(int failures) => TimeSpan.FromSeconds(failures <= 0 ? 0 : Math.Min(30, 1 << Math.Min(failures, 5)));
}
