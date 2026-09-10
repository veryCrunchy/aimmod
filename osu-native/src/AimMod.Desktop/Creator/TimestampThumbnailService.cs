using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace AimMod.Desktop.Creator;

public sealed record TimestampThumbnail(string Path, double Seconds);

/// <summary>Loads one selected moment through Twitch's public embedded player, never scans a VOD.</summary>
public sealed partial class TimestampThumbnailService(string cacheDirectory)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private const long maximumCacheBytes = 32 * 1024 * 1024;

    internal static string VideoId(string location)
    {
        if (!FootageIndex.TryVideoUri(location, out var uri) || uri!.Host is not ("twitch.tv" or "www.twitch.tv"))
            throw new InvalidOperationException("Timestamp previews currently support Twitch broadcasts. Open this recording to check the moment.");
        string id = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
        if (id.Length == 0 || !id.All(char.IsAsciiDigit)) throw new InvalidDataException("Invalid Twitch video ID.");
        return id;
    }

    public async Task<TimestampThumbnail> GetAsync(string location, double seconds, CancellationToken token)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > FootageIndex.MaximumDurationSeconds)
            throw new ArgumentException("Choose a valid time in the recording.");
        if (!FootageIndex.TryVideoUri(location, out _)) return await localFrame(location, seconds, token).ConfigureAwait(false);
        string id = VideoId(location);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            Prune(cacheDirectory, DateTime.UtcNow);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("frame-v2:" + id + ":" + seconds.ToString("F3", CultureInfo.InvariantCulture))));
            string path = System.IO.Path.Combine(cacheDirectory, key + ".jpg");
            string metadata = path + ".json";
            if (File.Exists(path) && File.Exists(metadata) && new FileInfo(metadata).Length <= 16 * 1024)
            {
                try
                {
                    var cached = JsonSerializer.Deserialize<TimestampThumbnail>(await File.ReadAllTextAsync(metadata, token));
                    if (cached is not null && cached.Path == path && Math.Abs(cached.Seconds - seconds) <= 1.5)
                        return cached;
                }
                catch (JsonException) { /* Rebuild a partially written cache entry. */ }
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
            await using var browser = await launch(playwright).ConfigureAwait(false);
            using var cancellation = deadline.Token.Register(() => { _ = browser.CloseAsync(); });
            await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 960, Height = 540 }, AcceptDownloads = false });
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(35_000);
            await page.RouteAsync("http://localhost/aimmod-preview", route => route.FulfillAsync(new()
            {
                ContentType = "text/html", Body = Html(id, seconds),
            }));
            await page.GotoAsync("http://localhost/aimmod-preview", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.WaitForFunctionAsync("() => window.player && player.getVideo().replace(/^v/,'') === window.vodId && Math.abs(player.getCurrentTime() - window.target) < 1.25 && player.getPlaybackStats().videoResolution");
            // The embed defaults to a low initial quality. Prefer 720p when offered.
            await page.EvaluateAsync("() => { const ids=player.getQualities().map(q=>typeof q==='string'?q:(q.group||q.name)); const q=ids.find(q=>q==='720p60')||ids.find(q=>q==='720p'); if(q) player.setQuality(q); }");
            var frame = page.Frames.FirstOrDefault(f => Uri.TryCreate(f.Url, UriKind.Absolute, out var url) && url.Host == "player.twitch.tv")
                ?? throw new InvalidOperationException("The Twitch player could not load this moment.");
            var video = frame.Locator("video").First;
            await video.WaitForAsync();
            await video.EvaluateAsync("(v, at) => new Promise((resolve, reject) => { const end=Date.now()+12000; const check=()=>{ if(Date.now()>end) return reject(new Error('Frame did not arrive')); if(v.readyState<2 || Math.abs(v.currentTime-at)>1.25) return setTimeout(check,80); v.pause(); requestAnimationFrame(()=>requestAnimationFrame(resolve)); }; check(); })", seconds);
            double actual = await video.EvaluateAsync<double>("v => v.currentTime");
            if (Math.Abs(actual - seconds) > 1.5) throw new InvalidOperationException("The player did not reach the selected timestamp. Try the preview again.");
            // Screenshots of a video element include overlapping player chrome.
            // Hide only presentation elements after the video frame has arrived.
            await frame.AddStyleTagAsync(new() { Content = "body *{visibility:hidden!important} video{visibility:visible!important}" });
            byte[] image = await video.ScreenshotAsync(new() { Type = ScreenshotType.Jpeg, Quality = 82 });
            deadline.Token.ThrowIfCancellationRequested();
            if (image.Length is < 1000 or > 2 * 1024 * 1024) throw new InvalidDataException("The timestamp preview could not be captured.");
            try
            {
                await File.WriteAllBytesAsync(path + ".tmp", image, token);
                File.Move(path + ".tmp", path, true);
            }
            finally { File.Delete(path + ".tmp"); }
            var result = new TimestampThumbnail(path, actual);
            await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(result), token);
            return result;
        }
        catch (PlaywrightException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException("Twitch could not show this moment. It may be unavailable or require sign-in. Open the VOD to check it.");
        }
        finally { gate.Release(); }
    }

    private static async Task<IBrowser> launch(IPlaywright playwright)
    {
        foreach (string channel in new[] { "msedge", "chrome" })
            try { return await playwright.Chromium.LaunchAsync(new() { Channel = channel, Headless = true, ChromiumSandbox = true, Timeout = 15_000 }); }
            catch (PlaywrightException) { }
        try { return await playwright.Chromium.LaunchAsync(new() { Headless = true, ChromiumSandbox = true, Timeout = 15_000 }); }
        catch (PlaywrightException) { throw new InvalidOperationException("Install Edge or Chrome to load timestamp previews."); }
    }

    internal static string Html(string id, double seconds) => "<!doctype html><html><head><style>html,body{margin:0;background:#000;overflow:hidden}</style></head><body><div id='video'></div><script src='https://player.twitch.tv/js/embed/v1.js'></script><script>"
        + "window.vodId=" + JsonSerializer.Serialize(id) + ";window.target=" + seconds.ToString("R", CultureInfo.InvariantCulture) + ";"
        + "window.player=new Twitch.Player('video',{width:960,height:540,video:'v'+vodId,parent:['localhost'],muted:true,autoplay:true,time:Math.floor(target)+'s'});"
        + "player.addEventListener(Twitch.Player.READY,()=>{player.setMuted(true);player.seek(target);player.play();});</script></body></html>";

    internal static void Prune(string directory, DateTime now)
    {
        var files = new DirectoryInfo(directory).EnumerateFiles("*.jpg").OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
        long kept = 0; int count = 0;
        foreach (var file in files)
        {
            kept += file.Length; count++;
            if (count <= 100 && kept <= maximumCacheBytes - 2 * 1024 * 1024 && now - file.LastWriteTimeUtc <= TimeSpan.FromDays(7)) continue;
            file.Delete(); File.Delete(file.FullName + ".json");
        }
    }
}
