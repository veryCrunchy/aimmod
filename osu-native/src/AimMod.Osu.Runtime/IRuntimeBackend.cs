using System.Text.Json;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Osu.Runtime;

public interface IRuntimeBackend
{
    RuntimeHello Describe();

    ValueTask<JsonElement?> ExecuteAsync(string command, JsonElement? payload, CancellationToken cancellationToken);
}

public sealed class RuntimeCommandException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
