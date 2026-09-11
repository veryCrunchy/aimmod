using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Trainers;
using osu.Game.Tests.Beatmaps;
using osu.Game.Beatmaps;

namespace AimMod.Desktop;

public partial class AimModGame
{
    private NativeDtTrainerWorkspace? dtTrainerWorkspace;

    private async void startDtTrainer(LocalReplay selected, int speed, Action<TrainerResult?> complete, Action<string> failure)
    {
        if (preparingTrainer || trainerPlayer is not null) { failure("Another practice session is preparing. Try again shortly."); return; }
        preparingTrainer = true;
        int owner = currentOsuProfile?.UserId ?? 0;
        var workspace = dtTrainerWorkspace;
        DtWorkingBeatmap? prepared = null;
        IBeatmapSourceLease? lease = null;
        try
        {
            await refreshTrainerSettingsAsync().ConfigureAwait(false);
            var inherited = trainerOsuSettings ?? throw new InvalidOperationException(trainerSettingsFailure ?? "Connect your osu! controls in Settings first.");
            var source = replayOpenService ?? throw new InvalidOperationException("Connect your osu! library in Settings first.");
            lease = await source.OpenBeatmapSourceAsync(selected, appLifetime.Token).ConfigureAwait(false);
            string path = lease.BeatmapPath;
            var data = await Task.Run(() =>
            {
                var map = new FlatWorkingBeatmap(path).Beatmap;
                if (map.BeatmapInfo.Ruleset.ShortName != "osu" || map.HitObjects.Count < 20)
                    throw new InvalidOperationException("Choose an osu!standard map with at least 20 objects.");
                string directory = Path.GetFullPath(Path.GetDirectoryName(path)!) + Path.DirectorySeparatorChar;
                string music = Path.GetFullPath(Path.Combine(directory, map.Metadata.AudioFile));
                if (!music.StartsWith(directory, StringComparison.OrdinalIgnoreCase) || !File.Exists(music) || new FileInfo(music).Length > 100 * 1024 * 1024)
                    throw new InvalidOperationException("This map's audio is missing or too large. Reinstall the map or choose another difficulty.");
                return (map, bytes: File.ReadAllBytes(music), extension: Path.GetExtension(music), directory);
            }, appLifetime.Token).ConfigureAwait(false);
            prepared = new DtWorkingBeatmap(data.map, data.bytes, data.extension, Audio, data.directory);
            var working = prepared; var sourceLease = lease;
            Schedule(() =>
            {
                preparingTrainer = false;
                if (currentRoute.Value != NativeRoute.Trainers || workspace is null || workspace != dtTrainerWorkspace || owner != (currentOsuProfile?.UserId ?? 0))
                {
                    working.ReleaseAudio(); _ = sourceLease.DisposeAsync(); complete(null); return;
                }
                var settings = new TrainerSettings(Seconds: 180, Keys: inherited.Keys, OffsetMs: inherited.OffsetMs);
                launchPreparedGameplay(settings, inherited.MouseButtons, working, complete,
                    () => { working.ReleaseAudio(); _ = sourceLease.DisposeAsync(); }, 0, null, DtProgression.Mods(speed));
            });
        }
        catch (Exception error)
        {
            prepared?.ReleaseAudio();
            if (lease is not null) await lease.DisposeAsync();
            if (!IsDisposed) Schedule(() => { preparingTrainer = false; failure(error is InvalidOperationException ? error.Message : "This map could not start. Check your osu! library and try again."); });
        }
    }
}
