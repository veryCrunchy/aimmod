using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Desktop.Trainers;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Beatmaps;

namespace AimMod.Desktop;

public partial class AimModGame
{
    private AdaptivePracticeStore adaptivePracticeStore() =>
        new(Storage.GetFullPath($"coaching/adaptive-{currentOsuProfile?.UserId ?? 0}.json", true));

    /// <summary>Plays the original map, slowing the sections the player fails and speeding them up as they clear them.</summary>
    private async void startAdaptivePractice(LocalReplay run, int? firstObjectIndex)
    {
        if (preparingTrainer || trainerPlayer is not null) return;
        preparingTrainer = true;
        int account = currentOsuProfile?.UserId ?? 0;
        var store = adaptivePracticeStore();
        coachingWorkspace?.SetPracticeRunStatus("Preparing adaptive practice...");
        DtWorkingBeatmap? prepared = null;
        IBeatmapSourceLease? lease = null;
        try
        {
            await refreshTrainerSettingsAsync().ConfigureAwait(false);
            var inherited = trainerOsuSettings ?? throw new InvalidOperationException(trainerSettingsFailure
                ?? "Select your osu! installation in Settings before starting adaptive practice.");
            var source = replayOpenService ?? throw new InvalidOperationException("Connect your osu! library in Settings first.");
            lease = await source.OpenBeatmapSourceAsync(run, appLifetime.Token).ConfigureAwait(false);
            string path = lease.BeatmapPath;
            var setup = AdaptivePracticeSetup.For(run);
            var ruleset = new OsuRuleset();
            Mod[] extra = setup.Mods.Select(ruleset.CreateModFromAcronym).OfType<Mod>().ToArray();
            var data = await Task.Run(() =>
            {
                var map = new FlatWorkingBeatmap(path).Beatmap;
                if (map.BeatmapInfo.Ruleset.ShortName != "osu" || map.HitObjects.Count < 20)
                    throw new InvalidOperationException("Adaptive practice needs an osu!standard map with at least 20 objects.");
                string directory = Path.GetFullPath(Path.GetDirectoryName(path)!) + Path.DirectorySeparatorChar;
                string music = Path.GetFullPath(Path.Combine(directory, map.Metadata.AudioFile));
                if (!music.StartsWith(directory, StringComparison.OrdinalIgnoreCase) || !File.Exists(music) || new FileInfo(music).Length > 100 * 1024 * 1024)
                    throw new InvalidOperationException("This map's audio is missing or too large. Reinstall the map or choose another difficulty.");
                return (map, bytes: File.ReadAllBytes(music), extension: Path.GetExtension(music), directory);
            }, appLifetime.Token).ConfigureAwait(false);
            prepared = new DtWorkingBeatmap(data.map, data.bytes, data.extension, Audio, data.directory, Host.Renderer);
            // Converted objects carry real end times; the decoded file's sliders do not until defaults are applied.
            var playable = prepared.GetPlayableBeatmap(ruleset.RulesetInfo, extra, appLifetime.Token);
            var sections = AdaptivePracticeSections.Split(playable);
            if (sections.Count == 0) throw new InvalidOperationException("This map has no sections to practise.");
            // The allowance for 100s follows the play being practised, so it is refreshed every session.
            var progress = AdaptivePracticeCoach.Resume(store.Load(setup.Key), setup.Key, run.Title, run.Difficulty,
                setup.TargetRate, sections.Count, DateTimeOffset.UtcNow) with { Leniency = setup.Leniency };
            int? start = firstObjectIndex is { } first && sections.FirstOrDefault(s => first >= s.FirstObject && first <= s.LastObject) is { } chosen
                ? chosen.Index : null;
            var plan = new AdaptiveAttemptPlan(sections, progress, AdaptivePracticeCoach.Next(progress, sections, start));
            var working = prepared;
            var sourceLease = lease;
            Schedule(() =>
            {
                preparingTrainer = false;
                if (currentRoute.Value != NativeRoute.Coaching || trainerPlayer is not null || account != (currentOsuProfile?.UserId ?? 0))
                {
                    working.ReleaseAudio(); _ = sourceLease.DisposeAsync();
                    coachingWorkspace?.SetPracticeRunStatus("Practice was not started. Open it again when you are ready.");
                    return;
                }
                working.LoadTrack();
                var settings = new TrainerSettings(Seconds: 180, Keys: inherited.Keys, OffsetMs: inherited.OffsetMs);
                AdaptivePracticeProgress final = plan.Progress;
                // Saves run one after another, so a quick clear and fail can never land out of order.
                Task saving = Task.CompletedTask;
                void save(AdaptivePracticeProgress value) => saving = saving.ContinueWith(_ =>
                {
                    try { store.Save(value); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { logFailure("save adaptive practice", error); }
                }, TaskScheduler.Default);
                launchGameplaySession(settings, inherited.MouseButtons, working, [.. extra, new AdaptiveSpeedMod { Plan = plan }],
                    () => { working.ReleaseAudio(); _ = sourceLease.DisposeAsync(); },
                    () => coachingWorkspace?.FinishAdaptivePractice(final),
                    hooks => new AdaptivePracticeScreen(plan, $"{run.Title} [{run.Difficulty}]", save, value => { final = value; hooks.Finished(); },
                        action => Schedule(action)) { OnAttemptReady = hooks.Apply });
            });
        }
        catch (Exception error)
        {
            prepared?.ReleaseAudio();
            // This is an async void handler: a failing cleanup must not escape it.
            try { if (lease is not null) await lease.DisposeAsync(); }
            catch (Exception cleanup) { logFailure("release adaptive practice map", cleanup); }
            if (error is not OperationCanceledException) logFailure("open adaptive practice", error);
            if (!IsDisposed) Schedule(() =>
            {
                preparingTrainer = false;
                coachingWorkspace?.SetPracticeRunStatus(error is InvalidOperationException ? error.Message : "Adaptive practice could not start. Check your osu! library and try again.");
            });
        }
    }
}
