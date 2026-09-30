using System.Globalization;
using System.Text.Json.Nodes;

namespace AimMod.InGame;

// --discord-test [--discord-test-seconds N]: connect to the running Discord
// client as the AimMod application, publish one sample activity, print every
// reply from Discord (the user object redacted), hold it for N seconds
// (default 20) and clear it. Bypasses the game handoff; nothing is written.
static class DiscordDiagnostics
{
    internal static string Redact(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            void Walk(JsonNode? n)
            {
                if (n is JsonObject o)
                {
                    foreach (var key in o.Select(p => p.Key).ToArray())
                        if (key is "user" or "access_token" or "email") o[key] = "[redacted]"; else Walk(o[key]);
                }
                else if (n is JsonArray a) foreach (var item in a) Walk(item);
            }
            Walk(node);
            return node?.ToJsonString() ?? "null";
        }
        catch (System.Text.Json.JsonException) { return "(unparsable frame, " + json.Length + " chars)"; }
    }
    static readonly string[] Opcodes = ["HANDSHAKE", "FRAME", "CLOSE", "PING", "PONG"];
    public static async Task<int> Run(string[] args)
    {
        var seconds = 20;
        var index = Array.IndexOf(args, "--discord-test-seconds");
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var n)) seconds = Math.Clamp(n, 1, 300);
        Console.WriteLine($"Discord test: application {DiscordPresenceHost.ClientId}");
        Console.WriteLine("Note: this bypasses the KovaaK's handoff. If KovaaK's is running with its own presence on, both may show.");
        await using var client = new DiscordIpcClient(DiscordPresenceHost.ClientId);
        client.Received = (op, json) => Console.WriteLine($"<- {(op is >= 0 and < 5 ? Opcodes[op] : op.ToString(CultureInfo.InvariantCulture))} {Redact(json)}");
        Console.WriteLine("-> HANDSHAKE {\"v\":1,\"client_id\":\"" + DiscordPresenceHost.ClientId + "\"}");
        if (!await client.Connect(CancellationToken.None)) { Console.WriteLine("Connect failed: " + client.LastError); return 2; }
        Console.WriteLine("Connected: READY received.");
        var now = DateTimeOffset.UtcNow;
        var live = new LiveOverlaySnapshot(true, true, false, "AimMod Discord test", 812.5, 18, 40, 38, 5, null, 95, 1020, 1020, 60, 1076, 56, ScorePerMinute: 2708, RemainingSeconds: seconds);
        var activity = DiscordActivityBuilder.Build(new(live, false, new DiscordSession(now, 0, null, false, null), null, now), new DiscordSettingsValue());
        var body = activity.ToJson();
        Console.WriteLine("-> SET_ACTIVITY " + body.ToJsonString());
        var result = await client.SetActivity(Environment.ProcessId, body, CancellationToken.None);
        Console.WriteLine("Result: " + result + (client.LastError is string error ? " (" + error + ")" : ""));
        if (result == DiscordSendResult.Rejected)
        {
            Console.WriteLine("-> SET_ACTIVITY without the play-this-scenario button");
            result = await client.SetActivity(Environment.ProcessId, activity.ToJson(scenarioButton: false), CancellationToken.None);
            Console.WriteLine("Result: " + result + (client.LastError is string again ? " (" + again + ")" : ""));
        }
        if (result == DiscordSendResult.Ok)
        {
            Console.WriteLine($"Check your Discord profile now; clearing in {seconds} s.");
            await Task.Delay(TimeSpan.FromSeconds(seconds));
        }
        if (client.Connected)
        {
            Console.WriteLine("-> SET_ACTIVITY null (clear)");
            Console.WriteLine("Result: " + await client.SetActivity(Environment.ProcessId, null, CancellationToken.None));
        }
        await client.Disconnect();
        Console.WriteLine("Closed.");
        return result == DiscordSendResult.Ok ? 0 : 1;
    }
}
