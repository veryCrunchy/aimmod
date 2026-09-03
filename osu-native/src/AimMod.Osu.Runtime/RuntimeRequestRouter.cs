using System.Text.Json;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Osu.Runtime;

public sealed class RuntimeRequestRouter(IRuntimeBackend backend)
{
    public async ValueTask<RuntimeResponse> RouteAsync(RuntimeRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ProtocolVersion != RuntimeProtocol.CurrentVersion)
        {
            return failure(request, "protocol_version", $"Protocol {request.ProtocolVersion} is not supported.");
        }

        try
        {
            JsonElement? payload = request.Command switch
            {
                RuntimeCommands.Hello => JsonSerializer.SerializeToElement(backend.Describe(), RuntimeProtocol.JsonOptions),
                RuntimeCommands.Shutdown => null,
                _ => await backend.ExecuteAsync(request.Command, request.Payload, cancellationToken),
            };

            return new RuntimeResponse(request.Id, RuntimeProtocol.CurrentVersion, true, payload);
        }
        catch (RuntimeCommandException exception)
        {
            return failure(request, exception.Code, exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return failure(request, "cancelled", "The request was cancelled.");
        }
    }

    private static RuntimeResponse failure(RuntimeRequest request, string code, string message) =>
        new(request.Id, RuntimeProtocol.CurrentVersion, false, Error: new RuntimeError(code, message));
}
