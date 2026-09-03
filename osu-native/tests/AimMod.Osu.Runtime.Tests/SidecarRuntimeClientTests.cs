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
        requirePosixShell();
        await using SidecarRuntimeClient client = startShell("IFS= read -r line; sleep 30");
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

    private static SidecarRuntimeClient startShell(string script)
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
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

    private static void requirePosixShell()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
            Assert.Ignore("This process-boundary test requires /bin/sh.");
    }
}
