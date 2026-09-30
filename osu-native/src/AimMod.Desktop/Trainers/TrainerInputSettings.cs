using System.Globalization;
using System.Text.Json;
using osu.Framework.Bindables;
using osu.Framework.Input.Handlers;
using osu.Framework.Input.Handlers.Mouse;
using osu.Framework.Input.Handlers.Tablet;
using osuTK;

namespace AimMod.Desktop.Trainers;

public sealed record TrainerTabletSettings(bool Enabled, Vector2 AreaSize, Vector2 AreaOffset,
    Vector2 OutputSize, Vector2 OutputOffset, float Rotation, float Pressure);

public sealed record TrainerInputSettings(double Sensitivity = 1, bool RelativeMouse = false, TrainerTabletSettings? Tablet = null)
{
    public static TrainerInputSettings Stable(string contents)
    {
        var values = TrainerOsuSettingsReader.StableValues(contents);
        // stable writes MouseSpeed. Keep the older alias for imported configurations.
        string? speed = values.GetValueOrDefault("MouseSpeed") ?? values.GetValueOrDefault("MouseSensitivity");
        double sensitivity = double.TryParse(speed, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n) ? Math.Clamp(n, .1, 10) : 1;
        return new(sensitivity, TrainerOsuSettingsReader.IsEnabled(values.GetValueOrDefault("RawInput")));
    }

    public static TrainerInputSettings Lazer(string contents)
    {
        using var json = JsonDocument.Parse(contents);
        double sensitivity = 1; bool relative = false; TrainerTabletSettings? tablet = null;
        if (!json.RootElement.TryGetProperty("InputHandlers", out var handlers) || handlers.ValueKind != JsonValueKind.Array) return new();
        foreach (var h in handlers.EnumerateArray())
        {
            string type = h.TryGetProperty("$type", out var t) ? t.GetString() ?? "" : "";
            // Whitelist fields; never instantiate types from imported JSON.
            if (type.Split(',')[0].EndsWith("MouseHandler", StringComparison.Ordinal))
            { sensitivity = Math.Clamp(number(h, "Sensitivity", 1), .1, 10); relative = boolean(h, "UseRelativeMode", true); }
            if (type.Split(',')[0].EndsWith("OpenTabletDriverHandler", StringComparison.Ordinal))
                tablet = new(boolean(h, "Enabled", false), vector(h, "AreaSize", Vector2.Zero), vector(h, "AreaOffset", Vector2.Zero),
                    vector(h, "OutputAreaSize", Vector2.One), vector(h, "OutputAreaOffset", new(.5f)),
                    (float)number(h, "Rotation", 0), (float)Math.Clamp(number(h, "PressureThreshold", 0), 0, 1));
        }
        return new(sensitivity, relative, tablet);
    }

    private static double number(JsonElement h, string name, double fallback) => h.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : fallback;
    private static bool boolean(JsonElement h, string name, bool fallback) => h.TryGetProperty(name, out var v)
        && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
    private static Vector2 vector(JsonElement h, string name, Vector2 fallback)
    {
        if (!h.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Object) return new((float)number(v, "x", number(v, "X", fallback.X)), (float)number(v, "y", number(v, "Y", fallback.Y)));
        // osu-framework serialises Vector2 as an invariant "X,Y" string.
        if (v.ValueKind == JsonValueKind.String && v.GetString()?.Split(',') is { Length: 2 } pair
            && float.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            && float.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
            && float.IsFinite(x) && float.IsFinite(y)) return new(x, y);
        return fallback;
    }

    public IDisposable Apply(IEnumerable<InputHandler> handlers)
    {
        var scope = new InputScope();
        foreach (var handler in handlers)
        {
            if (handler is MouseHandler mouse)
            { scope.Set(mouse.Sensitivity, Sensitivity); scope.Set(mouse.UseRelativeMode, RelativeMouse); }
            if (handler is ITabletHandler device)
            {
                // stable and externally driven tablets use the existing OS mapping.
                var mapping = Tablet;
                if (mapping is not null)
                {
                    scope.Set(device.AreaSize, mapping.AreaSize); scope.Set(device.AreaOffset, mapping.AreaOffset);
                    scope.Set(device.OutputAreaSize, mapping.OutputSize); scope.Set(device.OutputAreaOffset, mapping.OutputOffset);
                    scope.Set(device.Rotation, mapping.Rotation); scope.Set(device.PressureThreshold, mapping.Pressure);
                }
                scope.Set(device.Enabled, mapping?.Enabled ?? false);
            }
        }
        return scope;
    }

    private sealed class InputScope : IDisposable
    {
        private readonly List<Action> restore = [];
        public void Set<T>(Bindable<T> bindable, T value)
        { T previous = bindable.Value; restore.Add(() => bindable.Value = previous); bindable.Value = value; }
        public void Dispose() { foreach (var undo in restore.AsEnumerable().Reverse()) undo(); restore.Clear(); }
    }
}
