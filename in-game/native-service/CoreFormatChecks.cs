namespace AimMod.InGame;

// Byte-exact samples produced by the AimModCore formatter
// (in-game/native-mod: aimmod_core_tests --write-samples). Keeps the native
// mod and these readers on one contract. Synthetic data only.
static class CoreFormatChecks
{
    const string Journal = "run\t1790000000-42-1\tSynthetic target test\t321.5\t80\t60\t12\t1500\t2026-01-01T00:01:00Z\n";
    const string Live = "{\"version\":1,\"active\":true,\"paused\":false,\"scoreStatus\":\"available\",\"id\":\"1790000000-42-1\",\"scenario\":\"Synthetic target test\",\"score\":100.5,\"seconds\":12.25,\"shots\":10,\"hits\":8,\"kills\":3,\"remainingSeconds\":47.75,\"lastTimeToKillSeconds\":1.5}";
    const string Replay =
        "{\"kind\":\"header\",\"version\":1,\"id\":\"1790000000-42-1\",\"scenario\":\"Synthetic target test\",\"recordedAt\":\"2026-01-01T00:00:00Z\",\"coordinates\":\"unreal-centimeters\",\"nominalHz\":60,\"mapName\":\"Map_A\",\"mapScale\":1,\"startEvent\":\"native\"}\n" +
        "{\"kind\":\"frame\",\"t\":0.01666667,\"camera\":[1,2,3,4,5,6,90],\"actors\":[[1,10,20,30,40,90]],\"health\":[{\"id\":1,\"percent\":0.5}],\"appearance\":[{\"id\":1,\"profile\":\"Bot\",\"rotation\":[0,90,0]}],\"stats\":{\"score\":1,\"seconds\":0.01666667}}\n" +
        "{\"kind\":\"input\",\"t\":0.01666667,\"action\":\"AxisTurn\",\"value\":0.5}\n" +
        "{\"kind\":\"frame\",\"t\":0.03333333,\"camera\":[1,2,3,4,5,6,90],\"actors\":[[1,20,20,30,40,90]],\"health\":[{\"id\":1,\"percent\":0.5}],\"appearance\":[{\"id\":1,\"profile\":\"Bot\",\"rotation\":[0,90,0]}],\"stats\":{\"score\":2,\"seconds\":0.03333333}}\n" +
        "{\"kind\":\"input\",\"t\":0.03333333,\"action\":\"AxisTurn\",\"value\":0.5}\n" +
        "{\"kind\":\"frame\",\"t\":0.05,\"camera\":[1,2,3,4,5,6,90],\"actors\":[[1,30,20,30,40,90]],\"health\":[{\"id\":1,\"percent\":0.5}],\"appearance\":[{\"id\":1,\"profile\":\"Bot\",\"rotation\":[0,90,0]}],\"stats\":{\"score\":3,\"seconds\":0.05}}\n" +
        "{\"kind\":\"input\",\"t\":0.05,\"action\":\"AxisTurn\",\"value\":0.5}\n" +
        "{\"kind\":\"end\",\"reason\":\"completed\",\"frames\":3,\"inputEvents\":3,\"score\":321.5}\n";

    public static void Run()
    {
        int checks = 0;
        void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
        var root = Path.Combine(Path.GetTempPath(), "aimmod-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "replays"));
        try
        {
            var run = NativeRuns.Parse(Journal.TrimEnd('\n'));
            Check(run is { Id: "native:1790000000-42-1", Scenario: "Synthetic target test", Score: 321.5, Accuracy: 80, Duration: 60 } && run.Kills == 12 && run.Damage == 1500, "native journal line parses");

            File.WriteAllText(Path.Combine(root, "live-overlay.json"), Live);
            var live = LiveOverlayState.Read(root, _ => null, false);
            Check(live.Active && live.Scenario == "Synthetic target test" && live.Score == 100.5 && live.Seconds == 12.25 && live.Accuracy == 80 && live.RemainingSeconds == 47.75 && live.DurationSeconds == 60 && live.LastTimeToKillSeconds == 1.5 && live.AttemptId == "1790000000-42-1", "native live snapshot reads");

            File.WriteAllText(Path.Combine(root, "replays", "1790000000-42-1.amreplay"), Replay);
            var catalog = new ReplayCatalog(root);
            var replay = catalog.Read("1790000000-42-1");
            Check(replay is { Reason: "completed", MapName: "Map_A" } && replay.Frames.Count == 3 && replay.Inputs.Count == 3 && replay.Frames[^1].Stats?.Score == 321.5, "native replay reads");
            Check(catalog.List().Any(r => r.Id == "1790000000-42-1" && r.Frames == 3), "native replay is listed");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"{checks} native core format checks passed.");
    }
}
