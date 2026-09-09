using AimMod.Desktop.Practice;
using AimMod.Desktop.Trainers;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;

namespace AimMod.Desktop;

public partial class AimModGame
{
    private async void startPracticeDifficulty(SavedPracticeMap map, PracticeDifficultyIdentity difficulty)
    {
        if (preparingTrainer || trainerPlayer is not null || map.Tracking is not { } tracking
            || !tracking.Difficulties.Contains(difficulty)) return;
        int account = currentOsuProfile?.UserId ?? 0;
        if (tracking.AccountId != 0 && tracking.AccountId != account) return;
        preparingTrainer = true;
        coachingWorkspace?.SetPracticeRunStatus("Preparing this practice section...");
        var library = new PracticeMapLibrary(Storage.GetFullPath("practice-maps", true));
        try
        {
            await refreshTrainerSettingsAsync().ConfigureAwait(false);
            var inherited = trainerOsuSettings ?? throw new InvalidOperationException(trainerSettingsFailure
                ?? "Select your osu! installation in Settings before starting this practice section.");
            string archive = library.ArchivePath(map.Id);
            Live<BeatmapSetInfo>? imported = await BeatmapManager.Import(new PreservedBeatmapImportTask(archive)).ConfigureAwait(false);
            appLifetime.Token.ThrowIfCancellationRequested();
            var info = imported?.PerformRead(set => set.Beatmaps.FirstOrDefault(b =>
                string.Equals(b.MD5Hash, difficulty.Md5, StringComparison.OrdinalIgnoreCase))?.Detach());
            if (info is null) throw new IOException("This practice difficulty could not be read. Create the set again.");
            var working = BeatmapManager.GetWorkingBeatmap(info);
            working.LoadTrack();
            Schedule(() =>
            {
                preparingTrainer = false;
                if (IsDisposed || currentRoute.Value != NativeRoute.Coaching || trainerPlayer is not null
                    || account != (currentOsuProfile?.UserId ?? 0)) return;
                bool assisted = difficulty.RequiredMods == "RX";
                Mod[] mods = assisted ? [new OsuModNoFail(), new OsuModRelax()] : [new OsuModNoFail()];
                var settings = new TrainerSettings(Seconds: 180, Keys: inherited.Keys, OffsetMs: inherited.OffsetMs);
                coachingWorkspace?.SetPracticeRunStatus(assisted ? "Aim focus: tapping is assisted." : "Practice started.");
                launchPreparedGameplay(settings, inherited.MouseButtons, working,
                    result => finishPracticeDifficulty(library, map, difficulty, result, account), () => { }, 0, null, mods);
            });
        }
        catch (Exception error)
        {
            logFailure("open practice difficulty", error);
            osu.Framework.Logging.Logger.Error(error, "AimMod could not open the selected practice difficulty.");
            if (!IsDisposed) Schedule(() =>
            {
                preparingTrainer = false;
                coachingWorkspace?.SetPracticeRunStatus(error is InvalidOperationException ? error.Message : "Practice could not start. Try opening the set again.");
            });
        }
    }

    private async void finishPracticeDifficulty(PracticeMapLibrary library, SavedPracticeMap map,
        PracticeDifficultyIdentity difficulty, TrainerResult? result, int account)
    {
        if (result?.Accuracy is not { } accuracy || !double.IsFinite(accuracy))
        {
            coachingWorkspace?.SetPracticeRunStatus("Practice ended before completion. No completed result was recorded.");
            return;
        }
        try
        {
            bool assisted = difficulty.RequiredMods == "RX";
            // Generated sections use no-fail. This records completion, never a ranked pass on the source map.
            var attempt = new PracticeAttempt(result.Id, 0, result.CompletedAt, Math.Clamp(accuracy / 100, 0, 1),
                Math.Max(0, result.Misses), true, assisted ? "lazer / NF + RX / NF+RX" : "lazer / NF / NF",
                difficulty.Name, false, assisted, difficulty.BreakdownVariant, difficulty.BreakdownGroupId,
                difficulty.SourceStartMs, difficulty.SourceEndMs);
            await library.RecordAttemptAsync(map.Id, attempt, appLifetime.Token).ConfigureAwait(false);
            if (!IsDisposed) Schedule(() =>
            {
                if (account != (currentOsuProfile?.UserId ?? 0)) return;
                coachingWorkspace?.SetPracticeRunStatus(assisted
                    ? "Aim-focus practice completed. Assisted results are tracked separately."
                    : $"Practice completed: {accuracy:0.00}% accuracy, {attempt.Misses} misses.");
                coachingWorkspace?.ClosePractice();
            });
        }
        catch (Exception error)
        {
            logFailure("save practice result", error);
            if (!IsDisposed) Schedule(() => coachingWorkspace?.SetPracticeRunStatus("The result could not be saved. Refresh your practice history and try again."));
        }
    }
}
