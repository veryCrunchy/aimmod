using osu.Game.Rulesets.Osu.Configuration;
using osu.Game.Rulesets.UI;

namespace AimMod.Desktop.Trainers;

public sealed class TrainerRulesetPreferences(IReadOnlyDictionary<string, string> values)
{
    public static TrainerRulesetPreferences Stable(string contents)
    {
        var source = TrainerOsuSettingsReader.StableValues(contents);
        var mapped = new Dictionary<string, string>();
        if (source.TryGetValue("SnakingSliders", out var snake)) mapped["SnakingInSliders"] = snake;
        if (source.TryGetValue("CursorRipple", out var ripple)) mapped["ShowCursorRipples"] = ripple;
        // Stable slider bodies stay visible until their tail.
        mapped["SnakingOutSliders"] = "False";
        return new(mapped);
    }

    public IDisposable Apply(OsuRulesetConfigManager config)
    {
        var scope = new TrainerPreferenceScope();
        foreach (var setting in new[] { OsuRulesetSetting.SnakingInSliders, OsuRulesetSetting.SnakingOutSliders,
            OsuRulesetSetting.HitAnimations, OsuRulesetSetting.ShowCursorTrail, OsuRulesetSetting.ShowCursorRipples })
            if (values.GetValueOrDefault(setting.ToString()) is { } raw && (raw is "0" or "1" || bool.TryParse(raw, out _)))
                scope.Set(config.GetBindable<bool>(setting), TrainerOsuSettingsReader.IsEnabled(raw));
        if (Enum.TryParse<PlayfieldBorderStyle>(values.GetValueOrDefault("PlayfieldBorderStyle"), out var border) && Enum.IsDefined(border))
            scope.Set(config.GetBindable<PlayfieldBorderStyle>(OsuRulesetSetting.PlayfieldBorderStyle), border);
        return scope;
    }
}
