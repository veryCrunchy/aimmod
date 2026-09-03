using System.Text.Json;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using AimMod.Osu.Worker;
using NUnit.Framework;

namespace AimMod.Osu.Worker.Tests;

[TestFixture]
[NonParallelizable]
public sealed class WorkerProtocolHostTests
{
    [Test]
    public async Task WritesOnlyProtocolResponsesToStandardOutputChannel()
    {
        RuntimeRequest hello = RuntimeProtocol.CreateRequest(RuntimeCommands.Hello);
        RuntimeRequest shutdown = RuntimeProtocol.CreateRequest(RuntimeCommands.Shutdown);
        var input = new StringReader(string.Join('\n', serialise(hello), serialise(shutdown)) + '\n');
        var output = new StringWriter();
        var diagnostics = new StringWriter();

        int exitCode = await WorkerProtocolHost.RunAsync(input, output, diagnostics, new NoisyBackend());

        string[] lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        RuntimeResponse[] responses = lines.Select(line =>
            JsonSerializer.Deserialize<RuntimeResponse>(line, RuntimeProtocol.JsonOptions)!).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(lines, Has.Length.EqualTo(2));
            Assert.That(responses.Select(response => response.Id), Is.EqualTo(new[] { hello.Id, shutdown.Id }));
            Assert.That(responses.All(response => response.Success), Is.True);
            Assert.That(output.ToString(), Does.Not.Contain("backend noise"));
            Assert.That(diagnostics.ToString(), Is.Empty);
        });
    }

    [Test]
    public async Task KeepsInvalidInputOffTheProtocolOutputChannel()
    {
        RuntimeRequest shutdown = RuntimeProtocol.CreateRequest(RuntimeCommands.Shutdown);
        var input = new StringReader("not-json\n" + serialise(shutdown) + '\n');
        var output = new StringWriter();
        var diagnostics = new StringWriter();

        await WorkerProtocolHost.RunAsync(input, output, diagnostics, new NoisyBackend());

        Assert.Multiple(() =>
        {
            Assert.That(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), Has.Length.EqualTo(1));
            Assert.That(diagnostics.ToString(), Does.Contain("Invalid protocol message"));
        });
    }

    [Test]
    public async Task RejectsOversizedRequestsWithoutWritingDiagnosticsToProtocolOutput()
    {
        RuntimeRequest hello = RuntimeProtocol.CreateRequest(RuntimeCommands.Hello);
        RuntimeRequest shutdown = RuntimeProtocol.CreateRequest(RuntimeCommands.Shutdown);
        string oversizedRequest = serialise(hello).PadRight(RuntimeProtocolFraming.MaximumRequestLineCharacters + 1);
        var input = new StringReader(oversizedRequest + '\n' + serialise(shutdown) + '\n');
        var output = new StringWriter();
        var diagnostics = new StringWriter();

        await WorkerProtocolHost.RunAsync(input, output, diagnostics, new NoisyBackend());

        string[] lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        RuntimeResponse response = JsonSerializer.Deserialize<RuntimeResponse>(lines.Single(), RuntimeProtocol.JsonOptions)!;

        Assert.Multiple(() =>
        {
            Assert.That(response.Id, Is.EqualTo(shutdown.Id));
            Assert.That(response.Success, Is.True);
            Assert.That(output.ToString(), Does.Not.Contain("Invalid protocol message"));
            Assert.That(output.ToString(), Does.Not.Contain("backend noise"));
            Assert.That(diagnostics.ToString(), Does.Contain("request exceeds"));
            Assert.That(diagnostics.ToString(), Does.Not.Contain(hello.Id.ToString()));
        });
    }

    private static string serialise(RuntimeRequest request) => JsonSerializer.Serialize(request, RuntimeProtocol.JsonOptions);

    private sealed class NoisyBackend : IRuntimeBackend
    {
        public RuntimeHello Describe()
        {
            Console.WriteLine("backend noise");
            return new RuntimeHello("test-worker", "1", Array.Empty<string>());
        }

        public ValueTask<JsonElement?> ExecuteAsync(string command, JsonElement? payload, CancellationToken cancellationToken) =>
            ValueTask.FromException<JsonElement?>(new RuntimeCommandException("unsupported_command", command));
    }
}
