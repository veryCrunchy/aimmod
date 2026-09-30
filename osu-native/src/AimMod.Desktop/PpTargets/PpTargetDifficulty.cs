using System.Text.Json;

namespace AimMod.Desktop.PpTargets;

/// <summary>Effective OD and AR as perceived in play: difficulty-adjusting mods first, then the clock rate.</summary>
public static class PpTargetDifficulty
{
    public static double? ClockRate(IEnumerable<string> mods, string? modsJson)
    {
        if (customSpeed(modsJson) is { } custom) return custom;
        var set = PpTargetMods.Normalise(mods);
        return set.Contains("DT") || set.Contains("NC") ? 1.5 : set.Contains("HT") ? .75 : 1;
    }

    public static double? ApproachRate(double? baseValue, IEnumerable<string> mods, double? clockRate)
    {
        if (adjusted(baseValue, mods) is not { } ar || clockRate is not > 0) return null;
        double ms = (ar < 5 ? 1800 - 120 * ar : 1200 - 150 * (ar - 5)) / clockRate.Value;
        return ms > 1200 ? (1800 - ms) / 120 : 5 + (1200 - ms) / 150;
    }

    public static double? OverallDifficulty(double? baseValue, IEnumerable<string> mods, double? clockRate)
    {
        if (adjusted(baseValue, mods) is not { } od || clockRate is not > 0) return null;
        return (80 - (80 - 6 * od) / clockRate.Value) / 6;
    }

    private static double? adjusted(double? value, IEnumerable<string> mods)
    {
        if (value is not { } raw || !double.IsFinite(raw) || raw is < 0 or > 11) return null;
        var set = PpTargetMods.Normalise(mods);
        if (set.Contains("HR")) raw = Math.Min(10, raw * 1.4);
        if (set.Contains("EZ")) raw *= .5;
        return raw;
    }

    private static double? customSpeed(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 16_384) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var mod in document.RootElement.EnumerateArray())
                if (mod.ValueKind == JsonValueKind.Object && mod.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object
                    && settings.TryGetProperty("speed_change", out var speed) && speed.TryGetDouble(out double value) && value is > .25 and < 4)
                    return value;
        }
        catch (JsonException) { }
        return null;
    }
}
