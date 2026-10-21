using AimMod.Desktop.PpTargets;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

/// <summary>Times PP target scans on synthetic full-length maps. Explicit: it starts real workers and takes a while.</summary>
[TestFixture]
[Explicit("Benchmark: starts real PP workers.")]
public sealed class PpTargetScanBenchmarkTests
{
    private string directory = null!;

    [SetUp]
    public void SetUp() => Directory.CreateDirectory(directory = Path.Combine(Path.GetTempPath(), $"aimmod-scan-benchmark-{Guid.NewGuid():N}"));

    [TearDown]
    public void TearDown() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [TestCase(1)] [TestCase(3)] [TestCase(6)] [TestCase(8)] [TestCase(12)]
    public async Task ScanTime(int workers)
    {
        const int maps = 200;
        var service = new PpTargetExactCalculationService(directory, Path.Combine(directory, "cache.json"), new MapClient(),
            Path.Combine(directory, "downloads"), () => SidecarRuntimeClient.Start(executable()), workers);
        var requests = Enumerable.Range(1, maps).Select(id => new PpTargetExactRequest(10_000 + id, null, [], .94, .5)).ToArray();
        if (Environment.GetEnvironmentVariable("AIMMOD_BENCH_MINTHREADS") is { } extra && int.TryParse(extra, out int threads))
        {
            System.Threading.ThreadPool.GetMinThreads(out int worker, out int io);
            System.Threading.ThreadPool.SetMinThreads(Math.Max(worker, threads), io);
        }
        System.Threading.ThreadPool.GetMinThreads(out int minimum, out _);
        TestContext.Out.WriteLine($"BENCH   thread pool minimum {minimum}");
        var self = System.Diagnostics.Process.GetCurrentProcess();
        var cpuBefore = self.TotalProcessorTime;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var cold = await service.CalculateAsync(requests);
        double coldMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        // A changed skill profile changes every score shape, so every PP scenario is calculated again.
        var rescan = await service.CalculateAsync(requests.Select(r => r with { ExpectedAccuracy = .953, Attainability = .55 }).ToArray());
        double rescanMs = timer.Elapsed.TotalMilliseconds;
        self.Refresh();
        TestContext.Out.WriteLine($"BENCH   app process CPU {(self.TotalProcessorTime - cpuBefore).TotalMilliseconds:F0} ms over {coldMs + rescanMs:F0} ms wall = {(self.TotalProcessorTime - cpuBefore).TotalMilliseconds / (coldMs + rescanMs):F1} cores, GC server={System.Runtime.GCSettings.IsServerGC}");
        TestContext.Out.WriteLine($"BENCH workers={workers} cpus={Environment.ProcessorCount} maps={maps}: cold {coldMs:F0} ms ({coldMs / maps:F0} ms/map), rescan {rescanMs:F0} ms ({rescanMs / maps:F0} ms/map)");
        long workerMemory = System.Diagnostics.Process.GetProcessesByName("AimMod").Where(p => p.Id != Environment.ProcessId).Select(p => { try { return p.PeakWorkingSet64; } catch { return 0; } }).DefaultIfEmpty(0).Max();
        TestContext.Out.WriteLine($"BENCH   largest AimMod process peak working set {workerMemory / 1024 / 1024} MB");
        Assert.That(cold, Has.Count.EqualTo(maps));
        Assert.That(rescan, Has.Count.EqualTo(maps));
    }

    private static string executable() => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "AimMod.exe" : "AimMod");

    private sealed class MapClient : IOfficialBeatmapDifficultyClient
    {
        public Task<OfficialBeatmapDifficultyDownloadResult> DownloadDifficultyAsync(int beatmapId, string destinationDirectory, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(destinationDirectory);
            string path = Path.Combine(destinationDirectory, $"{beatmapId}.osu");
            File.WriteAllText(path, beatmap(beatmapId));
            return Task.FromResult(new OfficialBeatmapDifficultyDownloadResult(OfficialBeatmapRequestStatus.Success, beatmapId, path, new FileInfo(path).Length));
        }
    }

    /// <summary>About three minutes at 180 BPM: jumps, 16-note streams and sliders, roughly 900 objects.</summary>
    private static string beatmap(int id)
    {
        var random = new Random(id);
        var objects = new List<string>();
        double beat = 60000.0 / 180;
        double time = 1000;
        while (objects.Count < 900)
        {
            int kind = random.Next(3);
            if (kind == 0)
                for (int i = 0; i < 16; i++, time += beat / 4)
                    objects.Add($"{200 + i * 8},{180 + (i % 2) * 6},{(int)time},1,0,0:0:0:0:");
            else if (kind == 1)
                for (int i = 0; i < 8; i++, time += beat / 2)
                    objects.Add($"{random.Next(40, 470)},{random.Next(40, 340)},{(int)time},1,0,0:0:0:0:");
            else
            {
                objects.Add($"{random.Next(60, 300)},{random.Next(60, 300)},{(int)time},2,0,B|{random.Next(300, 460)}:{random.Next(60, 320)},1,140");
                time += beat;
            }
        }
        return $$"""
            osu file format v14

            [General]
            AudioFilename: audio.mp3
            Mode: 0

            [Metadata]
            Title:Benchmark
            Artist:AimMod
            Creator:AimMod
            Version:Benchmark {{id}}
            BeatmapID:{{id}}
            BeatmapSetID:1

            [Difficulty]
            HPDrainRate:5
            CircleSize:4
            OverallDifficulty:8.5
            ApproachRate:9.3
            SliderMultiplier:1.6
            SliderTickRate:1

            [TimingPoints]
            0,{{beat.ToString(System.Globalization.CultureInfo.InvariantCulture)}},4,2,1,50,1,0

            [HitObjects]
            {{string.Join('\n', objects)}}
            """;
    }
}
