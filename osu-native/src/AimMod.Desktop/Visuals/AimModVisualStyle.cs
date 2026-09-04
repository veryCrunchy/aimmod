using osu.Framework.Graphics;
using osu.Game.Graphics;

namespace AimMod.Desktop.Visuals;

public static class AimModVisualStyle
{
    private static readonly OsuColour osuColours = new();

    public const float ControlHeight = 40;
    public const float CompactControlHeight = 35;
    public const float ControlRadius = 5;
    public const float CardRadius = 8;
    public const float RelatedSpacing = 5;
    public const float RowSpacing = 10;
    public const float SectionSpacing = 20;
    public const double FastTransition = 100;
    public const double HoverTransition = 200;
    public const double SettleTransition = 800;

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
