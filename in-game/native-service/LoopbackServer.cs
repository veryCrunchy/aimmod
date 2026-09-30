using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AimMod.InGame;

// Shared hardening for the private loopback listeners. Neither listener reads
// environment, command-line or appsettings configuration: those sources can add
// Kestrel endpoints (for example a non-loopback address) or enable the
// development exception page, which would expose stack traces and local paths.
static class LoopbackServer
{
    internal const long MaxRequestBody = 128 * 1024;

    internal static WebApplication Build(int port)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = "Production",
            ApplicationName = typeof(LoopbackServer).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = MaxRequestBody;
            options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            options.Limits.MaxRequestLineSize = 8 * 1024;
            options.Limits.MaxRequestHeaderCount = 64;
            options.Limits.MaxConcurrentConnections = 64;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Listen(IPAddress.Loopback, port);
        });
        return builder.Build();
    }

    // Fail closed if anything other than the IPv4 loopback listener was bound.
    internal static string VerifiedAddress(WebApplication app)
    {
        var addresses = app.Urls.ToArray();
        if (addresses.Length != 1 || !Uri.TryCreate(addresses[0], UriKind.Absolute, out var uri) || uri.Host != "127.0.0.1" || uri.Scheme != "http")
            throw new InvalidOperationException("Local listener is not restricted to loopback.");
        return addresses[0];
    }

    // DNS-rebinding and cross-site protection. The Host must name the literal
    // loopback address and the port actually accepted by this listener. A
    // browser page from any web origin is rejected even before routing, so a
    // guessed capability path would still not be reachable from websites.
    // Requests without Origin (same-origin GET, OBS/CEF, Gameface) and the
    // opaque "null" origin (which cannot pass a preflight for X-AimMod-UI) stay
    // allowed; embedded renderer schemes cannot be claimed by a website.
    internal static bool RequestAllowed(HttpContext context)
    {
        var request = context.Request;
        var port = context.Connection.LocalPort;
        if (request.Host.Host != "127.0.0.1" || request.Host.Port is int hostPort && hostPort != port) return false;
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Equals("cross-site", StringComparison.OrdinalIgnoreCase)) return false;
        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0 || origin == "null") return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https" or "ws" or "wss")) return true;
        return uri.Scheme == "http" && uri.Host == "127.0.0.1" && uri.Port == port;
    }

    // Constant-time comparison of the first path segment with the capability.
    internal static bool CapabilityMatches(PathString path, byte[] capability)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value) || value[0] != '/') return false;
        var end = value.IndexOf('/', 1);
        var segment = end < 0 ? value.AsSpan(1) : value.AsSpan(1, end - 1);
        if (segment.Length != capability.Length) return false;
        Span<byte> bytes = stackalloc byte[128];
        if (segment.Length > bytes.Length) return false;
        var written = Encoding.ASCII.GetBytes(segment, bytes);
        return written == capability.Length && CryptographicOperations.FixedTimeEquals(bytes[..written], capability);
    }

    internal static void UseGuards(WebApplication app, string capability, Func<HttpContext, bool>? extra = null)
    {
        var expected = Encoding.ASCII.GetBytes(capability);
        app.Use(async (context, next) =>
        {
            if (!RequestAllowed(context) || extra?.Invoke(context) == false) { context.Response.StatusCode = 403; return; }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
            if (!CapabilityMatches(context.Request.Path, expected)) { context.Response.StatusCode = 404; return; }
            await next(context);
        });
    }
}
