using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace AimMod.Osu.Runtime.Tests;

[TestFixture]
public sealed class LazerBeatmapInstallServiceTests
{
    private string temporaryDirectory = null!;
    private string homeDirectory = null!;
    private string dataHome = null!;
    private string launcher = null!;
    private string desktopLauncher = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-lazer-handoff-{Guid.NewGuid():N}");
        homeDirectory = Path.Combine(temporaryDirectory, "home");
        dataHome = Path.Combine(temporaryDirectory, "data");
        launcher = Path.Combine(temporaryDirectory, "osu launcher");
        desktopLauncher = launcher.Replace('\\', '/');
        Directory.CreateDirectory(homeDirectory);
        Directory.CreateDirectory(Path.Combine(dataHome, "applications"));
        File.WriteAllText(launcher, "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, true);
    }

    [Test]
    public void FindsOsuDesktopEntryAndPreservesFlatpakFileForwardingSlot()
    {
        writeDesktopEntry($"\"{desktopLauncher}\" --flag @@u %U @@");
        var locator = createLocator();

        LazerLaunchCommand? command = locator.Find();

        Assert.That(command, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(command!.ExecutablePath, Is.EqualTo(launcher));
            Assert.That(command.ArgumentsBeforeArchive, Is.EqualTo(new[] { "--flag", "@@u" }));
            Assert.That(command.ArgumentsAfterArchive, Is.EqualTo(new[] { "@@" }));
            Assert.That(command.Source, Is.EqualTo("desktop entry osu.desktop"));
        });
    }

    [Test]
    public void IgnoresAnUnknownDesktopIdEvenWhenItClaimsToBeOsu()
    {
        writeDesktopEntry($"\"{desktopLauncher}\" %u", "not-really-osu.desktop");

        Assert.That(createLocator().Find(), Is.Null);
    }

    [TestCase("Type=Link\nMimeType=application/x-osu-beatmap-archive;")]
    [TestCase("Type=Application\nMimeType=application/octet-stream;")]
    public void RejectsDesktopEntriesWithoutTheApplicationTypeAndBeatmapMime(string identity)
    {
        File.WriteAllText(
            Path.Combine(dataHome, "applications", "osu.desktop"),
            $"[Desktop Entry]\nName=osu!\n{identity}\nExec=\"{desktopLauncher}\" %u\n");

        Assert.That(createLocator().Find(), Is.Null);
    }

    [Test]
    public async Task PreservesOneVerifiedArchiveAndLaunchesItAsAnArgument()
    {
        writeDesktopEntry($"\"{desktopLauncher}\" %u");
        string archivePath = createOsz("source.osz");
        ProcessStartInfo? observed = null;
        var service = new LazerBeatmapInstallService(
            Path.Combine(temporaryDirectory, "handoff"),
            createLocator(),
            (startInfo, _, _) =>
            {
                observed = startInfo;
                return Task.FromResult(new LazerLaunchOutcome(true, true, 0));
            });

        LazerBeatmapArchive archive = await service.PreserveAsync(archivePath, 123);
        LazerBeatmapInstallResult result = await service.InstallAsync(archive);

        Assert.That(result.Status, Is.EqualTo(LazerBeatmapInstallStatus.Sent));
        Assert.That(observed, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(observed!.FileName, Is.EqualTo(launcher));
            Assert.That(observed.UseShellExecute, Is.False);
            Assert.That(observed.ArgumentList, Has.Count.EqualTo(1));
            Assert.That(observed.ArgumentList[0], Does.EndWith($"beatmapset-123-{archive.Id:N}.osz"));
            Assert.That(File.Exists(observed.ArgumentList[0]), Is.True, "The copy must survive long enough for lazer's asynchronous importer.");
        });
    }

    [Test]
    public async Task PreservesAndLaunchesALocallyGeneratedPracticeArchive()
    {
        writeDesktopEntry($"\"{desktopLauncher}\" %u");
        string archivePath = createOsz("practice.osz");
        ProcessStartInfo? observed = null;
        var service = new LazerBeatmapInstallService(
            Path.Combine(temporaryDirectory, "handoff"),
            createLocator(),
            (startInfo, _, _) =>
            {
                observed = startInfo;
                return Task.FromResult(new LazerLaunchOutcome(true, true, 0));
            });

        LazerBeatmapArchive archive = await service.PreserveAsync(archivePath, 0);
        LazerBeatmapInstallResult result = await service.InstallAsync(archive);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(LazerBeatmapInstallStatus.Sent));
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed!.ArgumentList[0], Does.EndWith($"practice-{archive.Id:N}.osz"));
            Assert.That(File.Exists(observed.ArgumentList[0]), Is.True);
        });
    }

    [Test]
    public void RejectsANegativeArchiveIdentity()
    {
        string archivePath = createOsz("source.osz");
        var service = new LazerBeatmapInstallService(
            Path.Combine(temporaryDirectory, "handoff"),
            createLocator(),
            (_, _, _) => Task.FromResult(new LazerLaunchOutcome(true, true, 0)));

        Assert.That(
            async () => await service.PreserveAsync(archivePath, -1),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public async Task ReportsMissingLazerWithoutExecutingTheArchive()
    {
        string archivePath = createOsz("source.osz");
        bool launched = false;
        var locator = new LazerExecutableLocator(homeDirectory, dataHome, string.Empty, string.Empty, OSPlatform.Linux);
        var service = new LazerBeatmapInstallService(
            Path.Combine(temporaryDirectory, "handoff"),
            locator,
            (_, _, _) =>
            {
                launched = true;
                return Task.FromResult(new LazerLaunchOutcome(true, true, 0));
            });
        LazerBeatmapArchive archive = await service.PreserveAsync(archivePath, 123);

        LazerBeatmapInstallResult result = await service.InstallAsync(archive);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(LazerBeatmapInstallStatus.LazerNotFound));
            Assert.That(launched, Is.False);
        });
    }

    [Test]
    public async Task CacheNeverKeepsMoreThanEightArchives()
    {
        string archivePath = createOsz("source.osz");
        var service = new LazerBeatmapInstallService(
            Path.Combine(temporaryDirectory, "handoff"),
            createLocator(),
            (_, _, _) => Task.FromResult(new LazerLaunchOutcome(true, true, 0)));

        for (int id = 1; id <= 9; id++)
            await service.PreserveAsync(archivePath, id);

        Assert.That(Directory.EnumerateFiles(Path.Combine(temporaryDirectory, "handoff"), "*.osz").Count(), Is.EqualTo(8));
    }

    [Test]
    public void RejectsAFileThatIsNotABeatmapArchive()
    {
        string invalid = Path.Combine(temporaryDirectory, "invalid.osz");
        File.WriteAllText(invalid, "not a zip");
        var service = new LazerBeatmapInstallService(
            Path.Combine(temporaryDirectory, "handoff"),
            createLocator(),
            (_, _, _) => Task.FromResult(new LazerLaunchOutcome(true, true, 0)));

        Assert.That(
            async () => await service.PreserveAsync(invalid, 123),
            Throws.TypeOf<InvalidDataException>());
    }

    private LazerExecutableLocator createLocator() =>
        new(homeDirectory, dataHome, string.Empty, string.Empty, OSPlatform.Linux);

    private void writeDesktopEntry(string exec, string filename = "osu.desktop") =>
        File.WriteAllText(
            Path.Combine(dataHome, "applications", filename),
            $"[Desktop Entry]\nType=Application\nName=osu!\nExec={exec}\nMimeType=application/x-osu-beatmap-archive;application/x-osu-replay;\n");

    private string createOsz(string filename)
    {
        string path = Path.Combine(temporaryDirectory, filename);
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        using Stream beatmap = zip.CreateEntry("map.osu").Open();
        beatmap.Write([1, 2, 3]);
        return path;
    }
}
