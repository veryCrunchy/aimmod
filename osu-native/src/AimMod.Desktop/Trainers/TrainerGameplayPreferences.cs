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
        mapBoolean("ShowStoryboard", "ShowStoryboard");
        mapBoolean("Video", "PreferNoVideo", invert: true);
        mapBoolean("IHateHavingFun", "LightenDuringBreaks", invert: true);
        mapBoolean("HitLighting", "HitLighting");
        mapBoolean("KeyOverlay", "KeyOverlay");
        mapBoolean("FpsCounter", "ShowFpsDisplay");
        if (double.TryParse(source.GetValueOrDefault("DimLevel"), NumberStyles.Float, CultureInfo.InvariantCulture, out double dim)
            && double.IsFinite(dim) && dim is >= 0 and <= 100)
            mapped.Add($"DimLevel = {(dim / 100).ToString(CultureInfo.InvariantCulture)}");
        return new TrainerGameplayPreferences(string.Join('\n', mapped));

        void mapBoolean(string from, string to, bool invert = false)
        {
            if (!source.TryGetValue(from, out string? raw) || !(raw is "0" or "1" || bool.TryParse(raw, out _))) return;
            bool enabled = TrainerOsuSettingsReader.IsEnabled(raw);
            mapped.Add($"{to} = {(invert ? !enabled : enabled)}");
        }
    }

    private readonly Dictionary<string, string> values = TrainerOsuSettingsReader.StableValues(contents);

    public IDisposable Apply(OsuConfigManager config)
    {
        var scope = new TrainerPreferenceScope();
        foreach (var setting in new[] { OsuSetting.GameplayCursorSize, OsuSetting.ScalingSizeX, OsuSetting.ScalingSizeY,
            OsuSetting.ScalingPositionX, OsuSetting.ScalingPositionY, OsuSetting.ScalingBackgroundDim,
            OsuSetting.UIScale, OsuSetting.ComboColourNormalisationAmount, OsuSetting.PositionalHitsoundsLevel })
            if (float.TryParse(values.GetValueOrDefault(setting.ToString()), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value))
                scope.Set(config.GetBindable<float>(setting), value);
        foreach (var setting in new[] { OsuSetting.DimLevel, OsuSetting.BlurLevel })
            if (double.TryParse(values.GetValueOrDefault(setting.ToString()), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value))
                scope.Set(config.GetBindable<double>(setting), value);
        foreach (var setting in new[] { OsuSetting.AutoCursorSize, OsuSetting.BeatmapColours, OsuSetting.BeatmapSkins,
            OsuSetting.BeatmapHitsounds, OsuSetting.FadePlayfieldWhenHealthLow, OsuSetting.ShowStoryboard,
            OsuSetting.PreferNoVideo, OsuSetting.LightenDuringBreaks, OsuSetting.HitLighting, OsuSetting.KeyOverlay,
            OsuSetting.CursorRotation, OsuSetting.ShowFpsDisplay, OsuSetting.ShowHealthDisplayWhenCantFail,
            OsuSetting.IncreaseFirstObjectVisibility, OsuSetting.MouseDisableWheel, OsuSetting.SafeAreaConsiderations })
            if (values.GetValueOrDefault(setting.ToString()) is {} value && (bool.TryParse(value, out _) || value is "0" or "1"))
                scope.Set(config.GetBindable<bool>(setting), value is "1" || bool.TryParse(value, out bool enabled) && enabled);
        if (Enum.TryParse<ScalingMode>(values.GetValueOrDefault("Scaling"), out var scaling) && Enum.IsDefined(scaling))
            scope.Set(config.GetBindable<ScalingMode>(OsuSetting.Scaling), scaling);
        if (Enum.TryParse<HUDVisibilityMode>(values.GetValueOrDefault("HUDVisibilityMode"), true, out var hud) && Enum.IsDefined(hud))
            scope.Set(config.GetBindable<HUDVisibilityMode>(OsuSetting.HUDVisibilityMode), hud);
        return scope;
    }
}
