using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AimMod.InGame;

/// <summary>The KovaaK's settings an overlay settings card shows. Read-only, display only.</summary>
sealed record KovaaksSettingsView(bool Available, int? Dpi = null, double? Sens = null, string? SensScale = null, double? Cm360 = null,
    double? Fov = null, string? FovScale = null, string? Theme = null, string? Crosshair = null, double? CrosshairScale = null,
    string? CrosshairColor = null, string? CrosshairImage = null, string[]? HitSounds = null)
{
    public static readonly KovaaksSettingsView Unavailable = new(false);
}

/// <summary>
/// Reads KovaaK's own settings files from the game folder (FPSAimTrainer):
/// Saved/SaveGames/PrimaryUserSettings.json (DPI, sensitivity, FOV, theme),
/// Saved/SaveGames/weaponsettings.ini (crosshair, hit sounds, weapon overrides) and
/// Saved/SaveGames/FovSensConfig.json (sensitivity scale formulas, for cm/360).
/// Never writes, never follows files outside those paths.
/// </summary>
sealed class KovaaksSettings(string? gameFolder, Func<DateTime>? clock = null)
{
    readonly object gate = new();
    KovaaksSettingsView cached = KovaaksSettingsView.Unavailable;
    string stamp = "";
    DateTime checkedAt;
    public string? GameFolder => gameFolder;
    string Saved(string name) => Path.Combine(gameFolder!, "Saved", "SaveGames", name);

    public KovaaksSettingsView Read()
    {
        if (string.IsNullOrEmpty(gameFolder)) return KovaaksSettingsView.Unavailable;
        lock (gate)
        {
            var now = clock?.Invoke() ?? DateTime.UtcNow;
            if (now - checkedAt < TimeSpan.FromSeconds(2)) return cached;
            checkedAt = now;
            try
            {
                var files = new[] { Saved("PrimaryUserSettings.json"), Saved("weaponsettings.ini"), Saved("FovSensConfig.json") };
                var next = string.Join('|', files.Select(f => File.Exists(f) ? new FileInfo(f).Length + ":" + File.GetLastWriteTimeUtc(f).Ticks : "-"));
                if (next == stamp) return cached;
                stamp = next;
                cached = Parse(Text(files[0], 1 << 20), Text(files[1], 1 << 18), Text(files[2], 1 << 20), name => CrosshairExists(name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { cached = KovaaksSettingsView.Unavailable; stamp = ""; }
            return cached;
        }
    }

    static string? Text(string path, int limit)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > limit) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // A crosshair image name KovaaK's would load: a plain .png file name in <game>/crosshairs.
    internal static bool SafeImageName(string? name) => name is { Length: > 4 and <= 128 } && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Contains("..") && name.Trim() == name;
    bool CrosshairExists(string name) => SafeImageName(name) && new FileInfo(Path.Combine(gameFolder!, "crosshairs", name)) is { Exists: true, Length: > 0 and <= 2 * 1024 * 1024 };

    /// <summary>The current crosshair image, only when it is the one the settings name.</summary>
    public string? CrosshairPath(string? requested)
    {
        var view = Read();
        if (view.CrosshairImage is null || requested is null || !string.Equals(requested, view.CrosshairImage, StringComparison.Ordinal)) return null;
        var path = Path.Combine(gameFolder!, "crosshairs", view.CrosshairImage);
        return CrosshairExists(view.CrosshairImage) ? path : null;
    }

    static readonly Regex InchesFormula = new(@"^\s*360\s*/\s*\(\s*Inches\s*\*\s*([0-9]*\.?[0-9]+)\s*\*\s*DPI\s*\)\s*$", RegexOptions.CultureInvariant);
    static string? Clean(string? s, int max) { if (s is null) return null; s = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim(); return s.Length == 0 ? null : s.Length > max ? s[..max] : s; }

    internal static KovaaksSettingsView Parse(string? primary, string? weapons, string? scales, Func<string, bool>? imageExists = null)
    {
        if (primary is null && weapons is null) return KovaaksSettingsView.Unavailable;
        int? dpi = null; double? sens = null, fov = null; string? sensScale = null, fovScale = null, theme = null;
        if (primary is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(primary.TrimStart('﻿'), new JsonDocumentOptions { MaxDepth = 16 });
                var root = doc.RootElement;
                JsonElement Group(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var g) && g.ValueKind == JsonValueKind.Object ? g : default;
                double? Num(string group, string key) => Group(group) is { ValueKind: JsonValueKind.Object } g && g.TryGetProperty(key, out var v) && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
                string? Str(string key) => Group("stringSettings") is { ValueKind: JsonValueKind.Object } g && g.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? Clean(v.GetString(), 64) : null;
                if (Num("integerSettings", "EIntegerSettingId::DPI") is double d && d is > 0 and <= 100000) dpi = (int)Math.Round(d);
                if (Num("floatSettings", "EFloatSettingId::XSens") is double x && x is > 0 and < 100000) sens = x;
                if (Num("floatSettings", "EFloatSettingId::FOV") is double f && f is > 0 and < 180) fov = f;
                sensScale = Str("EStringSettingId::SensScaleString"); fovScale = Str("EStringSettingId::FOVScaleString"); theme = Str("EStringSettingId::CurrentThemeName");
            }
            catch (JsonException) { }
        }
        var ini = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (weapons is not null)
            foreach (var raw in weapons.TrimStart('﻿').Split('\n'))
            {
                var line = raw.TrimEnd('\r'); var eq = line.IndexOf('=');
                if (eq > 0 && !line.StartsWith('[') && !line.StartsWith(';')) ini.TryAdd(line[..eq].Trim(), line[(eq + 1)..].Trim());
            }
        double? IniNum(string key) => ini.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
        bool IniTrue(string key) => ini.TryGetValue(key, out var v) && v.Equals("true", StringComparison.OrdinalIgnoreCase);
        // A weapon that overrides the global sensitivity or FOV is what the player aims with.
        if (IniTrue("OverrideSens") && IniNum("HorizontalSens") is double ws && ws > 0) { sens = ws; sensScale = Clean(ini.GetValueOrDefault("SensScale"), 64) ?? sensScale; }
        if (IniTrue("OverrideFOV") && IniNum("FOV") is double wf && wf is > 0 and < 180) { fov = wf; fovScale = Clean(ini.GetValueOrDefault("FOVScale"), 64) ?? fovScale; }
        var crosshair = Clean(ini.GetValueOrDefault("CrosshairFile"), 128);
        double? crossScale = IniNum("CrosshairScale") is double cs && cs is > 0 and <= 100 ? cs : null;
        string? color = null;
        if (ini.TryGetValue("CrosshairColor", out var colorText))
        {
            var m = Regex.Match(colorText, @"X=([0-9.]+)\s+Y=([0-9.]+)\s+Z=([0-9.]+)", RegexOptions.CultureInvariant);
            if (m.Success)
            {
                static int C(string s) => (int)Math.Round(Math.Clamp(double.Parse(s, CultureInfo.InvariantCulture), 0, 1) * 255);
                color = "#" + C(m.Groups[1].Value).ToString("x2") + C(m.Groups[2].Value).ToString("x2") + C(m.Groups[3].Value).ToString("x2");
            }
        }
        var sounds = new List<string>();
        foreach (var key in new[] { "BodyHitSound", "HeadHitSound" })
            if (ini.TryGetValue(key, out var list))
                foreach (var part in list.Split(';'))
                    if (Clean(part, 64) is { } s && !s.Equals("None", StringComparison.OrdinalIgnoreCase) && !sounds.Contains(s, StringComparer.OrdinalIgnoreCase) && sounds.Count < 4) sounds.Add(s);
        double? cm = null;
        if (sens is double sv && sensScale is not null)
        {
            if (sensScale.Equals("cm/360", StringComparison.OrdinalIgnoreCase)) cm = sv;
            else if (sensScale.Equals("in/360", StringComparison.OrdinalIgnoreCase)) cm = sv * 2.54;
            else if (dpi is int dv && scales is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(scales.TrimStart('﻿'), new JsonDocumentOptions { MaxDepth = 8 });
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        foreach (var entry in doc.RootElement.EnumerateArray())
                        {
                            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("ScaleName", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != sensScale) continue;
                            if (entry.TryGetProperty("Sens", out var s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty("InchesFormula", out var formula) && formula.ValueKind == JsonValueKind.String
                                && InchesFormula.Match(formula.GetString()!) is { Success: true } fm && double.TryParse(fm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var k) && k > 0)
                                cm = 360 / (sv * k * dv) * 2.54;
                            break;
                        }
                }
                catch (JsonException) { }
            }
            if (cm is double c && (!double.IsFinite(c) || c <= 0 || c > 100000)) cm = null;
        }
        var image = crosshair is not null && imageExists?.Invoke(crosshair) == true ? crosshair : null;
        return new(true, dpi, sens, sensScale, cm is null ? null : Math.Round(cm.Value, 2), fov, fovScale, theme, crosshair, crossScale, color, image, sounds.ToArray());
    }
}
