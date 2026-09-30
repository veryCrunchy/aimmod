using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Osu.Worker;

public static partial class WorkerProtocolHost
{
    private const int id_recovery_prefix_characters = 512;
    private static readonly TimeSpan parent_exit_grace = TimeSpan.FromSeconds(2);

    [GeneratedRegex("\"id\"\\s*:\\s*\"(?<id>[0-9a-fA-F-]{32,36})\"")]
    private static partial Regex requestIdPattern();

    public static async Task<int> RunAsync(
        TextReader input,
        TextWriter protocolOutput,
        TextWriter diagnostics,
        IRuntimeBackend? backend = null,
        CancellationToken cancellationToken = default) =>
        await runAsync(input, protocolOutput, diagnostics, backend, restoreConsoleOutput: true, cancellationToken);

    public static async Task<int> RunConsoleAsync(CancellationToken cancellationToken = default)
    {
        TextWriter protocolOutput = Console.Out;
        using var parentExited = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, parentExited.Token);
        watchParentProcess(parentExited);
        return await runAsync(Console.In, protocolOutput, Console.Error, null, restoreConsoleOutput: false, linked.Token);
    }

    private static void watchParentProcess(CancellationTokenSource parentExited)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(RuntimeProtocol.ParentProcessIdVariable), NumberStyles.None, CultureInfo.InvariantCulture, out int parentId)
            || parentId <= 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using Process parent = Process.GetProcessById(parentId);
                await parent.WaitForExitAsync();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            // The request loop may be blocked reading stdin or inside ppy code that ignores
            // cancellation, so the process is ended outright once the grace period passes.
            await parentExited.CancelAsync();
            await Task.Delay(parent_exit_grace);
            Environment.Exit(1);
        });
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
        AimModTempDirectories.EnsureSwept();

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
                    await replyInvalidAsync(
                        protocolOutput,
                        framedRequest.Prefix,
                        "request_too_large",
                        $"The request exceeds {RuntimeProtocolFraming.MaximumRequestLineCharacters} characters.",
                        cancellationToken);
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
                    await replyInvalidAsync(
                        protocolOutput,
                        framedRequest.Line,
                        "invalid_request",
                        "The request could not be parsed.",
                        cancellationToken);
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
            CachedLazerLibrarySnapshotFactory.DisposeShared();
            if (restoreConsoleOutput)
                Console.SetOut(previousOutput);
        }
    }

    private static async Task replyInvalidAsync(
        TextWriter protocolOutput,
        string? requestText,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        if (!TryRecoverRequestId(requestText, out Guid id))
            return;

        var response = new RuntimeResponse(id, RuntimeProtocol.CurrentVersion, false, Error: new RuntimeError(code, message));
        await protocolOutput.WriteLineAsync(JsonSerializer.Serialize(response, RuntimeProtocol.JsonOptions));
        await protocolOutput.FlushAsync(cancellationToken);
    }

    internal static bool TryRecoverRequestId(string? text, out Guid id)
    {
        id = Guid.Empty;
        if (string.IsNullOrEmpty(text))
            return false;

        string prefix = text.Length <= id_recovery_prefix_characters ? text : text[..id_recovery_prefix_characters];
        Match match = requestIdPattern().Match(prefix);
        return match.Success && Guid.TryParse(match.Groups["id"].Value, out id) && id != Guid.Empty;
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

                if (consumeBuffered(ref lineLength, ref exceededLimit))
                    return createRequest(lineLength, exceededLimit);
            }
        }

        private bool consumeBuffered(ref int lineLength, ref bool exceededLimit)
        {
            ReadOnlySpan<char> chunk = readBuffer.AsSpan(bufferPosition, bufferedCharacters - bufferPosition);
            int newline = chunk.IndexOf('\n');
            ReadOnlySpan<char> segment = newline >= 0 ? chunk[..newline] : chunk;
            bufferPosition += segment.Length + (newline >= 0 ? 1 : 0);

            int copied = Math.Min(lineBuffer.Length - lineLength, segment.Length);
            segment[..copied].CopyTo(lineBuffer.AsSpan(lineLength));
            lineLength += copied;
            if (copied < segment.Length)
                exceededLimit = true;

            return newline >= 0;
        }

        private FramedRequest createRequest(int lineLength, bool exceededLimit)
        {
            int contentLength = lineLength > 0 && lineBuffer[lineLength - 1] == '\r'
                ? lineLength - 1
                : lineLength;

            if (exceededLimit || contentLength > RuntimeProtocolFraming.MaximumRequestLineCharacters)
                return FramedRequest.TooLong(new string(lineBuffer, 0, Math.Min(contentLength, id_recovery_prefix_characters)));

            return new FramedRequest(new string(lineBuffer, 0, contentLength), null, false, false);
        }
    }

    private readonly record struct FramedRequest(string? Line, string? Prefix, bool ExceededLimit, bool EndOfStream)
    {
        public static FramedRequest End { get; } = new(null, null, false, true);

        public static FramedRequest TooLong(string prefix) => new(null, prefix, true, false);
    }
}
