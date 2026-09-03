using System.Collections.Concurrent;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Game;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Osu;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Screens;
using osu.Game.Screens.Play;

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

if (args is ["--probe"])
{
    writeJson(new
    {
        type = "probe",
        protocolVersion = Protocol.Version,
        engine = "ppy.osu.Game",
        engineVersion = "2026.730.0",
        renderer = "native-window",
        audio = "native-bass-mixer",
        offscreen = false,
        childSurface = false,
        frameReadback = "GameHost.TakeScreenshotAsync",
    });
    return 0;
}

if (!Options.TryParse(args, out Options? options, out string? argumentError))
{
    Console.Error.WriteLine(argumentError);
    Console.Error.WriteLine("usage: osu-replay-host --beatmap <file.osu|set.osz> --replay <file.osr> [--width 1280] [--height 720] [--master 1] [--music 1] [--effects 1]");
    Console.Error.WriteLine("       osu-replay-host --probe");
    return 2;
}

try
{
    _ = typeof(OsuRuleset).Assembly;

    var output = new ProtocolWriter(jsonOptions);
    var game = new ReplayHostGame(options!, output);

    using DesktopGameHost host = Host.GetSuitableDesktopHost("aimmod-osu-replay-host", new HostOptions
    {
        FriendlyGameName = "AimMod Replay",
        IPCPipeName = null,
        BypassCompositor = false,
    });

    output.Write(new
    {
        type = "hello",
        protocolVersion = Protocol.Version,
        processId = Environment.ProcessId,
        renderer = "native-window",
    });

    _ = Task.Run(() => readCommands(game, output, jsonOptions));
    host.Run(game);
    return game.ExitCode;
}
catch (Exception error)
{
    writeJson(new { type = "fatal", protocolVersion = Protocol.Version, message = error.Message });
    return 4;
}

void writeJson(object value)
{
    Console.Out.WriteLine(JsonSerializer.Serialize(value, jsonOptions));
    Console.Out.Flush();
}

static async Task readCommands(ReplayHostGame game, ProtocolWriter output, JsonSerializerOptions jsonOptions)
{
    while (await Console.In.ReadLineAsync() is { } line)
    {
        if (string.IsNullOrWhiteSpace(line))
            continue;

        try
        {
            HostCommand? command = JsonSerializer.Deserialize<HostCommand>(line, jsonOptions);
            if (command?.Type is null)
                throw new JsonException("Command type is required.");

            game.Enqueue(command);
        }
        catch (Exception error)
        {
            output.Write(new { type = "error", message = error.Message });
        }
    }

    game.Enqueue(new HostCommand { Type = "close" });
}

sealed partial class ReplayHostGame : OsuGameBase
{
    private readonly Options options;
    private readonly ProtocolWriter output;
    private readonly ConcurrentQueue<HostCommand> pendingCommands = new();
    private FrameworkConfigManager frameworkConfig = null!;
    private NativeReplayPlayer? player;
    private bool ready;
    private bool exiting;

    public int ExitCode { get; private set; }

    public ReplayHostGame(Options options, ProtocolWriter output)
    {
        this.options = options;
        this.output = output;
    }

    public void Enqueue(HostCommand command)
    {
        pendingCommands.Enqueue(command);
        Scheduler.AddOnce(processCommands);
    }

    protected override IDictionary<FrameworkSetting, object> GetFrameworkConfigDefaults() =>
        new Dictionary<FrameworkSetting, object>
        {
            [FrameworkSetting.WindowMode] = WindowMode.Windowed,
            [FrameworkSetting.WindowedSize] = new Size(options.Width, options.Height),
            [FrameworkSetting.FrameSync] = FrameSync.Limit2x,
            [FrameworkSetting.VolumeUniversal] = options.MasterVolume,
            [FrameworkSetting.VolumeMusic] = options.MusicVolume,
            [FrameworkSetting.VolumeEffect] = options.EffectVolume,
        };

    [BackgroundDependencyLoader]
    private void load(FrameworkConfigManager config)
    {
        frameworkConfig = config;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        _ = loadReplay();
        Scheduler.AddDelayed(publishState, 250, true);
    }

    private async Task loadReplay()
    {
        try
        {
            string importPath = string.Equals(Path.GetExtension(options.BeatmapPath), ".osz", StringComparison.OrdinalIgnoreCase)
                ? options.BeatmapPath
                : Path.GetDirectoryName(options.BeatmapPath)!;

            Live<BeatmapSetInfo>? imported = await BeatmapManager.Import(new PreservedImportTask(importPath)).ConfigureAwait(false);
            if (imported is null)
                throw new InvalidOperationException("lazer could not import the selected beatmap bundle.");

            BeatmapInfo[] candidates = imported.PerformRead(set => set.Beatmaps.Select(b => b.Detach()).ToArray());

            Score score;
            var decoder = new ImportedBeatmapScoreDecoder(BeatmapManager, candidates);
            using (Stream replayStream = File.OpenRead(options.ReplayPath))
                score = decoder.Parse(replayStream);

            WorkingBeatmap workingBeatmap = decoder.SelectedBeatmap
                ?? throw new InvalidOperationException("The replay decoder did not select an imported beatmap.");
            workingBeatmap.LoadTrack();

            Schedule(() => showReplay(workingBeatmap, score));
        }
        catch (Exception error)
        {
            Schedule(() => fail(error));
        }
    }

    private void showReplay(WorkingBeatmap workingBeatmap, Score score)
    {
        Beatmap.Value = workingBeatmap;
        Ruleset.Value = score.ScoreInfo.Ruleset;
        SelectedMods.Value = score.ScoreInfo.Mods;

        var stack = new OsuScreenStack { RelativeSizeAxes = Axes.Both };
        Add(stack);

        player = new NativeReplayPlayer(score, onPlayerReady: () =>
        {
            ready = true;
            output.Write(new
            {
                type = "ready",
                beatmap = Path.GetFileName(options.BeatmapPath),
                replay = Path.GetFileName(options.ReplayPath),
                durationMs = player!.Duration,
            });
            processCommands();
        });
        player.OnShowingResults += () => output.Write(new { type = "ended", timeMs = player.CurrentTime });
        stack.Push(player);
    }

    private void processCommands()
    {
        while (pendingCommands.TryDequeue(out HostCommand? command))
        {
            try
            {
                if (!ready && command.Type is not "close" and not "getState" and not "setVolume")
                {
                    pendingCommands.Enqueue(command);
                    return;
                }

                switch (command.Type)
                {
                    case "play":
                        player!.StartPlayback();
                        acknowledge(command);
                        break;

                    case "pause":
                        player!.PausePlayback();
                        acknowledge(command);
                        break;

                    case "seek":
                        if (command.TimeMs is null || !double.IsFinite(command.TimeMs.Value))
                            throw new InvalidOperationException("seek requires a finite timeMs value.");
                        player!.Seek(Math.Clamp(command.TimeMs.Value, 0, player.Duration));
                        acknowledge(command);
                        break;

                    case "setPlaybackRate":
                        if (command.Rate is null || command.Rate is < 0.05 or > 2)
                            throw new InvalidOperationException("setPlaybackRate requires rate between 0.05 and 2.");
                        player!.PlaybackRate = command.Rate.Value;
                        acknowledge(command);
                        break;

                    case "setVolume":
                        setVolume(FrameworkSetting.VolumeUniversal, command.Master);
                        setVolume(FrameworkSetting.VolumeMusic, command.Music);
                        setVolume(FrameworkSetting.VolumeEffect, command.Effects);
                        acknowledge(command);
                        break;

                    case "getState":
                        publishState(command.Id);
                        break;

                    case "close":
                        acknowledge(command);
                        exiting = true;
                        Host.Exit();
                        return;

                    default:
                        throw new InvalidOperationException($"Unknown command type '{command.Type}'.");
                }
            }
            catch (Exception error)
            {
                output.Write(new { type = "error", id = command.Id, message = error.Message });
            }
        }
    }

    private void setVolume(FrameworkSetting setting, double? value)
    {
        if (value is null)
            return;
        if (!double.IsFinite(value.Value) || value is < 0 or > 1)
            throw new InvalidOperationException("Volume values must be between 0 and 1.");
        frameworkConfig.GetBindable<double>(setting).Value = value.Value;
    }

    private void acknowledge(HostCommand command) => output.Write(new { type = "ack", id = command.Id, command = command.Type });

    private void publishState() => publishState(null);

    private void publishState(string? responseId)
    {
        if (exiting)
            return;

        output.Write(new
        {
            type = "state",
            id = responseId,
            ready,
            timeMs = player?.CurrentTime ?? 0,
            durationMs = player?.Duration ?? 0,
            playing = player?.IsPlaying ?? false,
            playbackRate = player?.PlaybackRate ?? 1,
        });
    }

    private void fail(Exception error)
    {
        ExitCode = 4;
        output.Write(new { type = "fatal", protocolVersion = Protocol.Version, message = error.Message });
        exiting = true;
        Host.Exit();
    }
}

sealed partial class NativeReplayPlayer : ReplayPlayer
{
    private readonly Action onPlayerReady;

    protected override bool PauseOnFocusLost => false;

    public double CurrentTime => GameplayClockContainer.CurrentTime;
    public double Duration => GameplayState.Beatmap.GetLastObjectTime();
    public bool IsPlaying => GameplayClockContainer.IsRunning;

    public double PlaybackRate
    {
        get => (GameplayClockContainer as MasterGameplayClockContainer)?.UserPlaybackRate.Value ?? GameplayClockContainer.Rate;
        set
        {
            if (GameplayClockContainer is MasterGameplayClockContainer master)
                master.UserPlaybackRate.Value = value;
        }
    }

    public NativeReplayPlayer(Score score, Action onPlayerReady)
        : base(score, new PlayerConfiguration
        {
            AllowPause = false,
            AllowSkipping = false,
            ShowResults = false,
        })
    {
        this.onPlayerReady = onPlayerReady;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        if (!LoadedBeatmapSuccessfully)
            throw new InvalidOperationException("The official ReplayPlayer could not load the supplied beatmap.");
        onPlayerReady();
    }

    public void StartPlayback() => GameplayClockContainer.Start();
    public void PausePlayback() => GameplayClockContainer.Stop();
}

static class Protocol
{
    public const string Version = "aimmod.osu-replay-host.v1";
}

sealed class PreservedImportTask : ImportTask
{
    public PreservedImportTask(string path)
        : base(path)
    {
    }

    public override void DeleteFile()
    {
        // RealmArchiveModelImporter normally consumes external one-shot imports.
        // The host receives AimMod-owned paths and must never delete them.
    }
}

sealed class ImportedBeatmapScoreDecoder : LegacyScoreDecoder
{
    private readonly BeatmapManager beatmapManager;
    private readonly IReadOnlyList<BeatmapInfo> candidates;

    public WorkingBeatmap? SelectedBeatmap { get; private set; }

    public ImportedBeatmapScoreDecoder(BeatmapManager beatmapManager, IReadOnlyList<BeatmapInfo> candidates)
    {
        this.beatmapManager = beatmapManager;
        this.candidates = candidates;
    }

    protected override Ruleset GetRuleset(int rulesetId)
    {
        if (rulesetId != 0)
            throw new NotSupportedException($"The first native host supports osu!standard replay files, not ruleset {rulesetId}.");
        return new OsuRuleset();
    }

    protected override WorkingBeatmap GetBeatmap(string md5Hash)
    {
        BeatmapInfo? match = candidates.FirstOrDefault(b => string.Equals(md5Hash, b.MD5Hash, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            throw new InvalidOperationException($"The replay beatmap hash {md5Hash} was not present in the imported beatmap bundle.");

        return SelectedBeatmap = beatmapManager.GetWorkingBeatmap(match);
    }
}

sealed class ProtocolWriter
{
    private readonly JsonSerializerOptions options;
    private readonly object outputLock = new();

    public ProtocolWriter(JsonSerializerOptions options)
    {
        this.options = options;
    }

    public void Write(object value)
    {
        lock (outputLock)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(value, options));
            Console.Out.Flush();
        }
    }
}

sealed class HostCommand
{
    public string? Id { get; init; }
    public string? Type { get; init; }
    public double? TimeMs { get; init; }
    public double? Rate { get; init; }
    public double? Master { get; init; }
    public double? Music { get; init; }
    public double? Effects { get; init; }
}

sealed record Options(string BeatmapPath, string ReplayPath, int Width, int Height, double MasterVolume, double MusicVolume, double EffectVolume)
{
    public static bool TryParse(string[] args, out Options? options, out string? error)
    {
        options = null;
        error = null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Invalid argument near '{args[i]}'.";
                return false;
            }
            values[args[i]] = args[i + 1];
        }

        if (!values.TryGetValue("--beatmap", out string? beatmap) || !values.TryGetValue("--replay", out string? replay))
        {
            error = "Both --beatmap and --replay are required.";
            return false;
        }

        beatmap = Path.GetFullPath(beatmap);
        replay = Path.GetFullPath(replay);
        if (!File.Exists(beatmap) || !File.Exists(replay))
        {
            error = "The selected beatmap or replay file does not exist.";
            return false;
        }
        string beatmapExtension = Path.GetExtension(beatmap);
        if (!string.Equals(beatmapExtension, ".osu", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(beatmapExtension, ".osz", StringComparison.OrdinalIgnoreCase))
        {
            error = "--beatmap must point to a staged .osz or an extracted .osu file with its sibling resources.";
            return false;
        }

        if (!tryInt(values, "--width", 1280, 320, 7680, out int width)
            || !tryInt(values, "--height", 720, 240, 4320, out int height)
            || !tryDouble(values, "--master", 1, 0, 1, out double master)
            || !tryDouble(values, "--music", 1, 0, 1, out double music)
            || !tryDouble(values, "--effects", 1, 0, 1, out double effects))
        {
            error = "Width, height, or volume argument is outside its accepted range.";
            return false;
        }

        options = new Options(beatmap, replay, width, height, master, music, effects);
        return true;
    }

    private static bool tryInt(IReadOnlyDictionary<string, string> values, string key, int defaultValue, int min, int max, out int result)
    {
        if (!values.TryGetValue(key, out string? raw))
        {
            result = defaultValue;
            return true;
        }
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) && result >= min && result <= max;
    }

    private static bool tryDouble(IReadOnlyDictionary<string, string> values, string key, double defaultValue, double min, double max, out double result)
    {
        if (!values.TryGetValue(key, out string? raw))
        {
            result = defaultValue;
            return true;
        }
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result) && result >= min && result <= max;
    }
}
