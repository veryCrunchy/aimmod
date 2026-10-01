using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AimMod.InGame;

// Separate, read-only listener. Its persistent token never authorizes workspace commands.
sealed class ObsOverlayHost : IAsyncDisposable
{
    sealed record Binding(int Port, string Token);
    readonly string config;
    readonly Func<object> snapshot;
    readonly Func<object>? tournament;
    WebApplication? app;
    public string? Url { get; private set; }
    public bool Available => Url is not null;
    public ObsOverlayHost(string output, Func<object> snapshot, Func<object>? tournament = null)
    { config = Path.Combine(output, "obs-binding.json"); this.snapshot = snapshot; this.tournament = tournament; }
    // The tournament overlay for casters: bracket, the current match and its live scores.
    public string? TournamentUrl => Url is null ? null : Url[..Url.IndexOf("/overlay?", StringComparison.Ordinal)] + "/tournament";
    public async Task Start(CancellationToken token)
    {
        Binding binding;
        if (File.Exists(config)) {
            if (new FileInfo(config).Length > 1024) throw new InvalidDataException("Invalid OBS binding.");
            binding = JsonSerializer.Deserialize<Binding>(File.ReadAllText(config)) ?? throw new InvalidDataException("Invalid OBS binding.");
            if (binding.Port is < 1024 or > 65535 || string.IsNullOrEmpty(binding.Token) || binding.Token.Length != 64 || binding.Token.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException("Invalid OBS binding.");
        } else binding = new(0, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
        app = LoopbackServer.Build(binding.Port); var prefix = "/" + binding.Token;
        LoopbackServer.UseGuards(app, binding.Token, context => HttpMethods.IsGet(context.Request.Method));
        MapAssets(app, prefix);
        app.MapGet(prefix + "/overlay-state", () => Results.Json(snapshot()));
        if (tournament is not null) app.MapGet(prefix + "/tournament-state", () => Results.Json(tournament()));
        await app.StartAsync(token);
        var address = LoopbackServer.VerifiedAddress(app);
        if (binding.Port == 0) {
            binding = binding with { Port = new Uri(address).Port };
            AtomicFile.WriteText(config, JsonSerializer.Serialize(binding));
        }
        Url = address + prefix + "/overlay?surface=obs";
    }
    internal static void MapAssets(WebApplication application, string prefix)
    {
        application.MapGet(prefix + "/overlay", () => Asset("AimMod.OverlayHtml", "text/html"));
        application.MapGet(prefix + "/overlay.js", () => Asset("AimMod.OverlayScript", "application/javascript"));
        application.MapGet(prefix + "/overlay.css", () => Asset("AimMod.OverlayStyle", "text/css"));
        application.MapGet(prefix + "/tournament", () => Asset("AimMod.TournamentOverlayHtml", "text/html"));
        application.MapGet(prefix + "/tournament-overlay.js", () => Asset("AimMod.TournamentOverlayScript", "application/javascript"));
    }
    static IResult Asset(string name, string type) => Results.Stream(typeof(ObsOverlayHost).Assembly.GetManifestResourceStream(name)!, type);
    public async ValueTask DisposeAsync()
    {
        Url = null;
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); app = null; }
    }
}
