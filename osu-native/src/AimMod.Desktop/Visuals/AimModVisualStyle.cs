using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics;

namespace AimMod.Desktop.Visuals;

public static class AimModVisualStyle
{
    private static readonly OsuColour osuColours = new();

    public const float SidebarWidth = 184;
    public const float PageInset = 24;
    public static MarginPadding PagePadding => new() { Left = SidebarWidth + PageInset, Right = PageInset, Top = PageInset, Bottom = PageInset };
    public const float ControlHeight = 36;
    public const float CompactControlHeight = 32;
    public const float ControlRadius = 6;
    public const float CardRadius = 8;
    public const float RelatedSpacing = 8;
    public const float RowSpacing = 8;
    public const float SectionSpacing = 24;
    public const double FastTransition = 100;
    public const double HoverTransition = 200;
    public const double SettleTransition = 160;
    public const float CompactSidebarWidth = 64;
    public const float MinReadableFontSize = 11;

    public static MarginPadding PagePaddingFor(float sidebarWidth) =>
        new() { Left = sidebarWidth + PageInset, Right = PageInset, Top = PageInset, Bottom = PageInset };

    public static FontUsage CaptionFont => new(size: MinReadableFontSize);
    public static FontUsage CaptionStrongFont => new(size: MinReadableFontSize, weight: "SemiBold");
    public static FontUsage LabelFont => new(size: MinReadableFontSize, weight: "Bold");
    public static FontUsage BodyFont => new(size: 13);
    public static FontUsage BodyStrongFont => new(size: 13, weight: "SemiBold");
    public static FontUsage TitleFont => new(size: 16, weight: "SemiBold");
    public static FontUsage HeadingFont => new(size: 20, weight: "Bold");

    /// <summary>Raises decorative sizes to the readable minimum without changing larger text.</summary>
    public static float Readable(float size) => Math.Max(size, MinReadableFontSize);

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
