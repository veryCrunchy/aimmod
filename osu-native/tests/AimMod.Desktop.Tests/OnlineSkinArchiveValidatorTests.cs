using System.IO.Compression;
using System.Text;
using AimMod.Desktop.Skins.Online;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class OnlineSkinArchiveValidatorTests
{
    private string archivePath = null!;
    private const string validIni = "[General]\nName: Fixture\nAuthor: Mapper\n";

    [SetUp]
    public void SetUp() => archivePath = Path.Combine(Path.GetTempPath(), $"aimmod-skin-validation-{Guid.NewGuid():N}.osk");

    [TearDown]
    public void TearDown() => File.Delete(archivePath);

    [TestCase("exe")]
    [TestCase("DLL")]
    [TestCase("bat")]
    [TestCase("cmd")]
    [TestCase("ps1")]
    [TestCase("psm1")]
    [TestCase("psd1")]
    [TestCase("vbs")]
    [TestCase("vbe")]
    [TestCase("js")]
    [TestCase("jse")]
    [TestCase("mjs")]
    [TestCase("cjs")]
    [TestCase("msi")]
    [TestCase("msp")]
    [TestCase("scr")]
    [TestCase("com")]
    [TestCase("lnk")]
    [TestCase("hta")]
    [TestCase("cpl")]
    [TestCase("wsf")]
    [TestCase("wsh")]
    [TestCase("reg")]
    [TestCase("url")]
    [TestCase("inf")]
    [TestCase("msix")]
    [TestCase("appx")]
    [TestCase("sh")]
    [TestCase("command")]
    [TestCase("desktop")]
    [TestCase("py")]
    [TestCase("jar")]
    public async Task RejectsExecutableAndScriptPayloadsInOptionalDirectories(string extension)
    {
        createArchive(("skin.ini", validIni), ($"Extras\\Tools\\preview.png.{extension}", "payload"));
        await assertRejected("unsafe_payload");
    }

    [TestCase("../outside.png")]
    [TestCase("Extras/../outside.png")]
    [TestCase("Extras\\..\\outside.png")]
    [TestCase("/absolute.png")]
    [TestCase("\\\\server\\share\\file.png")]
    [TestCase("C:\\file.png")]
    [TestCase("file.png:payload.exe")]
    [TestCase("file.png.")]
    [TestCase("file.png ")]
    [TestCase("Extras./file.png")]
    [TestCase("Extras /file.png")]
    [TestCase("./file.png")]
    [TestCase("Extras//file.png")]
    [TestCase("Extras//")]
    [TestCase("CON")]
    [TestCase("extras/nul.png")]
    [TestCase("AUX.txt")]
    [TestCase("PRN/skin.png")]
    [TestCase("COM1.png")]
    [TestCase("lpt9.txt")]
    [TestCase("COM\u00b9.png")]
    [TestCase("LPT\u00b2.png")]
    [TestCase("CON .png")]
    [TestCase("CONIN$")]
    [TestCase("CONOUT$.txt")]
    [TestCase("bad?.png")]
    [TestCase("bad*.png")]
    [TestCase("bad|.png")]
    [TestCase("bad<.png")]
    [TestCase("bad>.png")]
    [TestCase("bad\".png")]
    [TestCase("bad\0.png")]
    [TestCase("bad\t.png")]
    public async Task RejectsUnsafePaths(string entryPath)
    {
        createArchive(("skin.ini", validIni), (entryPath, ""));
        await assertRejected("unsafe_entry");
    }

    [TestCase("skin.ini", "skin.ini")]
    [TestCase("skin.ini", "SKIN.INI")]
    [TestCase("Extras/cursor.png", "extras\\CURSOR.PNG")]
    [TestCase("Extras/", "extras\\")]
    [TestCase("Extras", "EXTRAS/")]
    public async Task RejectsDuplicateNormalizedPaths(string first, string second)
    {
        createArchive((first, validIni), (second, validIni));
        await assertRejected("duplicate_entry");
    }

    [TestCase("")]
    [TestCase(" \n\t")]
    [TestCase("// [General]\n// Name: Fake")]
    [TestCase("[General]")]
    [TestCase("[General]\nName:")]
    [TestCase("[General]\nName: // empty")]
    [TestCase("Name: Fixture")]
    [TestCase("[Unrelated]\nName: Fixture")]
    [TestCase("<html>download</html>")]
    [TestCase("[General]\nName: Fixture\n\0binary")]
    [TestCase("[General]\n[Unrelated]\nName: Fixture")]
    public async Task RejectsImplausibleSkinIni(string content)
    {
        createArchive(("skin.ini", content), ("readme.txt", "Not a skin"));
        await assertRejected("skin_ini_invalid");
    }

    [TestCase("[General]\nName: Fixture")]
    [TestCase("// Comment\n[general] // comment\nName: Fixture\n")]
    [TestCase("[Colours]\nCombo1: 255,128,0")]
    [TestCase("[Fonts]\nHitCirclePrefix: default")]
    [TestCase("[Mania]\nKeys: 4\n[Mania]\nKeys: 7")]
    [TestCase("[CatchTheBeat]\nHyperDash: 255,0,0")]
    public async Task AcceptsKnownSkinSections(string content)
    {
        createArchive(("skin.ini", content));
        Assert.That((await new OnlineSkinArchiveValidator().ValidateAsync(archivePath)).IsValid, Is.True);
    }

    [TestCase("Skin/")]
    [TestCase("Skin\\")]
    [TestCase("")]
    public async Task AcceptsNestedRootsAndCommonSkinExtras(string root)
    {
        createArchive(
            (root + "SKIN.INI", validIni),
            (root + "Extras/", ""),
            (root + "Extras/Source.psd", "source"),
            (root + "Extras/Artwork.svg", "<svg/>"),
            (root + "Extras/Font.ttf", "font"),
            (root + "Extras/Font.otf", "font"),
            (root + "Extras/Font.woff2", "font"),
            (root + "Credits.txt", "credits"),
            (root + "hitcircle@2x.png", "image"),
            (root + "normal-hitnormal.wav", "audio"),
            (root + "COM10.png", "image"));

        var result = await new OnlineSkinArchiveValidator().ValidateAsync(archivePath);
        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.True, result.Message);
            Assert.That(result.EntryCount, Is.EqualTo(11));
            Assert.That(result.ArchiveBytes, Is.EqualTo(new FileInfo(archivePath).Length));
            Assert.That(result.ExpandedBytes, Is.GreaterThan(0));
            Assert.That(result.Sha256, Does.Match("^[0-9a-f]{64}$"));
        });
    }

    [TestCase("skin.ini/")]
    [TestCase("readme.txt")]
    public async Task RejectsMissingSkinIniFile(string entryPath)
    {
        createArchive((entryPath, ""));
        await assertRejected("skin_ini_missing");
    }

    [Test]
    public async Task ValidatesEverySkinIni()
    {
        createArchive(("skin.ini", validIni), ("Extras/skin.ini", ""));
        await assertRejected("skin_ini_invalid");
    }

    [Test]
    public async Task RejectsOversizedSecondarySkinIni()
    {
        createArchive(("skin.ini", validIni), ("Extras/skin.ini", new string('a', 1024 * 1024 + 1)));
        await assertRejected("skin_ini_size");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AcceptsBomEncodedSkinIni(bool utf16)
    {
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("skin.ini").Open(), utf16 ? Encoding.Unicode : new UTF8Encoding(true)))
            writer.Write(validIni);
        Assert.That((await new OnlineSkinArchiveValidator().ValidateAsync(archivePath)).IsValid, Is.True);
    }

    [Test]
    public async Task RejectsSymbolicLink()
    {
        createArchive(("skin.ini", validIni));
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
            archive.CreateEntry("link.png").ExternalAttributes = unchecked((int)0xA1FF0000);
        await assertRejected("unsafe_entry");
    }

    private void createArchive(params (string Path, string Content)[] entries)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry.Path, CompressionLevel.NoCompression).Open());
            writer.Write(entry.Content);
        }
    }

    private async Task assertRejected(string errorCode)
    {
        var result = await new OnlineSkinArchiveValidator().ValidateAsync(archivePath);
        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.ErrorCode, Is.EqualTo(errorCode));
            Assert.That(result.Sha256, Is.Null);
        });
    }
}
