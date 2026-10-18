using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Coaching;

/// <summary>Adaptive practice is coaching's default: play the real map, slower only where you fail.</summary>
public partial class NativeCoachingWorkspace
{
    private Func<AdaptivePracticeStore>? adaptiveStore;
    private Action<LocalReplay, int?>? startAdaptive;
    private Action<LocalReplay, IReadOnlyList<int>>? startFocusedAdaptive;
    private Dictionary<string, AdaptivePracticeProgress> adaptiveProgress = [];
    private int adaptiveLoad;

    public void ConfigureAdaptivePractice(Func<AdaptivePracticeStore> store, Action<LocalReplay, int?> start,
        Action<LocalReplay, IReadOnlyList<int>>? startFocused = null)
    {
        adaptiveStore = store;
        startAdaptive = start;
        startFocusedAdaptive = startFocused;
        refreshAdaptiveProgress();
    }

    public void FinishAdaptivePractice(AdaptivePracticeProgress progress)
    {
        adaptiveProgress = new(adaptiveProgress) { [progress.MapKey] = progress };
        SetPracticeRunStatus(progress.Completed
            ? $"Adaptive practice: cleared at full speed. {progress.Attempts} attempts in total."
            : $"Adaptive practice saved: {progress.FullSpeedSections} of {progress.Sections.Length} sections at full speed. Continue any time.");
        refreshAdaptiveProgress();
    }

    private void refreshAdaptiveProgress()
    {
        if (adaptiveStore is not { } store) return;
        int load = ++adaptiveLoad;
        _ = Task.Run(() => store().List()).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) Schedule(() =>
            {
                if (load != adaptiveLoad || IsDisposed) return;
                var loaded = task.Result.ToDictionary(p => p.MapKey);
                // A session's last save may still be in flight; never replace a newer result with the file's copy.
                foreach (var (key, known) in adaptiveProgress)
                    if (!loaded.TryGetValue(key, out var stored) || stored.UpdatedAt < known.UpdatedAt) loaded[key] = known;
                adaptiveProgress = loaded;
                if (IsLoaded) renderCoachingMap();
            });
        });
    }

    private bool canStartAdaptive(LocalReplay? run) => run is not null && startAdaptive is not null && run.RulesetShortName == "osu";

    private void playAdaptive(LocalReplay run, int? firstObjectIndex = null) => startAdaptive?.Invoke(run, firstObjectIndex);

    /// <summary>Drills these sections first, in this order, then the rest of the map as usual.</summary>
    private void playAdaptiveFocused(LocalReplay run, IReadOnlyList<int> firstObjects)
    {
        if (startFocusedAdaptive is { } focused) focused(run, firstObjects);
        else playAdaptive(run, firstObjects.Count > 0 ? firstObjects[0] : null);
    }

    /// <summary>The first step on a map: play it through, with sections slowing down only where needed.</summary>
    private IEnumerable<Drawable> adaptiveStep(LocalReplay run)
    {
        var setup = AdaptivePracticeSetup.For(run);
        var progress = adaptiveProgress.GetValueOrDefault(setup.Key);
        string mods = setup.Mods.Count == 0 && Math.Abs(setup.TargetRate - 1) < .001 ? "" : $" Full speed is your play's setup: {setup.Label}.";
        if (progress is null)
        {
            yield return startStep("Practise the whole map at your pace",
                "Play through it. A section you fail repeats a little slower, then speeds back up as you clear it, until you can beat the map at full speed." + mods,
                new CoachingButton("Start adaptive practice", () => playAdaptive(run), true, true));
            yield break;
        }
        int total = progress.Sections.Length;
        yield return progress.Completed
            ? startStep("Cleared at full speed", $"You beat this map at full speed on {progress.CompletedAt!.Value.ToLocalTime():dd MMM}. Play it again to keep it consistent.",
                new CoachingButton("Play again", () => playAdaptive(run), true, true))
            : startStep("Continue adaptive practice", $"{progress.FullSpeedSections} of {total} sections cleared at full speed. Slowest section: {AdaptivePracticeCoach.Percent(progress.SlowestSpeed)}." + mods,
                new CoachingButton("Continue", () => playAdaptive(run), true, true));
        var strip = new AdaptiveSectionStrip(total) { RelativeSizeAxes = Axes.X, Height = 10 };
        strip.Set(progress, -1);
        yield return new CoachingCard(new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(6),
            Children = [label("Section speeds", 12, AimModPalette.Muted, "SemiBold"), strip,
                flow("Mint sections are cleared at full speed. Amber to red sections are still slowed down.", 12, AimModPalette.Muted)],
        }, 12);
    }
}
