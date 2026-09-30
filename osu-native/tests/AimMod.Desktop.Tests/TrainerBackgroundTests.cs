using AimMod.Desktop.Trainers;
using NUnit.Framework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AimMod.Desktop.Tests;

public sealed class TrainerBackgroundTests
{
    [Test]
    public void ReadsSourceImageAndRejectsMissingCorruptOrEscapingFiles()
    {
        string root = Directory.CreateTempSubdirectory("aimmod-background-").FullName;
        try
        {
            string mapRoot = Directory.CreateDirectory(Path.Combine(root, "map")).FullName;
            using (var image = new Image<Rgba32>(32, 32)) image.SaveAsPng(Path.Combine(root, "outside.png"));
            File.Copy(Path.Combine(root, "outside.png"), Path.Combine(mapRoot, "background.png"));
            File.WriteAllText(Path.Combine(mapRoot, "broken.png"), "not an image");
            Assert.That(TrainerBackground.Read(mapRoot, "background.png"), Is.Not.Empty);
            Assert.That(TrainerBackground.Read(mapRoot, "../outside.png"), Is.Null);
            Assert.That(TrainerBackground.Read(mapRoot, "missing.png"), Is.Null);
            Assert.That(TrainerBackground.Read(mapRoot, "broken.png"), Is.Null);
        }
        finally { Directory.Delete(root, true); }
    }
}
