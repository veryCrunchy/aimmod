using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AimMod.InGame.Multiplayer;

namespace AimMod.InGame.Developer;

// Developer tools that need no second player: the avatar test path, a loopback
// spectate stream (your own view, delayed, or a replay played as if live), a
// content transfer loopback into a scratch folder, the Workshop query and the
// log tail. Nothing here writes to the game folder or starts a run.
sealed partial class DeveloperTools : IDisposable
{
    readonly string output;
    readonly ContentLibrary library;
    readonly MultiplayerService multiplayer;
    readonly object gate = new();
    readonly Timer timer;

    public DeveloperTools(string output, ContentLibrary library, MultiplayerService multiplayer)
    {
        this.output = output; this.library = library; this.multiplayer = multiplayer;
        timer = new Timer(_ => { try { Tick(); } catch (Exception ex) { Console.Error.WriteLine("Developer tick failed: " + ex.GetType().Name + ": " + ex.Message); } }, null, 50, 50);
    }
    public void Dispose() { timer.Dispose(); lock (gate) { StopLoopback(); Disposed = true; } }
    internal bool Disposed { get; private set; }

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---- replays and the avatar test path ---------------------------------

    public IReadOnlyList<object> Replays() =>
        new ReplayCatalog(output).List().Where(r => !r.Id.Contains("-clip", StringComparison.Ordinal)).Take(30)
            .Select(r => (object)new { r.Id, r.Scenario, r.RecordedAt, seconds = Math.Round(r.Frames / 60.0) }).ToArray();

    // avatar-test-path.tsv for AimModSteam's avatar test (in-game/docs/game-modes.md, phase 0).
    public LobbyResult AvatarPath(string? id)
    {
        if (id is null || new ReplayCatalog(output).Read(id) is not { Frames.Count: >= 2 } replay) return LobbyResult.Fail("missing", "That replay isn’t readable.");
        try { AtomicFile.WriteText(Path.Combine(output, AvatarPathExport.FileName), AvatarPathExport.Build(replay)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return LobbyResult.Fail("save", "Couldn’t write the avatar path."); }
        pathScenario = replay.Scenario;
        return LobbyResult.Success;
    }
    string? pathScenario;

    // Import a replay file (format 2, e.g. one a friend sent) into the local library.
    public LobbyResult Import(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return LobbyResult.Fail("invalid", "Enter the full path of a .amreplay file.");
        try
        {
            var file = new FileInfo(path.Trim().Trim('"'));
            if (!file.Exists || !file.Extension.Equals(".amreplay", StringComparison.OrdinalIgnoreCase)) return LobbyResult.Fail("missing", "No .amreplay file at that path.");
            if (file.Length is <= 24 or > ReplaySwap.MaxBytes) return LobbyResult.Fail("size", "That file is empty or too large for a replay.");
            var (id, _, error) = ReplayImport.Import(output, File.ReadAllBytes(file.FullName));
            return id is not null ? LobbyResult.Success : LobbyResult.Fail("import", error ?? "The replay couldn’t be imported.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return LobbyResult.Fail("read", "Couldn’t read that file."); }
    }

    // ---- loopback spectate stream (spectate-pose.tsv) ----------------------

    string? loopSource; double loopDelay = 2; NativeReplay? loopReplay; long loopStart, loopSequence, lastSelfPose = long.MinValue, selfRequested;
    readonly List<LivePose> loopBuffer = [];
    string loopScenario = "", loopMap = ""; double loopScale = 1;
    string PosePath => Path.Combine(output, "spectate-pose.tsv");

    public LobbyResult Loopback(string? source, string? replayId, double delay)
    {
        lock (gate)
        {
            if (source is null or "off") { StopLoopback(); return LobbyResult.Success; }
            if (multiplayer.WatchingSomeone) return LobbyResult.Fail("busy", "Stop spectating first; the loopback uses the same stream file.");
            loopDelay = Math.Clamp(delay, 0.5, 5);
            loopBuffer.Clear(); loopSequence = 0; lastSelfPose = long.MinValue;
            if (source == "replay")
            {
                if (replayId is null || new ReplayCatalog(output).Read(replayId) is not { Frames.Count: >= 2 } replay) return LobbyResult.Fail("missing", "That replay isn’t readable.");
                if (string.IsNullOrWhiteSpace(replay.MapName)) return LobbyResult.Fail("no-map", "That run was recorded without its map, so the spectator view can’t follow it.");
                loopReplay = replay; loopStart = Now();
                loopScenario = replay.Scenario; loopMap = replay.MapName ?? ""; loopScale = replay.MapScale is > 0 ? replay.MapScale.Value : 1;
            }
            else if (source == "self") loopReplay = null;
            else return LobbyResult.Fail("invalid", "Unknown loopback source.");
            loopSource = source;
            return LobbyResult.Success;
        }
    }

    void StopLoopback()
    {
        if (loopSource is null) return;
        loopSource = null; loopReplay = null; loopBuffer.Clear();
        try { if (File.Exists(PosePath)) File.Delete(PosePath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Keep AimModCore publishing self-pose.tsv (it stops 5 s after the last request).
    void RequestSelfPose(long now)
    {
        if (now - selfRequested < 2000) return;
        selfRequested = now;
        try { File.WriteAllText(Path.Combine(output, "self-pose.request"), now.ToString(CultureInfo.InvariantCulture)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public double[]? Camera()
    {
        RequestSelfPose(Now());
        return LivePoseFrame.Read(Path.Combine(output, "self-pose.tsv"), TimeSpan.FromSeconds(1.5)) is { } f ? f.Poses[^1].Camera : null;
    }

    void Tick()
    {
        lock (gate)
        {
            PumpContent();
            if (loopSource is null) return;
            if (multiplayer.WatchingSomeone) { StopLoopback(); return; }
            var now = Now();
            if (loopSource == "self")
            {
                RequestSelfPose(now);
                if (LivePoseFrame.Read(Path.Combine(output, "self-pose.tsv"), TimeSpan.FromSeconds(1.5)) is { } frame)
                {
                    loopScenario = frame.Scenario; loopMap = frame.MapName; loopScale = frame.MapScale ?? 1;
                    foreach (var p in frame.Poses) if (p.UnixMs > lastSelfPose) { lastSelfPose = p.UnixMs; loopBuffer.Add(p); }
                }
                // Your view, delayed: poses older than the delay, re-stamped as if they were live now.
                var shift = (long)(loopDelay * 1000);
                var due = loopBuffer.Where(p => p.UnixMs + shift <= now).TakeLast(24).Select(p => new LivePose(p.UnixMs + shift, p.Camera)).ToList();
                loopBuffer.RemoveAll(p => p.UnixMs + shift < now - 3000);
                if (due.Count > 0) WritePoses(due);
            }
            else if (loopReplay is { } replay)
            {
                // A recorded run as a live stream: 30 Hz camera samples, looping.
                var poses = new List<LivePose>();
                for (var back = 5; back >= 0; back--)
                {
                    var at = now - back * 33;
                    var t = ((at - loopStart) / 1000.0) % Math.Max(replay.Duration, 1);
                    var c = NativeReplayPlayback.Sample(replay, t).Camera;
                    poses.Add(new LivePose(at, [c[0], c[1], c[2], c.Length > 3 ? c[3] : 0, c.Length > 4 ? c[4] : 0, c.Length > 5 ? c[5] : 0, c.Length > 6 && c[6] is > 1 and < 179 ? c[6] : 90]));
                }
                WritePoses(poses);
            }
        }
    }

    void WritePoses(IReadOnlyList<LivePose> poses)
    {
        static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        var text = new StringBuilder("AIMMOD_POSE_1\t").Append(++loopSequence).Append('\n');
        text.Append("meta\t").Append(Uri.EscapeDataString(loopScenario)).Append('\t').Append(Uri.EscapeDataString(loopMap)).Append('\t').Append(N(loopScale)).Append('\n');
        long last = long.MinValue;
        foreach (var p in poses)
        {
            if (p.UnixMs <= last || p.Camera.Length < 7) continue;
            last = p.UnixMs;
            text.Append("pose\t").Append(p.UnixMs);
            for (var i = 0; i < 7; i++) text.Append('\t').Append(N(p.Camera[i]));
            text.Append('\n');
        }
        try { AtomicFile.WriteText(PosePath, text.ToString()); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    object? LoopbackView() => loopSource is null ? null : new { source = loopSource, delay = loopDelay, scenario = loopScenario, mapName = loopMap, mapScale = loopScale };

    // ---- content transfer loopback ------------------------------------------

    ContentServer? server; ContentDownload? download; LobbySettings? contentSettings; bool corrupt, corrupted;
    string LoopRoot => Path.Combine(output, "dev-loopback");

    // "Receive" the scenario (and a map override) from a pretend host, into a scratch game folder.
    public LobbyResult ContentLoop(string? scenario, string? map, bool fail)
    {
        lock (gate)
        {
            if (scenario is null || library.Scenario(scenario) is not { } choice) return LobbyResult.Fail("missing", "Pick a scenario from your library.");
            var settings = new LobbySettings(Mode: LobbyModes.Rounds, Scenario: choice, MapOverride: map is null ? null : library.Map(map));
            try { if (Directory.Exists(LoopRoot)) Directory.Delete(LoopRoot, true); Directory.CreateDirectory(Path.Combine(LoopRoot, "game", "Saved")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return LobbyResult.Fail("scratch", "Couldn’t prepare the scratch folder."); }
            server = new ContentServer(library, Now);
            if (server.Manifest(settings) is not { } manifest) return LobbyResult.Fail("empty", "Nothing to send for that scenario.");
            download = new ContentDownload(Path.Combine(LoopRoot, "game"), Path.Combine(LoopRoot, "tmp"), Now);
            contentSettings = settings; corrupt = fail; corrupted = false;
            download.Offer(manifest);
            return download.Start() ? LobbyResult.Success : LobbyResult.Fail(download.Code ?? "error", download.Error ?? "The download didn’t start.");
        }
    }

    void PumpContent()
    {
        if (server is null || download is null || contentSettings is null || download.State != "downloading") return;
        if (download.Next() is { } want) server.Request("loopback", contentSettings, want.Hash, want.Offset, want.Length);
        server.Pump(false, (peer, chunk) =>
        {
            var e = JsonSerializer.SerializeToElement(chunk);
            var data = Convert.FromBase64String(e.GetProperty("data").GetString()!);
            // Fail on purpose: one flipped byte, which the hash check must catch.
            if (corrupt && !corrupted && data.Length > 0) { data[data.Length / 2] ^= 0x5A; corrupted = true; }
            download.Chunk(e.GetProperty("hash").GetString()!, e.GetProperty("offset").GetInt64(), e.GetProperty("total").GetInt64(), data);
        });
    }

    object? ContentView() => download is null ? null : new { state = download.State, code = download.Code, error = download.Error, done = download.Done, total = download.Total, files = download.Files.Select(f => new { f.Kind, f.Name, f.Size, f.State }) };

    // ---- logs ---------------------------------------------------------------

    [GeneratedRegex(@"7656119\d{10}")] private static partial Regex SteamId();
    [GeneratedRegex(@"\b[0-9a-fA-F]{24,}\b")] private static partial Regex Token();
    [GeneratedRegex(@"(?i)([A-Z]:[\\/]+Users[\\/]+)[^\\/\s""']+")] private static partial Regex UserPath();
    public static string Redact(string line) => UserPath().Replace(Token().Replace(SteamId().Replace(line, "<steam id>"), "<id>"), "$1<user>");

    static IReadOnlyList<string> Tail(string? path, int lines, Func<string, bool>? keep = null)
    {
        try
        {
            if (path is null || !File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - 256 * 1024), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0 && (keep is null || keep(l)));
            return all.TakeLast(lines).Select(l => Redact(l.Length > 300 ? l[..300] + "…" : l)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public object View()
    {
        lock (gate)
        {
            var ue4ss = library.Root is { } root ? Path.Combine(root, "Binaries", "Win64", "ue4ss", "UE4SS.log") : null;
            return new
            {
                replays = Replays(),
                avatarPath = pathScenario,
                loopback = LoopbackView(),
                content = ContentView(),
                // AimMod map ports first, then the rest of the library.
                ports = multiplayer.DevPorts(),
                scenarios = library.Available ? multiplayer.DevPorts().Concat(library.Scenarios.Select(s => s.Name)).Distinct().Take(200).ToArray() : [],
                maps = library.Available ? library.Maps.Take(100).Select(m => m.Name).ToArray() : [],
                logs = new { service = Tail(Path.Combine(output, "service.log"), 40), game = Tail(ue4ss, 40, l => l.Contains("AimMod", StringComparison.Ordinal)) },
            };
        }
    }
}
