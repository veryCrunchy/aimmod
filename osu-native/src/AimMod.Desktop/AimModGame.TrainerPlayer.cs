using osu.Game.Tests.Beatmaps;
using AimMod.Desktop.Trainers;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Bindings;
using osu.Framework.Platform;
using osu.Game.Input;
using osu.Framework.Input.Handlers.Tablet;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Input.Bindings;
using osu.Game.Rulesets.Osu;
using osu.Game.Screens;

namespace AimMod.Desktop;

public partial class AimModGame
{
    private NativeTrainerPlayer? trainerPlayer;
    private Container? trainerOverlay;
    private Action? restoreTrainerSettings;

    private bool preparingTrainer;
    private async void startOsuTrainer(TrainerSettings settings, bool mouseButtons, double volume)
    {
        if (trainerPlayer is not null || preparingTrainer) return;
        preparingTrainer = true;
        trainersWorkspace?.SetPreparationStatus(true, "Preparing your practice...");
        TrainerBeatmap? prepared = null;
        try
        {
            if (settings.Music == "song")
            {
                var selected = trainersWorkspace?.SelectedSong ?? throw new InvalidOperationException("Choose a song first.");
                var service = replayOpenService ?? throw new InvalidOperationException("Connect your osu! library first.");
                await using var lease = await service.OpenBeatmapSourceAsync(selected).ConfigureAwait(false);
                var data = await Task.Run(() =>
                {
                    var source = new FlatWorkingBeatmap(lease.BeatmapPath).Beatmap;
                    string root = Path.GetFullPath(Path.GetDirectoryName(lease.BeatmapPath)!) + Path.DirectorySeparatorChar;
                    string path = Path.GetFullPath(Path.Combine(root, source.Metadata.AudioFile));
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                        throw new IOException("This song's audio could not be found. Reinstall the map in osu! and try again.");
                    if (new FileInfo(path).Length > 100 * 1024 * 1024) throw new IOException("This audio file is too large. Choose another song.");
                    return (source, bytes: File.ReadAllBytes(path), extension: Path.GetExtension(path));
                }).ConfigureAwait(false);
                prepared = TrainerBeatmap.FromSong(settings, data.source, data.bytes, data.extension, Audio, volume);
            }
            else prepared = await Task.Run(() => new TrainerBeatmap(settings, Audio, volume)).ConfigureAwait(false);
            var map = prepared;
            Schedule(() =>
            {
                preparingTrainer = false;
                trainersWorkspace?.SetPreparationStatus(false, "Escape ends the session.");
                if (currentRoute.Value != NativeRoute.Trainers || trainerPlayer is not null) { map.ReleaseAudio(); return; }
                launchPreparedTrainer(settings, mouseButtons, map);
            });
        }
        catch (Exception error)
        {
            prepared?.ReleaseAudio();
            Schedule(() =>
            {
                preparingTrainer = false;
                trainersWorkspace?.SetPreparationStatus(false, error is IOException or InvalidOperationException
                    ? error.Message : "Practice could not start. Choose another song or try again.");
            });
        }
    }

    private void launchPreparedTrainer(TrainerSettings settings, bool mouseButtons, TrainerBeatmap map)
    {
        WorkingBeatmap previousMap = Beatmap.Value;
        var previousRuleset = Ruleset.Value;
        var previousMods = SelectedMods.Value;
        double previousOffset = LocalConfig.Get<double>(OsuSetting.AudioOffset);
        bool previousMouse = LocalConfig.Get<bool>(OsuSetting.MouseDisableButtons);
        var appearance = trainerGameplayPreferences?.Apply(LocalConfig);
        var input = (trainerOsuSettings?.Input ?? new TrainerInputSettings()).Apply(Host.AvailableInputHandlers);
        restoreTrainerSettings = () =>
        {
            LocalConfig.SetValue(OsuSetting.AudioOffset, previousOffset);
            LocalConfig.SetValue(OsuSetting.MouseDisableButtons, previousMouse);
            appearance?.Dispose(); input.Dispose(); restoreTrainerSettings = null;
        };
        var stack = new OsuScreenStack { RelativeSizeAxes = Axes.Both };
        var bindings = TrainerSettings.ParseKeys(settings.Keys).Select(k => Enum.Parse<InputKey>(k.ToString())).ToArray();
        var inheritedBindings = settings.Keys == trainerOsuSettings?.Keys && trainerOsuSettings.Bindings is { Count: > 0 } ? trainerOsuSettings.Bindings : null;
        var gameplayBindings = inheritedBindings?.Where(b => Enum.IsDefined((OsuAction)b.Action)).Take(128)
            .Select(b => new KeyBinding((KeyCombination)b.Combination, (OsuAction)b.Action))
            .Where(b => RealmKeyBindingStore.CheckValidForGameplay(b.KeyCombination)).ToArray()
            ?? [new KeyBinding(bindings[0], OsuAction.LeftButton), new KeyBinding(bindings[1], OsuAction.RightButton),
                new KeyBinding(InputKey.MouseLeft, OsuAction.LeftButton), new KeyBinding(InputKey.MouseRight, OsuAction.RightButton)];
        // This is AimMod's private Realm, never the source osu! installation.
        Dependencies.Get<RealmAccess>().Run(r => r.Write(() =>
        {
            foreach (var b in r.All<RealmKeyBinding>().ToArray().Where(b => b.RulesetName == "osu" && b.Variant == 0
                && (inheritedBindings is not null || b.ActionInt is (int)OsuAction.LeftButton or (int)OsuAction.RightButton))) r.Remove(b);
            foreach (var binding in gameplayBindings)
                r.Add(new RealmKeyBinding(binding.Action, binding.KeyCombination, "osu", 0));
        }));
        LocalConfig.SetValue(OsuSetting.AudioOffset, (double)settings.OffsetMs);
        LocalConfig.SetValue(OsuSetting.MouseDisableButtons, !mouseButtons);
        Beatmap.Value = map;
        Ruleset.Value = new OsuRuleset().RulesetInfo;
        SelectedMods.Value = [];
        // Use the entire client viewport: a small card would change physical aiming distance.
        var previousCursor = Host.Window?.CursorState;
        if (Host.Window is {} window) window.CursorState |= CursorState.Confined;
        content.Hide(); header.Hide();
        Add(trainerOverlay = new Container { RelativeSizeAxes = Axes.Both, Depth = -100,
            Children = [new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas }, stack] });
        bool finished = false;
        trainerPlayer = new NativeTrainerPlayer(settings, result => Schedule(() =>
        {
            if (finished) return;
            finished = true;
            trainerPlayer = null;
            trainerOverlay?.Clear(); trainerOverlay?.Expire(); trainerOverlay = null;
            Beatmap.Value = previousMap; Ruleset.Value = previousRuleset; SelectedMods.Value = previousMods;
            restoreTrainerSettings?.Invoke();
            if (previousCursor is {} cursor && Host.Window is {} window) window.CursorState = cursor;
            content.Show(); header.Show();
            trainersWorkspace?.CompleteOsuSession(result);
            // Player disposal is deferred by the framework. Release its private audio afterwards.
            Scheduler.AddDelayed(map.ReleaseAudio, 1000);
        })) { SeekTime = map.SeekTime, OutroProgress = map.FadeOutro, OnReady = () =>
        {
            if (trainerOsuSettings?.Input?.Tablet is {} mapping)
                foreach (var device in Host.AvailableInputHandlers.OfType<ITabletHandler>())
                { device.OutputAreaSize.Value = mapping.OutputSize; device.OutputAreaOffset.Value = mapping.OutputOffset; }
        } };
        stack.Push(trainerPlayer);
    }
}
