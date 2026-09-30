using System.Diagnostics;
using System.Text.Json;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Osu.Runtime.Tests;

[TestFixture]
public sealed class SidecarRuntimeClientTests
{
    [Test]
    public void ResponseLimitIs64MiB() =>
        Assert.That(RuntimeProtocolFraming.MaximumResponseLineCharacters, Is.EqualTo(64 * 1024 * 1024));

    [Test]
    public async Task BoundedResponseReaderAcceptsTheLimitAndWindowsLineEnding()
    {
        var reader = new SidecarRuntimeClient.BoundedResponseReader(new StringReader("12345678\r\nnext\n"), 8);

        string? first = await reader.ReadLineAsync();
        string? second = await reader.ReadLineAsync();
        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo("12345678"));
            Assert.That(second, Is.EqualTo("next"));
        });
        Assert.That(await reader.ReadLineAsync(), Is.Null);
    }

    [Test]
    public void BoundedResponseReaderRejectsAnOversizedLine()
    {
        var reader = new SidecarRuntimeClient.BoundedResponseReader(new StringReader("123456789\n"), 8);

        Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadLineAsync());
    }

    [Test]
    public async Task ConcurrentRequestsWriteCompleteFrames()
    {
        requirePosixShell();
        await using SidecarRuntimeClient client = startShellResponder(initialDelaySeconds: 1);
        JsonElement payload = JsonSerializer.SerializeToElement(new string('x', 128 * 1024));

        Task<RuntimeResponse>[] requests = Enumerable.Range(0, 12)
            .Select(_ => client.SendAsync(RuntimeProtocol.CreateRequest("test.concurrent", payload)))
            .ToArray();

        RuntimeResponse[] responses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.That(responses.All(response => response.Success), Is.True);
    }

    [Test]
    public async Task CancellationAfterDispatchTerminatesWorkerAndPoisonsClient()
    {
        await using SidecarRuntimeClient client = startBlockingResponder();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await client.SendAsync(RuntimeProtocol.CreateRequest("test.cancel"), cancellation.Token));

        Assert.Multiple(() =>
        {
            Assert.That(client.HasExited, Is.True);
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await client.SendAsync(RuntimeProtocol.CreateRequest("test.after-cancel")));
        });
    }

    [Test]
    public async Task CancellationWhileWaitingToWriteDoesNotPoisonClient()
    {
        requirePosixShell();
        await using SidecarRuntimeClient client = startShellResponder(initialDelaySeconds: 1);
        JsonElement largePayload = JsonSerializer.SerializeToElement(new string('x', 900 * 1024));
        Task<RuntimeResponse> first = client.SendAsync(RuntimeProtocol.CreateRequest("test.large", largePayload));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await client.SendAsync(RuntimeProtocol.CreateRequest("test.cancel-before-write"), cancellation.Token));

        Assert.That((await first.WaitAsync(TimeSpan.FromSeconds(10))).Success, Is.True);
        Assert.That((await client.SendAsync(RuntimeProtocol.CreateRequest("test.after-cancel"))).Success, Is.True);
    }

    [Test]
    public async Task MalformedResponseFaultsPendingAndFutureRequests()
    {
        requirePosixShell();
        await using SidecarRuntimeClient client = startShell("IFS= read -r line; sleep 1; printf 'not-json\\n'; sleep 30");
        Task<RuntimeResponse> first = client.SendAsync(RuntimeProtocol.CreateRequest("test.malformed.first"));
        Task<RuntimeResponse> second = client.SendAsync(RuntimeProtocol.CreateRequest("test.malformed.second"));

        Assert.ThrowsAsync<InvalidDataException>(async () =>
            await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.ThrowsAsync<InvalidDataException>(async () =>
            await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.SendAsync(RuntimeProtocol.CreateRequest("test.after-malformed")));
    }

    [Test]
    public async Task OversizedRequestIsRejectedWithoutPoisoningClient()
    {
        requirePosixShell();
        await using SidecarRuntimeClient client = startShellResponder();
        JsonElement payload = JsonSerializer.SerializeToElement(
            new string('x', RuntimeProtocolFraming.MaximumRequestLineCharacters));

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.SendAsync(RuntimeProtocol.CreateRequest("test.oversized", payload)));

        RuntimeResponse response = await client.SendAsync(RuntimeProtocol.CreateRequest("test.valid"));
        Assert.That(response.Success, Is.True);
    }

    [Test]
    public async Task UnansweredRequestTimesOutKillsTheWorkerAndPoisonsTheClient()
    {
        await using SidecarRuntimeClient client = startBlockingResponder();

        Assert.ThrowsAsync<TimeoutException>(async () =>
            await client.SendAsync(RuntimeProtocol.CreateRequest("test.timeout"), TimeSpan.FromMilliseconds(300)));

        Assert.Multiple(() =>
        {
            Assert.That(client.HasExited, Is.True);
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await client.SendAsync(RuntimeProtocol.CreateRequest("test.after-timeout")));
        });
    }

    [Test]
    public async Task AQueuedRequestTimeoutStartsOnlyAfterEarlierFramesAreAnswered()
    {
        string shell = findShell();
        await using SidecarRuntimeClient client = startShell(shell, """
            IFS= read -r first; IFS= read -r second; sleep 2
            for line in "$first" "$second"; do
                id=${line#*\"id\":\"}
                id=${id%%\"*}
                printf '{"id":"%s","protocolVersion":1,"success":true}\n' "$id"
            done
            sleep 30
            """);

        Task<RuntimeResponse> slow = client.SendAsync(RuntimeProtocol.CreateRequest("test.slow"), TimeSpan.FromSeconds(20));
        Task<RuntimeResponse> queued = client.SendAsync(RuntimeProtocol.CreateRequest("test.queued"), TimeSpan.FromSeconds(1));

        RuntimeResponse[] responses = await Task.WhenAll(slow, queued).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.That(responses.All(response => response.Success), Is.True);
    }

    [Test]
    public async Task DisposeAfterATimeoutCompletesPromptlyAndHasExitedStaysSafe()
    {
        SidecarRuntimeClient client = startBlockingResponder();
        Assert.ThrowsAsync<TimeoutException>(async () =>
            await client.SendAsync(RuntimeProtocol.CreateRequest("test.timeout"), TimeSpan.FromMilliseconds(200)));

        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(client.HasExited, Is.True);
        await client.DisposeAsync();
    }

    [Test]
    public void EveryCommandGivesTheWorkerTimeToAnswerBeforeTheHostKillsIt()
    {
        string[] commands =
        [
            RuntimeCommands.Hello,
            RuntimeCommands.AnalyseReplay,
            RuntimeCommands.CalculatePp,
            RuntimeCommands.SearchExternalLazerCatalog,
            RuntimeCommands.SearchExternalLazerSkins,
            RuntimeCommands.ResolveExternalLazerAssets,
            "unknown.command",
        ];

        Assert.Multiple(() =>
        {
            foreach (string command in commands)
            {
                Assert.That(
                    RuntimeProtocolTimeouts.ClientTimeout(command),
                    Is.GreaterThan(RuntimeProtocolTimeouts.WorkerTimeout(command)),
                    command);
            }

            Assert.That(
                RuntimeProtocolTimeouts.WorkerTimeout(RuntimeCommands.AnalyseReplay),
                Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(ReplayAnalysisProtocol.WallClockTimeoutMs)));
        });
    }

    [TestCase(1)]
    [TestCase(3)]
    [TestCase(4096)]
    public async Task BoundedResponseReaderHandlesLinesSplitAcrossReads(int chunk)
    {
        string text = "12345678\r\nnext-line\n" + new string('z', 100_000) + "\nlast";
        var reader = new SidecarRuntimeClient.BoundedResponseReader(new TrickleReader(text, chunk), 200_000);

        string?[] lines = [await reader.ReadLineAsync(), await reader.ReadLineAsync(), await reader.ReadLineAsync(), await reader.ReadLineAsync(), await reader.ReadLineAsync()];

        Assert.Multiple(() =>
        {
            Assert.That(lines[0], Is.EqualTo("12345678"));
            Assert.That(lines[1], Is.EqualTo("next-line"));
            Assert.That(lines[2], Is.EqualTo(new string('z', 100_000)));
            Assert.That(lines[3], Is.EqualTo("last"));
            Assert.That(lines[4], Is.Null);
        });
    }

    [Test]
    public void BoundedResponseReaderRejectsAnOversizedLineSplitAcrossReads()
    {
        var reader = new SidecarRuntimeClient.BoundedResponseReader(new TrickleReader("123456789\n", 2), 8);

        Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadLineAsync());
    }

    private sealed class TrickleReader(string text, int chunk) : TextReader
    {
        private int position;

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            int count = Math.Min(Math.Min(chunk, buffer.Length), text.Length - position);
            text.AsSpan(position, count).CopyTo(buffer.Span);
            position += count;
            return ValueTask.FromResult(count);
        }
    }

    private static SidecarRuntimeClient startShellResponder(int initialDelaySeconds = 0)
    {
        string delay = initialDelaySeconds == 0 ? string.Empty : $"sleep {initialDelaySeconds}; ";
        return startShell(delay + """
            while IFS= read -r line; do
                id=${line#*\"id\":\"}
                id=${id%%\"*}
                printf '{"id":"%s","protocolVersion":1,"success":true}\n' "$id"
                case "$line" in
                    *'"command":"shutdown"'*) exit 0 ;;
                esac
            done
            """);
    }

    private static SidecarRuntimeClient startShell(string script) => startShell("/bin/sh", script);

    private static SidecarRuntimeClient startShell(string shell, string script)
    {
        var startInfo = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        return SidecarRuntimeClient.Start(startInfo);
    }

    private static SidecarRuntimeClient startBlockingResponder()
    {
        if (!OperatingSystem.IsWindows())
            return startShell("IFS= read -r line; sleep 30");

        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/D");
        startInfo.ArgumentList.Add("/Q");
        startInfo.ArgumentList.Add("/C");
        startInfo.ArgumentList.Add("set /p request= & ping 127.0.0.1 -n 31 >nul");
        return SidecarRuntimeClient.Start(startInfo);
    }

    // Prefers /bin/sh; on Windows a POSIX sh on PATH (for example from Git) is used when present.
    private static string findShell()
    {
        if (!OperatingSystem.IsWindows() && File.Exists("/bin/sh"))
            return "/bin/sh";

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "sh.exe" : "sh");
            if (File.Exists(candidate))
                return candidate;
        }

        Assert.Ignore("This process-boundary test requires a POSIX shell.");
        return string.Empty;
    }

    private static void requirePosixShell()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
            Assert.Ignore("This process-boundary test requires /bin/sh.");
    }
}
