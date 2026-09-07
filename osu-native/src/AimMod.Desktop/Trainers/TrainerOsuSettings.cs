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
        var values = contents.Split('\n').Select(l => l.Split('=', 2)).Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);
        string left = key(values.GetValueOrDefault("keyOsuLeft", "Z")) ?? "Z";
        string right = key(values.GetValueOrDefault("keyOsuRight", "X")) ?? "X";
        int offset = int.TryParse(values.GetValueOrDefault("Offset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int o) ? Math.Clamp(o, -500, 500) : 0;
        bool mouse = values.GetValueOrDefault("MouseDisableButtons") is not ("1" or "true" or "True");
        return new($"{left} / {right}", offset, mouse, "osu!stable", TrainerInputSettings.Stable(contents));
    }

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
        value = value switch { "OemSemicolon" => "Semicolon", "OemQuotes" => "Quote", "OemOpenBrackets" => "BracketLeft",
            "OemCloseBrackets" => "BracketRight", "Oemcomma" => "Comma", "OemPeriod" => "Period",
            "OemQuestion" => "Slash", "OemMinus" => "Minus", "Oemplus" => "Plus", _ => value };
        return Enum.TryParse<Key>(value, true, out var parsed) && Enum.IsDefined(parsed)
            && parsed is not (Key.Unknown or Key.Escape) ? parsed.ToString() : null;
    }
}
