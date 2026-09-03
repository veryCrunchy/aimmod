using System.Text.Json;
using System.Diagnostics;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Osu.Runtime.Tests;

[TestFixture]
public sealed class RuntimeRequestRouterTests
{
    [Test]
    public async Task HelloReturnsBackendDescription()
    {
        var router = new RuntimeRequestRouter(new TestBackend());
        RuntimeRequest request = RuntimeProtocol.CreateRequest(RuntimeCommands.Hello);

        RuntimeResponse response = await router.RouteAsync(request);
        RuntimeHello? hello = response.Payload?.Deserialize<RuntimeHello>(RuntimeProtocol.JsonOptions);

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(response.Id, Is.EqualTo(request.Id));
            Assert.That(hello?.RuntimeName, Is.EqualTo("test-runtime"));
            Assert.That(hello?.Capabilities, Does.Contain(RuntimeCapabilities.ReplayDecode));
        });
    }

    [Test]
    public async Task RejectsUnsupportedProtocolVersion()
    {
        var router = new RuntimeRequestRouter(new TestBackend());
        var request = new RuntimeRequest(Guid.NewGuid(), RuntimeProtocol.CurrentVersion + 1, RuntimeCommands.Hello, null);

        RuntimeResponse response = await router.RouteAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.False);
            Assert.That(response.Error?.Code, Is.EqualTo("protocol_version"));
        });
    }

    [Test]
    public async Task KeepsExpectedBackendErrorsOnProtocolBoundary()
    {
        var router = new RuntimeRequestRouter(new TestBackend());

        RuntimeResponse response = await router.RouteAsync(RuntimeProtocol.CreateRequest("unknown.command"));

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.False);
            Assert.That(response.Error?.Code, Is.EqualTo("unsupported_command"));
            Assert.That(response.Error?.Message, Does.Contain("unknown.command"));
        });
    }

    [Test]
    public void RequestRoundTripsThroughJson()
    {
        RuntimeRequest expected = RuntimeProtocol.CreateRequest(RuntimeCommands.SearchBeatmapSets, new { Query = "artist" });
        string json = JsonSerializer.Serialize(expected, RuntimeProtocol.JsonOptions);

        RuntimeRequest? actual = JsonSerializer.Deserialize<RuntimeRequest>(json, RuntimeProtocol.JsonOptions);

        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.Not.Null);
            Assert.That(actual?.Id, Is.EqualTo(expected.Id));
            Assert.That(actual?.Command, Is.EqualTo(RuntimeCommands.SearchBeatmapSets));
            Assert.That(actual?.Payload?.GetProperty("query").GetString(), Is.EqualTo("artist"));
        });
    }

    [Test]
    public void WorkerProcessReusesAimModExecutableWithoutAShell()
    {
        ProcessStartInfo startInfo = SidecarRuntimeClient.CreateStartInfo("/opt/aimmod/AimMod");

        Assert.Multiple(() =>
        {
            Assert.That(startInfo.FileName, Is.EqualTo("/opt/aimmod/AimMod"));
            Assert.That(startInfo.ArgumentList, Is.EqualTo(new[] { "--worker" }));
            Assert.That(startInfo.UseShellExecute, Is.False);
            Assert.That(startInfo.RedirectStandardInput, Is.True);
            Assert.That(startInfo.RedirectStandardOutput, Is.True);
            Assert.That(startInfo.CreateNoWindow, Is.True);
        });
    }

    private sealed class TestBackend : IRuntimeBackend
    {
        public RuntimeHello Describe() => new("test-runtime", "1.0.0", new[] { RuntimeCapabilities.ReplayDecode });

        public ValueTask<JsonElement?> ExecuteAsync(string command, JsonElement? payload, CancellationToken cancellationToken) =>
            ValueTask.FromException<JsonElement?>(new RuntimeCommandException("unsupported_command", $"Unknown command: {command}"));
    }
}
