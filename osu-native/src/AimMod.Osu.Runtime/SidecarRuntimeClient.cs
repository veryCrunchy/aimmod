using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Osu.Runtime;

public sealed class SidecarRuntimeClient : IAsyncDisposable
{
    private static readonly TimeSpan termination_wait = TimeSpan.FromSeconds(5);

    private readonly Process process;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RuntimeResponse>> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly object stateLock = new();
    private readonly Task responsePump;
    private Exception? terminalFailure;
    private Task? terminationTask;
    private Task lastDispatched = Task.CompletedTask;
    private TaskCompletionSource<object?>? disposalCompletion;
    private bool disposing;
    private bool processDisposed;

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
        WorkerJobObject.TryAssign(started);
        return new SidecarRuntimeClient(started);
    }

    internal bool HasExited => hasExited();

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
        startInfo.Environment[RuntimeProtocol.ParentProcessIdVariable] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return startInfo;
    }

    public Task<RuntimeResponse> SendAsync(RuntimeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return sendAsync(request, RuntimeProtocolTimeouts.ClientTimeout(request.Command), cancellationToken, allowWhileDisposing: false);
    }

    /// <summary>
    /// Sends a request and kills the worker if no response arrives within <paramref name="timeout"/>.
    /// </summary>
    public Task<RuntimeResponse> SendAsync(RuntimeRequest request, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        sendAsync(request, timeout, cancellationToken, allowWhileDisposing: false);

    private async Task<RuntimeResponse> sendAsync(
        RuntimeRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool allowWhileDisposing)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (timeout != Timeout.InfiniteTimeSpan)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

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

        using var timeoutCancellation = new CancellationTokenSource();
        CancellationTokenSource? requestCancellation = null;
        try
        {
            requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
            return await dispatchAsync(
                request.Id,
                json,
                completion,
                () =>
                {
                    if (timeout != Timeout.InfiniteTimeSpan)
                        timeoutCancellation.CancelAfter(timeout);
                },
                requestCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"The osu runtime worker did not answer '{request.Command}' within {timeout.TotalSeconds:0.#} seconds.");
        }
        finally
        {
            requestCancellation?.Dispose();
            pending.TryRemove(request.Id, out _);
        }
    }

    private async Task<RuntimeResponse> dispatchAsync(
        Guid requestId,
        string json,
        TaskCompletionSource<RuntimeResponse> completion,
        Action startTimeout,
        CancellationToken requestToken)
    {
        bool dispatched = false;
        Task predecessor;
        await writeGate.WaitAsync(requestToken).ConfigureAwait(false);
        try
        {
            Exception? failure = Volatile.Read(ref terminalFailure);
            if (failure is not null)
                throw unavailable(failure);

            // The worker answers frames in the order they were written, so a request's time
            // limit starts only once every earlier frame has been answered or has failed.
            predecessor = lastDispatched;
            lastDispatched = completion.Task.ContinueWith(
                static _ => { },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            try
            {
                // A write blocked on a full pipe cannot be interrupted directly, so the worker is
                // killed when the request is cancelled mid-frame. That also releases the gate.
                using CancellationTokenRegistration killOnCancel = requestToken.Register(
                    static state => _ = ((SidecarRuntimeClient)state!).terminateWorkerAsync(
                        new IOException("The osu runtime worker was terminated while a request frame was being written.")),
                    this);
                await process.StandardInput.WriteLineAsync(json.AsMemory(), requestToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(requestToken).ConfigureAwait(false);
                dispatched = true;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException or ObjectDisposedException)
            {
                var protocolFailure = new IOException("The osu runtime request frame could not be written completely.", exception);
                await terminateWorkerAsync(protocolFailure).ConfigureAwait(false);
                requestToken.ThrowIfCancellationRequested();
                throw protocolFailure;
            }
        }
        finally
        {
            writeGate.Release();
        }

        try
        {
            await Task.WhenAny(predecessor, completion.Task).WaitAsync(requestToken).ConfigureAwait(false);
            startTimeout();
            return await completion.Task.WaitAsync(requestToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (dispatched && requestToken.IsCancellationRequested)
        {
            await terminateWorkerAsync(new IOException($"The osu runtime worker was terminated after request {requestId} was cancelled or timed out.")).ConfigureAwait(false);
            throw;
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
        if (!hasExited() && Volatile.Read(ref terminalFailure) is null)
        {
            using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await sendAsync(
                    RuntimeProtocol.CreateRequest(RuntimeCommands.Shutdown),
                    TimeSpan.FromSeconds(2),
                    shutdownTimeout.Token,
                    allowWhileDisposing: true).ConfigureAwait(false);
                await process.WaitForExitAsync(shutdownTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                await terminateWorkerAsync(new IOException("The osu runtime worker did not shut down cleanly.", exception)).ConfigureAwait(false);
            }
        }
        else if (!hasExited())
        {
            await terminateWorkerAsync(terminalFailure ?? new ObjectDisposedException(nameof(SidecarRuntimeClient))).ConfigureAwait(false);
        }

        lifetime.Cancel();
        try
        {
            await responsePump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        Task? termination;
        lock (stateLock)
            termination = terminationTask;
        if (termination is not null)
            await termination.ConfigureAwait(false);

        Volatile.Write(ref processDisposed, true);
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
            if (!hasExited())
                process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
        }

        // Bounded so a worker that cannot be killed never hangs disposal.
        using var exitTimeout = new CancellationTokenSource(termination_wait);
        try
        {
            if (!hasExited())
                await process.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
        {
        }
    }

    private bool hasExited()
    {
        if (Volatile.Read(ref processDisposed))
            return true;

        try
        {
            return process.HasExited;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
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
            StringBuilder? line = null;

            while (true)
            {
                if (bufferPosition == bufferedCharacters)
                {
                    bufferedCharacters = await input.ReadAsync(readBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    bufferPosition = 0;

                    if (bufferedCharacters == 0)
                        return line is null || line.Length == 0 ? null : createLine(line);
                }

                line ??= new StringBuilder(Math.Min(maximumLineCharacters + 1, RuntimeProtocolFraming.LineReadBufferCharacters));
                if (consumeBuffered(line))
                    return createLine(line);
            }
        }

        private bool consumeBuffered(StringBuilder line)
        {
            ReadOnlySpan<char> chunk = readBuffer.AsSpan(bufferPosition, bufferedCharacters - bufferPosition);
            int newline = chunk.IndexOf('\n');
            ReadOnlySpan<char> segment = newline >= 0 ? chunk[..newline] : chunk;
            bufferPosition += segment.Length + (newline >= 0 ? 1 : 0);

            if (line.Length + segment.Length > maximumLineCharacters + 1)
                throw responseTooLong();

            line.Append(segment);
            return newline >= 0;
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
