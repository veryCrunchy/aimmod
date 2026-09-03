using System.Text.Json;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Osu.Runtime;

/// <summary>
/// The small request boundary used by feature-specific runtime clients.
/// </summary>
public interface IRuntimeRequestClient
{
    Task<RuntimeResponse> SendAsync(RuntimeRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapts the worker process client without exposing it to replay UI code.
/// </summary>
public sealed class SidecarRuntimeRequestClient : IRuntimeRequestClient
{
    private readonly SidecarRuntimeClient client;

    public SidecarRuntimeRequestClient(SidecarRuntimeClient client)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public Task<RuntimeResponse> SendAsync(RuntimeRequest request, CancellationToken cancellationToken = default) =>
        client.SendAsync(request, cancellationToken);
}

public interface IReplayAnalysisClient
{
    Task<ReplayAnalysisResult> AnalyseAsync(ReplayAnalysisRequest request, CancellationToken cancellationToken = default);
}

public sealed class ReplayAnalysisClient : IReplayAnalysisClient
{
    private readonly IRuntimeRequestClient runtimeClient;

    public ReplayAnalysisClient(IRuntimeRequestClient runtimeClient)
    {
        this.runtimeClient = runtimeClient ?? throw new ArgumentNullException(nameof(runtimeClient));
    }

    public async Task<ReplayAnalysisResult> AnalyseAsync(ReplayAnalysisRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        RuntimeResponse response = await runtimeClient.SendAsync(
            RuntimeProtocol.CreateRequest(RuntimeCommands.AnalyseReplay, request),
            cancellationToken).ConfigureAwait(false);

        if (response.ProtocolVersion != RuntimeProtocol.CurrentVersion)
            throw invalidResponse();

        if (!response.Success)
        {
            RuntimeError error = response.Error ?? new RuntimeError("worker_error", "Replay analysis failed without an error response.");
            throw new ReplayAnalysisClientException(error.Code, error.Message);
        }

        if (response.Payload is null)
            throw invalidResponse();

        try
        {
            ReplayAnalysisResult result = response.Payload.Value.Deserialize<ReplayAnalysisResult>(RuntimeProtocol.JsonOptions) ?? throw invalidResponse();
            if (string.IsNullOrWhiteSpace(result.EngineVersion)
                || string.IsNullOrWhiteSpace(result.TimeBasis)
                || !result.HeadlessAudioMuted
                || result.WallClockTimeoutMs <= 0
                || result.Pauses is null
                || result.Pauses.Count > ReplayAnalysisProtocol.MaximumPauses
                || result.Judgements is null
                || result.Judgements.Count > ReplayAnalysisProtocol.MaximumJudgements
                || result.Summary is null)
            {
                throw invalidResponse();
            }

            return result;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw invalidResponse();
        }
    }

    private static ReplayAnalysisClientException invalidResponse() =>
        new("invalid_worker_response", "The replay worker returned an invalid result.");
}

public sealed class ReplayAnalysisClientException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
