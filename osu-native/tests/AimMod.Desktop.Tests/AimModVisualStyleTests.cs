using AimMod.Desktop.Visuals;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Game.Graphics;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class AimModVisualStyleTests
{
    [TestCase(-1, 0)]
    [TestCase(double.NaN, 0)]
    [TestCase(double.PositiveInfinity, 0)]
    [TestCase(6.42, 6.42)]
    public void StarRatingsAreNormalised(double input, double expected) =>
        Assert.That(AimModVisualStyle.NormaliseStarRating(input), Is.EqualTo(expected));

    [Test]
    public void DifficultyColoursComeFromOsuPalette()
    {
        const double stars = 5.73;
        var osuColours = new OsuColour();
        Colour4 expectedBackground = osuColours.ForStarDifficulty(stars);
        Colour4 expectedText = osuColours.ForStarDifficultyText(stars);

        Assert.Multiple(() =>
        {
            assertColour(AimModVisualStyle.DifficultyColour(stars), expectedBackground);
            assertColour(AimModVisualStyle.DifficultyTextColour(stars), expectedText);
            Assert.That(AimModVisualStyle.FormatStarRating(stars), Is.EqualTo("5.73*"));
        });
    }

    [Test]
    public void BeatmapBannerNormalisesDisplayMetadataWithoutAWindow()
    {
        var banner = new AimModBeatmapBanner(new AimModBeatmapBannerModel(
            "  ",
            "  Camellia  ",
            "  ",
            -2,
            "  mapper  ",
            "  osu!  "));

        Assert.Multiple(() =>
        {
            Assert.That(banner.Model.Title, Is.EqualTo("Untitled beatmap"));
            Assert.That(banner.Model.Artist, Is.EqualTo("Camellia"));
            Assert.That(banner.Model.Difficulty, Is.EqualTo("Unknown difficulty"));
            Assert.That(banner.Model.StarRating, Is.Zero);
            Assert.That(banner.Model.Creator, Is.EqualTo("mapper"));
            Assert.That(banner.Model.Ruleset, Is.EqualTo("osu!"));
        });
    }

    private static void assertColour(Colour4 actual, Colour4 expected)
    {
        Assert.That(actual.R, Is.EqualTo(expected.R).Within(0.000001f));
        Assert.That(actual.G, Is.EqualTo(expected.G).Within(0.000001f));
        Assert.That(actual.B, Is.EqualTo(expected.B).Within(0.000001f));
        Assert.That(actual.A, Is.EqualTo(expected.A).Within(0.000001f));
    }
}
