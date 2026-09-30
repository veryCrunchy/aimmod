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
    public async Task AnswersOversizedRequestsWithAnErrorWhenTheIdIsRecoverable()
    {
        RuntimeRequest hello = RuntimeProtocol.CreateRequest(RuntimeCommands.Hello);
        RuntimeRequest shutdown = RuntimeProtocol.CreateRequest(RuntimeCommands.Shutdown);
        string oversizedRequest = serialise(hello).PadRight(RuntimeProtocolFraming.MaximumRequestLineCharacters + 1);
        var input = new StringReader(oversizedRequest + '\n' + serialise(shutdown) + '\n');
        var output = new StringWriter();
        var diagnostics = new StringWriter();

        await WorkerProtocolHost.RunAsync(input, output, diagnostics, new NoisyBackend());

        RuntimeResponse[] responses = readResponses(output);

        Assert.Multiple(() =>
        {
            Assert.That(responses, Has.Length.EqualTo(2));
            Assert.That(responses[0].Id, Is.EqualTo(hello.Id));
            Assert.That(responses[0].Success, Is.False);
            Assert.That(responses[0].Error?.Code, Is.EqualTo("request_too_large"));
            Assert.That(responses[1].Id, Is.EqualTo(shutdown.Id));
            Assert.That(responses[1].Success, Is.True);
            Assert.That(output.ToString(), Does.Not.Contain("Invalid protocol message"));
            Assert.That(output.ToString(), Does.Not.Contain("backend noise"));
            Assert.That(diagnostics.ToString(), Does.Contain("request exceeds"));
            Assert.That(diagnostics.ToString(), Does.Not.Contain(hello.Id.ToString()));
        });
    }

    [Test]
    public async Task DropsOversizedRequestsSilentlyWhenNoIdCanBeRecovered()
    {
        RuntimeRequest shutdown = RuntimeProtocol.CreateRequest(RuntimeCommands.Shutdown);
        string oversizedRequest = new('x', RuntimeProtocolFraming.MaximumRequestLineCharacters + 10);
        var input = new StringReader(oversizedRequest + '\n' + serialise(shutdown) + '\n');
        var output = new StringWriter();

        await WorkerProtocolHost.RunAsync(input, output, new StringWriter(), new NoisyBackend());

        RuntimeResponse[] responses = readResponses(output);
        Assert.That(responses.Select(response => response.Id), Is.EqualTo(new[] { shutdown.Id }));
    }

    [Test]
    public async Task AnswersMalformedRequestsWithAnErrorWhenTheIdIsRecoverable()
    {
        Guid id = Guid.NewGuid();
        RuntimeRequest shutdown = RuntimeProtocol.CreateRequest(RuntimeCommands.Shutdown);
        var input = new StringReader($"{{\"id\":\"{id}\",\"protocolVersion\":\"oops\"}}\n" + serialise(shutdown) + '\n');
        var output = new StringWriter();

        await WorkerProtocolHost.RunAsync(input, output, new StringWriter(), new NoisyBackend());

        RuntimeResponse[] responses = readResponses(output);
        Assert.Multiple(() =>
        {
            Assert.That(responses, Has.Length.EqualTo(2));
            Assert.That(responses[0].Id, Is.EqualTo(id));
            Assert.That(responses[0].Error?.Code, Is.EqualTo("invalid_request"));
        });
    }

    [Test]
    public async Task ReadsRequestsDeliveredInTinyChunks()
    {
        RuntimeRequest hello = RuntimeProtocol.CreateRequest(RuntimeCommands.Hello);
        RuntimeRequest shutdown = RuntimeProtocol.CreateRequest(RuntimeCommands.Shutdown);
        var input = new TrickleReader(serialise(hello) + "\r\n" + serialise(shutdown) + "\n", 7);
        var output = new StringWriter();

        await WorkerProtocolHost.RunAsync(input, output, new StringWriter(), new NoisyBackend());

        Assert.That(readResponses(output).Select(response => response.Id), Is.EqualTo(new[] { hello.Id, shutdown.Id }));
    }

    [TestCase("{\"id\":\"6f1d1c57-2c4b-4e0f-9a52-0a9d4f8f3c11\",\"x\":", true)]
    [TestCase("{\"command\":\"hello\"}", false)]
    [TestCase("{\"id\":\"not-a-guid\"}", false)]
    [TestCase("", false)]
    public void RecoversRequestIdsOnlyWhenTheyAreWellFormed(string text, bool expected) =>
        Assert.That(WorkerProtocolHost.TryRecoverRequestId(text, out _), Is.EqualTo(expected));

    private static RuntimeResponse[] readResponses(StringWriter output) =>
        output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
              .Select(line => JsonSerializer.Deserialize<RuntimeResponse>(line, RuntimeProtocol.JsonOptions)!)
              .ToArray();

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
