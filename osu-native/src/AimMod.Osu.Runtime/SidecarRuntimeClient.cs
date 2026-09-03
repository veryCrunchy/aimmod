using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Osu.Runtime;

public sealed class SidecarRuntimeClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RuntimeResponse>> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly object stateLock = new();
    private readonly Task responsePump;
    private Exception? terminalFailure;
    private Task? terminationTask;
    private TaskCompletionSource<object?>? disposalCompletion;
    private bool disposing;

    private SidecarRuntimeClient(Process process)
    {
        this.process = process;
        responsePump = readResponsesAsync();
    }

    public static SidecarRuntimeClient Start()
    {
        string executablePath = Environment.ProcessPath
                                ?? throw new InvalidOperationException("AimMod could not resolve its own executable path.");
        return Start(executablePath);
    }

    public static SidecarRuntimeClient Start(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        ProcessStartInfo startInfo = CreateStartInfo(executablePath);

        return Start(startInfo);
    }

    internal static SidecarRuntimeClient Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!startInfo.RedirectStandardInput || !startInfo.RedirectStandardOutput)
            throw new ArgumentException("The osu runtime worker requires redirected protocol streams.", nameof(startInfo));

        Process started = Process.Start(startInfo) ?? throw new InvalidOperationException("The osu runtime worker did not start.");
        return new SidecarRuntimeClient(started);
    }

    internal bool HasExited => process.HasExited;

    internal static ProcessStartInfo CreateStartInfo(string executablePath)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--worker");
        return startInfo;
    }

    public Task<RuntimeResponse> SendAsync(RuntimeRequest request, CancellationToken cancellationToken = default) =>
        sendAsync(request, cancellationToken, allowWhileDisposing: false);

    private async Task<RuntimeResponse> sendAsync(
        RuntimeRequest request,
        CancellationToken cancellationToken,
        bool allowWhileDisposing)
    {
        ArgumentNullException.ThrowIfNull(request);

        string json = JsonSerializer.Serialize(request, RuntimeProtocol.JsonOptions);
        if (json.Length > RuntimeProtocolFraming.MaximumRequestLineCharacters)
        {
            throw new ArgumentException(
                $"The runtime request exceeds {RuntimeProtocolFraming.MaximumRequestLineCharacters} characters.",
                nameof(request));
        }

        var completion = new TaskCompletionSource<RuntimeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (stateLock)
        {
            if (disposing && !allowWhileDisposing)
                throw new ObjectDisposedException(nameof(SidecarRuntimeClient));
            if (terminalFailure is not null)
                throw unavailable(terminalFailure);
            if (!pending.TryAdd(request.Id, completion))
                throw new InvalidOperationException($"Request {request.Id} is already pending.");
        }

        bool dispatched = false;
        try
        {
            await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Exception? failure = Volatile.Read(ref terminalFailure);
                if (failure is not null)
                    throw unavailable(failure);

                try
                {
                    await process.StandardInput.WriteLineAsync(json.AsMemory(), lifetime.Token).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(lifetime.Token).ConfigureAwait(false);
                    dispatched = true;
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
                {
                    var protocolFailure = new IOException("The osu runtime request frame could not be written completely.", exception);
                    await terminateWorkerAsync(protocolFailure).ConfigureAwait(false);
                    throw protocolFailure;
                }
            }
            finally
            {
                writeGate.Release();
            }

            try
            {
                return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (dispatched && cancellationToken.IsCancellationRequested)
            {
                await terminateWorkerAsync(new IOException("The osu runtime worker was terminated after a dispatched request was cancelled.")).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            pending.TryRemove(request.Id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        TaskCompletionSource<object?> completion;
        bool ownsDisposal;
        lock (stateLock)
        {
            if (disposalCompletion is null)
            {
                disposalCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                disposing = true;
                ownsDisposal = true;
            }
            else
            {
                ownsDisposal = false;
            }

            completion = disposalCompletion!;
        }

        if (!ownsDisposal)
        {
            await completion.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            await disposeCoreAsync().ConfigureAwait(false);
            completion.TrySetResult(null);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
            throw;
        }
    }

    private async Task disposeCoreAsync()
    {
        if (!process.HasExited && Volatile.Read(ref terminalFailure) is null)
        {
            using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await sendAsync(
                    RuntimeProtocol.CreateRequest(RuntimeCommands.Shutdown),
                    shutdownTimeout.Token,
                    allowWhileDisposing: true).ConfigureAwait(false);
                await process.WaitForExitAsync(shutdownTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                await terminateWorkerAsync(new IOException("The osu runtime worker did not shut down cleanly.", exception)).ConfigureAwait(false);
            }
        }
        else if (!process.HasExited)
        {
            await terminateWorkerAsync(terminalFailure ?? new ObjectDisposedException(nameof(SidecarRuntimeClient))).ConfigureAwait(false);
        }

        lifetime.Cancel();
        await responsePump.ConfigureAwait(false);

        process.Dispose();
        lifetime.Dispose();
    }

    private async Task readResponsesAsync()
    {
        Exception terminal = new EndOfStreamException("The osu runtime worker closed its protocol stream.");
        try
        {
            var reader = new BoundedResponseReader(process.StandardOutput);
            while (!lifetime.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(lifetime.Token).ConfigureAwait(false);

                if (line is null)
                    break;

                RuntimeResponse? response;
                try
                {
                    response = JsonSerializer.Deserialize<RuntimeResponse>(line, RuntimeProtocol.JsonOptions);
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException("The osu runtime worker returned malformed JSON.", exception);
                }

                if (response is null)
                    throw new InvalidDataException("The osu runtime worker returned an empty response.");

                if (pending.TryGetValue(response.Id, out TaskCompletionSource<RuntimeResponse>? completion))
                    completion.TrySetResult(response);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            terminal = new ObjectDisposedException(nameof(SidecarRuntimeClient));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            terminal = exception;
            await terminateWorkerAsync(terminal).ConfigureAwait(false);
        }
        finally
        {
            setTerminalAndFailPending(terminal);
        }
    }

    private Task terminateWorkerAsync(Exception failure)
    {
        setTerminalAndFailPending(failure);
        lock (stateLock)
            return terminationTask ??= terminateWorkerCoreAsync();
    }

    private async Task terminateWorkerCoreAsync()
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
        }

        try
        {
            if (!process.HasExited)
                await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void setTerminalAndFailPending(Exception failure)
    {
        Exception effectiveFailure;
        lock (stateLock)
        {
            terminalFailure ??= failure;
            effectiveFailure = terminalFailure!;
        }

        foreach ((Guid id, TaskCompletionSource<RuntimeResponse> completion) in pending)
        {
            if (pending.TryRemove(id, out TaskCompletionSource<RuntimeResponse>? removed))
                removed.TrySetException(effectiveFailure);
        }
    }

    private static InvalidOperationException unavailable(Exception failure) =>
        new("The osu runtime worker is no longer available.", failure);

    internal sealed class BoundedResponseReader
    {
        private readonly TextReader input;
        private readonly int maximumLineCharacters;
        private readonly char[] readBuffer = new char[RuntimeProtocolFraming.LineReadBufferCharacters];
        private int bufferedCharacters;
        private int bufferPosition;

        public BoundedResponseReader(
            TextReader input,
            int maximumLineCharacters = RuntimeProtocolFraming.MaximumResponseLineCharacters)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumLineCharacters, 1);
            this.input = input;
            this.maximumLineCharacters = maximumLineCharacters;
        }

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
        {
            var line = new StringBuilder(Math.Min(maximumLineCharacters + 1, RuntimeProtocolFraming.LineReadBufferCharacters));

            while (true)
            {
                if (bufferPosition == bufferedCharacters)
                {
                    bufferedCharacters = await input.ReadAsync(readBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    bufferPosition = 0;

                    if (bufferedCharacters == 0)
                        return line.Length == 0 ? null : createLine(line);
                }

                char character = readBuffer[bufferPosition++];
                if (character == '\n')
                    return createLine(line);

                if (line.Length == maximumLineCharacters + 1)
                    throw responseTooLong();

                line.Append(character);
            }
        }

        private string createLine(StringBuilder line)
        {
            int contentLength = line.Length > 0 && line[^1] == '\r'
                ? line.Length - 1
                : line.Length;
            if (contentLength > maximumLineCharacters)
                throw responseTooLong();

            return line.ToString(0, contentLength);
        }

        private InvalidDataException responseTooLong() =>
            new($"The osu runtime response exceeds {maximumLineCharacters} characters.");
    }
}
