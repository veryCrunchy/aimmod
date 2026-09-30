using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AimMod.InGame;

// Separate, read-only listener. Its persistent token never authorizes workspace commands.
sealed class ObsOverlayHost : IAsyncDisposable
{
    sealed record Binding(int Port, string Token);
    readonly string config;
    readonly Func<object> snapshot;
    WebApplication? app;
    public string? Url { get; private set; }
    public bool Available => Url is not null;
    public ObsOverlayHost(string output, Func<object> snapshot)
    { config = Path.Combine(output, "obs-binding.json"); this.snapshot = snapshot; }
    public async Task Start(CancellationToken token)
    {
        Binding binding;
        if (File.Exists(config)) {
            if (new FileInfo(config).Length > 1024) throw new InvalidDataException("Invalid OBS binding.");
            binding = JsonSerializer.Deserialize<Binding>(File.ReadAllText(config)) ?? throw new InvalidDataException("Invalid OBS binding.");
            if (binding.Port is < 1024 or > 65535 || string.IsNullOrEmpty(binding.Token) || binding.Token.Length != 64 || binding.Token.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException("Invalid OBS binding.");
        } else binding = new(0, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, binding.Port));
        app = builder.Build(); var prefix = "/" + binding.Token;
        app.Use(async (context, next) => {
            if (context.Request.Host.Host != "127.0.0.1" || context.Request.Method != "GET") { context.Response.StatusCode = 403; return; }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await next(context);
        });
        MapAssets(app, prefix);
        app.MapGet(prefix + "/overlay-state", () => Results.Json(snapshot()));
        await app.StartAsync(token);
        var address = app.Urls.Single();
        if (binding.Port == 0) {
            binding = binding with { Port = new Uri(address).Port };
            var temp = config + ".next"; File.WriteAllText(temp, JsonSerializer.Serialize(binding)); File.Move(temp, config, true);
        }
        Url = address + prefix + "/overlay?surface=obs";
    }
    internal static void MapAssets(WebApplication application, string prefix)
    {
        application.MapGet(prefix + "/overlay", () => Asset("AimMod.OverlayHtml", "text/html"));
        application.MapGet(prefix + "/overlay.js", () => Asset("AimMod.OverlayScript", "application/javascript"));
        application.MapGet(prefix + "/overlay.css", () => Asset("AimMod.OverlayStyle", "text/css"));
    }
    static IResult Asset(string name, string type) => Results.Stream(typeof(ObsOverlayHost).Assembly.GetManifestResourceStream(name)!, type);
    public async ValueTask DisposeAsync()
    {
        Url = null;
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); app = null; }
    }
}
