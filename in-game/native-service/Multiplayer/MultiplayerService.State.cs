using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

sealed partial class MultiplayerService
{
    // Even a base64 string whose characters all need JSON escaping stays below 10 KiB.
    internal const int StateChunkBytes = 1024, StateMaxParts = 768;
    long stateSequence;
    (string Peer, long Id, string Kind, long At, byte[][] Parts)? incomingState;
    bool SendState(string peer, string kind, LobbySnapshot snapshot)
    {
        var body = new { member = peer, snapshot };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Protocol.Json);
        if (bytes.Length < 12_000) return Send(peer, kind, body);
        Console.Error.WriteLine("[multiplayer] Large lobby state split into bounded frames: " + bytes.Length);
        var total = (bytes.Length + StateChunkBytes - 1) / StateChunkBytes;
        if (total > StateMaxParts) { Console.Error.WriteLine("[multiplayer] Lobby state exceeds assembly budget"); return false; }
        var id = ++stateSequence;
        for (var part = 0; part < total; part++)
        {
            var data = Convert.ToBase64String(bytes, part * StateChunkBytes, Math.Min(StateChunkBytes, bytes.Length - part * StateChunkBytes));
            if (!Send(peer, "state-part", new { id, part, total, kind, data })) return false;
        }
        return true;
    }
    void ReceiveState(string peer, Envelope message)
    {
        var body = message.Body;
        if (!PacketJson.Int64(body.GetProperty("id"), out var id) || !PacketJson.Int32(body.GetProperty("part"), out var part)
            || !PacketJson.Int32(body.GetProperty("total"), out var total) || total is < 1 or > StateMaxParts || part < 0 || part >= total)
            throw new JsonException("Invalid state fragment");
        var kind = body.GetProperty("kind").GetString();
        if (kind is not ("snapshot" or "welcome")) throw new JsonException("Invalid state kind");
        var data = Convert.FromBase64String(body.GetProperty("data").GetString()!);
        if (data.Length > StateChunkBytes) throw new JsonException("State fragment exceeds budget");
        if (incomingState is not { } old || old.Peer != peer || old.Id != id || clock() - old.At > 5000)
            incomingState = (peer, id, kind, clock(), new byte[total][]);
        var state = incomingState.Value;
        if (state.Parts.Length != total || state.Kind != kind) throw new JsonException("Inconsistent state fragments");
        state.Parts[part] = data;
        if (state.Parts.Any(p => p is null)) return;
        incomingState = null;
        using var doc = JsonDocument.Parse(state.Parts.SelectMany(p => p).ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        HandleAsClient(peer, message with { T = kind, Body = doc.RootElement.Clone() });
    }
}
