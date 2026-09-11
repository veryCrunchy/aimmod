using System.Drawing;
using System.Globalization;
using osu.Framework.Configuration;
using osu.Framework.Platform;
using osu.Game.Configuration;

namespace AimMod.Desktop.Trainers;

/// <summary>Read-only import. Window changes last only for the embedded practice session.</summary>
public sealed class TrainerDisplayPreferences(string contents, bool stable = false)
{
    private readonly Dictionary<string, string> values = TrainerOsuSettingsReader.StableValues(contents);

    internal Size? WindowSize => stable ? pair("Width", "Height") : size("WindowedSize");
    internal Size? FullscreenSize => stable ? pair("WidthFullscreen", "HeightFullscreen") : size("SizeFullscreen");
    internal bool Letterboxing => stable && flag("Letterboxing") == true && flag("Fullscreen") == true;
    internal WindowMode? Mode(Size desktop)
    {
        if (!stable) return enumeration<WindowMode>("WindowMode");
        return flag("Fullscreen") switch
        {
            true => Letterboxing ? WindowMode.Borderless : WindowMode.Fullscreen,
            false => WindowSize == desktop ? WindowMode.Borderless : WindowMode.Windowed,
            _ => null
        };
    }

    public IDisposable Apply(FrameworkConfigManager framework, OsuConfigManager game, IWindow? window = null)
    {
        var scope = new TrainerPreferenceScope();
        // Snapshot all related values before changing any: window events can update other bindables.
        scope.Remember(framework.GetBindable<WindowMode>(FrameworkSetting.WindowMode));
        scope.Remember(framework.GetBindable<Size>(FrameworkSetting.WindowedSize));
        scope.Remember(framework.GetBindable<Size>(FrameworkSetting.SizeFullscreen));
        scope.Remember(framework.GetBindable<double>(FrameworkSetting.WindowedPositionX));
        scope.Remember(framework.GetBindable<double>(FrameworkSetting.WindowedPositionY));
        if (window is not null) scope.Remember(window.CurrentDisplayBindable);
        try
        {
            var display = window?.CurrentDisplayBindable.Value;
            int? configuredDisplay = !stable && string.Equals(values.GetValueOrDefault("LastDisplayDevice"), "Primary", StringComparison.OrdinalIgnoreCase)
                ? -1 : integer(stable ? "Display" : "LastDisplayDevice");
            if (configuredDisplay is { } index && window is not null)
            {
                // Stable uses 1-based monitor selection (0 means primary); framework uses 0-based.
                display = stable && index == 0 || !stable && index == -1 ? window.PrimaryDisplay
                    : window.Displays.FirstOrDefault(d => d.Index == (stable ? index - 1 : index)) ?? display;
                if (display is not null) window.CurrentDisplayBindable.Value = display;
            }
            Size desktop = display?.Bounds.Size ?? new Size(1920, 1080);
            if (WindowSize is { } windowSize) framework.SetValue(FrameworkSetting.WindowedSize, windowSize);
            if (FullscreenSize is { } fullscreenSize && !Letterboxing) framework.SetValue(FrameworkSetting.SizeFullscreen, fullscreenSize);
            foreach (var setting in new[] { FrameworkSetting.WindowedPositionX, FrameworkSetting.WindowedPositionY })
                if (!stable && number(setting.ToString()) is { } position && position is >= -.5 and <= 1.5)
                    framework.SetValue(setting, position);
            if (Mode(desktop) is { } mode && (window is null || window.SupportedWindowModes.Contains(mode)))
                framework.SetValue(FrameworkSetting.WindowMode, mode);
            if (!stable)
            {
                if (enumeration<FrameSync>("FrameSync") is { } sync) scope.Set(framework.GetBindable<FrameSync>(FrameworkSetting.FrameSync), sync);
                foreach (var setting in new[] { FrameworkSetting.MinimiseOnFocusLossInFullscreen })
                    if (flag(setting.ToString()) is { } enabled) scope.Set(framework.GetBindable<bool>(setting), enabled);
            }
            if (stable)
            {
                scope.Set(game.GetBindable<ScalingMode>(OsuSetting.Scaling), ScalingMode.Off);
                if (Letterboxing && FullscreenSize is { } area)
                {
                    scope.Set(game.GetBindable<ScalingMode>(OsuSetting.Scaling), ScalingMode.Everything);
                    scope.SetExactScale(game.GetBindable<float>(OsuSetting.ScalingSizeX), Math.Clamp((float)area.Width / desktop.Width, .01f, 1));
                    scope.SetExactScale(game.GetBindable<float>(OsuSetting.ScalingSizeY), Math.Clamp((float)area.Height / desktop.Height, .01f, 1));
                    scope.SetExactScale(game.GetBindable<float>(OsuSetting.ScalingPositionX), letterboxPosition("LetterboxPositionX"));
                    scope.SetExactScale(game.GetBindable<float>(OsuSetting.ScalingPositionY), letterboxPosition("LetterboxPositionY"));
                    scope.Set(game.GetBindable<float>(OsuSetting.ScalingBackgroundDim), 1);
                }
            }
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }

    private float letterboxPosition(string key) => (float)((Math.Clamp(number(key) ?? 0, -100, 100) + 100) / 200);
    private double? number(string key) => double.TryParse(values.GetValueOrDefault(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
    private int? integer(string key) => int.TryParse(values.GetValueOrDefault(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    private bool? flag(string key) => values.GetValueOrDefault(key) is { } raw && (raw is "0" or "1" || bool.TryParse(raw, out _)) ? TrainerOsuSettingsReader.IsEnabled(raw) : null;
    private T? enumeration<T>(string key) where T : struct, Enum => Enum.TryParse<T>(values.GetValueOrDefault(key), true, out var value) && Enum.IsDefined(value) ? value : null;
    private static Size? validated(int? width, int? height) => width is >= 320 and <= 16384 && height is >= 240 and <= 16384 ? new Size(width.Value, height.Value) : null;
    private Size? pair(string width, string height) => validated(integer(width), integer(height));
    private Size? size(string key)
    {
        var parts = values.GetValueOrDefault(key)?.Split('x', StringSplitOptions.TrimEntries);
        return parts?.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height) ? validated(width, height) : null;
    }
}
