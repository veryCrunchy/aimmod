using System.ComponentModel;
using System.Diagnostics;
using AimMod.Desktop.PpTargets;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

public sealed class DefenderScanExclusionTests
{
    private static readonly string[] folders = [@"C:\Users\Player\AppData\Roaming\AimMod\cache\pp-target-exact-v2.json.beatmaps", @"C:\Users\Player's PC\AimMod\downloads\pp-target-difficulties"];
    private string state = null!;

    [SetUp]
    public void SetUp() => state = Path.Combine(Path.GetTempPath(), $"aimmod-defender-{Guid.NewGuid():N}.json");

    [TearDown]
    public void TearDown() { if (File.Exists(state)) File.Delete(state); }

    [Test]
    public void TheCommandIsPlainAndOnlyNamesTheScanFolders()
    {
        string add = DefenderScanExclusion.Arguments(true, folders);
        Assert.Multiple(() =>
        {
            Assert.That(add, Does.StartWith("-NoProfile -Command \""));
            Assert.That(add, Does.Contain(@"Add-MpPreference -ExclusionPath 'C:\Users\Player\AppData\Roaming\AimMod\cache\pp-target-exact-v2.json.beatmaps','C:\Users\Player''s PC\AimMod\downloads\pp-target-difficulties' -ErrorAction Stop"));
            Assert.That(add, Does.Not.Contain("EncodedCommand").And.Not.Contain("ExecutionPolicy").And.Not.Contain("-WindowStyle Hidden"));
            Assert.That(add, Does.Contain("exit 0").And.Contain("exit 1"));
            Assert.That(add.Count(c => c == '"'), Is.EqualTo(2), "the whole script is one argument");
            Assert.That(DefenderScanExclusion.Arguments(false, folders), Does.Contain("Remove-MpPreference -ExclusionPath").And.Not.Contain("Add-MpPreference"));
        });
        Assert.Throws<ArgumentException>(() => _ = new DefenderScanExclusion([@"relative\folder"], state));
    }

    [Test]
    public async Task AddingRunsElevatedPowerShellAndRemembersIt()
    {
        if (!DefenderScanExclusion.Supported) Assert.Ignore("Microsoft Defender exclusions are Windows only.");
        ProcessStartInfo? started = null;
        var exclusion = new DefenderScanExclusion(folders, state, (start, _) => { started = start; return Task.FromResult(0); });
        var result = await exclusion.AddAsync();
        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(DefenderExclusionOutcome.Done));
            Assert.That((started!.FileName, started.Verb, started.UseShellExecute), Is.EqualTo(("powershell.exe", "runas", true)));
            Assert.That(started.WindowStyle, Is.EqualTo(ProcessWindowStyle.Normal), "the player sees the window");
            Assert.That(exclusion.Load(), Is.EqualTo(exclusion.Load() with { Added = true, Dismissed = true }));
        });
        Assert.That((await exclusion.RemoveAsync()).Outcome, Is.EqualTo(DefenderExclusionOutcome.Done));
        Assert.That(exclusion.Load().Added, Is.False);
    }

    [Test]
    public async Task DeclinedPermissionAndFailuresChangeNothing()
    {
        if (!DefenderScanExclusion.Supported) Assert.Ignore("Microsoft Defender exclusions are Windows only.");
        var declined = new DefenderScanExclusion(folders, state, (_, _) => throw new Win32Exception(1223));
        Assert.That((await declined.AddAsync()).Outcome, Is.EqualTo(DefenderExclusionOutcome.Cancelled));
        var refused = new DefenderScanExclusion(folders, state, (_, _) => Task.FromResult(1));
        var result = await refused.AddAsync();
        Assert.That(result.Outcome, Is.EqualTo(DefenderExclusionOutcome.Failed));
        Assert.That(result.Message, Does.Contain("organisation"));
        Assert.That(File.Exists(state), Is.False, "nothing is remembered when nothing changed");
    }
}
