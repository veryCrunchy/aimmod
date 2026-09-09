using System.Globalization;
using osu.Framework.Bindables;
using osu.Game.Configuration;

namespace AimMod.Desktop.Trainers;

public sealed class TrainerGameplayPreferences(string contents)
{
    public static TrainerGameplayPreferences Stable(string contents)
    {
        var source = TrainerOsuSettingsReader.StableValues(contents);
        var mapped = new List<string>();
        if (source.TryGetValue("CursorSize", out string? size)) mapped.Add($"GameplayCursorSize = {size}");
        mapBoolean("AutomaticCursorSizing", "AutoCursorSize");
        mapBoolean("IgnoreBeatmapSkins", "BeatmapSkins", invert: true);
        mapBoolean("IgnoreBeatmapSamples", "BeatmapHitsounds", invert: true);
        return new TrainerGameplayPreferences(string.Join('\n', mapped));

        void mapBoolean(string from, string to, bool invert = false)
        {
            if (!source.TryGetValue(from, out string? raw) || !(raw is "0" or "1" || bool.TryParse(raw, out _))) return;
            bool enabled = TrainerOsuSettingsReader.IsEnabled(raw);
            mapped.Add($"{to} = {(invert ? !enabled : enabled)}");
        }
    }

    private readonly Dictionary<string, string> values = contents.Split('\n').Select(l => l.Split('=', 2)).Where(p => p.Length == 2)
        .GroupBy(p => p[0].Trim()).ToDictionary(g => g.Key, g => g.Last()[1].Trim());

    public IDisposable Apply(OsuConfigManager config)
    {
        var scope = new Scope();
        foreach (var setting in new[] { OsuSetting.GameplayCursorSize, OsuSetting.ScalingSizeX, OsuSetting.ScalingSizeY,
            OsuSetting.ScalingPositionX, OsuSetting.ScalingPositionY })
            if (float.TryParse(values.GetValueOrDefault(setting.ToString()), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value))
                scope.Set(config.GetBindable<float>(setting), value);
        foreach (var setting in new[] { OsuSetting.AutoCursorSize, OsuSetting.BeatmapColours, OsuSetting.BeatmapSkins,
            OsuSetting.BeatmapHitsounds, OsuSetting.FadePlayfieldWhenHealthLow })
            if (values.GetValueOrDefault(setting.ToString()) is {} value && (bool.TryParse(value, out _) || value is "0" or "1"))
                scope.Set(config.GetBindable<bool>(setting), value is "1" || bool.TryParse(value, out bool enabled) && enabled);
        if (Enum.TryParse<ScalingMode>(values.GetValueOrDefault("Scaling"), out var scaling) && Enum.IsDefined(scaling))
            // The trainer owns one gameplay viewport; modes which shrink the whole client
            // therefore apply at that viewport instead of the surrounding AimMod shell.
            scope.Set(config.GetBindable<ScalingMode>(OsuSetting.Scaling), scaling == ScalingMode.Off ? ScalingMode.Off : ScalingMode.Gameplay);
        return scope;
    }

    private sealed class Scope : IDisposable
    {
        private readonly List<Action> restore = [];
        public void Set<T>(Bindable<T> b, T value) { T previous = b.Value; restore.Add(() => b.Value = previous); b.Value = value; }
        public void Dispose() { foreach (var undo in restore.AsEnumerable().Reverse()) undo(); restore.Clear(); }
    }
}
