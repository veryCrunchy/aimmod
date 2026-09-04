using System.Diagnostics;
using AimMod.Desktop.Skins.Online;
using AimMod.Osu.Runtime;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class OsuSkinArchiveDestinationServiceTests
{
    private string root = null!;
    private string executable = null!;

    [SetUp]
    public void SetUp()
    {
        root = Directory.CreateTempSubdirectory("aimmod-skin-destination-").FullName;
        executable = Path.Combine(root, "osu!.exe");
        File.WriteAllText(executable, "fixture");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(root, recursive: true);

    [Test]
    public async Task StablePreferenceCopiesArchiveAndLaunchesSelectedClient()
    {
        string source = Path.Combine(root, "source.osk");
        await File.WriteAllTextAsync(source, "validated fixture");
        ProcessStartInfo? observed = null;
        var service = new OsuSkinArchiveDestinationService(
            () => OsuClientDestination.Stable,
            Path.Combine(root, "handoff"),
            executable,
            new LazerExecutableLocator(),
            (command, _, _) =>
            {
                observed = command;
                return Task.FromResult(new OsuSkinArchiveDestinationService.LaunchOutcome(true, false, 0));
            });

        OnlineSkinImportResult result = await service.ImportAsync(source);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(observed?.FileName, Is.EqualTo(executable));
            Assert.That(observed?.ArgumentList, Has.Count.EqualTo(1));
            Assert.That(observed?.ArgumentList[0], Does.EndWith(".osk"));
            Assert.That(File.Exists(observed?.ArgumentList[0]), Is.True);
        });
    }

    [Test]
    public async Task MissingSelectedClientDoesNotAttemptLaunch()
    {
        string source = Path.Combine(root, "source.osk");
        await File.WriteAllTextAsync(source, "validated fixture");
        bool launched = false;
        var service = new OsuSkinArchiveDestinationService(
            () => OsuClientDestination.Stable,
            Path.Combine(root, "handoff"),
            stableExecutable: null,
            new LazerExecutableLocator(),
            (_, _, _) =>
            {
                launched = true;
                return Task.FromResult(new OsuSkinArchiveDestinationService.LaunchOutcome(true, false, 0));
            });

        OnlineSkinImportResult result = await service.ImportAsync(source);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(launched, Is.False);
        });
    }
}
