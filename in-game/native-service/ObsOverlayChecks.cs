using System.Net;
using System.Text;
using System.Text.Json;

namespace AimMod.InGame;

static class ObsOverlayChecks
{
    public static async Task Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "aimmod-obs-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); int count = 0, snapshots = 0;
        void Check(bool ok, string name) { count++; if (!ok) throw new Exception("OBS check failed: " + name); }
        object Snapshot() { snapshots++; return new { live = new { active = true, scenario = "Synthetic scenario", score = 42 }, settings = new { obsEnabled = true } }; }
        try
        {
            string firstUrl, binding;
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            await using (var host = new ObsOverlayHost(folder, Snapshot))
            {
                await host.Start(default); firstUrl = host.Url!;
                var uri = new Uri(firstUrl); var prefix = firstUrl[..firstUrl.LastIndexOf('/')] ;
                Check(host.Available && uri.Host == "127.0.0.1" && uri.Query == "?surface=obs", "stable source uses explicit loopback OBS surface");
                binding = File.ReadAllText(Path.Combine(folder, "obs-binding.json"));
                using var json = JsonDocument.Parse(binding);
                Check(json.RootElement.GetProperty("Port").GetInt32() == uri.Port && json.RootElement.GetProperty("Token").GetString()?.Length == 64, "persist actual assigned port and strong independent token");
                foreach (var asset in new[] { (firstUrl, "text/html"), (prefix + "/overlay.js", "application/javascript"), (prefix + "/overlay.css", "text/css") })
                {
                    using var response = await client.GetAsync(asset.Item1);
                    Check(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == asset.Item2 && (await response.Content.ReadAsStringAsync()).Length > 100, "read-only overlay assets served");
                    Check(response.Headers.CacheControl?.NoStore == true, "OBS response is never cached");
                }
                using (var response = await client.GetAsync(prefix + "/overlay-state"))
                {
                    using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    Check(response.StatusCode == HttpStatusCode.OK && data.RootElement.GetProperty("live").GetProperty("score").GetInt32() == 42 && snapshots == 1, "only authenticated read requests evaluate snapshot");
                }
                foreach (var route in new[] { "command", "settings", "overlay-settings", "replay-library", "overlay-opponents", "overlay-setup", "ui" })
                {
                    using var response = await client.GetAsync(prefix + "/" + route);
                    Check(response.StatusCode == HttpStatusCode.NotFound, "OBS capability never exposes workspace route");
                }
                foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Options })
                {
                    using var request = new HttpRequestMessage(method, prefix + "/overlay-state");
                    request.Headers.Add("X-AimMod-UI", "1"); request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using var response = await client.SendAsync(request);
                    Check(response.StatusCode == HttpStatusCode.Forbidden, "UI header cannot make OBS listener writable");
                }
                using (var response = await client.GetAsync(new Uri(uri, "/invalid-token/overlay-state")))
                    Check(response.StatusCode == HttpStatusCode.NotFound, "wrong capability cannot read snapshot");
                using (var request = new HttpRequestMessage(HttpMethod.Get, prefix + "/overlay-state"))
                {
                    request.Headers.Host = "untrusted.invalid";
                    using var response = await client.SendAsync(request);
                    Check(response.StatusCode == HttpStatusCode.Forbidden, "non-loopback Host rejected before snapshot");
                }
                Check(snapshots == 1 && File.ReadAllText(Path.Combine(folder, "obs-binding.json")) == binding, "rejected requests cannot access data or mutate persistent binding");
            }
            await using (var restarted = new ObsOverlayHost(folder, Snapshot))
            {
                await restarted.Start(default);
                Check(restarted.Url == firstUrl && File.ReadAllText(Path.Combine(folder, "obs-binding.json")) == binding, "OBS URL and token survive worker restart");
                using var response = await client.GetAsync(firstUrl);
                Check(response.StatusCode == HttpStatusCode.OK, "saved browser source remains usable after restart");
            }
            File.WriteAllText(Path.Combine(folder, "obs-binding.json"), "{\"Port\":12345,\"Token\":null}");
            await using (var invalid = new ObsOverlayHost(folder, Snapshot))
            {
                bool rejected = false;
                try { await invalid.Start(default); } catch (InvalidDataException) { rejected = true; }
                Check(rejected && !invalid.Available, "null persisted token fails before creating listener");
            }
            Console.WriteLine($"PASS {count} OBS overlay integration checks");
        }
        finally { Directory.Delete(folder, true); }
    }
}
