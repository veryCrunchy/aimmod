using System.Text.Json;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Osu.Worker;

public static class WorkerProtocolHost
{
    public static async Task<int> RunAsync(
        TextReader input,
        TextWriter protocolOutput,
        TextWriter diagnostics,
        IRuntimeBackend? backend = null,
        CancellationToken cancellationToken = default) =>
        await runAsync(input, protocolOutput, diagnostics, backend, restoreConsoleOutput: true, cancellationToken);

    public static Task<int> RunConsoleAsync(CancellationToken cancellationToken = default)
    {
        TextWriter protocolOutput = Console.Out;
        return runAsync(Console.In, protocolOutput, Console.Error, null, restoreConsoleOutput: false, cancellationToken);
    }

    private static async Task<int> runAsync(
        TextReader input,
        TextWriter protocolOutput,
        TextWriter diagnostics,
        IRuntimeBackend? backend,
        bool restoreConsoleOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(protocolOutput);
        ArgumentNullException.ThrowIfNull(diagnostics);

        // Keep Console.Out unavailable while ppy code runs. Only protocolOutput may write
        // to the pipe consumed by SidecarRuntimeClient.
        TextWriter previousOutput = Console.Out;
        Console.SetOut(TextWriter.Null);

        try
        {
            var router = new RuntimeRequestRouter(backend ?? new ReplayAnalysisBackend());
            var requestReader = new BoundedRequestReader(input);

            while (await requestReader.ReadAsync(cancellationToken) is { EndOfStream: false } framedRequest)
            {
                if (framedRequest.ExceededLimit)
                {
                    await diagnostics.WriteLineAsync(
                        $"Invalid protocol message: request exceeds {RuntimeProtocolFraming.MaximumRequestLineCharacters} characters.");
                    continue;
                }

                RuntimeRequest? request;

                try
                {
                    request = JsonSerializer.Deserialize<RuntimeRequest>(framedRequest.Line!, RuntimeProtocol.JsonOptions);
                }
                catch (JsonException exception)
                {
                    await diagnostics.WriteLineAsync($"Invalid protocol message: {exception.Message}");
                    continue;
                }

                if (request is null)
                    continue;

                RuntimeResponse response;
                try
                {
                    response = await router.RouteAsync(request, cancellationToken);
                }
                catch (Exception exception)
                {
                    await diagnostics.WriteLineAsync($"Worker request failed: {exception.GetType().Name}");
                    response = new RuntimeResponse(
                        request.Id,
                        RuntimeProtocol.CurrentVersion,
                        false,
                        Error: new RuntimeError("worker_failure", "The replay worker could not complete the request."));
                }

                await protocolOutput.WriteLineAsync(JsonSerializer.Serialize(response, RuntimeProtocol.JsonOptions));
                await protocolOutput.FlushAsync(cancellationToken);

                if (request.Command == RuntimeCommands.Shutdown)
                    break;
            }

            return 0;
        }
        finally
        {
            if (restoreConsoleOutput)
                Console.SetOut(previousOutput);
        }
    }

    private sealed class BoundedRequestReader(TextReader input)
    {
        private readonly char[] lineBuffer = new char[RuntimeProtocolFraming.MaximumRequestLineCharacters + 1];
        private readonly char[] readBuffer = new char[RuntimeProtocolFraming.LineReadBufferCharacters];
        private int bufferedCharacters;
        private int bufferPosition;

        public async ValueTask<FramedRequest> ReadAsync(CancellationToken cancellationToken)
        {
            int lineLength = 0;
            bool exceededLimit = false;

            while (true)
            {
                if (bufferPosition == bufferedCharacters)
                {
                    bufferedCharacters = await input.ReadAsync(readBuffer.AsMemory(), cancellationToken);
                    bufferPosition = 0;

                    if (bufferedCharacters == 0)
                    {
                        if (lineLength == 0 && !exceededLimit)
                            return FramedRequest.End;

                        return createRequest(lineLength, exceededLimit);
                    }
                }

                char character = readBuffer[bufferPosition++];

                if (character == '\n')
                    return createRequest(lineLength, exceededLimit);

                if (lineLength < lineBuffer.Length)
                    lineBuffer[lineLength++] = character;
                else
                    exceededLimit = true;
            }
        }

        private FramedRequest createRequest(int lineLength, bool exceededLimit)
        {
            int contentLength = lineLength > 0 && lineBuffer[lineLength - 1] == '\r'
                ? lineLength - 1
                : lineLength;

            if (exceededLimit || contentLength > RuntimeProtocolFraming.MaximumRequestLineCharacters)
                return FramedRequest.TooLong;

            return new FramedRequest(new string(lineBuffer, 0, contentLength), false, false);
        }
    }

    private readonly record struct FramedRequest(string? Line, bool ExceededLimit, bool EndOfStream)
    {
        public static FramedRequest TooLong { get; } = new(null, true, false);
        public static FramedRequest End { get; } = new(null, false, true);
    }
}
