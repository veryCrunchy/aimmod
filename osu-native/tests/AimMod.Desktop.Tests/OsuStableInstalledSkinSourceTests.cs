using AimMod.Desktop.Skins;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class OsuStableInstalledSkinSourceTests
{
    [Test]
    public async Task ReadsAndSearchesStableSkinFolders()
    {
        string root = Directory.CreateTempSubdirectory("aimmod-stable-skins-").FullName;
        try
        {
            string skin = Directory.CreateDirectory(Path.Combine(root, "folder-name")).FullName;
            File.WriteAllText(Path.Combine(skin, "skin.ini"), "[General]\nName: Clean Skin\nAuthor: Mapper\n");
            File.WriteAllText(Path.Combine(skin, "menu-background.jpg"), "image");
            var source = new OsuStableInstalledSkinSource(root);

            InstalledLazerSkinPage page = await source.SearchAsync("mapper");

            Assert.Multiple(() =>
            {
                Assert.That(page.Total, Is.EqualTo(1));
                Assert.That(page.Items[0].Name, Is.EqualTo("Clean Skin"));
                Assert.That(page.Items[0].Creator, Is.EqualTo("Mapper"));
                Assert.That(page.Items[0].Origin, Is.EqualTo(InstalledSkinOrigin.Stable));
                Assert.That(page.Items[0].HasPreview, Is.True);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
