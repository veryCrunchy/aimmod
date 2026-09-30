namespace AimMod.InGame;
static class NativeReplayPlaybackChecks
{
    public static async Task Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "aimmod-playback-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try {
            var replay = new NativeReplay(1, "synthetic", "Synthetic", "2026-01-01", "completed", .1,
                [new(0, [0,0,0,0,179,0,103], [[1,100,0,0,10,10]]), new(.1, [0,0,0,0,-179,0,103], [[1,100,10,0,10,10],[2,200,0,0,10,10]])], []);
            var checks = 0;
            void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
            await using (var keyboardPlayer = new NativeReplayPlayback(folder)) {
                Check(keyboardPlayer.KeyboardState.Session == 0, "No keyboard scope before load");
                keyboardPlayer.Load(replay); var first = keyboardPlayer.KeyboardState;
                Check(keyboardPlayer.KeyboardCommand(first.Session, "seek-relative", .05), "Active keyboard scope commands private playback");
                keyboardPlayer.Load(replay); var second = keyboardPlayer.KeyboardState;
                Check(second.Session != first.Session && second.Revision > first.Revision, "Reloading same replay requires new renderer acknowledgement");
                Check(!keyboardPlayer.KeyboardCommand(first.Session, "toggle"), "Stale keyboard session cannot operate new load");
                keyboardPlayer.Command("close");
                Check(keyboardPlayer.KeyboardState.Session == 0 && !keyboardPlayer.KeyboardCommand(second.Session, "toggle"), "Close immediately revokes keyboard commands");
            }
            var withStats = replay with { Frames = [replay.Frames[0] with { Stats = new(10,2,1,0,5,null) }, replay.Frames[1] with { Stats = new(20,3,2,1,10,null,.1,1) }] };
            Check(NativeReplayPlayback.Sample(withStats,.05).Stats?.Score == 10, "Score is stepped, never interpolated");
            Check(NativeReplayPlayback.Sample(replay,.05).Stats is null, "Old replay metrics stay unknown");
            await using (var statsPlayer = new NativeReplayPlayback(folder)) {
                statsPlayer.Load(withStats);
                Check(statsPlayer.Snapshot().Contains("stats\t10\t2\t1\t0\t5\t\n") && !statsPlayer.Snapshot().Contains("hit\t"), "Optional stats wire format and paused hit suppression");
                Check(statsPlayer.Snapshot().StartsWith("AIMMOD_REPLAY_5\t"), "Current renderer uses version 5 header");
            }
            var renderer = Path.Combine(folder, "native-replay-renderer.json");
            await using (var mixedPlayer = new NativeReplayPlayback(folder, rendererProtocol: () => WorkspaceHost.ReadRendererProtocol(renderer))) {
                mixedPlayer.Load(withStats with { Frames = [withStats.Frames[0] with { Stats = new(10,2,1,0,5,null,0,1) }, withStats.Frames[1] with { T = 10 }] });
                bool Legacy() {
                    var snapshot = mixedPlayer.Snapshot();
                    return snapshot.StartsWith("AIMMOD_REPLAY_2\t") && snapshot.Contains("actor\t")
                        && !snapshot.Contains("transport\t") && !snapshot.Contains("stats\t") && !snapshot.Contains("hit\t");
                }
                Check(Legacy(), "Absent renderer capability uses strict legacy rows");
                File.WriteAllText(renderer, "{\"state\":\"ready\",\"mode\":\"main\"}");
                Check(Legacy(), "Old loaded Lua remains compatible with new worker");
                File.WriteAllText(renderer, "{\"state\":\"ready\",\"mode\":\"main\",\"protocol\":3}");
                Check(mixedPlayer.Snapshot().StartsWith("AIMMOD_REPLAY_3\t") && mixedPlayer.Snapshot().Contains("transport\t") && mixedPlayer.Snapshot().Contains("stats\t"), "New Lua capability enables extended stream without worker restart");
                File.SetLastWriteTimeUtc(renderer, DateTime.UtcNow.AddSeconds(-10));
                Check(Legacy(), "Stale capability cannot enable extended rows");
                foreach (var capability in new[] { "{", "{\"mode\":\"main\",\"protocol\":\"3\"}", "{\"mode\":\"main\",\"protocol\":2}", "{\"mode\":\"other\",\"protocol\":3}" }) {
                    File.WriteAllText(renderer, capability);
                    Check(Legacy(), "Invalid or unsupported renderer capability stays legacy");
                }
                File.WriteAllText(renderer, "{\"state\":\"ready\",\"mode\":\"main\",\"protocol\":3}");
                mixedPlayer.Command("play");
                Check(mixedPlayer.Snapshot().StartsWith("AIMMOD_REPLAY_3\t") && mixedPlayer.Snapshot().Contains("hit\t"), "Active stream negotiates hit events only with current capability");
                File.WriteAllText(renderer, "{\"state\":\"ready\",\"mode\":\"main\"}");
                Check(Legacy(), "Renderer downgrade removes every extension during active playback");
            }
            var middle = NativeReplayPlayback.Sample(replay, .05);
            var styled = replay with { Frames = [
                replay.Frames[0] with { Appearance = [new(1,"Bot A",[0,179,0])] },
                replay.Frames[1] with { Appearance = [new(1,"Bot A",[0,-179,0])], Stats = new(3310,null,null,null,null,null) }] };
            Check(NativeReplayPlayback.Sample(styled,.05).Appearance?[0].Rotation[1] == 180, "Recorded target rotation interpolates shortest angle");
            await using (var stylesPlayer = new NativeReplayPlayback(folder, rendererProtocol: () => 5)) {
                stylesPlayer.Load(styled);
                Check(stylesPlayer.Snapshot().Contains("appearance\t1\tBot%20A\t0\t179\t0\n") && stylesPlayer.Snapshot().Contains("result\t3310\n"), "Version5 includes profile and explicitly separate final result");
            }
            await using (var legacyStyles = new NativeReplayPlayback(folder, rendererProtocol: () => 4)) {
                legacyStyles.Load(styled);
                Check(!legacyStyles.Snapshot().Contains("appearance\t") && !legacyStyles.Snapshot().Contains("result\t"), "Older renderers never receive unsupported appearance rows");
            }
            await using (var healthPlayer = new NativeReplayPlayback(folder, rendererProtocol: () => 4)) {
                healthPlayer.Load(replay with { Frames = [replay.Frames[0] with { Health = [new(1,.5)] }, replay.Frames[1]] });
                Check(healthPlayer.Snapshot().Contains("health\t1\t0.5\n"), "Version4 transports measured health");
            }
            await using (var legacyHealth = new NativeReplayPlayback(folder, rendererProtocol: () => 3)) {
                legacyHealth.Load(replay with { Frames = [replay.Frames[0] with { Health = [new(1,.5)] }, replay.Frames[1]] });
                Check(!legacyHealth.Snapshot().Contains("health\t"), "Version3 never receives unsupported health rows");
            }
            Check(middle.Camera[4] == 180 && middle.Camera[6] == 103, "Shortest rotation and recorded FOV preserved");
            Check(middle.Actors.Length == 1 && middle.Actors[0][2] == 5, "Target identity interpolation");
            Check(NativeReplayPlayback.Sample(replay,.1).Actors.Length == 2, "Spawn appears only at recorded frame");
            await using (var player = new NativeReplayPlayback(folder)) {
                Check(!player.Command("play"), "No playback before loading"); player.Load(replay);
                Check(!player.Command("seek", double.NaN) && !player.Command("speed", -1), "Reject invalid controls");
                Check(!player.Command("layout", area:[.9,0,.5,1]), "Viewport stays inside owned UI");
                Check(player.Command("seek", 500) && player.Snapshot().Contains("actor\t2\t"), "Seek clamps at end");
                player.Command("close"); Check(!player.Snapshot().Contains("actor"), "Close removes preview scene data");
                player.Load(replay);
                var command = Path.Combine(folder, "native-replay-command.tsv");
                File.WriteAllText(command, "synthetic-1\tseek-relative\t0.05\n"); player.ReadCommand();
                Check(player.Snapshot().Contains("time\t0.05\t"), "HUD seek changes replay position");
                player.ReadCommand(); Check(player.Snapshot().Contains("time\t0.05\t"), "HUD commands consumed once");
                File.WriteAllText(command, "synthetic-seek\tseek\t0.025\n"); player.ReadCommand();
                Check(player.Snapshot().Contains("time\t0.025\t"), "Timeline seek uses absolute replay time");
                File.WriteAllText(command, "synthetic-seek-restore\tseek\t0.05\n"); player.ReadCommand();
                File.WriteAllText(command, "synthetic-2\tseek-relative\tNaN\n"); player.ReadCommand();
                Check(player.Snapshot().Contains("time\t0.05\t"), "Reject malformed HUD value");
                File.WriteAllText(command, "synthetic-3\tclose\n"); player.ReadCommand();
                Check(!player.Snapshot().Contains("camera"), "HUD exit removes frame stream");
            }
            static async Task Until(Func<bool> predicate) {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                // The pump replaces these files concurrently; a read that loses that
                // race (sharing violation or access denied) is simply retried.
                bool Observed() { try { return predicate(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; } }
                while (!Observed()) {
                    if (DateTime.UtcNow > deadline) throw new Exception("Playback pump did not recover within five seconds");
                    await Task.Delay(25);
                }
            }
            var framePath = Path.Combine(folder, "replay-frame.tsv");
            var heartbeat = Path.Combine(folder, "native-replay-worker.txt");
            var longReplay = replay with { Frames = [replay.Frames[0], replay.Frames[1] with { T = 60 }] };
            await using (var live = new NativeReplayPlayback(folder)) {
                live.Load(longReplay); live.Start();
                await Until(() => File.Exists(heartbeat) && File.ReadAllText(framePath).Contains("camera"));
                // UE4SS opens these files without FILE_SHARE_DELETE. On Windows,
                // replacing that open destination throws UnauthorizedAccessException.
                using (var held = new FileStream(framePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                    var initial = File.ReadAllText(heartbeat);
                    live.Command("play");
                    await Until(() => File.Exists(framePath + ".next"));
                    await Until(() => File.ReadAllText(heartbeat) != initial);
                    Check(live.Snapshot().Contains("camera"), "Locked frame publication does not stop playback or heartbeat");
                }
                await Until(() => !File.Exists(framePath + ".next"));
                Check(File.ReadAllText(framePath).Contains("camera"), "Frame publication resumes when Lua closes its read handle");
                var prior = File.ReadAllText(heartbeat);
                using (var held = new FileStream(heartbeat, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                    await Until(() => File.Exists(heartbeat + ".next"));
                    Check(live.Snapshot().Contains("camera"), "Locked heartbeat publication does not fault the pump");
                }
                await Until(() => File.ReadAllText(heartbeat) != prior);
                live.Command("close"); live.Load(longReplay);
                await Until(() => File.ReadAllText(framePath).Contains("time\t0\t60\t0\t1"));
                Check(true, "Another replay can load after publication contention");
            }
            var failOnce = 0;
            await using (var recovering = new NativeReplayPlayback(folder, rendererReady: () => Interlocked.Exchange(ref failOnce, 0) == 1 ? throw new InvalidOperationException("Synthetic renderer failure") : true)) {
                recovering.Load(longReplay); failOnce = 1; recovering.Start();
                await Until(() => !recovering.Snapshot().Contains("camera"));
                recovering.Load(longReplay);
                await Until(() => File.ReadAllText(framePath).Contains("camera"));
                Check(true, "Unexpected pump failure closes safely and accepts the next replay");
            }
            Console.WriteLine($"{checks} native replay playback checks passed.");
        } finally { Directory.Delete(folder, true); }
    }
}

