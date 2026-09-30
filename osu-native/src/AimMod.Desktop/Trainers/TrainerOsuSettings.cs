using System.Globalization;
using osu.Framework.Input.Bindings;
using osu.Game.Rulesets.Osu;
using osuTK.Input;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Trainers;

public sealed record TrainerOsuSettings(string Keys, int OffsetMs, bool MouseButtons, string Source, TrainerInputSettings? Input = null, IReadOnlyList<ExternalTrainerKeyBinding>? Bindings = null);

public static class TrainerOsuSettingsReader
{
    public static TrainerOsuSettings Stable(string contents)
    {
        var values = StableValues(contents);
        string left = key(values.GetValueOrDefault("keyOsuLeft", "Z")) ?? "Z";
        string right = key(values.GetValueOrDefault("keyOsuRight", "X")) ?? "X";
        int offset = int.TryParse(values.GetValueOrDefault("Offset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int o) ? Math.Clamp(o, -500, 500) : 0;
        bool mouse = !IsEnabled(values.GetValueOrDefault("MouseDisableButtons"));
        return new($"{left} / {right}", offset, mouse, "osu!stable", TrainerInputSettings.Stable(contents));
    }

    internal static Dictionary<string, string> StableValues(string contents) => contents.TrimStart('\uFEFF').Split('\n')
        .Select(line => line.Split('=', 2)).Where(pair => pair.Length == 2)
        .GroupBy(pair => pair[0].Trim(), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);

    internal static bool IsEnabled(string? value) => value == "1" || bool.TryParse(value, out bool enabled) && enabled;

    public static bool LazerMouseButtons(string contents)
    {
        string? disabled = contents.Split('\n').Select(l => l.Split('=', 2)).Where(p => p.Length == 2
            && p[0].Trim().Equals("MouseDisableButtons", StringComparison.OrdinalIgnoreCase)).LastOrDefault()?[1].Trim();
        return disabled is not ("1" or "true" or "True");
    }

    public static TrainerOsuSettings Lazer(ExternalTrainerSettingsResult result, double offset, bool mouseButtons)
    {
        string keyboard(OsuAction action, string fallback)
        {
            foreach (var binding in result.Bindings.Where(b => b.Action == (int)action))
            {
                try
                {
                    KeyCombination combination = binding.Combination;
                    var keys = combination.Keys.ToArray();
                    if (keys.Length == 1 && key(keys[0].ToString()) is { } valid) return valid;
                }
                catch (Exception e) when (e is FormatException or ArgumentException or OverflowException) { }
            }
            return fallback;
        }
        return new($"{keyboard(OsuAction.LeftButton, "Z")} / {keyboard(OsuAction.RightButton, "X")}",
            (int)Math.Clamp(Math.Round(offset), -500, 500), mouseButtons, "osu!lazer", Bindings: result.Bindings);
    }
    private static string? key(string value)
    {
        // stable stores WinForms key names; lazer uses framework key names.
        if (value.Length == 2 && value[0] == 'D' && char.IsAsciiDigit(value[1])) value = "Number" + value[1];
        if (value.Length == 7 && value.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(value[6])) value = "Keypad" + value[6];
        value = value.ToLowerInvariant() switch { "oemsemicolon" or "oem1" => "Semicolon", "oemquotes" or "oem7" => "Quote", "oemopenbrackets" or "oem4" => "BracketLeft",
            "oemclosebrackets" or "oem6" => "BracketRight", "oemcomma" => "Comma", "oemperiod" => "Period",
            "oemquestion" or "oem2" => "Slash", "oemminus" => "Minus", "oemplus" => "Plus",
            "oempipe" or "oem5" => "BackSlash", "oemtilde" or "oem3" => "Tilde",
            "lcontrolkey" => "ControlLeft", "rcontrolkey" => "ControlRight", "lshiftkey" => "ShiftLeft", "rshiftkey" => "ShiftRight",
            "lmenu" => "AltLeft", "rmenu" => "AltRight", "back" => "BackSpace", "return" => "Enter", "capital" => "CapsLock", _ => value };
        return Enum.TryParse<Key>(value, true, out var parsed) && Enum.IsDefined(parsed)
            && parsed is not (Key.Unknown or Key.Escape) ? parsed.ToString() : null;
    }
}
