using osu.Framework.Graphics;
using osu.Game.Graphics;

namespace AimMod.Desktop.Visuals;

public static class AimModVisualStyle
{
    private static readonly OsuColour osuColours = new();

    public static double NormaliseStarRating(double starRating) =>
        double.IsFinite(starRating) ? Math.Max(0, starRating) : 0;

    public static string FormatStarRating(double starRating) =>
        $"{NormaliseStarRating(starRating):0.00}*";

    public static Colour4 DifficultyColour(double starRating) =>
        osuColours.ForStarDifficulty(NormaliseStarRating(starRating));

    public static Colour4 DifficultyTextColour(double starRating) =>
        osuColours.ForStarDifficultyText(NormaliseStarRating(starRating));
}

public enum AimModPillTone
{
    Neutral,
    Accent,
    Info,
    Success,
}

public sealed record AimModBeatmapBannerModel(
    string Title,
    string Artist,
    string Difficulty,
    double StarRating,
    string? Creator = null,
    string? Ruleset = null);
