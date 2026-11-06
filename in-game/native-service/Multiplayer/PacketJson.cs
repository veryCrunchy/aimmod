using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

static class PacketJson
{
    public static bool Int64(JsonElement value, out long result) { result = 0; return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result); }
    public static bool Int32(JsonElement value, out int result) { result = 0; return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result); }
    // Check supplied fields before dispatch, including optional fields. Missing required fields remain
    // the individual parser's responsibility. Never include untrusted payloads in diagnostics.
    public static void Validate(Envelope message)
    {
        var schema = message.T switch
        {
            "state-part" => "id:n part:n total:n kind:s data:s",
            "hello" => "name:s proto:n app:s bridge:s avatar:s",
            "welcome" => "member:s snapshot:o", "snapshot" => "snapshot:o", "chat" => "lines:a",
            "reject" => "code:s message:s", "command" => "id:n action:s args:o",
            "result" => "id:n ok:b code:s message:s",
            "score" or "finish" => "match:s round:n t:n score:n shots:n hits:n kills:n remaining:n replay:s",
            "hit" => "match:s round:n seq:n t:n o:a r:a head:b w:n target:a shot:n gh:b gd:n src:n sp:a",
            "hit-ack" => "match:s round:n seq:n shot:n ok:b reason:s detail:s victim:s damage:n head:b kill:b dup:b",
            "combat" => "match:s round:n events:a", "bots" => "match:s round:n b:a",
            "fired" or "shots" => "match:s round:n s:a", "track" => "match:s round:n s:a v:a hull:a",
            "ping" => "t0:n", "pong" => "t0:n t1:n", "bye" => "reason:s",
            "content.request" => "", "content.manifest" => "key:s files:a workshop:s",
            "content.get" => "hash:s offset:n length:n transfer:n chunk:n", "content.chunk" => "hash:s offset:n total:n data:s",
            "content.error" => "hash:s code:s", "content.done" => "transfer:n", "cosmetic.look" => "items:a",
            "replay.chunk" => "match:s round:n owner:s id:s kind:s label:s size:n hash:s offset:n data:s",
            _ => throw new JsonException("Unknown message")
        };
        ValidateFields(message.Body, schema);
        if (message.T == "command" && message.Body.TryGetProperty("args", out var args))
            ValidateFields(args, "text:s scope:s kind:s strength:n o:a r:a member:s");
    }
    static void ValidateFields(JsonElement body, string schema)
    {
        if (body.ValueKind != JsonValueKind.Object) throw new JsonException("Expected object");
        foreach (var field in schema.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = field[..^2];
            if (!body.TryGetProperty(key, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Null && key is "remaining" or "replay" or "target" or "gd" or "sp" or "bridge" or "avatar" or "code" or "message" or "reason" or "detail" or "victim" or "workshop" or "label") continue;
            var valid = field[^1] switch
            {
                's' => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= (key == "data" ? 12000 : key == "text" ? 500 : 256),
                'n' => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n),
                'a' => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 256,
                'o' => value.ValueKind == JsonValueKind.Object,
                'b' => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => false
            };
            if (!valid) throw new JsonException("Invalid field kind or size");
        }
    }
}
