using System.Text.Json;
using System.Text.Json.Nodes;

namespace AimMod.InGame.Multiplayer;

static class SnapshotClock
{
    public static LobbySnapshot Rebase(LobbySnapshot snapshot, long hostOffset)
    {
        if (hostOffset == 0) return snapshot;
        var root = JsonSerializer.SerializeToNode(snapshot, Protocol.Json)!;
        void Visit(JsonNode? node, bool relative = false)
        {
            if (node is JsonArray array) { foreach (var child in array) Visit(child, relative); }
            else if (node is JsonObject obj)
                foreach (var (key, value) in obj.ToArray())
                {
                    if (!relative && (key.EndsWith("At", StringComparison.Ordinal) || key is "now" or "readyCheck" or "protectedUntil" or "at" or "t" or "ends")
                        && value is JsonValue scalar && scalar.TryGetValue<long>(out var time)) obj[key] = checked(time - hostOffset);
                    else Visit(value, relative || key == "keys");
                }
        }
        Visit(root);
        return root.Deserialize<LobbySnapshot>(Protocol.Json)!;
    }
}
